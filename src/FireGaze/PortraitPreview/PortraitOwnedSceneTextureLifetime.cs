using System.Runtime.CompilerServices;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Owns one public texture-provider operation and at most two copied frames. The factory's
/// synchronous prefix runs on its caller's native phase; its task must complete only after
/// all retained source inputs have been released. It must not use native scene pointers
/// asynchronously. Draw borrowers must keep their lease until their last use of the wrap.
/// This manages physical ownership, not a GPU fence or scene/slot admission evidence.
/// </summary>
internal sealed class PortraitOwnedSceneTextureLifetime<T> : IDisposable where T : class, IDisposable
{
    private readonly object sync = new();
    private readonly PortraitCaptureRetirement physical = new();
    private readonly HashSet<Frame> frames = [];
    private readonly ConditionalWeakTable<T, object> seenResults = new();
    private Frame? current;
    private bool copying, closing;
    private int borrowers;
    private long generation = 1;
    private string? lastCopyFailureType;
    private Task? drainTask;

    internal sealed class Frame(T texture, IDisposable lifetime, long generation)
    {
        public readonly T Texture = texture;
        public readonly IDisposable Lifetime = lifetime;
        public readonly long Generation = generation;
        public int Borrowers;
        public bool Published = true, Releasing;
    }

    public int InFlight { get { lock (sync) return copying ? 1 : 0; } }
    public int FrameCount { get { lock (sync) return frames.Count; } }
    public int Borrowers { get { lock (sync) return borrowers; } }
    public int CleanupFailures => physical.CleanupFailures;
    public string? LastCopyFailureType { get { lock (sync) return lastCopyFailureType; } }
    public long Generation { get { lock (sync) return closing ? 0 : generation; } }

    /// <summary>
    /// Invalidate before any owner/revision/slot/descriptor change. Existing frames remain
    /// explicitly borrowable as a static last frame; pending results can never publish them.
    /// This does not inspect native descriptors or grant their caller any native capability.
    /// </summary>
    public long InvalidateSource()
    {
        lock (sync)
        {
            if (closing) return 0;
            if (generation < long.MaxValue) return ++generation;
        }
        Dispose();
        return 0;
    }

    public bool TryCopy(long expectedGeneration, Func<Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        IDisposable lifetime;
        lock (sync)
        {
            // Releasing frames retain their physical slot through Dispose's synchronous prefix.
            if (closing || expectedGeneration != generation || expectedGeneration <= 0
                || copying || frames.Count >= 2 || !physical.TryEnter(out lifetime)) return false;
            copying = true;
        }
        // Do not Task.Run: callers acquire the retained source and initiate its public copy
        // synchronously before the native phase ends. Completion uses only managed ownership.
        _ = Copy(expectedGeneration, factory, lifetime);
        return true;
    }

    private async Task Copy(long expectedGeneration, Func<Task<T>> factory, IDisposable lifetime)
    {
        T? result = null;
        Frame? replaced = null;
        var transferred = false;
        try
        {
            result = await (factory() ?? throw new InvalidOperationException("Texture provider returned no task.")).ConfigureAwait(false);
            lock (sync)
            {
                if (result is not null && seenResults.TryGetValue(result, out _))
                {
                    // A second ownership claim on the same disposable cannot be released safely.
                    // Keep it quarantined instead of destroying an image still used by a draw.
                    physical.ObserveFailure(new PortraitCaptureCleanupException([result],
                        [new InvalidOperationException("Texture provider returned an already owned wrap.")]));
                    result = null;
                }
                if (result is not null) seenResults.Add(result, new object());
                if (!closing && generation == expectedGeneration && physical.CleanupFailures == 0 && result is not null)
                {
                    var frame = new Frame(result, lifetime, expectedGeneration);
                    frames.Add(frame);
                    replaced = current;
                    if (replaced is not null) replaced.Published = false;
                    current = frame;
                    result = null;
                    transferred = true;
                }
            }
        }
        catch (Exception failure)
        {
            lock (sync) lastCopyFailureType = failure.GetType().FullName;
            physical.ObserveFailure(failure);
        }
        finally
        {
            // Pending admission is retained while a late result or old image is disposed.
            // A failure is owned permanently by physical's quarantine and prevents drain.
            physical.Release(result);
            ReleaseIfUnused(replaced);
            lock (sync) copying = false;
            if (!transferred) lifetime.Dispose();
        }
    }

    public Borrow? TryBorrow(bool allowPreviousGeneration = false)
    {
        lock (sync)
        {
            if (closing || physical.CleanupFailures != 0 || current is not { } frame
                || !allowPreviousGeneration && frame.Generation != generation) return null;
            frame.Borrowers++;
            borrowers++;
            return new Borrow(this, frame, frame.Texture);
        }
    }

    public void Dispose()
    {
        Frame? released;
        lock (sync)
        {
            if (closing) return;
            closing = true;
            released = current;
            current = null;
            if (released is not null) released.Published = false;
        }
        ReleaseIfUnused(released);
    }

    public Task DrainPhysicalAsync()
    {
        Dispose();
        lock (sync) return drainTask ??= Drain();
    }

    private async Task Drain()
    {
        await physical.Seal().ConfigureAwait(false);
        // The returned task itself must own this scope, including quarantined resources.
        // A bare TCS.Task has no back-reference to its owner and can outlive a collected scope.
        GC.KeepAlive(this);
    }

    private void Return(Frame frame)
    {
        lock (sync) { frame.Borrowers--; borrowers--; }
        ReleaseIfUnused(frame);
    }

    private void ReleaseIfUnused(Frame? frame)
    {
        if (frame is null) return;
        lock (sync)
        {
            if (frame.Published || frame.Borrowers != 0 || frame.Releasing) return;
            frame.Releasing = true;
        }
        physical.Release(frame.Texture);
        lock (sync) frames.Remove(frame);
        frame.Lifetime.Dispose();
    }

    internal sealed class Borrow : IDisposable
    {
        private PortraitOwnedSceneTextureLifetime<T>? owner;
        private readonly Frame frame;
        private readonly T texture;
        internal Borrow(PortraitOwnedSceneTextureLifetime<T> owner, Frame frame, T texture)
        { this.owner = owner; this.frame = frame; this.texture = texture; }
        public T Texture => owner is not null ? texture : throw new ObjectDisposedException(nameof(Borrow));
        public long Generation => frame.Generation;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Return(frame);
    }
}
