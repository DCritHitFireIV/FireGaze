using System.Threading.Channels;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Receives immutable snapshots from Framework and performs disk writes on one worker in submission order.
/// The write delegate must not call Dispose. Disposal drains accepted work before returning.
/// </summary>
internal sealed class OwnedCacheWriter : IDisposable
{
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly Task worker;
    private int disposed;
    private int completedWrites;
    private int failedWrites;
    private int lastWriteResult;

    public OwnedCacheWriter(Func<string, bool> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        worker = Task.Run(async () =>
        {
            await foreach (var snapshot in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var success = false;
                try { success = write(snapshot); }
                catch (Exception) { /* One failed disk write must not strand later accepted snapshots. */ }
                if (!success) Interlocked.Increment(ref failedWrites);
                Volatile.Write(ref lastWriteResult, success ? 1 : 2);
                Interlocked.Increment(ref completedWrites);
            }
        });
    }

    public int CompletedWrites => Volatile.Read(ref completedWrites);
    public int FailedWrites => Volatile.Read(ref failedWrites);
    public bool? LastWriteSucceeded => Volatile.Read(ref lastWriteResult) switch { 1 => true, 2 => false, _ => null };

    /// <summary>Returns immediately; false means no work was accepted.</summary>
    public bool Queue(string snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot) || Volatile.Read(ref disposed) != 0) return false;
        return queue.Writer.TryWrite(snapshot);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) queue.Writer.TryComplete();
        worker.GetAwaiter().GetResult();
    }
}
