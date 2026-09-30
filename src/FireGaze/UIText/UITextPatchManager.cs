using System.Reflection;
using FireGaze.RepoAudit;

namespace FireGaze.UIText;

/// <summary>
///     补丁在盘上的状态。
/// </summary>
internal enum UITextPatchStatus
{
    /// <summary>没打过补丁。</summary>
    NotPatched,

    /// <summary>打过补丁，且盘上的 DLL 还是我们打过的那个。</summary>
    Applied,

    /// <summary>刚打完，等重载生效 / 等确认没把插件搞坏。</summary>
    PendingReload,

    /// <summary>插件更新过（DLL 被换掉了），补丁需要重打。</summary>
    NeedsRepatch,

    /// <summary>上次打补丁失败 / 已自动还原。</summary>
    Failed,
}

/// <summary>
///     打补丁 / 还原 / 重载 / 更新后重打 的调度：所有会碰插件目录的操作都从这里走。
/// </summary>
/// <remarks>
///     安全网（顺序即优先级）：
///       ① 打进 DLL 之前先备份原始文件，备份在配置目录里，插件目录只留一份最终 DLL；
///       ② 打完先写「待确认」标记，重载后插件加载失败 → 自动还原；
///       ③ 重载后 15 秒内还活着 → 标记取消；游戏如果在这之前崩了/被强杀，下次启动会自动还原；
///       ④ 插件更新（DLL 被换掉）→ 按原文匹配自动重打（默认开）。
/// </remarks>
internal sealed class UITextPatchManager
{
    private const int PendingVerifySeconds = 15;
    private const int CrashSuspectSeconds = 180;
    private const int RepatchCheckSeconds = 30;
    private const int TickSeconds = 5;

    private readonly Plugin plugin;
    private readonly UITextPatchStore store;
    private readonly UITextStore packs;

    private readonly DateTime processStartedAt = DateTime.Now;
    private DateTime lastTick = DateTime.MinValue;
    private DateTime lastRepatchCheck = DateTime.MinValue;
    private InstalledPluginsIndex? index;

    public UITextPatchManager(Plugin plugin)
    {
        this.plugin = plugin;
        this.store = new UITextPatchStore(plugin.ConfigDirectory);
        this.packs = plugin.TextPacks;
    }

    /// <summary>
    ///     补丁状态（给界面显示用）。
    /// </summary>
    public UITextPatchStatus StatusOf(InstalledPluginEntry entry, out string detail)
    {
        detail = string.Empty;
        var state = this.store.Load(entry.InternalName);
        if (state is null)
        {
            return UITextPatchStatus.NotPatched;
        }

        if (state.LastError is { Length: > 0 } error)
        {
            detail = error;
            return UITextPatchStatus.Failed;
        }

        if (state.PendingVerify)
        {
            detail = $"已打补丁 {state.AppliedEntries} 处，等待重载/确认";
            return UITextPatchStatus.PendingReload;
        }

        if (!string.IsNullOrEmpty(entry.DLLPath) && File.Exists(entry.DLLPath))
        {
            var hash = UITextPatchStore.HashOf(entry.DLLPath);
            if (hash.Length > 0
                && !string.Equals(hash, state.SourceHash, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(hash, state.PatchedHash, StringComparison.OrdinalIgnoreCase))
            {
                detail = "插件更新过，补丁需要重打";
                return UITextPatchStatus.NeedsRepatch;
            }

            if (state.PatchedHash is { Length: > 0 } && string.Equals(hash, state.SourceHash, StringComparison.OrdinalIgnoreCase))
            {
                detail = "盘上是原始文件（补丁被还原/被更新顶掉了）";
                return UITextPatchStatus.NeedsRepatch;
            }
        }

        detail = $"已打补丁 {state.AppliedEntries} 处（{state.PatchedAt}）";
        return UITextPatchStatus.Applied;
    }

    /// <summary>
    ///     这个插件有没有可还原的备份。
    /// </summary>
    public bool HasBackup(InstalledPluginEntry entry) => this.store.Load(entry.InternalName)?.HasBackup == true;

    /// <summary>
    ///     重新抽取该对哪份 DLL：盘上是我们自己的补丁时改读原始备份。
    ///     否则抽到的是 <c>译文###原文</c>，会把包里好好的条目当成「原文没了」整批清掉（2026-10-01 踩过）。
    ///     返回的 <paramref name="note" /> 非空时是一句给人看的说明。
    /// </summary>
    public string ExtractionSourceOf(InstalledPluginEntry entry, out string note)
    {
        note = string.Empty;
        var path = entry.DLLPath ?? string.Empty;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return path;
        }

        var state = this.store.Load(entry.InternalName);
        if (state is null || !state.HasBackup || state.PatchedHash is not { Length: > 0 })
        {
            return path;
        }

        var currentHash = UITextPatchStore.HashOf(path);
        var chosen = ChooseExtractionSource(path, state, currentHash, out note);
        return chosen;
    }

    /// <summary>
    ///     抽取源判定的纯逻辑（能离线测）：盘上是我们打过的补丁就返回备份，否则返回当前文件。
    /// </summary>
    public static string ChooseExtractionSource(string dllPath, UITextPatchState? state, string currentHash, out string note)
    {
        note = string.Empty;
        if (state is null || !state.HasBackup || state.PatchedHash is not { Length: > 0 })
        {
            return dllPath;
        }

        if (!string.Equals(currentHash, state.PatchedHash, StringComparison.OrdinalIgnoreCase))
        {
            // 盘上不是我们的补丁（插件更新过 / 已还原）——当前文件就是原始件
            return dllPath;
        }

        note = "盘上是打过补丁的 DLL，这次从原始备份抽取";
        return state.BackupPath!;
    }

    /// <summary>
    ///     把包里的译文打进插件 DLL（不自动重载）。
    /// </summary>
    public (bool Ok, string Message) Apply(InstalledPluginEntry entry)
    {
        var dllPath = entry.DLLPath;
        if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
        {
            return (false, "读不到插件主程序集路径。");
        }

        // 基线判定：盘上是「我们自己的旧补丁」就先还原回原始文件，避免在译文上叠译文；
        // 盘上是别的内容（插件更新 / 第三方改过）就以当前内容为新基线，绝不能拿旧备份去顶掉新版本。
        var existing = this.store.Load(entry.InternalName);
        var samePath = existing is not null && string.Equals(existing.DLLPath, dllPath, StringComparison.OrdinalIgnoreCase);
        if (samePath && existing!.HasBackup)
        {
            var currentHash = UITextPatchStore.HashOf(dllPath);
            if (existing.PatchedHash is { Length: > 0 } && string.Equals(currentHash, existing.PatchedHash, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Copy(existing.BackupPath!, dllPath, overwrite: true);
                }
                catch (Exception e)
                {
                    return (false, "还原旧补丁失败：" + e.Message);
                }
            }
        }

        var pack = this.packs.Load(entry.InternalName);
        if (pack.Entries.Count == 0)
        {
            return (false, "这个插件还没有本地译文包。");
        }

        var sourceHash = UITextPatchStore.HashOf(dllPath);
        if (sourceHash.Length == 0)
        {
            return (false, "算不出插件 DLL 的哈希，先不冒险。");
        }

        var backup = this.store.Backup(entry.InternalName, dllPath, sourceHash);
        if (backup is null)
        {
            return (false, "备份原始 DLL 失败，已中止打补丁。");
        }

        var outcome = UITextPatcher.Patch(dllPath, dllPath, pack, entry.Version);
        if (!outcome.Ok)
        {
            return (false, outcome.Error ?? "打补丁失败。");
        }

        this.store.Save(new UITextPatchState
        {
            InternalName = entry.InternalName,
            DLLPath = dllPath,
            PluginVersion = entry.Version,
            SourceHash = sourceHash,
            PatchedHash = UITextPatchStore.HashOf(dllPath),
            PatchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            BackupPath = backup,
            AppliedEntries = outcome.PatchedLiterals,
            PendingVerify = true,
            PendingSince = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        });

        var message = $"已写入 {outcome.PatchedLiterals} 处译文";
        if (outcome.Missing.Count > 0)
        {
            message += $"（{outcome.Missing.Count} 条在 DLL 里没找到，可能是插件版本变了）";
        }

        message += "；重载插件后生效。";
        Plugin.Log?.Information($"[内部文本] {entry.InternalName}：{message}");
        return (true, message);
    }

    /// <summary>
    ///     还原成原始 DLL。
    /// </summary>
    public (bool Ok, string Message) Restore(InstalledPluginEntry entry, string? reason = null)
    {
        var state = this.store.Load(entry.InternalName);
        if (state is null || string.IsNullOrEmpty(entry.DLLPath))
        {
            return (false, "这个插件没有打过补丁的记录。");
        }

        if (!state.HasBackup)
        {
            this.store.Save(state with { LastError = "找不到备份文件，无法自动还原" });
            return (false, "找不到备份文件（备份目录被清过？）——需要手动重装这个插件。");
        }

        try
        {
            File.Copy(state.BackupPath!, entry.DLLPath, overwrite: true);
        }
        catch (Exception e)
        {
            return (false, "还原失败：" + e.Message);
        }

        this.store.Delete(entry.InternalName);
        var suffix = reason is null ? string.Empty : $"（{reason}）";
        Plugin.Log?.Information($"[内部文本] {entry.InternalName}：已还原原始 DLL{suffix}");
        return (true, "已还原成原始文件" + suffix + "；重载插件后恢复英文。");
    }

    /// <summary>
    ///     重载插件（反射调卫月的 <c>LocalPlugin.ReloadAsync</c>）。
    /// </summary>
    public async Task<(bool Ok, string Message)> ReloadAsync(InstalledPluginEntry entry)
    {
        // 注意：不要拿 manifest 的 CanUnloadAsync 当闸门。那个标志只决定「Dispose 回不回主线程」，
        // 卫月自己的重载（LocalPlugin.ReloadAsync）和插件管理器里的禁用/启用都不看它——
        // 2026-10-01 就是把它当成「不能热重载」误拦过一次（用户手动开关明明有效）。
        var method = entry.RawPlugin.GetType().GetMethod(
            "ReloadAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        if (method is null)
        {
            return (false, "卫月这个版本没找到 ReloadAsync，请到插件列表里手动「禁用 → 启用」。");
        }

        try
        {
            var state = this.store.Load(entry.InternalName);
            if (state is not null)
            {
                this.store.Save(state with { ReloadAttempts = state.ReloadAttempts + 1 });
            }

            if (method.Invoke(entry.RawPlugin, null) is Task task)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            var inner = e.InnerException ?? e;
            return (false, "重载失败：" + inner.Message);
        }

        var loaded = entry.RawPlugin.GetType().GetProperty("IsLoaded", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry.RawPlugin) as bool? ?? false;
        if (loaded)
        {
            this.MarkVerified(entry.InternalName);
            return (true, "插件已重载，补丁生效。");
        }

        // 重载后没起来：多半是补丁把插件搞坏了 → 自动还原
        var (ok, message) = this.Restore(entry, "重载后插件没有加载成功，已自动还原");
        return (false, ok ? message : "插件重载失败，而且自动还原也失败了：" + message);
    }

    /// <summary>
    ///     周期检查：复核「待确认」的补丁、插件更新后自动重打。
    /// </summary>
    public void Tick()
    {
        // 每帧都会调到这里：先限流，别把状态文件当帧循环的 I/O
        if ((DateTime.Now - this.lastTick).TotalSeconds < TickSeconds)
        {
            return;
        }

        this.lastTick = DateTime.Now;
        this.CheckPending();

        if ((DateTime.Now - this.lastRepatchCheck).TotalSeconds < RepatchCheckSeconds)
        {
            return;
        }

        this.lastRepatchCheck = DateTime.Now;
        this.RepatchUpdated();
    }

    /// <summary>
    ///     干净退出时把「待确认」全部转正：游戏跑完了一整场还没事，补丁就是好的。
    /// </summary>
    public void MarkAllVerified()
    {
        foreach (var state in this.store.ListAll())
        {
            if (state.PendingVerify || state.LastError is { Length: > 0 })
            {
                this.store.Save(state with { PendingVerify = false, LastError = null });
            }
        }
    }

    /// <summary>
    ///     启动时复核：上次打补丁后游戏没跑满 15 秒就没了（崩了/被强杀），自动还原。
    /// </summary>
    public void VerifyOnStartup()
    {
        foreach (var state in this.store.ListAll())
        {
            if (!state.PendingVerify)
            {
                continue;
            }

            if (!DateTime.TryParse(state.PendingSince, out var since) || (DateTime.Now - since).TotalSeconds < CrashSuspectSeconds)
            {
                continue;
            }

            var entry = this.FindEntry(state.InternalName);
            if (entry is null)
            {
                this.store.Save(state with { PendingVerify = false });
                continue;
            }

            Plugin.Log?.Warning($"[内部文本] {state.InternalName}：上次打补丁后游戏很快退出，按崩溃处理，自动还原");
            this.Restore(entry, "上次打补丁后游戏异常退出");
        }
    }

    /// <summary>
    ///     单个插件：如果打过补丁但 DLL 被更新换掉了，自动重打。
    /// </summary>
    public void RepatchIfNeeded(InstalledPluginEntry entry)
    {
        var state = this.store.Load(entry.InternalName);
        if (state is null || state.PendingVerify || string.IsNullOrEmpty(entry.DLLPath) || !File.Exists(entry.DLLPath))
        {
            return;
        }

        var hash = UITextPatchStore.HashOf(entry.DLLPath);
        if (hash.Length == 0
            || string.Equals(hash, state.PatchedHash, StringComparison.OrdinalIgnoreCase)
            || string.Equals(hash, state.SourceHash, StringComparison.OrdinalIgnoreCase) && state.PatchedHash is null)
        {
            // 我们的补丁还在盘上 / 还从没打过：都不用重打
            return;
        }

        if (!this.plugin.Config.UITextAutoRepatch)
        {
            return;
        }

        var (ok, message) = this.Apply(entry);
        Plugin.Log?.Information($"[内部文本] {entry.InternalName} 更新后自动重打：{(ok ? message : "失败：" + message)}");
    }

    private void RepatchUpdated()
    {
        var items = this.store.ListAll();
        if (items.Count == 0)
        {
            return;
        }

        var current = this.EnsureIndex();
        if (current is null)
        {
            return;
        }

        foreach (var state in items)
        {
            if (state.PendingVerify)
            {
                continue;
            }

            var entry = current.All.FirstOrDefault(e => string.Equals(e.InternalName, state.InternalName, StringComparison.Ordinal));
            if (entry is not null)
            {
                this.RepatchIfNeeded(entry);
            }
        }
    }

    private void CheckPending()
    {
        var pending = this.store.ListAll().Where(s => s.PendingVerify).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var current = this.EnsureIndex();
        if (current is null)
        {
            return;
        }

        foreach (var state in pending)
        {
            var entry = current.All.FirstOrDefault(e => string.Equals(e.InternalName, state.InternalName, StringComparison.Ordinal));
            if (entry is null || !entry.IsLoaded)
            {
                continue;
            }

            if (!DateTime.TryParse(state.PendingSince, out var since) || (DateTime.Now - since).TotalSeconds < PendingVerifySeconds)
            {
                continue;
            }

            // 本会话刚打上、又还没重载过：盘上换了文件不等于「验证通过」——等重载（或下次启动）再说
            if (since > this.processStartedAt && state.ReloadAttempts == 0)
            {
                continue;
            }

            this.MarkVerified(state.InternalName);
        }
    }

    private void MarkVerified(string internalName)
    {
        var state = this.store.Load(internalName);
        if (state is null || !state.PendingVerify)
        {
            return;
        }

        this.store.Save(state with { PendingVerify = false });
    }

    private InstalledPluginEntry? FindEntry(string internalName) =>
        this.EnsureIndex()?.All.FirstOrDefault(e => string.Equals(e.InternalName, internalName, StringComparison.Ordinal));

    private InstalledPluginsIndex? EnsureIndex()
    {
        if (this.index is null || (DateTime.Now - this.index.CapturedLocal).TotalSeconds > 30)
        {
            this.index = InstalledPluginsIndex.Build();
        }

        return this.index.Available ? this.index : null;
    }
}
