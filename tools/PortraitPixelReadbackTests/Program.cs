#if !HAS_PIXEL_READBACK
Console.Error.WriteLine("FAIL: portrait readback has no bounded frame-pumped nonblocking ownership state machine");
return 1;
#else
using FireGaze.PortraitPreview;

var checks = 0;
void Check(bool condition, string reason)
{
    checks++;
    if (!condition) throw new InvalidOperationException(reason);
}
var identity = new PortraitPixelReadbackIdentity(101, 7);

// A blocking map, a second attempt in the same frame, or losing padding bytes catches this test.
{
    var source = new Source(identity);
    var backend = new Backend(identity);
    var transfer = new Transfer(2);
    backend.Transfers.Enqueue(transfer);
    var reader = new PortraitPixelReadback(backend, identity);
    var task = reader.ReadAsync(source);
    source.Dispose();
    Check(source.Lease is { Disposed: false }, "ReadAsync retains a distinct source before returning");
    Check(backend.Begins == 0, "ReadAsync never submits native work on its caller");
    reader.Pump(1);
    reader.Pump(1);
    Check(transfer.MapCalls == 1 && !task.IsCompleted, "StillDrawing yields to the next distinct frame");
    reader.Pump(2);
    Check(transfer.MapCalls == 2 && !task.IsCompleted, "a second busy frame does not spin");
    reader.Pump(3);
    var pixels = await task;
    Check(transfer.MapCalls == 3 && transfer.Unmaps == 1, "successful mapping is unmapped exactly once");
    Check(pixels.Width == 2 && pixels.Height == 2 && pixels.RowPitch == 8 && pixels.DxgiFormat == 28,
        "published pixels describe tight rows");
    Check(pixels.Pixels.SequenceEqual(new byte[] { 1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16 }),
        "RowPitch padding is skipped and DepthPitch zero never truncates data");
    Check(transfer.Mapping.Offsets.SequenceEqual(new[] { 0, 12 }), "row offsets use RowPitch, never DepthPitch");
    Check(source.Lease!.Disposed && transfer.Retired && reader.PendingCount == 0, "completion follows physical retirement and source release");
    Check((await reader.Close().ContinueWith(t => t.IsCompletedSuccessfully)), "close drains an idle engine");
}

// A cancellation task completing early would allow the caller to unload the native owner.
{
    var backend = new Backend(identity);
    var transfer = new Transfer(20) { CanRetire = false };
    backend.Transfers.Enqueue(transfer);
    var source = new Source(identity);
    var reader = new PortraitPixelReadback(backend, identity);
    using var cancel = new CancellationTokenSource();
    var task = reader.ReadAsync(source, cancel.Token);
    reader.Pump(1);
    cancel.Cancel();
    var drain = reader.Close();
    reader.Pump(2);
    Check(!task.IsCompleted && !drain.IsCompleted && !source.Lease!.Disposed,
        "cancel and close retain physical work and the source owner");
    Check(transfer.MapCalls == 1 && transfer.RetireCalls == 1, "cancel never maps again and only polls retirement once per frame");
    transfer.CanRetire = true;
    reader.Pump(3);
    Check(task.IsCanceled && drain.IsCompletedSuccessfully && source.Lease!.Disposed,
        "cancellation completes only when physical retirement has succeeded");
    Check(reader.ReadAsync(new Source(identity)).IsFaulted, "close seals new admission");
}

// A mapped result is not publishable while GPU/backend references remain outstanding.
{
    var backend = new Backend(identity);
    var transfer = new Transfer(0) { CanRetire = false };
    backend.Transfers.Enqueue(transfer);
    var source = new Source(identity);
    var reader = new PortraitPixelReadback(backend, identity);
    var task = reader.ReadAsync(source);
    reader.Pump(1);
    Check(!task.IsCompleted && !source.Lease!.Disposed, "mapped pixels wait for real physical retirement");
    reader.Pump(2);
    Check(transfer.MapCalls == 1 && transfer.Unmaps == 1 && transfer.RetireCalls == 2,
        "retiring a completed map never remaps or recopies pixels");
    transfer.CanRetire = true;
    reader.Pump(3);
    Check(task.IsCompletedSuccessfully, "physical completion makes the result available");
}

// Generation loss must never map an old source or let a late result cross session ownership.
foreach (var changed in new[] { default(PortraitPixelReadbackIdentity), new PortraitPixelReadbackIdentity(101, 8), new PortraitPixelReadbackIdentity(102, 7) })
{
    var backend = new Backend(identity);
    var transfer = new Transfer(20) { CanRetire = false };
    backend.Transfers.Enqueue(transfer);
    var source = new Source(identity);
    var reader = new PortraitPixelReadback(backend, identity);
    var task = reader.ReadAsync(source);
    reader.Pump(1);
    backend.Identity = changed;
    reader.Pump(2);
    Check(transfer.MapCalls == 1 && !task.IsCompleted && !source.Lease!.Disposed,
        "unknown or changed device/generation suppresses Map and retains native ownership");
    transfer.CanRetire = true;
    reader.Pump(3);
    Check(task.IsFaulted && reader.ReadAsync(new Source(identity)).IsFaulted,
        "identity loss fails closed even after physical retirement");
}

// Capacity counts retiring work; otherwise cancellation could grow an unbounded GPU queue.
{
    var backend = new Backend(identity);
    var transfer = new Transfer(20) { CanRetire = false };
    backend.Transfers.Enqueue(transfer);
    var reader = new PortraitPixelReadback(backend, identity, capacity: 2);
    var first = reader.ReadAsync(new Source(identity));
    using var cancel = new CancellationTokenSource();
    var secondSource = new Source(identity);
    var second = reader.ReadAsync(secondSource, cancel.Token);
    var rejected = new Source(identity);
    Check(reader.ReadAsync(rejected).IsFaulted && rejected.Retains == 0, "full capacity is rejected before retaining another source");
    reader.Pump(1);
    cancel.Cancel();
    var drain = reader.Close();
    reader.Pump(2);
    Check(!drain.IsCompleted && reader.PendingCount == 2, "queued cancellation cannot hide still-retiring occupancy");
    transfer.CanRetire = true;
    reader.Pump(3);
    reader.Pump(4);
    Check(first.IsCanceled && second.IsCanceled && secondSource.Lease!.Disposed && backend.Begins == 1 && drain.IsCompletedSuccessfully,
        "queued canceled work releases its own lease without creating GPU work");
}

// Invalid pitches and mapping faults must unmap, then retire before reporting failure.
foreach (var badPitch in new[] { -1, 0, 7, int.MaxValue })
{
    var backend = new Backend(identity);
    var transfer = new Transfer(0);
    transfer.Mapping.RowPitch = badPitch;
    backend.Transfers.Enqueue(transfer);
    var source = new Source(identity);
    var reader = new PortraitPixelReadback(backend, identity);
    var task = reader.ReadAsync(source);
    reader.Pump(1);
    Check(task.IsFaulted && transfer.Unmaps == 1 && transfer.Retired && source.Lease!.Disposed,
        "invalid row spans fail safely without exposing pixels or leaving a map open");
}
{
    var backend = new Backend(identity);
    var transfer = new Transfer(0);
    transfer.Mapping.ThrowOnCopy = true;
    backend.Transfers.Enqueue(transfer);
    var reader = new PortraitPixelReadback(backend, identity);
    var task = reader.ReadAsync(new Source(identity));
    reader.Pump(1);
    Check(task.IsFaulted && transfer.Unmaps == 1 && transfer.Retired, "row-copy failure still unmaps and retires");
}

// Unknown cleanup outcomes must keep the actual objects rooted and block further admissions.
foreach (var fault in new[] { "unmap", "retire", "release" })
{
    var backend = new Backend(identity);
    var transfer = new Transfer(0) { ThrowOnUnmap = fault == "unmap", ThrowOnRetire = fault == "retire" };
    backend.Transfers.Enqueue(transfer);
    var source = new Source(identity) { ThrowOnRelease = fault == "release" };
    var reader = new PortraitPixelReadback(backend, identity);
    var task = reader.ReadAsync(source);
    reader.Pump(1);
    var drain = reader.Close();
    reader.Pump(2);
    Check(!task.IsCompleted && !drain.IsCompleted && reader.QuarantinedCount == 1,
        "uncertain cleanup never reports physical completion");
    Check(reader.ReadAsync(new Source(identity)).IsFaulted, "quarantine seals native admission");
}

// Retained wrappers must be independent, bounded, supported and match the current device.
foreach (var defect in new[] { "same", "width", "format", "identity" })
{
    var backend = new Backend(identity);
    var source = new Source(identity) { ReturnSelf = defect == "same", Width = defect == "width" ? int.MaxValue : 2,
        DxgiFormat = defect == "format" ? 2 : 28 };
    if (defect == "identity") source.Identity = new(101, 8);
    var reader = new PortraitPixelReadback(backend, identity);
    var task = reader.ReadAsync(source);
    reader.Pump(1);
    Check(task.IsFaulted && backend.Begins == 0 && (!source.ReturnSelf || !source.Disposed),
        "unsafe retained input is refused without touching the caller wrapper or native backend");
}
// Native format discovery is deferred to the authorized render thread, before any submission.
foreach (var resolvedFormat in new[] { 28, 29, 87, 91, 0, 2 })
{
    var backend = new Backend(identity) { ResolvedFormat = resolvedFormat };
    var transfer = new Transfer(0);
    backend.Transfers.Enqueue(transfer);
    var reader = new PortraitPixelReadback(backend, identity);
    var task = reader.ReadAsync(new Source(identity) { DxgiFormat = 0 });
    reader.Pump(1);
    var supported = resolvedFormat is 28 or 29 or 87 or 91;
    Check(supported ? task.IsCompletedSuccessfully && task.Result.DxgiFormat == resolvedFormat : task.IsFaulted,
        "deferred format must be resolved and supported before pixel publication");
    Check(transfer.SubmitCalls == (supported ? 1 : 0), "unresolved or unsupported format never submits native work");
}

// A partially submitted transfer must stay owned after Submit throws.
{
    var backend = new Backend(identity);
    var transfer = new Transfer(0) { ThrowOnSubmit = true, CanRetire = false };
    backend.Transfers.Enqueue(transfer);
    var source = new Source(identity);
    var reader = new PortraitPixelReadback(backend, identity);
    var task = reader.ReadAsync(source);
    reader.Pump(1);
    Check(!task.IsCompleted && !source.Lease!.Disposed && transfer.MapCalls == 0,
        "submission exception is still physically owned and never mapped");
    transfer.CanRetire = true;
    reader.Pump(2);
    Check(task.IsFaulted && source.Lease!.Disposed && transfer.SubmitCalls == 1,
        "submission failure is reported only after retirement, without retrying submission");
}

// Reentrant callbacks cannot retire the engine while Retain is acquiring ownership.
{
    var backend = new Backend(identity);
    var reader = new PortraitPixelReadback(backend, identity);
    Task? drain = null;
    var source = new Source(identity) { OnRetain = () => drain = reader.Close() };
    var task = reader.ReadAsync(source);
    Check(drain is { IsCompleted: false } && !task.IsCompleted, "an in-flight Retain counts toward physical drain");
    reader.Pump(1);
    Check(task.IsCanceled && drain!.IsCompletedSuccessfully && source.Lease!.Disposed && backend.Begins == 0,
        "close during Retain prevents native submission and safely releases the acquired lease");
}

// A nested pump cannot map a second time while the first render callback still owns the transfer.
{
    var backend = new Backend(identity);
    var transfer = new Transfer(1);
    backend.Transfers.Enqueue(transfer);
    var reader = new PortraitPixelReadback(backend, identity);
    transfer.OnMap = () => reader.Pump(2);
    var task = reader.ReadAsync(new Source(identity));
    reader.Pump(1);
    Check(transfer.MapCalls == 1 && !task.IsCompleted, "nested frame callbacks never reenter Map");
    reader.Pump(2);
    Check(task.IsCompletedSuccessfully && transfer.MapCalls == 2, "a later real pump still advances after rejected reentry");
}

// Device changes or Close triggered by native completion must revoke the already copied image.
foreach (var changeIdentity in new[] { false, true })
{
    var backend = new Backend(identity);
    var transfer = new Transfer(0);
    backend.Transfers.Enqueue(transfer);
    var reader = new PortraitPixelReadback(backend, identity);
    transfer.OnRetire = () => { if (changeIdentity) backend.Identity = new(101, 8); else reader.Close(); };
    var task = reader.ReadAsync(new Source(identity));
    reader.Pump(1);
    Check(changeIdentity ? task.IsFaulted : task.IsCanceled,
        "the publication edge revalidates close and current device generation");
}
// Cancellation between native Map and byte detachment is cancellation, never a fault or image.
{
    var backend = new Backend(identity);
    var transfer = new Transfer(0) { CanRetire = false };
    backend.Transfers.Enqueue(transfer);
    using var cancel = new CancellationTokenSource();
    transfer.OnMap = cancel.Cancel;
    var source = new Source(identity);
    var reader = new PortraitPixelReadback(backend, identity);
    var task = reader.ReadAsync(source, cancel.Token);
    reader.Pump(1);
    Check(!task.IsCompleted && !source.Lease!.Disposed && transfer.Unmaps == 1,
        "cancellation during mapping still unmaps and physically drains");
    transfer.CanRetire = true;
    reader.Pump(2);
    Check(task.IsCanceled && transfer.MapCalls == 1, "cancellation during pixel copy is reported as cancelled after retirement");
}
Console.WriteLine($"PortraitPixelReadbackTests: {checks} passed, 0 failed");
return 0;

sealed class Source(PortraitPixelReadbackIdentity identity) : IPortraitPixelReadbackSource, IPortraitPixelReadbackLease
{
    public PortraitPixelReadbackIdentity Identity { get; set; } = identity;
    public int Width { get; set; } = 2;
    public int Height => 2;
    public int DxgiFormat { get; set; } = 28;
    public int Retains { get; private set; }
    public bool Disposed { get; private set; }
    public bool ReturnSelf { get; init; }
    public bool ThrowOnRelease { get; init; }
    public Action? OnRetain { get; init; }
    public Lease? Lease { get; private set; }
    public IPortraitPixelReadbackLease Retain()
    {
        Retains++;
        if (Disposed) throw new ObjectDisposedException(nameof(Source));
        OnRetain?.Invoke();
        if (ReturnSelf) return this;
        return Lease = new(Identity, Width, Height, DxgiFormat, ThrowOnRelease);
    }
    public void Dispose() => Disposed = true;
}
sealed class Lease(PortraitPixelReadbackIdentity identity, int width, int height, int format, bool throwOnDispose) : IPortraitPixelReadbackLease
{
    public PortraitPixelReadbackIdentity Identity => identity;
    public int Width => width;
    public int Height => height;
    public int DxgiFormat { get; set; } = format;
    public bool Disposed { get; private set; }
    public void Dispose()
    {
        if (throwOnDispose) throw new InvalidOperationException("unknown release outcome");
        if (Disposed) throw new InvalidOperationException("double release");
        Disposed = true;
    }
}
sealed class Backend(PortraitPixelReadbackIdentity identity) : IPortraitPixelReadbackBackend
{
    public PortraitPixelReadbackIdentity Identity { get; set; } = identity;
    public Queue<Transfer> Transfers { get; } = new();
    public int Begins { get; private set; }
    public int? ResolvedFormat { get; init; }
    public IPortraitPixelReadbackTransfer CreateTransfer(IPortraitPixelReadbackLease source)
    {
        if (source is not Lease { Disposed: false }) throw new InvalidOperationException("source ownership missing");
        Begins++;
        if (ResolvedFormat is { } resolved) ((Lease)source).DxgiFormat = resolved;
        return Transfers.Dequeue();
    }
}
sealed class Transfer(int busyFrames) : IPortraitPixelReadbackTransfer
{
    public Mapping Mapping { get; } = new();
    public int MapCalls { get; private set; }
    public int Unmaps { get; private set; }
    public int RetireCalls { get; private set; }
    public bool CanRetire { get; set; } = true;
    public bool Retired { get; private set; }
    public bool ThrowOnUnmap { get; init; }
    public bool ThrowOnRetire { get; init; }
    public bool ThrowOnSubmit { get; init; }
    public int SubmitCalls { get; private set; }
    public Action? OnMap { get; set; }
    public Action? OnRetire { get; set; }
    public void Submit()
    {
        SubmitCalls++;
        if (ThrowOnSubmit) throw new InvalidOperationException("partially submitted copy");
    }
    public PortraitPixelReadbackMapStatus TryMap(out IPortraitPixelReadbackMapping? mapping)
    {
        MapCalls++;
        var callback = OnMap;
        OnMap = null;
        callback?.Invoke();
        mapping = MapCalls <= busyFrames ? null : Mapping;
        return mapping is null ? PortraitPixelReadbackMapStatus.StillDrawing : PortraitPixelReadbackMapStatus.Ready;
    }
    public void Unmap()
    {
        Unmaps++;
        if (ThrowOnUnmap) throw new InvalidOperationException("unknown unmap outcome");
    }
    public bool TryRetire()
    {
        RetireCalls++;
        OnRetire?.Invoke();
        if (ThrowOnRetire) throw new InvalidOperationException("unknown retire outcome");
        return Retired = CanRetire;
    }
}
sealed class Mapping : IPortraitPixelReadbackMapping
{
    private readonly byte[] bytes = [1,2,3,4,5,6,7,8,99,99,99,99,9,10,11,12,13,14,15,16];
    public int RowPitch { get; set; } = 12;
    public int DepthPitch => 0;
    public bool ThrowOnCopy { get; set; }
    public List<int> Offsets { get; } = new();
    public void CopyBytes(int sourceByteOffset, Span<byte> destination)
    {
        if (ThrowOnCopy) throw new InvalidOperationException("invalid source bytes");
        Offsets.Add(sourceByteOffset);
        bytes.AsSpan(sourceByteOffset, destination.Length).CopyTo(destination);
    }
}
#endif
