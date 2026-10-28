using FireGaze.Lifecycle;

namespace FireGaze.UIText;

internal sealed partial class UITextPatchManager
{
    private const string ClosingMessage = "插件正在关闭，未开始新的汉化操作。";
    // Only accessed while holding applyGate. Nested synchronous steps belong
    // to the already-admitted transaction, which must finish even during close.
    private int fileOwnerDepth;

    private T WithFileOwner<T>(Func<T> operation, T rejected)
    {
        if (Monitor.IsEntered(this.applyGate) && this.fileOwnerDepth > 0) return operation();
        if (!this.plugin.WorkLifetime.TryAcquire(out var lease, out _)) return rejected;
        using (lease)
        {
            var result = rejected;
            lease!.TryExecute(() =>
            {
                lock (this.applyGate)
                {
                    if (!lease.IsCurrent) return;
                    this.fileOwnerDepth++;
                    try { result = operation(); }
                    finally { this.fileOwnerDepth--; }
                }
            });
            return result;
        }
    }

    private void WithFileOwner(Action operation) => this.WithFileOwner(() =>
    {
        operation();
        return true;
    }, false);

    private async Task<T> WithWorkOwnerAsync<T>(Func<PluginWorkLease, Task<T>> operation, T rejected)
    {
        if (!this.plugin.WorkLifetime.TryAcquire(out var lease, out _)) return rejected;
        using (lease)
        {
            var result = rejected;
            await lease!.TryExecuteAsync(async current =>
            {
                result = await operation(current).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        }
    }
}
