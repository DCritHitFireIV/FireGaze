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
        if (Interlocked.Exchange(ref disposed, 1) == 0) PortraitCaptureCleanupException.DisposeAll(texture);
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
    private readonly PortraitCaptureRetirement physical = new();

    public PortraitNativeLiveView(IPortraitNativeLiveSource source)
    {
        this.source = source;
        source.Invalidated += Invalidate;
    }

    public int DisposalFailures => physical.CleanupFailures;
    internal bool IsPhysicallyIdle => physical.Pending == 0 && physical.CleanupFailures == 0;
    public Task DrainPhysicalAsync() => physical.Seal();

    /// <summary>
    /// One stable official SRV may remain visible while history makes an independent read-only
    /// copy of the same native source. This is not physical idle and must never admit native
    /// mutation, resource recreation or teardown. The sole pending lifetime must be this slot:
    /// an acquisition prefix, retired borrower, release or quarantine adds another lifetime.
    /// </summary>
    internal bool CanShareReadOnlyPhotoSource(PortraitIdentity editor, PortraitCaptureOwner owner)
    {
        lock (gate)
            return !disposed && enabled && current is { Retired: false } slot
                && slot.Image.Key.IsValid && slot.Image.Key.Editor == editor && slot.Image.Key.Owner == owner
                && physical.Pending == 1 && physical.CleanupFailures == 0;
    }

    public void SetEnabled(bool value)
    {
        Slot? retired = null;
        lock (gate)
        {
            if (disposed || enabled == value) return;
            enabled = value;
            revision++;
            if (!value) retired = RetireCurrent();
        }
        ReleaseSlot(retired);
    }

    public void Invalidate()
    {
        Slot? retired;
        lock (gate)
        {
            revision++;
            retired = RetireCurrent();
        }
        ReleaseSlot(retired);
    }

    public void Update() => Update(false, null);

    /// <summary>
    /// The owning controller has freshly checked the same editor and owner. A missing
    /// new frame may retain its last descriptor, but publication still requires the
    /// post-acquire context check before replacing that descriptor.
    /// </summary>
    internal bool UpdatePreservingCurrent(Func<PortraitNativeLiveImage, bool> canPublish)
        => Update(true, canPublish);

    private bool Update(bool preserveCurrentOnTransientMiss, Func<PortraitNativeLiveImage, bool>? canPublish)
    {
        long ticket;
        IDisposable? candidateLifetime;
        lock (gate)
        {
            if (disposed || !enabled || !physical.TryEnter(out candidateLifetime)) return false;
            ticket = revision;
        }
        PortraitNativeLiveFrame? candidate = null;
        Slot? retired = null;
        try
        {
            if (!source.IsFrameworkThread)
            {
                Invalidate();
                return false;
            }
            if (!source.TryAcquire(out candidate) || candidate is null)
            {
                if (!preserveCurrentOnTransientMiss || candidate is not null) Invalidate();
                return false;
            }
            if (!candidate.TryDescribe(0, out var image))
            {
                // A replacement can be observed while the native portrait render target is
                // still being rebuilt (zero dimensions, stale bounds, or a transient handle).
                // Keep the last verified frame for this same session; the caller will retry
                // after its ownership check. Initial acquisition still has no frame to retain.
                if (!preserveCurrentOnTransientMiss) Invalidate();
                return false;
            }
            if (canPublish is not null && !canPublish(image)) return false;
            lock (gate)
            {
                if (disposed || !enabled || revision != ticket || physical.CleanupFailures != 0) return false;
                // Never deduplicate on SRV: the same render target can contain a new animated frame.
                retired = RetireCurrent();
                current = new(candidate, image with { FrameNumber = ++frameNumber }, candidateLifetime);
                candidate = null;
                candidateLifetime = null;
                return true;
            }
        }
        catch (Exception error)
        {
            physical.ObserveFailure(error);
            Invalidate();
            return false;
        }
        finally
        {
            physical.Release(candidate);
            candidateLifetime?.Dispose();
            ReleaseSlot(retired);
        }
    }

    public bool TryBorrow(out PortraitNativeLiveBorrow? borrow)
    {
        lock (gate)
        {
            borrow = null;
            if (disposed || !enabled || current is null || current.Retired || physical.CleanupFailures != 0) return false;
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
        Slot? retired = null;
        lock (gate)
        {
            if (--slot.Borrowers == 0 && slot.Retired) retired = slot;
        }
        ReleaseSlot(retired);
    }

    // Called under gate. A currently borrowed wrap stays alive until the final Draw borrower returns.
    private Slot? RetireCurrent()
    {
        var slot = current;
        current = null;
        if (slot is null) return null;
        slot.Retired = true;
        return slot.Borrowers == 0 ? slot : null;
    }

    private void ReleaseSlot(Slot? slot)
    {
        if (slot is null) return;
        physical.Release(slot.Frame);
        slot.Lifetime.Dispose();
    }

    public void Dispose()
    {
        Slot? retired;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            enabled = false;
            revision++;
            retired = RetireCurrent();
        }
        source.Invalidated -= Invalidate;
        ReleaseSlot(retired);
    }

    internal sealed class Slot(PortraitNativeLiveFrame frame, PortraitNativeLiveImage image, IDisposable lifetime)
    {
        public PortraitNativeLiveFrame Frame { get; } = frame;
        public PortraitNativeLiveImage Image { get; } = image;
        public IDisposable Lifetime { get; } = lifetime;
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
