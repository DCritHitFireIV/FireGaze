using System.Reflection;
using dnlib.DotNet;
using FireGaze.Diagnostics;
using FireGaze.Internal;
using FireGaze.RepoAudit;
using FireGaze.Lifecycle;

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
///       ③ 仅由匹配操作、实例和文件字节的成功加载回执确认；时间和已加载标记不能代替回执；
///       ④ 插件更新（DLL 被换掉）→ 按原文匹配自动重打（默认开）。
/// </remarks>
internal sealed partial class UITextPatchManager
{
    private readonly HashSet<string> reloading = new(StringComparer.OrdinalIgnoreCase);
    private const int RepatchCheckSeconds = 30;
    private const int TickSeconds = 5;

    private readonly Plugin plugin;
    private readonly UITextPatchStore store;
    private readonly UITextStore packs;

    private readonly DateTime processStartedAt = DateTime.Now;
    private DateTime lastTick = DateTime.MinValue;
    private DateTime lastRepatchCheck = DateTime.MinValue;
    private InstalledPluginsIndex? index;
    private bool migrationStarted;

    /// <summary>认领失败的缓存：同一份 DLL（按写入时间）不重复算哈希——清单陈旧时列表每 5 秒刷一次，大 DLL 哈希很贵。</summary>
    private readonly Dictionary<string, DateTime> adoptRejectedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly object adoptGate = new();

    /// <summary>文件哈希缓存（按写入时间 + 长度失效）：编辑器每帧 / 列表每 5 秒问状态，不能每次都全量 SHA-256（2026-10-03 B-01/B-03）。</summary>
    private readonly Dictionary<string, (long Length, DateTime WriteUtc, string Hash)> hashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>写盘（打补丁 / 还原）的互斥：后台自动重打与界面发起的操作可能撞在一起，备份清理必须串行（2026-10-03 B-07）。</summary>
    private readonly object applyGate = new();

    /// <summary>后台补丁巡检是否在跑（Tick 每 5 秒只派一次）。</summary>
    private volatile bool tickWorkActive;

    /// <summary>已装插件索引的重建锁（后台巡检与界面可能同时问）。</summary>
    private readonly object indexGate = new();

    public UITextPatchManager(Plugin plugin)
    {
        this.plugin = plugin;
        this.store = new UITextPatchStore(plugin.ConfigDirectory, UITextDataRoot.Originals(plugin.DurableDataDirectory));
        this.packs = plugin.TextPacks;
    }

    /// <summary>
    ///     读补丁状态；配置被重置（记录没了）时再从持久目录认领一次——
    ///     只有「盘上文件哈希 == 清单里的 patchedHash」才认（插件更新过就认不回来，绝不会把旧原文盖回去）。
    /// </summary>
    private UITextPatchState? LoadStateOrAdopt(InstalledPluginEntry entry) =>
        this.WithFileOwner(() => this.LoadStateOrAdoptCore(entry), (UITextPatchState?)null);

    private UITextPatchState? LoadStateOrAdoptCore(InstalledPluginEntry entry)
    {
        lock (this.applyGate)
        {
            if (this.store.HasPendingOperation(entry.InternalName))
                return this.store.ReadPendingOperation(entry.InternalName, entry.DLLPath ?? string.Empty)
                    ?? new UITextPatchState { InternalName = entry.InternalName, DLLPath = entry.DLLPath ?? string.Empty,
                        WritePhase = "InvalidPending", LastError = "恢复操作记录无法校验，请保留原件并检查记录。" };
            var state = this.store.LoadCached(entry.InternalName);
            if (state is not null)
            {
                return state;
            }

            // 清单被否过一次就别每 5 秒重算（大 DLL 哈希很贵）；DLL 更新过（写入时间变了）再重新试
            DateTime writeTime;
            try
            {
                writeTime = string.IsNullOrEmpty(entry.DLLPath) ? DateTime.MinValue : File.GetLastWriteTime(entry.DLLPath);
            }
            catch (Exception)
            {
                writeTime = DateTime.MinValue;
            }

            lock (this.adoptGate)
            {
                if (this.adoptRejectedAt.TryGetValue(entry.InternalName, out var rejectedAt) && rejectedAt == writeTime)
                {
                    return null;
                }
            }

            var adopted = this.store.TryAdopt(entry.InternalName, entry.DLLPath ?? string.Empty, entry.Version);
            if (adopted is not null)
            {
                Plugin.Log?.Information($"[内部文本] {entry.InternalName}：补丁记录丢了，已从持久备份目录认领（{adopted.EffectiveFiles.Count} 个文件）");
                return adopted;
            }

            lock (this.adoptGate)
            {
                this.adoptRejectedAt[entry.InternalName] = writeTime;
            }

            return null;
        }
    }

    /// <summary>
    ///     补丁状态（给界面显示用）。
    /// </summary>
    public UITextPatchStatus StatusOf(InstalledPluginEntry entry, out string detail)
    {
        detail = string.Empty;
        var state = this.LoadStateOrAdopt(entry);
        if (state is null)
        {
            return UITextPatchStatus.NotPatched;
        }

        if (!state.IsWriteComplete)
        {
            detail = state.WritePhase == "Restored" ? "原件已还原，等待清理记录。" : "文件操作未完成，请先还原。";
            return UITextPatchStatus.Failed;
        }

        if (state.LastError is { Length: > 0 } error)
        {
            detail = error;
            return UITextPatchStatus.Failed;
        }

        if (!UITextFileIdentity.TryMap(state, entry.DLLPath ?? string.Empty, out var mapped, out detail))
            return UITextPatchStatus.Failed;
        if (!UITextFileIdentity.MatchesUnmodifiedMain(state, entry.DLLPath!, mapped))
        {
            detail = "主程序集已变化，需要核对当前版本的本地化文件。";
            return UITextPatchStatus.NeedsRepatch;
        }
        if (state.PendingVerify)
        {
            detail = $"已打补丁 {state.AppliedEntries} 处，等待重载/确认";
            return UITextPatchStatus.PendingReload;
        }

        // 主程序集 + 伴生程序集逐个看：任意一个变了都算要重打
        foreach (var (path, file) in mapped)
        {
            if (!File.Exists(path))
            {
                detail = "补丁记录里的文件不在了（插件更新过？）";
                return UITextPatchStatus.NeedsRepatch;
            }

            var hash = this.CachedHash(path);
            if (hash.Length == 0)
            {
                detail = "算不出文件哈希";
                return UITextPatchStatus.NeedsRepatch;
            }

            if (file.PatchedHash is { Length: > 0 } && string.Equals(hash, file.PatchedHash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(hash, file.SourceHash, StringComparison.OrdinalIgnoreCase))
            {
                detail = "盘上是原始文件（补丁被还原/被更新顶掉了）";
                return UITextPatchStatus.NeedsRepatch;
            }

            detail = "插件更新过，补丁需要重打";
            return UITextPatchStatus.NeedsRepatch;
        }

        detail = $"已打补丁 {state.AppliedEntries} 处（{state.PatchedAt}）";
        return UITextPatchStatus.Applied;
    }

    /// <summary>
    ///     上次打补丁的时间（没有就 null）。用来判断「译文包是不是在那之后又改过」。
    /// </summary>
    public DateTime? PatchedAt(InstalledPluginEntry entry)
    {
        var state = this.LoadStateOrAdopt(entry);
        if (state?.PatchedAt is not { Length: > 0 } text || !DateTime.TryParse(text, out var at))
        {
            return null;
        }

        return at;
    }

    /// <summary>
    ///     译文包比已应用的补丁更新（云端下载 / 编辑校对改过译文之后）——列表据此把按钮从「打开」换回「一键汉化」，
    ///     并提示「有改动待写入」；编辑器据此提醒还没写入插件（盲评 CF-01 / CF-07）。
    /// </summary>
    public bool PackNewerThanPatch(InstalledPluginEntry entry)
    {
        var patchedAt = this.PatchedAt(entry);
        if (patchedAt is null)
        {
            return false;
        }

        try
        {
            var packTime = this.packs.LastWriteTime(entry.InternalName);
            return packTime > patchedAt.Value.AddSeconds(1);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     这个插件有没有可还原的备份。
    /// </summary>
    public bool HasBackup(InstalledPluginEntry entry) => this.LoadStateOrAdopt(entry)?.HasBackup == true;

    public int FunctionalRepairCount(InstalledPluginEntry entry)
    {
        lock (this.applyGate)
        {
            var state = this.LoadStateOrAdopt(entry);
            if (state is null) return 0;
            if (!state.IsWriteComplete) throw new IOException("文件操作尚未结束，请先还原。");
            if (!UITextFileIdentity.TryMap(state, entry.DLLPath ?? string.Empty, out var mapped, out var error))
                throw new IOException(error);
            var files = UITextRules.ResolveCompanions(entry.DLLPath, entry.InternalName);
            var count = 0;
            foreach (var path in files)
            {
                if (!mapped.TryGetValue(Path.GetFullPath(path), out var previous) || !previous.HasBackup
                    || string.IsNullOrEmpty(previous.PatchedHash)
                    || !UITextPatchStore.HashOf(path).Equals(previous.PatchedHash, StringComparison.OrdinalIgnoreCase)) continue;
                count += UITextMaintenance.FunctionalRepairs(previous.BackupPath!, path, previous.SourceHash, previous.PatchedHash!);
            }
            return count;
        }
    }

    public string MaintenanceSourceSignature(InstalledPluginEntry entry)
    {
        lock (this.applyGate)
            return UITextMaintenance.FileSignature(this.ExtractionSourceOf(entry, out _, out _));
    }

    /// <summary>Checks can acquire a verified original baseline, but never rewrite installed plugin files.</summary>
    public Task<(UITextExtraction Extraction, string Note)> ExtractWithRecoveryAsync(InstalledPluginEntry entry, CancellationToken cancel = default) =>
        this.WithWorkOwnerAsync(async lease =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, lease.ShutdownToken);
            return await this.ExtractWithRecoveryCoreAsync(entry, linked.Token).ConfigureAwait(false);
        }, (new UITextExtraction { Error = ClosingMessage }, string.Empty));

    private async Task<(UITextExtraction Extraction, string Note)> ExtractWithRecoveryCoreAsync(InstalledPluginEntry entry, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        string expectedState;
        string expectedFiles;
        List<string> files;
        lock (this.applyGate)
        {
        cancel.ThrowIfCancellationRequested();
        var pack = this.packs.Load(entry.InternalName);
        var state = this.LoadStateOrAdopt(entry);
        files = UITextRules.ResolveCompanions(entry.DLLPath, entry.InternalName);
        if (state is { IsWriteComplete: false })
            return (new UITextExtraction { Error = "文件操作尚未结束，请先还原。" }, string.Empty);
        var trustedBaseline = UITextMaintenance.HasVerifiedBaseline(files, state, entry.DLLPath);
        var invalidBackup = InvalidRecordedBaseline(files, state, entry.DLLPath) is not null;
        var guard = invalidBackup ? (new UITextExtraction { Error = "需要恢复原始包" }, string.Empty)
            : this.ExtractWithGuard(entry, allowRecovery: false);
        if (UITextRules.IsDoNotLocalize(entry.InternalName)) return guard;
        if (!invalidBackup && guard.Item1.Error is null && (trustedBaseline || !UITextMaintenance.SuspectFunctionalTranslations(pack, guard.Item1)))
        {
            try { _ = this.FunctionalRepairCount(entry); return guard; }
            catch (IOException) { /* Reacquire originals when an old record cannot be trusted. */ }
        }
        if (string.IsNullOrEmpty(entry.DLLPath) || !File.Exists(entry.DLLPath)) return guard;
        Plugin.Log?.Information("[内部文本] {Name}：正在自动补齐原始包并核对构建", entry.InternalName);
        expectedState = System.Text.Json.JsonSerializer.Serialize(state);
        expectedFiles = UITextMaintenance.FileSignature(files);
        }
        var recovery = new UITextOriginalRecovery(Path.Combine(this.plugin.DurableDataDirectory, "uit-recovery"),
            gate: this.applyGate, admission: () => !cancel.IsCancellationRequested
                && !this.reloading.Contains(entry.InternalName)
                && !this.store.HasPendingOperation(entry.InternalName)
                && System.Text.Json.JsonSerializer.Serialize(this.LoadStateOrAdopt(entry)) == expectedState
                && UITextMaintenance.FileSignature(files) == expectedFiles);
        await recovery.RecoverAsync(entry, this.store, cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        var (recovered, _) = this.ExtractWithGuard(entry, allowRecovery: false);
        if (recovered.Error is not null)
            throw new UITextRecoveryPendingException();
        _ = this.FunctionalRepairCount(entry);
        return (recovered, "已自动补齐对应版本的原始包，安全修复已准备好");
    }

    // A recorded patch has an exact original hash. All entry points must reject
    // a missing or changed original before reading it or replacing installed
    // bytes; translation-count heuristics cannot substitute for this check.
    private static string? InvalidRecordedBaseline(IReadOnlyList<string> paths, UITextPatchState? state, string? currentMainPath = null)
    {
        if (state is null) return null;
        if (!state.IsWriteComplete) return "文件操作尚未结束，请先还原。";
        if (!UITextFileIdentity.TryMap(state, currentMainPath ?? paths.FirstOrDefault() ?? string.Empty, out var mapped, out var error))
            return error;
        foreach (var path in paths)
        {
            var currentHash = UITextPatchStore.HashOf(path);
            if (currentHash.Length == 0) continue;
            if (mapped.TryGetValue(Path.GetFullPath(path), out var file)
                && string.Equals(currentHash, file.PatchedHash, StringComparison.OrdinalIgnoreCase))
            {
                if (!file.HasBackup || string.IsNullOrEmpty(file.SourceHash)
                    || !string.Equals(UITextPatchStore.HashOf(file.BackupPath!), file.SourceHash, StringComparison.OrdinalIgnoreCase))
                    return "原始备份已经变化或丢失，需要重新核对原始包。";
            }
        }
        return null;
    }

    /// <summary>
    ///     重新抽取该对哪几份 DLL：主程序集 + 伴生程序集；其中「盘上是我们自己的补丁」的改读它的原始备份。
    ///     否则抽到的是 <c>译文###原文</c> 或中文资源值，会把包里好好的条目当成「原文没了」整批清掉。
    ///     <paramref name="note" /> 非空时是一句给人看的说明。
    /// </summary>
    public List<string> ExtractionSourceOf(InstalledPluginEntry entry, out string note, out List<string> searchDirectories)
    {
        note = string.Empty;
        searchDirectories = [];
        var dllPath = entry.DLLPath ?? string.Empty;

        // 原插件目录要进 resolver 搜索路径：抽取源可能是 uitrans/backups 里的备份，
        // 跨程序集的特性参数类型（如 Basic 里的枚举）得去插件目录找（2026-10-02 ARSR 实测）。
        var pluginDirectory = string.IsNullOrEmpty(dllPath) ? null : Path.GetDirectoryName(dllPath);
        if (!string.IsNullOrEmpty(pluginDirectory) && Directory.Exists(pluginDirectory))
        {
            searchDirectories.Add(pluginDirectory);
        }

        var state = this.LoadStateOrAdopt(entry);
        if (state is { IsWriteComplete: false }) throw new IOException("文件操作尚未结束，请先还原。");
        var paths = UITextRules.ResolveCompanions(dllPath, entry.InternalName);
        if (paths.Count == 0)
        {
            if (dllPath.Length > 0)
            {
                paths.Add(dllPath);
            }

            return paths;
        }

        if (state is null || !state.HasBackup)
        {
            return paths;
        }

        if (!UITextFileIdentity.TryMap(state, dllPath, out var byName, out var error))
            throw new IOException(error);
        var result = new List<string>(paths.Count);
        var swapped = false;
        foreach (var path in paths)
        {
            if (File.Exists(path)
                && byName.TryGetValue(Path.GetFullPath(path), out var file)
                && file.PatchedHash is { Length: > 0 }
                && string.Equals(UITextPatchStore.HashOf(path), file.PatchedHash, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(file.SourceHash)
                && string.Equals(UITextPatchStore.HashOf(file.BackupPath!), file.SourceHash, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(file.BackupPath!);
                swapped = true;
                continue;
            }

            result.Add(path);
        }

        if (swapped)
        {
            note = "盘上是打过补丁的 DLL，这次从原始备份抽取";
        }

        return result;
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
    /// <remarks>
    ///     未完成操作保留完整恢复凭据；成功后只清理本次新建且无持久记录引用的原件。
    /// </remarks>
    public (bool Ok, string Message) Apply(InstalledPluginEntry entry, bool allowRecovery = true, bool safetyRepair = false)
        => this.WithFileOwner(() => this.ApplyOwned(entry, allowRecovery, safetyRepair), (false, ClosingMessage));

    private (bool Ok, string Message) ApplyOwned(InstalledPluginEntry entry, bool allowRecovery, bool safetyRepair)
    {
        lock (this.applyGate)
        {
            if (this.reloading.Contains(entry.InternalName)) return (false, "插件正在重载，稍后再应用汉化。");
            var originalsBefore = this.store.SnapshotOriginals();
            var (ok, message) = this.ApplyCore(entry, allowRecovery, out var statePersisted, safetyRepair);

            // Failed or incomplete plans keep their complete recovery evidence.
            // Successful cleanup consults config, pending and durable manifests.
            if (ok && statePersisted && !this.store.HasPendingOperation(entry.InternalName))
            {
                this.DeleteUnreferencedOriginals(originalsBefore);
            }
            else
            {
                Plugin.Log?.Warning($"[内部文本] {entry.InternalName}：操作未确认完整成功，保留本次恢复凭据");
            }

            this.ClearHashCache();
            return (ok, message);
        }
    }

    /// <summary>打补丁的主体（失败清理包在外面，见 <see cref="Apply" />）。<paramref name="statePersisted" /> = 补丁状态是否写盘成功。</summary>
    private (bool Ok, string Message) ApplyCore(InstalledPluginEntry entry, bool allowRecovery, out bool statePersisted, bool safetyRepair = false)
    {
        statePersisted = false;
        if (UITextRules.IsDoNotLocalize(entry.InternalName))
        {
            return (false, "识别为中文插件。");
        }

        var dllPath = entry.DLLPath;
        if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
        {
            return (false, "读不到插件主程序集路径。");
        }

        var pluginDirectory = Path.GetDirectoryName(dllPath) ?? string.Empty;

        var existing = this.LoadStateOrAdopt(entry);
        if (existing is { IsWriteComplete: false }) return (false, "上次文件操作尚未结束，请先还原。");
        if (safetyRepair)
        {
            try { _ = this.FunctionalRepairCount(entry); }
            catch (Exception e) { return (false, e.GetBaseException().Message); }
        }
        var files = UITextRules.ResolveCompanions(dllPath, entry.InternalName);
        if (files.Count == 0)
        {
            files.Add(dllPath);
        }

        if (InvalidRecordedBaseline(files, existing, entry.DLLPath) is { } baselineError)
            return (false, baselineError);

        var pack = this.packs.Load(entry.InternalName);
        if (pack.Entries.Count == 0 && pack.Resources.Count == 0 && pack.Attributes.Count == 0)
        {
            return (false, "这个插件还没有本地译文包。");
        }

        // Literal inverse recovery cannot restore changed/inserted metadata.
        // The async consumer acquires the exact original package; direct calls
        // must stop before creating a backup from an already translated DLL.
        if (!UITextMaintenance.HasVerifiedBaseline(files, existing, entry.DLLPath) && MayContainAttributePatch(files, pack))
        {
            return (false, "需要自动补齐原始插件包后再应用汉化。");
        }

        // Pure-text patches have no ### marker. A surviving untranslated
        // label must not let an old patched DLL become a fresh SourceHash.
        if (!UITextMaintenance.HasVerifiedBaseline(files, existing, entry.DLLPath))
        {
            var current = UIStringExtractor.ExtractMany(files, [pluginDirectory]);
            if (current.Error is not null) return (false, current.Error);
            if (LooksLikePatchedDLL(current, entry.InternalName))
                return (false, "需要自动补齐原始插件包后再应用汉化。");
        }


        return this.ApplyPrepared(entry, pack, files, existing, pluginDirectory, safetyRepair, out statePersisted);
    }

    /// <summary>
    ///     多程序集插件的「真缺失」= 每个文件都没见到的条目（逐文件 Missing 的**交集**）。
    ///     <para>
    ///         不能把各文件的 Missing 直接拼起来：同一个字符串不会同时出现在每个 DLL 里，
    ///         拼接会把「只属于伴生程序集的条目」也算成主程序集缺（2026-10-02 ARSR 实测虚报 2211 条，
    ///         修复后应回落到几百条以内）。
    ///     </para>
    /// </summary>
    public static List<string> IntersectMissing(IReadOnlyList<IReadOnlyCollection<string>> perFileMissing)
    {
        if (perFileMissing.Count == 0)
        {
            return [];
        }

        var result = new List<string>(perFileMissing[0]);
        for (var i = 1; i < perFileMissing.Count && result.Count > 0; i++)
        {
            var here = new HashSet<string>(perFileMissing[i], StringComparer.Ordinal);
            result.RemoveAll(item => !here.Contains(item));
        }

        return result;
    }

    /// <summary>
    ///     抽取 + 「从补丁后 DLL 抽取」的兜底体检。返回（结果, 说明）。
    /// </summary>
    /// <remarks>
    ///     抽出来的**字面量原文**里若有一批「译文###原文」（且译文能在当前包里找到——说明是我们翻的），
    ///     盘上很可能已经是打过的补丁：这种结果绝不能进包（假原文会把真原文挤成「不翻」、
    ///     打补丁又匹配不上；2026-10-02 AbilityAnts 实测）。
    ///     体检不过时改用原始备份重抽；没有备份或仍不过就返回带 Error 的结果（流程停下，不写包）。
    /// </remarks>
    public (UITextExtraction Extraction, string Note) ExtractWithGuard(InstalledPluginEntry entry, bool allowRecovery = true)
    {
        lock (this.applyGate)
        {
            if (UITextRules.IsDoNotLocalize(entry.InternalName))
            {
                return (new UITextExtraction
                {
                    Error = "识别为中文插件。",
                }, string.Empty);
            }

            var files = UITextRules.ResolveCompanions(entry.DLLPath, entry.InternalName);
            var state = this.LoadStateOrAdopt(entry);
            if (InvalidRecordedBaseline(files, state, entry.DLLPath) is { } baselineError)
                return (new UITextExtraction { Error = baselineError }, string.Empty);

            var sources = this.ExtractionSourceOf(entry, out var note, out var searchDirectories);
            var extraction = UIStringExtractor.ExtractMany(sources, searchDirectories);
            var verifiedBaseline = UITextMaintenance.HasVerifiedBaseline(files, state, entry.DLLPath);
            if (extraction.Error is null && !verifiedBaseline
                && MayContainAttributePatch(sources, this.packs.Load(entry.InternalName)))
            {
                // ExtractWithRecoveryAsync already handles this result by acquiring
                // the matching original package. Never return translated attributes
                // as original source text or adopt a partial literal-only inverse.
                return (new UITextExtraction { Error = "需要自动补齐原始插件包" }, note);
            }
            if (extraction.Error is not null
                || verifiedBaseline
                || !LooksLikePatchedDLL(extraction, entry.InternalName))
            {
                return (extraction, note);
            }

            // The asynchronous consumer can acquire the matching publisher package.
            // Translation matches alone never establish a replacement original.
            return (new UITextExtraction
            {
                AssemblyPath = extraction.AssemblyPath,
                AnalysisDiagnostics = extraction.AnalysisDiagnostics,
                Error = "需要自动补齐原始插件包",
            }, note);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉无所谓
        }
    }

    /// <summary>Detect evidence of a previous patch when attribute translations require an original baseline.</summary>
    private static bool MayContainAttributePatch(IReadOnlyList<string> paths, UITextPack pack)
    {
        // Include skipped entries: a newer pack may have disabled a translation
        // that was already applied by an older pack.
        var translations = pack.Attributes.Where(attribute => attribute.HasTranslation)
            .Select(attribute => attribute.Translated.Trim()).ToHashSet(StringComparer.Ordinal);
        if (translations.Count == 0) return false;

        // Attribute translations can change between packs. Existing literal
        // patch evidence still requires the original; a new matching literal
        // must not let Apply adopt the old translated metadata as its baseline.
        if (MayContainOurPatch(paths, pack)) return true;

        bool Matches(CAArgument argument) => argument.Value switch
        {
            UTF8String text => translations.Contains(text.String),
            string text => translations.Contains(text),
            IList<CAArgument> items => items.Any(Matches),
            _ => false,
        };
        bool MatchesHost(IHasCustomAttribute host)
        {
            foreach (var attribute in host.CustomAttributes)
            {
                // Old versions admitted unknown short names, resource lookup
                // keys and arbitrary arrays. Recovery must recognize those
                // translations even when the current write contract rejects them.
                foreach (var argument in attribute.ConstructorArguments)
                    if (Matches(argument)) return true;
                foreach (var named in attribute.NamedArguments)
                    if (Matches(named.Argument)) return true;
            }
            return false;
        }

        try
        {
            foreach (var path in paths)
            {
                using var module = UIStringExtractor.LoadModule(path, paths.Select(Path.GetDirectoryName)
                    .Where(directory => !string.IsNullOrEmpty(directory)).Select(directory => directory!).Distinct().ToArray());
                foreach (var type in module.GetTypes())
                    if (MatchesHost(type) || type.Methods.Any(MatchesHost)
                        || type.Fields.Any(MatchesHost) || type.Properties.Any(MatchesHost)) return true;
            }
        }
        catch (Exception)
        {
            // An unreadable metadata source cannot authorize using itself as its
            // own original. The existing package recovery reports the failure.
            return true;
        }
        return false;
    }

    /// <summary>
    ///     便宜的预检：盘上的文件里有没有我们打过的补丁痕迹（只扫字节，不动 dnlib）。
    ///     两个条件都要满足：① 至少 3 条**译文**命中；② 至少 1 条 <c>###原文</c> 命中。
    /// </summary>
    /// <remarks>
    ///     只看译文会在「自带中文界面」的插件上误触发（它的原生中文文本可能正好等于我们的某条译文，
    ///     2026-10-02 BOCCHI 实测：干净 DLL 上也有 8 条「可反向还原」）——所以要搭一条 <c>###原文</c>，
    ///     这只是可疑补丁的线索，不能据此反向生成原版；恢复须取得经核对的原始备份或发行包。
    /// </remarks>
    internal static bool MayContainOurPatch(IReadOnlyList<string> paths, UITextPack pack)
    {
        var translationProbes = new List<byte[]>();
        var suffixProbes = new List<byte[]>();
        foreach (var entry in pack.Entries)
        {
            if (!entry.HasTranslation || pack.IsSkipped(entry.Original))
            {
                continue;
            }

            var original = entry.Original.Trim();
            var translated = entry.Translated.Trim();
            if (original.Length == 0
                || translated.Length == 0
                || string.Equals(original, translated, StringComparison.Ordinal))
            {
                continue;
            }

            if (translationProbes.Count < 64)
            {
                translationProbes.Add(System.Text.Encoding.Unicode.GetBytes(translated));
            }

            if (suffixProbes.Count < 64)
            {
                suffixProbes.Add(System.Text.Encoding.Unicode.GetBytes(UITextText.IDSeparator + original));
            }

            if (translationProbes.Count >= 64 && suffixProbes.Count >= 64)
            {
                break;
            }
        }

        if (translationProbes.Count == 0 || suffixProbes.Count == 0)
        {
            return false;
        }

        var translationHits = 0;
        var suffixHit = false;
        foreach (var path in paths)
        {
            try
            {
                var span = File.ReadAllBytes(path).AsSpan();
                foreach (var probe in translationProbes)
                {
                    if (span.IndexOf(probe) >= 0)
                    {
                        translationHits++;
                    }
                }

                if (!suffixHit)
                {
                    foreach (var probe in suffixProbes)
                    {
                        if (span.IndexOf(probe) >= 0)
                        {
                            suffixHit = true;
                            break;
                        }
                    }
                }

                if (translationHits >= 3 && suffixHit)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 读不动当没命中（后面还会按正常流程报错）
            }
        }

        return false;
    }

    /// <summary>
    ///     抽出来的 UI 候选里是否成批出现**我们打过的补丁**形态。
    /// </summary>
    /// <remarks>
    ///     不能只看「中文###」——有些插件原生就用中文标签 + ###ID（那样会误报，2026-10-02 踩过）。
    ///     真判据：### 前的显示段能在**当前包**里找到对应的译文（说明这段是我们翻的）。
    /// </remarks>
    private bool LooksLikePatchedDLL(UITextExtraction extraction, string internalName)
    {
        var pack = this.packs.Load(internalName);
        if (pack is null || pack.Entries.Count == 0)
        {
            return false; // 没有包，不可能是「我们打的补丁」
        }

        var translations = new HashSet<string>(StringComparer.Ordinal);
        var originals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in pack.Entries)
        {
            originals.Add(item.Original.Trim());
            if (item.HasTranslation)
            {
                translations.Add(item.Translated.Trim());
            }
        }

        if (translations.Count == 0)
        {
            return false;
        }

        // 普通补丁（PreserveID=false，写进去的是纯译文、没有 ###）也要能认出来：
        // 字面量正好等于某条唯一译文、而对应的原文已不在 DLL 里 → 打过的痕迹
        // （2026-10-02 用户实测：重装/重置后丢了记录，纯译文补丁就看不出来了）。
        var literals = new HashSet<string>(extraction.Entries.Select(e => e.Original), StringComparer.Ordinal);
        var uniqueTranslations = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in pack.Entries)
        {
            if (!item.HasTranslation)
            {
                continue;
            }

            var translated = item.Translated.Trim();
            if (uniqueTranslations.TryGetValue(translated, out var existing))
            {
                if (!string.Equals(existing, item.Original, StringComparison.Ordinal))
                {
                    conflicts.Add(translated);
                }
            }
            else
            {
                uniqueTranslations[translated] = item.Original;
            }
        }

        var patched = 0;
        foreach (var item in extraction.Entries)
        {
            // 不再只看 UI：中文判定会把「译文###原文」当成「已是中文」排掉，
            // 那种条目恰恰是最硬的补丁证据（2026-10-02 修）。
            var literal = item.Original;
            var mark = literal.IndexOf(UITextText.IDSeparator, StringComparison.Ordinal);
            if (mark <= 0)
            {
                var trimmed = literal.Trim();
                if (uniqueTranslations.TryGetValue(trimmed, out var source)
                    && !conflicts.Contains(trimmed)
                    && !literals.Contains(source))
                {
                    patched++;
                }

                continue;
            }

            var prefix = item.Original[..mark].Trim();
            if (translations.Contains(prefix))
            {
                patched++;
                continue;
            }

            // 译文改过 / 包换过时再来一道保险：看「### 之后」是不是包里的原文。
            // 覆盖 T###原文 与  译文##ID###原文（历史上双层拼接过的形态），不靠文案前缀猜。
            var matched = false;
            var index = mark;
            while (index >= 0 && !matched)
            {
                var suffix = item.Original[(index + UITextText.IDSeparator.Length)..].Trim();
                matched = originals.Contains(suffix);
                index = item.Original.IndexOf(UITextText.IDSeparator, index + UITextText.IDSeparator.Length, StringComparison.Ordinal);
            }

            if (matched)
            {
                patched++;
            }
        }

        return patched >= 3;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 临时文件删不掉无所谓
        }
    }

    /// <summary>Clean new unused durable originals only when every owning record is readable.</summary>
    private void DeleteUnreferencedOriginals(HashSet<string> before)
    {
        this.store.CleanupUnreferencedOriginals(before);
    }

    /// <summary>
    ///     一次性迁移：把以前留在配置目录里的原始备份搬进持久目录（重置插件配置后不再丢）。
    ///     后台跑；每个状态写回前再读一遍，发现被动过就跳过（避免盖掉正在进行的补丁）。
    /// </summary>
    private void MigrateLegacyBackups() => this.WithFileOwner(this.MigrateLegacyBackupsCore);

    private void MigrateLegacyBackupsCore()
    {
        lock (this.applyGate)
        {

            try
            {
                var moved = 0;
                foreach (var state in this.store.ListAll())
                {
                    if (!state.IsWriteComplete || this.store.HasPendingOperation(state.InternalName) || state.PendingVerify || state.LastError is { Length: > 0 })
                    {
                        continue;
                    }

                    var files = new List<UITextPatchFile>();
                    var pendingDeletes = new List<string>();
                    var updated = false;
                    foreach (var file in state.EffectiveFiles)
                    {
                        if (!file.HasBackup
                            || this.store.IsDurableBackup(file.BackupPath)
                            || string.IsNullOrEmpty(file.Path))
                        {
                            files.Add(file);
                            continue;
                        }

                        var durable = this.store.MoveBackupToOriginals(state.InternalName, file.Path, file.BackupPath!, file.SourceHash);
                        if (durable is null)
                        {
                            files.Add(file);
                            continue;
                        }

                        files.Add(file with { BackupPath = durable });
                        pendingDeletes.Add(file.BackupPath!);
                        updated = true;
                    }

                    if (!updated)
                    {
                        continue;
                    }

                    // 写回前再读一遍：状态被动过（比如补丁任务刚好在跑）就放弃这一条
                    var current = this.store.Load(state.InternalName);
                    if (current is null
                        || !string.Equals(current.PatchedHash, state.PatchedHash, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(current.BackupPath, state.BackupPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var migrated = current with
                    {
                        Files = files,
                        BackupPath = files.FirstOrDefault(file => string.Equals(file.Path, current.DLLPath, StringComparison.OrdinalIgnoreCase))?.BackupPath,
                    };
                    if (!this.store.Save(migrated)) continue;
                    if (!this.store.PublishManifest(state.InternalName, state.PluginVersion, state.PatchedAt,
                            state.AppliedEntries, files, Path.GetDirectoryName(state.DLLPath) ?? string.Empty,
                            state.DLLPath, state.OperationID, state.PatchedHash ?? state.SourceHash)) continue;

                    // 状态已经指向持久目录了，配置目录里的旧副本才能删
                    foreach (var path in pendingDeletes)
                    {
                        TryDelete(path);
                    }

                    moved += files.Count(f => this.store.IsDurableBackup(f.BackupPath));
                }

                if (moved > 0)
                {
                    Plugin.Log?.Information($"[内部文本] 已把 {moved} 份原始备份搬进持久目录（重置插件配置不再丢）");
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.Warning(e, "[内部文本] 迁移原始备份失败");
            }
        }
    }

    /// <summary>
    ///     还原成原始 DLL（与打补丁互斥：后台自动重打可能同时改同一个插件）。
    /// </summary>
    public (bool Ok, string Message) Restore(InstalledPluginEntry entry, string? reason = null)
        => this.WithFileOwner(() => this.RestoreOwned(entry, reason), (false, ClosingMessage));

    private (bool Ok, string Message) RestoreOwned(InstalledPluginEntry entry, string? reason)
    {
        lock (this.applyGate)
        {
            if (this.reloading.Contains(entry.InternalName)) return (false, "插件正在重载，稍后再还原。");
            return this.RestoreCore(entry, reason);
        }
    }

    private (bool Ok, string Message) RestoreCore(InstalledPluginEntry entry, string? reason)
    {
        var state = this.LoadStateOrAdopt(entry);
        if (state is null) return (false, "这个插件没有打过补丁的记录。");
        if (!state.IsKnownWritePhase) return (false, "恢复操作状态未知，保留现有文件和恢复凭据。");
        (bool Ok, string Message) InvalidPlan(string message)
        {
            this.store.Save(state with { LastError = message });
            return (false, message);
        }
        if (!UITextFileIdentity.TryMap(state, entry.DLLPath ?? string.Empty, out var mapped, out var error))
            return InvalidPlan(error);
        if (!UITextFileIdentity.MatchesUnmodifiedMain(state, entry.DLLPath!, mapped))
            return InvalidPlan("主程序集已变化，未将旧本地化文件还原到新的插件版本。");
        var restoredTerminal = state.WritePhase == "Restored";
        foreach (var (target, file) in mapped)
        {
            var current = UITextPatchStore.HashOf(target);
            if (restoredTerminal)
            {
                if (!HashEquals(current, file.SourceHash)) return InvalidPlan("已还原记录与当前字节不符，未执行清理。");
                continue;
            }
            if (!Path.IsPathFullyQualified(file.BackupPath ?? string.Empty)
                || !HashEquals(UITextPatchStore.HashOf(file.BackupPath!), file.SourceHash)
                || (!HashEquals(current, file.SourceHash) && !HashEquals(current, file.PatchedHash)
                    && !(state.OperationID is not null && Guid.TryParse(state.OperationID, out _)
                        && HashEquals(current, file.BeforeHash))))
                return InvalidPlan("原始备份或插件文件已经变化，需要重新核对原始包后再还原。");
        }
        var normalized = state with { DLLPath = Path.GetFullPath(entry.DLLPath!),
            Files = mapped.Select(pair => pair.Value with { Path = pair.Key }).ToList() };
        if (!restoredTerminal)
        {
            // Restore can also stop between files. Its complete plan must
            // survive configuration loss before the first replacement occurs.
            var recoveryFiles = new List<UITextPatchFile>();
            foreach (var (target, file) in mapped)
            {
                var current = UITextPatchStore.HashOf(target);
                if (!HashEquals(current, file.SourceHash) && !HashEquals(current, file.PatchedHash)
                    && !(Guid.TryParse(state.OperationID, out _) && HashEquals(current, file.BeforeHash)))
                    return InvalidPlan("准备还原期间目标发生变化，尚未写入。");
                var backup = file.BackupPath;
                if (!this.store.IsDurableBackup(backup))
                    backup = this.store.MoveBackupToOriginals(entry.InternalName, target, backup!, file.SourceHash);
                if (backup is null || !HashEquals(UITextPatchStore.HashOf(backup), file.SourceHash))
                    return InvalidPlan("无法保存可校验的持久原件，尚未还原目标。");
                recoveryFiles.Add(file with { Path = target, BackupPath = backup,
                    BeforeHash = current, PatchedHash = file.PatchedHash ?? file.SourceHash });
            }
            normalized = normalized with { Files = recoveryFiles, WritePhase = "RecoveryRequired",
                OperationID = this.store.HasPendingOperation(entry.InternalName)
                    ? state.OperationID : Guid.NewGuid().ToString("N"), PendingVerify = false };
            if (!this.store.SavePendingOperation(normalized))
                return (false, "保存持久还原计划失败，未还原目标文件。");
            if (!this.store.Save(normalized))
                return (false, "保存还原状态失败，未还原目标文件；持久计划已保留。");
            foreach (var file in recoveryFiles)
            {
                var target = file.Path;
                try
                {
                    var current = UITextPatchStore.HashOf(target);
                    if (HashEquals(current, file.SourceHash)) continue;
                    if (!HashEquals(UITextPatchStore.HashOf(file.BackupPath!), file.SourceHash)
                        || (!HashEquals(current, file.PatchedHash)
                            && !(Guid.TryParse(normalized.OperationID, out _) && HashEquals(current, file.BeforeHash))))
                        throw new IOException("恢复期间目标或原件发生变化。");
                    AtomicFile.ReplaceFrom(file.BackupPath!, target);
                    if (!HashEquals(UITextPatchStore.HashOf(target), file.SourceHash))
                        throw new IOException("恢复后的文件哈希无法确认。");
                }
                catch (Exception failure)
                {
                    var failed = normalized with { LastError = "还原失败：" + failure.GetBaseException().Message };
                    if (this.store.HasPendingOperation(entry.InternalName)) this.store.SavePendingOperation(failed);
                    this.store.Save(failed);
                    return (false, failed.LastError!);
                }
            }
        }
        var restored = normalized with { WritePhase = "Restored", PendingVerify = false, LastError = null };
        if (this.store.HasPendingOperation(entry.InternalName) && !this.store.SavePendingOperation(restored))
            return InvalidPlan("原件已还原，持久恢复记录尚未更新，保留备份。");
        if (!this.store.Save(restored)) return (false, "原件已还原，完成记录保存失败，保留备份。");
        if (!this.store.RemoveOriginals(restored)) return (false, "原件已还原，备份清理尚未完成，可重试还原以继续清理。");
        if (!this.store.ClearPendingOperation(entry.InternalName)) return (false, "原件已还原，恢复记录尚未清理，保留备份。");
        this.store.Delete(entry.InternalName);
        if (this.store.Load(entry.InternalName) is not null) return (false, "原件已还原，状态清理失败，保留备份。");
        this.ClearHashCache();
        var suffix = reason is null ? string.Empty : $"（{reason}）";
        return (true, "已还原成原始文件" + suffix + "；重载插件后恢复原文。");
    }

    /// <summary>
    ///     写补丁 + 自动重载：「应用汉化」就调它——重载是可合并的中间步骤，不单独给用户看。
    /// </summary>
    public Task<(bool Ok, string Message)> ApplyAndReloadAsync(InstalledPluginEntry entry, bool safetyRepair = false) =>
        this.WithWorkOwnerAsync(_ => this.ApplyAndReloadCoreAsync(entry, safetyRepair), (false, ClosingMessage));

    private async Task<(bool Ok, string Message)> ApplyAndReloadCoreAsync(InstalledPluginEntry entry, bool safetyRepair)
    {
        try
        {
            var guard = await Task.Run(() => this.ExtractWithRecoveryAsync(entry)).ConfigureAwait(false);
            if (guard.Extraction.Error is { } error) return (false, error);
        }
        catch (UITextRecoveryPendingException e) { return (false, e.Message); }
        var (ok, message) = await Task.Run(() => this.Apply(entry, allowRecovery: !safetyRepair, safetyRepair: safetyRepair)).ConfigureAwait(false);
        if (!ok)
        {
            ActivityLog.Error("应用汉化", $"{entry.InternalName}：{message}");
            return (false, message);
        }

        ActivityLog.Info("应用汉化", $"{entry.InternalName}：{message}");

        // Apply 的提示里带着「重载插件后生效」；这里已经自动重载了，不要再说一遍
        message = message.Replace("；重载插件后生效。", string.Empty, StringComparison.Ordinal);

        if (!entry.IsLoaded)
        {
            return (true, message + "；插件当前未加载，下次加载自动生效。");
        }

        var (reloadOk, reloadMessage) = await this.ReloadAsync(entry).ConfigureAwait(false);
        Plugin.Log?.Information($"[内部文本] {entry.InternalName}：应用汉化 + 自动重载 {(reloadOk ? "成功" : "失败")}");
        return reloadOk
            ? (true, message + "；插件已重新加载，界面即刻生效。")
            : (false, message + "；但重载失败：" + reloadMessage);
    }

    /// <summary>
    ///     还原 + 自动重载（「撤回汉化」也一步到位）。插件没加载时只还原文件。
    /// </summary>
    public Task<(bool Ok, string Message)> RestoreAndReloadAsync(InstalledPluginEntry entry, string? reason = null) =>
        this.WithWorkOwnerAsync(_ => this.RestoreAndReloadCoreAsync(entry, reason), (false, ClosingMessage));

    private async Task<(bool Ok, string Message)> RestoreAndReloadCoreAsync(InstalledPluginEntry entry, string? reason)
    {
        var (ok, message) = await Task.Run(() => this.Restore(entry, reason)).ConfigureAwait(false);
        if (!ok)
        {
            ActivityLog.Error("还原原文", $"{entry.InternalName}：{message}");
            return (false, message);
        }

        ActivityLog.Info("还原原文", $"{entry.InternalName}：{message}");

        message = message.Replace("；重载插件后恢复原文。", string.Empty, StringComparison.Ordinal);
        if (!entry.IsLoaded)
        {
            return (true, message + "；插件当前未加载，下次加载自动恢复原文。");
        }

        var (reloadOk, reloadMessage) = await this.ReloadAsync(entry).ConfigureAwait(false);
        Plugin.Log?.Information($"[内部文本] {entry.InternalName}：还原 + 自动重载 {(reloadOk ? "成功" : "失败")}");
        return reloadOk
            ? (true, message + "；插件已重新加载。")
            : (false, message + "；但重载失败：" + reloadMessage);
    }

    /// <summary>
    ///     重载插件（反射调卫月的 <c>LocalPlugin.ReloadAsync</c>）。
    /// </summary>
    public Task<(bool Ok, string Message)> ReloadAsync(InstalledPluginEntry entry) =>
        this.WithWorkOwnerAsync(lease => this.ReloadCoreAsync(entry, lease), (false, ClosingMessage));

    private async Task<(bool Ok, string Message)> ReloadCoreAsync(InstalledPluginEntry entry, PluginWorkLease lease)
    {
        var method = entry.RawPlugin.GetType().GetMethod("ReloadAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (method is null) return (false, "卫月这个版本没找到 ReloadAsync，请到插件列表里手动重载。");
        if (!typeof(Task).IsAssignableFrom(method.ReturnType))
            return (false, "卫月这个版本的 ReloadAsync 没有返回 Task，未开始重载。");
        LoadReceipt receipt;
        Task? pending;
        lock (this.applyGate)
        {
            if (!lease.IsCurrent) return (false, ClosingMessage);
            var state = this.LoadStateOrAdopt(entry);
            if (state is { IsWriteComplete: false }) return (false, "文件操作尚未结束，未重载插件。");
            if (!this.reloading.Add(entry.InternalName)) return (false, "插件正在重载。");
            try
            {
                // Reject unknown bytes before mutating even the attempt count.
                _ = this.CaptureLoadReceipt(entry, state);
                if (state is not null)
                {
                    state = state with { ReloadAttempts = state.ReloadAttempts + 1 };
                    if (!this.store.Save(state)) throw new IOException("重载记录保存失败。");
                }
                receipt = this.CaptureLoadReceipt(entry, state);
                pending = method.Invoke(entry.RawPlugin, null) as Task
                    ?? throw new InvalidOperationException("ReloadAsync 返回了空 Task，未确认插件加载。");
            }
            catch (Exception error)
            {
                this.reloading.Remove(entry.InternalName);
                return (false, "重载失败：" + error.GetBaseException().Message);
            }
        }
        try
        {
            if (pending is not null) await pending.ConfigureAwait(false);
            lock (this.applyGate)
            {
                if (!lease.IsCurrent) return (false, ClosingMessage);
                var loaded = entry.RawPlugin.GetType().GetProperty("IsLoaded", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(entry.RawPlugin) as bool? ?? false;
                entry.IsLoaded = loaded;
                if (loaded)
                {
                    if (!this.ConfirmCompletedLoad(entry, receipt))
                        return (false, "插件已重载，但补丁加载回执未能保存，保留待确认状态。");
                    return (true, "插件已重载，补丁生效。");
                }
                // Same operation only; a late reload must not restore a newer patch.
                if (!this.MatchesLoadReceipt(entry, receipt))
                    return (false, "重载期间补丁身份已变化，未确认新的补丁。");
                var (restoredOk, restoredMessage) = this.WithFileOwner(
                    () => this.RestoreCore(entry, "重载后插件没有加载成功，已自动还原"),
                    (false, ClosingMessage));
                return (false, restoredOk ? restoredMessage : "插件重载失败，自动还原也失败：" + restoredMessage);
            }
        }
        catch (Exception error) { return (false, "重载失败：" + error.GetBaseException().Message); }
        finally { lock (this.applyGate) this.reloading.Remove(entry.InternalName); }
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

        // 一次性迁移旧备份（后台跑，别占渲染线程）：以前备份在配置目录里，重置一把就没了
        if (!this.migrationStarted && (DateTime.Now - this.processStartedAt).TotalSeconds > 20)
        {
            this.migrationStarted = true;
            _ = this.plugin.RunTextTask(this.MigrateLegacyBackups);
        }

        var checkRepatch = (DateTime.Now - this.lastRepatchCheck).TotalSeconds >= RepatchCheckSeconds;
        if (checkRepatch)
        {
            this.lastRepatchCheck = DateTime.Now;
        }

        // 状态复核与「更新后自动重打」都含读盘 / 全量哈希 / dnlib 读改写（自动重打甚至会写盘 + 重载）——
        // 一律放后台，绝不能占渲染线程（2026-10-03 B-02）。同一时刻只跑一轮。
        if (this.tickWorkActive)
        {
            return;
        }

        this.tickWorkActive = true;
        _ = this.plugin.RunTextTask(() =>
        {
            try
            {
                this.CheckPending();
                if (checkRepatch)
                {
                    this.RepatchUpdated();
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.Warning(e, "[内部文本] 后台补丁巡检失败");
            }
            finally
            {
                this.tickWorkActive = false;
            }
        });
    }

    /// <summary>
    ///     重试已有成功加载回执的持久化；正常退出或无错误不能创建加载证据。
    /// </summary>
    public void MarkAllVerified() => this.WithFileOwner(this.MarkAllVerifiedCore);

    private void MarkAllVerifiedCore()
    {
        lock (this.applyGate)
        {

            this.ConfirmRecordedLoads();
        }
    }

    /// <summary>
    ///     启动时只重试已记录的精确加载回执；旧待确认记录没有失败或成功证据。
    /// </summary>
    public void VerifyOnStartup() => this.WithFileOwner(this.VerifyOnStartupCore);

    private void VerifyOnStartupCore()
    {
        lock (this.applyGate)
        {

            // Committed + PendingVerify means awaiting a load receipt, including
            // legacy records. Elapsed time, clean exit and an installed/loaded
            // name cannot establish failure or authorize a restore. Keep those
            // records and their originals unchanged across sessions. Known load
            // failure is handled by ReloadCoreAsync against its exact receipt;
            // interrupted writes retain their existing durable recovery plan.
            this.ConfirmRecordedLoads();
        }
    }

    /// <summary>
    ///     单个插件：如果打过补丁但 DLL 被更新换掉了，自动重打。
    /// </summary>
    public void RepatchIfNeeded(InstalledPluginEntry entry) => this.WithFileOwner(() => this.RepatchIfNeededCore(entry));

    private void RepatchIfNeededCore(InstalledPluginEntry entry)
    {
        lock (this.applyGate)
        {

            var state = this.LoadStateOrAdopt(entry);
            if (state is null || !state.IsWriteComplete || state.PendingVerify || string.IsNullOrEmpty(entry.DLLPath) || !File.Exists(entry.DLLPath))
            {
                return;
            }

            if (!UITextFileIdentity.TryMap(state, entry.DLLPath, out var mapped, out _)
                || !UITextFileIdentity.MatchesUnmodifiedMain(state, entry.DLLPath, mapped)) return;

            // 主程序集 + 伴生程序集：任意一个「不是我们打过的那个内容」就要重打
            var needs = false;
            foreach (var (path, file) in mapped)
            {
                if (!File.Exists(path))
                {
                    needs = true;
                    break;
                }

                var hash = this.CachedHash(path);
                if (hash.Length == 0)
                {
                    continue;
                }

                if (file.PatchedHash is { Length: > 0 } && string.Equals(hash, file.PatchedHash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(hash, file.SourceHash, StringComparison.OrdinalIgnoreCase) && file.PatchedHash is null)
                {
                    continue; // 这个文件从没打上过
                }

                needs = true;
                break;
            }

            if (!needs || !this.plugin.Config.UITextAutoRepatch)
            {
                return;
            }

            var (ok, message) = this.Apply(entry);
            Plugin.Log?.Information($"[内部文本] {entry.InternalName} 更新后自动重打：{(ok ? message : "失败：" + message)}");
        }
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
            if (!state.IsWriteComplete || this.store.HasPendingOperation(state.InternalName) || state.PendingVerify)
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

    private void CheckPending() => this.WithFileOwner(this.CheckPendingCore);

    private void CheckPendingCore()
    {
        lock (this.applyGate)
        {

            this.ConfirmRecordedLoads();
        }
    }

    /// <summary>
    ///     重试该插件已记录的加载回执；仅有插件名称不能确认补丁。
    /// </summary>
    public void MarkVerified(string internalName) => this.WithFileOwner(() => this.MarkVerifiedCore(internalName));

    private void MarkVerifiedCore(string internalName)
    {
        lock (this.applyGate)
        {

            this.ConfirmRecordedLoad(internalName);
        }
    }

    private InstalledPluginEntry? FindEntry(string internalName) =>
        this.EnsureIndex()?.All.FirstOrDefault(e => string.Equals(e.InternalName, internalName, StringComparison.Ordinal));

    private InstalledPluginsIndex? EnsureIndex()
    {
        lock (this.indexGate)
        {
            if (this.index is null || (DateTime.Now - this.index.CapturedLocal).TotalSeconds > 30)
            {
                this.index = InstalledPluginsIndex.Build();
            }

            return this.index.Available ? this.index : null;
        }
    }

    /// <summary>按「写入时间 + 长度」缓存的 SHA-256：文件没变就不重算（编辑器每帧 / 列表每 5 秒都会问到）。</summary>
    private string CachedHash(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return string.Empty;
            }

            lock (this.hashCache)
            {
                if (this.hashCache.TryGetValue(path, out var cached)
                    && cached.Length == info.Length
                    && cached.WriteUtc == info.LastWriteTimeUtc)
                {
                    return cached.Hash;
                }
            }

            var hash = UITextPatchStore.HashOf(path);
            lock (this.hashCache)
            {
                this.hashCache[path] = (info.Length, info.LastWriteTimeUtc, hash);
            }

            return hash;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void ClearHashCache()
    {
        lock (this.hashCache)
        {
            this.hashCache.Clear();
        }
    }
}
