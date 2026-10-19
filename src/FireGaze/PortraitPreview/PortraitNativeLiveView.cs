using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

// Native addresses are opaque equality tokens. Draw must never dereference them.
internal readonly record struct PortraitNativeLiveKey(PortraitCaptureOwner Owner, PortraitIdentity Editor,
    nint Addon, nint Component, nint Image, long Epoch)
{
    public bool IsValid => Owner.IsValid && Editor.IsValid && Editor.Generation != 0
        && Addon != 0 && Component != 0 && Image != 0 && Epoch > 0;
}

internal readonly record struct PortraitNativeLiveImage(ImTextureID Handle, Vector2 UV0, Vector2 UV1,
    uint Tint, int PixelWidth, int PixelHeight, long FrameNumber, PortraitNativeLiveKey Key);

/// <summary>One independently owned official SRV wrap, with no native texture pointer.</summary>
internal sealed class PortraitNativeLiveFrame(PortraitNativeLiveKey key, IDalamudTextureWrap texture,
    uint allocatedWidth, uint allocatedHeight, uint actualWidth, uint actualHeight, uint tint) : IDisposable
{
    private int disposed;

    public bool TryDescribe(long frameNumber, out PortraitNativeLiveImage image)
    {
        image = default;
        if (Volatile.Read(ref disposed) != 0 || !key.IsValid
            || allocatedWidth is 0 or > 16384 || allocatedHeight is 0 or > 16384
            || actualWidth == 0 || actualHeight == 0 || actualWidth > allocatedWidth || actualHeight > allocatedHeight
            || texture.Width != allocatedWidth || texture.Height != allocatedHeight) return false;
        var handle = texture.Handle;
        if (handle.Handle == 0) return false;
        image = new(handle, Vector2.Zero, new(actualWidth / (float)allocatedWidth, actualHeight / (float)allocatedHeight),
            tint, (int)actualWidth, (int)actualHeight, frameNumber, key);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) texture.Dispose();
    }
}

internal interface IPortraitNativeLiveSource
{
    bool IsFrameworkThread { get; }
    event Action? Invalidated;
    // A returned frame transfers its independent official wrap to the caller, including on false.
    bool TryAcquire(out PortraitNativeLiveFrame? frame);
}

/// <summary>
/// Acquire-only Phase A. It shows the current native portrait camera, not an independent 3D camera.
/// The caller owns the source and calls Update on Framework; this view starts disabled.
/// Draw borrows only detached image data and keeps its lease through ordinary ImGui AddImage submission.
/// </summary>
internal sealed class PortraitNativeLiveView : IDisposable
{
    private readonly IPortraitNativeLiveSource source;
    private readonly object gate = new();
    private bool enabled;
    private bool disposed;
    private long revision;
    private long frameNumber;
    private Slot? current;
    private int disposalFailures;

    public PortraitNativeLiveView(IPortraitNativeLiveSource source)
    {
        this.source = source;
        source.Invalidated += Invalidate;
    }

    public int DisposalFailures => Volatile.Read(ref disposalFailures);

    public void SetEnabled(bool value)
    {
        PortraitNativeLiveFrame? retired = null;
        lock (gate)
        {
            if (disposed || enabled == value) return;
            enabled = value;
            revision++;
            if (!value) retired = RetireCurrent();
        }
        ReleaseFrame(retired);
    }

    public void Invalidate()
    {
        PortraitNativeLiveFrame? retired;
        lock (gate)
        {
            revision++;
            retired = RetireCurrent();
        }
        ReleaseFrame(retired);
    }

    public void Update()
    {
        long ticket;
        lock (gate)
        {
            if (disposed || !enabled) return;
            ticket = revision;
        }
        PortraitNativeLiveFrame? candidate = null;
        PortraitNativeLiveFrame? retired = null;
        try
        {
            if (!source.IsFrameworkThread || !source.TryAcquire(out candidate) || candidate is null
                || !candidate.TryDescribe(0, out var image))
            {
                Invalidate();
                return;
            }
            lock (gate)
            {
                if (disposed || !enabled || revision != ticket) return;
                // Never deduplicate on SRV: the same render target can contain a new animated frame.
                retired = RetireCurrent();
                current = new(candidate, image with { FrameNumber = ++frameNumber });
                candidate = null;
            }
        }
        catch (Exception)
        {
            Invalidate();
        }
        finally
        {
            ReleaseFrame(candidate);
            ReleaseFrame(retired);
        }
    }

    public bool TryBorrow(out PortraitNativeLiveBorrow? borrow)
    {
        lock (gate)
        {
            borrow = null;
            if (disposed || !enabled || current is null || current.Retired) return false;
            current.Borrowers++;
            borrow = new(this, current);
            return true;
        }
    }

    internal bool TryRead(Slot slot, PortraitNativeLiveBorrow borrow, out PortraitNativeLiveImage image)
    {
        lock (gate)
        {
            image = default;
            if (disposed || !enabled || borrow.IsReleased || slot.Retired || current != slot) return false;
            image = slot.Image;
            return true;
        }
    }

    internal void Return(Slot slot)
    {
        PortraitNativeLiveFrame? retired = null;
        lock (gate)
        {
            if (--slot.Borrowers == 0 && slot.Retired) retired = slot.Frame;
        }
        ReleaseFrame(retired);
    }

    // Called under gate. A currently borrowed wrap stays alive until the final Draw borrower returns.
    private PortraitNativeLiveFrame? RetireCurrent()
    {
        var slot = current;
        current = null;
        if (slot is null) return null;
        slot.Retired = true;
        return slot.Borrowers == 0 ? slot.Frame : null;
    }

    private void ReleaseFrame(PortraitNativeLiveFrame? frame)
    {
        try { frame?.Dispose(); }
        catch (Exception) { Interlocked.Increment(ref disposalFailures); }
    }

    public void Dispose()
    {
        PortraitNativeLiveFrame? retired;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            enabled = false;
            revision++;
            retired = RetireCurrent();
        }
        source.Invalidated -= Invalidate;
        ReleaseFrame(retired);
    }

    internal sealed class Slot(PortraitNativeLiveFrame frame, PortraitNativeLiveImage image)
    {
        public PortraitNativeLiveFrame Frame { get; } = frame;
        public PortraitNativeLiveImage Image { get; } = image;
        public int Borrowers;
        public bool Retired;
    }
}

/// <summary>Use on one Draw invocation; submit AddImage before disposing this borrow.</summary>
internal sealed class PortraitNativeLiveBorrow(PortraitNativeLiveView owner, PortraitNativeLiveView.Slot slot) : IDisposable
{
    private PortraitNativeLiveView? owner = owner;
    internal bool IsReleased => Volatile.Read(ref owner) is null;

    public bool TryGetImage(out PortraitNativeLiveImage image)
    {
        image = default;
        return Volatile.Read(ref owner) is { } current && current.TryRead(slot, this, out image);
    }

    public void Dispose() => Interlocked.Exchange(ref owner, null)?.Return(slot);
}
