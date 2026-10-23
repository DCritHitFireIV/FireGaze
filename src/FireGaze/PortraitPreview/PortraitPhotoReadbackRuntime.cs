using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Owns an independent public Draw subscription until every accepted GPU transfer retires.
/// Feature/window disposal seals admission but cannot remove this retirement callback early.
/// </summary>
internal sealed class PortraitPhotoReadbackRuntime
{
    private readonly object sync = new();
    private readonly IUiBuilder ui;
    private readonly IPortraitPixelReadbackDrawBackend backend;
    private readonly TaskCompletionSource drained;
    private readonly Action<string>? report;
    private PortraitPixelReadback? reader;
    private PortraitPixelReadbackIdentity readerIdentity;
    private Task? retiring;
    private long frame;
    private bool closed;
    private bool detached;
    private Exception? failure;

    public PortraitPhotoReadbackRuntime(IUiBuilder ui, IPortraitPixelReadbackDrawBackend backend,
        Action<string>? report = null)
    {
        this.ui = ui;
        this.backend = backend;
        this.report = report;
        drained = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
        Provider = new PortraitPhotoReadbackProvider(Acquire);
        ui.Draw += Draw;
        ui.ResizeBuffers += Resize;
    }

    public PortraitPhotoReadbackProvider Provider { get; }
    public int Pending { get { lock (sync) return reader?.PendingCount ?? 0; } }
    public int Quarantined { get { lock (sync) return reader?.QuarantinedCount ?? 0; } }
    public string? Failure { get { lock (sync) return failure?.GetType().FullName ?? reader?.LastFailureType; } }

    private Task<(RawImageSpecification Specification, byte[] RawData)> Acquire(
        IDalamudTextureWrap wrap, CancellationToken token)
    {
        lock (sync)
        {
            if (closed || failure is not null || reader is null || retiring is not null)
                return Task.FromException<(RawImageSpecification, byte[])>(
                    new InvalidOperationException("Portrait readback is waiting for an available render device."));
            // ReadAsync must run synchronously here: it retains its own wrapper before returning.
            return Convert(reader.ReadAsync(backend.CreateSource(wrap), token));
        }
    }

    private static async Task<(RawImageSpecification, byte[])> Convert(Task<PortraitPixelReadbackImage> pending)
    {
        var raw = await pending.ConfigureAwait(false);
        return (new(raw.Width, raw.Height, raw.DxgiFormat, raw.RowPitch), raw.Pixels);
    }

    private void Draw()
    {
        lock (sync)
        {
            if (detached) return;
            try
            {
                using var nativeScope = backend.EnterDraw();
                var identity = backend.Identity;
                if (reader is not null && (identity != readerIdentity || reader.LastFailureType is not null))
                    retiring ??= reader.Close();
                if (reader is null && !closed && failure is null && identity.IsValid)
                {
                    readerIdentity = identity;
                    reader = new(backend, identity);
                }
                reader?.Pump(checked(++frame));
                if (retiring?.IsCompletedSuccessfully == true)
                {
                    reader = null;
                    retiring = null;
                    if (!closed && failure is null && identity.IsValid)
                    {
                        readerIdentity = identity;
                        reader = new(backend, identity);
                    }
                }
                FinishClose();
            }
            catch (Exception error)
            {
                if (failure is null)
                {
                    failure = error;
                    try { report?.Invoke(error.GetType().FullName ?? error.GetType().Name); } catch { }
                }
                retiring ??= reader?.Close();
                // Failure before physical completion retains this subscription and its owner.
                // No synchronous wait or speculative COM release is allowed on this path.
                FinishClose();
            }
        }
    }

    private void Resize()
    {
        lock (sync)
        {
            if (closed) return;
            backend.Invalidate();
            retiring ??= reader?.Close();
        }
    }

    public Task Close()
    {
        lock (sync)
        {
            closed = true;
            retiring ??= reader?.Close();
            FinishClose();
            return drained.Task;
        }
    }

    private void FinishClose()
    {
        if (!closed || detached || reader is not null && retiring?.IsCompletedSuccessfully != true) return;
        ui.Draw -= Draw;
        ui.ResizeBuffers -= Resize;
        detached = true;
        reader = null;
        drained.TrySetResult();
    }
}
