namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitPixelReadbackIdentity(nint Device, long Generation)
{
    public bool IsValid => Device != 0 && Generation > 0;
}

internal interface IPortraitPixelReadbackSource
{
    // Must synchronously acquire independently disposable source ownership. This call submits no
    // GPU work. Throwing must leave no acquired ownership hidden from the caller.
    IPortraitPixelReadbackLease Retain();
}

internal interface IPortraitPixelReadbackLease : IDisposable
{
    PortraitPixelReadbackIdentity Identity { get; }
    int Width { get; }
    int Height { get; }
    int DxgiFormat { get; }
}

internal interface IPortraitPixelReadbackBackend
{
    PortraitPixelReadbackIdentity Identity { get; }
    // Creates an owned transfer object WITHOUT submitting GPU work. On exception the backend must
    // release anything created internally; the source lease remains owned by the state machine.
    IPortraitPixelReadbackTransfer CreateTransfer(IPortraitPixelReadbackLease source);
}

internal enum PortraitPixelReadbackMapStatus { StillDrawing, Ready, Failed }

internal interface IPortraitPixelReadbackTransfer
{
    // Called once after ownership of the transfer has been stored, so even a partially successful
    // submission which throws still has a physical retirement owner.
    void Submit();
    // Exactly one nonblocking Map attempt. StillDrawing MUST NOT spin, flush, sleep or wait.
    PortraitPixelReadbackMapStatus TryMap(out IPortraitPixelReadbackMapping? mapping);
    void Unmap();
    // A nonblocking physical-completion probe and release. False leaves all needed resources
    // owned. True proves all transfer-owned resources were released, including late GPU work.
    // Uses this transfer's owned device/context, never a possibly changed current global device.
    bool TryRetire();
}

internal interface IPortraitPixelReadbackMapping
{
    int RowPitch { get; }
    int DepthPitch { get; }
    // Copies exactly destination.Length bytes from the live mapping. No padding or length is
    // inferred from DepthPitch. The mapping is valid only until its transfer is unmapped.
    void CopyBytes(int sourceByteOffset, Span<byte> destination);
}

internal sealed record PortraitPixelReadbackImage(int Width, int Height, int DxgiFormat,
    int RowPitch, byte[] Pixels);

/// <summary>
/// Bounded, frame-pumped readback. ReadAsync retains on its caller but submits no native work;
/// Pump belongs to the authorized native/render thread and attempts at most one Map per frame.
/// Cancellation and Close revoke publication, never physical ownership. Keep pumping after Close
/// until its returned task completes. A stalled GPU or uncertain cleanup deliberately blocks drain.
/// This core adds no production entry point, render hook, or native ABI dependency.
/// </summary>
internal sealed class PortraitPixelReadback
{
    private const int MaximumDimension = 16_384;
    private const int MaximumPixels = 16_777_216;
    private const long MaximumSourceBytes = MaximumPixels * 4L;
    private readonly object sync = new();
    private readonly IPortraitPixelReadbackBackend backend;
    private readonly PortraitPixelReadbackIdentity identity;
    private readonly int capacity;
    private readonly Queue<Request> queued = new();
    // Failed native cleanup is never retried blindly. Holding the complete request also keeps the
    // source, transfer and original task rooted for diagnostics and the plugin's unload gate.
    private readonly List<Request> quarantine = new();
    private readonly TaskCompletionSource drained;
    private Request? active;
    private Exception? admissionFailure;
    private int pending;
    private long lastFrame = long.MinValue;
    private bool closed;
    private bool pumping;

    public PortraitPixelReadback(IPortraitPixelReadbackBackend backend,
        PortraitPixelReadbackIdentity identity, int capacity = 3)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (!identity.IsValid) throw new ArgumentException("A known device generation is required.", nameof(identity));
        if (capacity is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.backend = backend;
        this.identity = identity;
        this.capacity = capacity;
        drained = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public int PendingCount { get { lock (sync) return pending; } }
    public int QuarantinedCount { get { lock (sync) return quarantine.Count; } }
    public string? LastFailureType { get { lock (sync) return admissionFailure?.GetType().FullName; } }

    // Intentionally not async. The independently owned source reference is established before the
    // caller is allowed to dispose its wrapper, even if no later Pump ever runs.
    public Task<PortraitPixelReadbackImage> ReadAsync(IPortraitPixelReadbackSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (sync)
        {
            if (closed || admissionFailure is not null)
                return Task.FromException<PortraitPixelReadbackImage>(admissionFailure
                    ?? new ObjectDisposedException(nameof(PortraitPixelReadback)));
            if (pending >= capacity)
                return Task.FromException<PortraitPixelReadbackImage>(new InvalidOperationException("Readback capacity is full."));
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<PortraitPixelReadbackImage>(cancellationToken);
            // Reserve before invoking an external Retain: it can synchronously reenter Close.
            // A completed drain must never be followed by an unaccounted newly acquired lease.
            pending++;
            IPortraitPixelReadbackLease lease;
            try
            {
                lease = source.Retain();
                if (lease is null || ReferenceEquals(source, lease))
                {
                    pending--;
                    CompleteDrainIfSafe();
                    return Task.FromException<PortraitPixelReadbackImage>(new InvalidOperationException("Independent source ownership is required."));
                }
            }
            catch (Exception error)
            {
                pending--;
                CompleteDrainIfSafe();
                return Task.FromException<PortraitPixelReadbackImage>(error);
            }
            var request = new Request(this, lease, cancellationToken);
            try
            {
                request.Width = lease.Width;
                request.Height = lease.Height;
                request.Format = lease.DxgiFormat;
                if (lease.Identity != identity || !ValidDimensions(request.Width, request.Height)
                    || request.Format is not (0 or 28 or 29 or 87 or 91))
                    request.Failure = new InvalidOperationException("Unsupported source or changed source identity.");
            }
            catch (Exception error) { request.Failure = error; }
            queued.Enqueue(request);
            return request.Completion.Task;
        }
    }

    public Task Close()
    {
        lock (sync)
        {
            closed = true;
            CompleteDrainIfSafe();
            return drained.Task;
        }
    }

    public void Pump(long frameID)
    {
        lock (sync)
        {
            if (pumping) return;
            pumping = true;
            try { PumpFrame(frameID); }
            finally { pumping = false; }
        }
    }

    // Invoked only under sync and the nonreentrant pump gate.
    private void PumpFrame(long frameID)
    {
            if (frameID == lastFrame) return;
            if (frameID < lastFrame) admissionFailure ??= new InvalidOperationException("Readback frame identity moved backwards.");
            lastFrame = frameID;
        try
        {
            if (backend.Identity != identity)
                admissionFailure ??= new InvalidOperationException("Readback device generation is unavailable or changed.");
        }
        catch (Exception error) { admissionFailure ??= error; }

        if (active is null && queued.Count > 0) active = queued.Dequeue();
        if (active is not { } request) { CompleteDrainIfSafe(); return; }
        request.Failure ??= admissionFailure;
        if (closed || request.Token.IsCancellationRequested) request.Cancelled = true;

        if (request.Failure is null && !request.Cancelled && request.Transfer is null)
        {
            try
            {
                request.Transfer = backend.CreateTransfer(request.Source)
                    ?? throw new InvalidOperationException("No readback transfer was created.");
                // A native adapter may only discover the typed SRV format on this render
                // thread. Zero is provisional on Retain, never accepted for submission.
                request.Format = request.Source.DxgiFormat;
                if (request.Format is not (28 or 29 or 87 or 91)
                    || request.Source.Identity != identity || request.Source.Width != request.Width
                    || request.Source.Height != request.Height)
                    request.Failure = new InvalidOperationException("Source identity or resolved format changed before submission.");
                RefreshAdmission(request);
                if (request.Failure is null && !request.Cancelled) request.Transfer.Submit();
            }
            catch (Exception error) { request.Failure = error; }
        }
        RefreshAdmission(request);
        if (request.Failure is null && !request.Cancelled && request.Result is null && request.Transfer is { } transfer)
        {
            PortraitPixelReadbackMapStatus status;
            IPortraitPixelReadbackMapping? mapping;
            try { status = transfer.TryMap(out mapping); }
            // An exception has no trustworthy Map outcome. Never guess whether Unmap/release is safe.
            catch (Exception error) { Quarantine(request, error); return; }
            if (status == PortraitPixelReadbackMapStatus.StillDrawing && mapping is null) return;
            if (status == PortraitPixelReadbackMapStatus.Ready)
            {
                try { request.Result = CopyRows(request, mapping); }
                catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { request.Cancelled = true; }
                catch (Exception error) { request.Failure = error; }
                try { transfer.Unmap(); }
                catch (Exception error) { Quarantine(request, error); return; }
            }
            else if (status == PortraitPixelReadbackMapStatus.Failed && mapping is null)
                request.Failure = new InvalidOperationException("Nonblocking readback mapping failed.");
            else
            {
                Quarantine(request, new InvalidOperationException("Unknown readback mapping ownership."));
                return;
            }
        }
        // The token may be cancelled while the native probe or bounded row copy was running.
        // No result, including an already-copied image, may cross that revoked publication edge.
        if (closed || request.Token.IsCancellationRequested) request.Cancelled = true;
        if (request.Transfer is { } retiring)
        {
            try { if (!retiring.TryRetire()) return; }
            catch (Exception error) { Quarantine(request, error); return; }
            request.Transfer = null;
        }
        try { request.Source.Dispose(); }
        catch (Exception error) { Quarantine(request, error); return; }
        RefreshAdmission(request);
        active = null;
        pending--;
        if (request.Failure is { } failure) request.Completion.TrySetException(failure);
        else if (request.Cancelled) request.Completion.TrySetCanceled(request.Token.IsCancellationRequested ? request.Token : new CancellationToken(true));
        else if (request.Result is { } result) request.Completion.TrySetResult(result);
        else request.Completion.TrySetException(new InvalidOperationException("Readback completed without pixels."));
        CompleteDrainIfSafe();
    }

    private void RefreshAdmission(Request request)
    {
        try
        {
            if (backend.Identity != identity)
                admissionFailure ??= new InvalidOperationException("Readback device generation is unavailable or changed.");
        }
        catch (Exception error) { admissionFailure ??= error; }
        request.Failure ??= admissionFailure;
        if (closed || request.Token.IsCancellationRequested) request.Cancelled = true;
    }

    private static bool ValidDimensions(int width, int height)
        => width is > 0 and <= MaximumDimension && height is > 0 and <= MaximumDimension
            && (long)width * height <= MaximumPixels;

    private static PortraitPixelReadbackImage CopyRows(Request request, IPortraitPixelReadbackMapping? mapping)
    {
        if (mapping is null) throw new InvalidOperationException("Mapped readback has no row source.");
        var rowBytes = checked(request.Width * 4);
        var pitch = mapping.RowPitch;
        // DepthPitch is undefined for this 2D contract, including zero. It is never a byte length.
        var required = (long)(request.Height - 1) * pitch + rowBytes;
        if (pitch < rowBytes || required > MaximumSourceBytes)
            throw new InvalidOperationException("Invalid or over-budget mapped row pitch.");
        var pixels = new byte[checked(rowBytes * request.Height)];
        for (var row = 0; row < request.Height; row++)
        {
            request.Token.ThrowIfCancellationRequested();
            mapping.CopyBytes(checked(row * pitch), pixels.AsSpan(row * rowBytes, rowBytes));
        }
        return new(request.Width, request.Height, request.Format, rowBytes, pixels);
    }

    private void Quarantine(Request request, Exception failure)
    {
        request.Failure ??= failure;
        admissionFailure ??= failure;
        quarantine.Add(request);
        active = null;
        // pending intentionally does not fall. Unknown native cleanup can never satisfy unload.
    }

    private void CompleteDrainIfSafe()
    {
        if (closed && pending == 0 && quarantine.Count == 0) drained.TrySetResult();
    }

    private sealed class Request(PortraitPixelReadback owner, IPortraitPixelReadbackLease source, CancellationToken token)
    {
        public IPortraitPixelReadbackLease Source { get; } = source;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<PortraitPixelReadbackImage> Completion { get; } = new(owner, TaskCreationOptions.RunContinuationsAsynchronously);
        public int Width;
        public int Height;
        public int Format;
        public IPortraitPixelReadbackTransfer? Transfer;
        public PortraitPixelReadbackImage? Result;
        public Exception? Failure;
        public bool Cancelled;
    }
}
