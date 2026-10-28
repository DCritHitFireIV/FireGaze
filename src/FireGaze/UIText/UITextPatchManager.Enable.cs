using FireGaze.RepoAudit;
using FireGaze.Lifecycle;

namespace FireGaze.UIText;

internal sealed partial class UITextPatchManager
{
    public Task<(bool Ok, string Message)> EnableAsync(InstalledPluginEntry entry,
        Func<object?, Task<(bool Ok, string Message)>>? enable = null) =>
        this.WithWorkOwnerAsync(lease => this.EnableCoreAsync(entry, enable, lease), (false, ClosingMessage));

    private async Task<(bool Ok, string Message)> EnableCoreAsync(InstalledPluginEntry entry,
        Func<object?, Task<(bool Ok, string Message)>>? enable, PluginWorkLease lease)
    {
        LoadReceipt receipt;
        Task<PluginEnableBridge.EnableReceipt> pending;
        lock (this.applyGate)
        {
            if (!lease.IsCurrent) return (false, ClosingMessage);
            var state = this.LoadStateOrAdopt(entry);
            if (state is { IsWriteComplete: false }) return (false, "文件操作尚未结束，未启用插件。");
            if (!this.reloading.Add(entry.InternalName)) return (false, "插件正在加载。");
            try
            {
                receipt = this.CaptureLoadReceipt(entry, state);
                pending = enable is null ? PluginEnableBridge.EnableWithReceiptAsync(entry.RawPlugin)
                    : AwaitControlledEnable(enable(entry.RawPlugin));
            }
            catch (Exception error)
            {
                this.reloading.Remove(entry.InternalName);
                return (false, "启用失败：" + error.GetBaseException().Message);
            }
        }
        try
        {
            var result = await pending.ConfigureAwait(false);
            lock (this.applyGate)
            {
                if (!lease.IsCurrent) return (false, ClosingMessage);
                if (!result.Ok) return (false, result.Message);
                entry.IsLoaded = true;
                if (!result.LoadedByCall)
                {
                    if (!this.MatchesLoadReceipt(entry, receipt))
                        return (false, "启用期间补丁身份已变化，未确认新的补丁。");
                    return (true, result.Message + " 磁盘补丁仍须重载后确认。");
                }
                // Record the completed load before trying to read persistence.
                // A temporary read lock is inconclusive, not a lost load event.
                if (!this.ConfirmCompletedLoad(entry, receipt))
                    return (false, "插件已启用，但补丁加载回执未能保存，保留待确认状态。");
                return (true, result.Message);
            }
        }
        catch (Exception error) { return (false, "启用失败：" + error.GetBaseException().Message); }
        finally { lock (this.applyGate) this.reloading.Remove(entry.InternalName); }
    }

    // The injected managed boundary represents an actual controlled load, as
    // used by offline consumers; production always uses the bridge receipt.
    private static async Task<PluginEnableBridge.EnableReceipt> AwaitControlledEnable(Task<(bool Ok, string Message)> pending)
    {
        var result = await pending.ConfigureAwait(false);
        return new(result.Ok, result.Message, result.Ok);
    }
}
