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
    private bool migrationStarted;

    /// <summary>认领失败的缓存：同一份 DLL（按写入时间）不重复算哈希——清单陈旧时列表每 5 秒刷一次，大 DLL 哈希很贵。</summary>
    private readonly Dictionary<string, DateTime> adoptRejectedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly object adoptGate = new();

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
    private UITextPatchState? LoadStateOrAdopt(InstalledPluginEntry entry)
    {
        var state = this.store.Load(entry.InternalName);
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

        // 主程序集 + 伴生程序集逐个看：任意一个变了都算要重打
        foreach (var file in state.EffectiveFiles)
        {
            if (string.IsNullOrEmpty(file.Path) || !File.Exists(file.Path))
            {
                detail = "补丁记录里的文件不在了（插件更新过？）";
                return UITextPatchStatus.NeedsRepatch;
            }

            var hash = UITextPatchStore.HashOf(file.Path);
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

        var paths = UITextRules.ResolveCompanions(dllPath, entry.InternalName);
        if (paths.Count == 0)
        {
            if (dllPath.Length > 0)
            {
                paths.Add(dllPath);
            }

            return paths;
        }

        var state = this.LoadStateOrAdopt(entry);
        if (state is null || !state.HasBackup)
        {
            return paths;
        }

        var byName = state.EffectiveFiles
            .Where(f => f.HasBackup)
            .ToDictionary(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(paths.Count);
        var swapped = false;
        foreach (var path in paths)
        {
            if (File.Exists(path)
                && byName.TryGetValue(Path.GetFileName(path), out var file)
                && file.PatchedHash is { Length: > 0 }
                && string.Equals(UITextPatchStore.HashOf(path), file.PatchedHash, StringComparison.OrdinalIgnoreCase))
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
    ///     失败时把这次新建的备份清掉——不然会留下一堆「当前文件的快照」（既不是原始件，又会被后来者当成备份），
    ///     2026-10-02 ActionTimelineReborn / DailyRoutines 实测就是这么被坑的。
    /// </remarks>
    public (bool Ok, string Message) Apply(InstalledPluginEntry entry, bool allowRecovery = true)
    {
        var backupsBefore = this.SnapshotBackupFiles();
        var originalsBefore = this.store.SnapshotOriginals();
        var (ok, message) = this.ApplyCore(entry, allowRecovery);

        // 清理本次新建、又没人引用的备份：失败时要清（不然留一堆「当前文件的快照」）；
        // 成功时也要清——被跳过的伴生程序集（一条都对不上）同样在备份阶段留了一份，
        // 但它不是补丁记录的一部分（2026-10-02 AutoHook.FishSolver 实测）。
        this.DeleteUnreferencedBackups(backupsBefore);
        this.DeleteUnreferencedOriginals(originalsBefore);

        return (ok, message);
    }

    /// <summary>打补丁的主体（失败清理包在外面，见 <see cref="Apply" />）。</summary>
    private (bool Ok, string Message) ApplyCore(InstalledPluginEntry entry, bool allowRecovery = true)
    {
        if (UITextRules.IsDoNotLocalize(entry.InternalName))
        {
            return (false, "识别为中文插件（由朋友维护），FireGaze 不汉化它。");
        }

        var dllPath = entry.DLLPath;
        if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
        {
            return (false, "读不到插件主程序集路径。");
        }

        var pluginDirectory = Path.GetDirectoryName(dllPath) ?? string.Empty;

        var existing = this.LoadStateOrAdopt(entry);
        var files = UITextRules.ResolveCompanions(dllPath, entry.InternalName);
        if (files.Count == 0)
        {
            files.Add(dllPath);
        }

        var pack = this.packs.Load(entry.InternalName);
        if (pack.Entries.Count == 0 && pack.Resources.Count == 0 && pack.Attributes.Count == 0)
        {
            return (false, "这个插件还没有本地译文包。");
        }

        // 记录丢了、或记录里的「原始备份」本身就带着我们的补丁痕迹（记录丢失时把打过的 DLL 当成了原文）：
        // 先把「原文」反向还原出来再备份 / 打补丁。不先做的话，会把已经打过的 DLL 当成基线（真原文就彻底没了），
        // 而新补丁只盖住没打过的另一半——变成「旧补丁 + 新补丁」的杂种 DLL（2026-10-02 AutoHook 实测）。
        var backupLooksPatched = false;
        if (existing is not null && pack.TranslatedCount > 0)
        {
            var backupPaths = existing.EffectiveFiles
                .Where(f => f.HasBackup)
                .Select(f => f.BackupPath!)
                .ToList();
            backupLooksPatched = backupPaths.Count > 0 && MayContainOurPatch(backupPaths, pack);
        }

        if (pack.TranslatedCount > 0
            && (existing is null || backupLooksPatched)
            && MayContainOurPatch(files, pack)
            && this.TryRebuildPatchRecord(entry, files, pack, existing?.EffectiveFiles, null, out _, out var preRecoveryTmp, out var preRecoveryNote))
        {
            TryDeleteDirectory(preRecoveryTmp);
            Plugin.Log?.Information($"[内部文本] {entry.InternalName}：盘上像是旧补丁但没（可用的）记录，{preRecoveryNote}；先还原再打");
            existing = this.LoadStateOrAdopt(entry);
        }

        // 基线判定：盘上是「我们自己的旧补丁」的文件先还原回原始内容，避免在译文上叠译文。
        // 按**文件名**匹配（插件更新会换版本目录）；哈希对得上才还原，绝不拿旧备份顶掉新版本。
        if (existing is not null)
        {
            var oldFiles = new Dictionary<string, UITextPatchFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var old in existing.EffectiveFiles)
            {
                if (old.HasBackup && old.PatchedHash is { Length: > 0 })
                {
                    oldFiles[Path.GetFileName(old.Path)] = old;
                }
            }

            foreach (var path in files)
            {
                if (!oldFiles.TryGetValue(Path.GetFileName(path), out var old))
                {
                    continue;
                }

                if (!string.Equals(UITextPatchStore.HashOf(path), old.PatchedHash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Copy(old.BackupPath!, path, overwrite: true);
                }
                catch (Exception e)
                {
                    return (false, $"还原旧补丁失败（{Path.GetFileName(path)}）：{e.Message}");
                }
            }
        }

        // 先备份全部要动的文件
        var fileStates = new List<UITextPatchFile>();
        foreach (var path in files)
        {
            var sourceHash = UITextPatchStore.HashOf(path);
            if (sourceHash.Length == 0)
            {
                return (false, $"算不出 {Path.GetFileName(path)} 的哈希，先不冒险。");
            }

            var backup = this.store.Backup(entry.InternalName, path, sourceHash);
            if (backup is null)
            {
                return (false, $"备份 {Path.GetFileName(path)} 失败，已中止打补丁。");
            }

            fileStates.Add(new UITextPatchFile { Path = path, SourceHash = sourceHash, BackupPath = backup });
        }

        // 全部先打进 .new，一个失败就整套放弃——不会留下「主程序集打了、伴生没打」的半套状态
        var staged = new List<(UITextPatchFile State, string NewPath)>();
        var patchedTotal = 0;
        var perFileMissing = new List<IReadOnlyCollection<string>>();
        foreach (var fileState in fileStates)
        {
            var isMain = ReferenceEquals(fileState, fileStates[0]);
            var newPath = fileState.Path + ".fguitext.new";
            UITextPatchOutcome outcome;
            try
            {
                outcome = UITextPatcher.Patch(
                    fileState.Path,
                    newPath,
                    pack,
                    entry.Version,
                    includeAmbiguous: this.plugin.Config.UITextTranslateGreyList);
            }
            catch (Exception e)
            {
                foreach (var (_, pending) in staged)
                {
                    TryDelete(pending);
                }

                return (false, $"打补丁出错（{Path.GetFileName(fileState.Path)}）：{e.Message}");
            }

            if (!outcome.Ok)
            {
                // 伴生程序集一条都对不上很正常：包里根本没有它的条目（2026-10-02 用户实测：
                // AutoHook.FishSolver / Browsingway.Common 全落在“一条都没对上”上）——这个文件跳过，
                // 不把整个插件判失败。只有主程序集对不上才是异常（盘上是旧补丁 / 版本全变了）。
                if (outcome.NoMatch && !isMain)
                {
                    TryDelete(newPath);
                    Plugin.Log?.Information($"[内部文本] {entry.InternalName}：{Path.GetFileName(fileState.Path)} 没有可写的译文（包未收录伴生程序集的条目），跳过这个文件");
                    continue;
                }

                foreach (var (_, pending) in staged)
                {
                    TryDelete(pending);
                }

                TryDelete(newPath);

                // 主程序集一条都没对上：盘上可能还是我们的旧补丁、而记录丢了（第一次点击时包里还没译文，
                // 抽取体检认不出「译文###原文」；用户看到的就是这条红字）。用现在的译文包反向还原出原文、
                // 补回记录，然后整套重来一次——重来还失败才把错误给用户（不用用户自己重装插件）。
                if (allowRecovery && outcome.NoMatch)
                {
                    var carry = existing?.EffectiveFiles;
                    if (this.TryRebuildPatchRecord(entry, files, pack, carry, null, out _, out var recoveryTmp, out var recoveryNote))
                    {
                        TryDeleteDirectory(recoveryTmp); // 重试用的是持久备份，还原出来的临时文件用不上了
                        Plugin.Log?.Information($"[内部文本] {entry.InternalName}：打补丁一条都没对上，{recoveryNote}；已自动重试");
                        var (retryOk, retryMessage) = this.ApplyCore(entry, allowRecovery: false);
                        return retryOk ? (true, recoveryNote + "；" + retryMessage) : (false, retryMessage);
                    }
                }

                return (false, $"{Path.GetFileName(fileState.Path)}：{outcome.Error}");
            }

            staged.Add((fileState, newPath));
            patchedTotal += outcome.PatchedTotal;
            perFileMissing.Add(outcome.Missing);
        }

        // 「没找到」取所有文件的交集：同一个字符串不会同时出现在每个 DLL 里，
        // 逐文件拼接会把「只属于伴生程序集的条目」也算成主程序集缺（2026-10-02 ARSR 实测虚报 2211 条）。
        var missing = IntersectMissing(perFileMissing);

        // 全部成功才替换（出错则把已替换的从备份还原）
        try
        {
            foreach (var (fileState, newPath) in staged)
            {
                File.Move(newPath, fileState.Path, overwrite: true);
                fileState.PatchedHash = UITextPatchStore.HashOf(fileState.Path);
            }
        }
        catch (Exception e)
        {
            foreach (var fileState in fileStates)
            {
                if (fileState.HasBackup)
                {
                    try
                    {
                        File.Copy(fileState.BackupPath!, fileState.Path, overwrite: true);
                    }
                    catch (Exception)
                    {
                        // 尽力而为，下一条继续
                    }
                }
            }

            return (false, "写入补丁失败，已尝试还原：" + e.Message);
        }

        // 真正写进去的文件才进补丁记录：被跳过的伴生程序集（一条都对不上）不记——
        // 记了会让「盘上是原始文件」被当成待重打（StatusOf 会一直催重打），持久清单也发不出去。
        var stateFiles = staged.Select(s => s.State).ToList();

        // 插件自带的本地化文件（JSON）：中文侧缺的键写回中文文件——独立于 DLL，备份/状态/还原走同一套。
        // 与 DLL 同一套基线语义：盘上是我们的补丁时从原始备份重新写一遍（改了译文再打一次才会真的更新），
        // 需要新基线时才备份。失败不影响 DLL 补丁（写不动不会弄坏插件），只在消息里说明。
        var localizationWritten = 0;
        var localizationNote = string.Empty;
        try
        {
            var previousFiles = existing?.EffectiveFiles
                .Select(f => new UITextLocalizationPreviousFile(f.Path, f.SourceHash, f.PatchedHash, f.BackupPath))
                .ToList();
            if (UITextLocalizationFiles.TryWrite(pluginDirectory, pack, previousFiles, out var fileWrites, out var fileError))
            {
                foreach (var write in fileWrites)
                {
                    string? backup;
                    if (write.NeedsBackup)
                    {
                        backup = this.store.Backup(entry.InternalName, write.Path, write.SourceHash);
                        if (backup is null)
                        {
                            localizationNote = $"备份 {Path.GetFileName(write.Path)} 失败，跳过这个文件";
                            continue;
                        }
                    }
                    else
                    {
                        // 盘上已经是我们的补丁：沿用当初那份原始备份，绝不能把补丁内容当新基线备一份
                        backup = write.ReuseBackup;
                        if (backup is null || !File.Exists(backup))
                        {
                            localizationNote = $"找不到 {Path.GetFileName(write.Path)} 的原始备份，跳过这个文件";
                            continue;
                        }
                    }

                    try
                    {
                        var temp = write.Path + ".fguitext.tmp";
                        File.WriteAllText(temp, write.Content, new System.Text.UTF8Encoding(false));
                        File.Move(temp, write.Path, overwrite: true);
                    }
                    catch (Exception e)
                    {
                        localizationNote = $"写 {Path.GetFileName(write.Path)} 失败：{e.Message}";
                        continue;
                    }

                    stateFiles.Add(new UITextPatchFile
                    {
                        Path = write.Path,
                        SourceHash = write.SourceHash,
                        PatchedHash = UITextPatchStore.HashOf(write.Path),
                        BackupPath = backup,
                    });
                    localizationWritten += write.Count;
                }

                if (localizationNote.Length == 0 && fileError is { Length: > 0 })
                {
                    localizationNote = fileError;
                }
            }
            else if (fileError is { Length: > 0 })
            {
                localizationNote = fileError;
            }
        }
        catch (Exception e)
        {
            localizationNote = e.Message;
            Plugin.Log?.Warning(e, "[内部文本] 写本地化文件失败");
        }

        // 本轮没重写、但盘上还是我们补丁的文件（多半是译文没变化的本地化文件）也要继续留在记录里：
        // 丢了它，还原 / 更新后重打 / 持久清单都会找不到这个文件。
        if (existing is not null)
        {
            var known = new HashSet<string>(stateFiles.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var previous in existing.EffectiveFiles)
            {
                if (string.IsNullOrEmpty(previous.Path)
                    || known.Contains(previous.Path)
                    || !File.Exists(previous.Path)
                    || !string.Equals(UITextPatchStore.HashOf(previous.Path), previous.PatchedHash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                stateFiles.Add(previous);
            }
        }

        var main = stateFiles[0];
        var newState = new UITextPatchState
        {
            InternalName = entry.InternalName,
            DLLPath = main.Path,
            PluginVersion = entry.Version,
            SourceHash = main.SourceHash,
            PatchedHash = main.PatchedHash,
            PatchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            BackupPath = main.BackupPath,
            AppliedEntries = patchedTotal,
            Files = stateFiles,
            PendingVerify = true,
            PendingSince = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        this.store.Save(newState);
        // 持久目录里落一份自证清单：补丁记录被重置时靠它 + 文件哈希认领回原始备份
        this.store.PublishManifest(entry.InternalName, entry.Version, newState.PatchedAt, patchedTotal, stateFiles, pluginDirectory);

        var message = $"已写入 {patchedTotal} 处译文";
        if (localizationWritten > 0)
        {
            message += $"，另补了本地化文件 {localizationWritten} 条";
        }

        if (localizationNote.Length > 0)
        {
            message += $"（本地化文件部分失败：{localizationNote}）";
        }
        var assembliesPatched = stateFiles.Count(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (assembliesPatched > 1)
        {
            message += $"（跨 {assembliesPatched} 个程序集）";
        }

        if (missing.Count > 0)
        {
            message += $"（{missing.Count} 条在 DLL 里没找到，可能是插件版本变了）";
        }

        message += "；重载插件后生效。";
        Plugin.Log?.Information($"[内部文本] {entry.InternalName}：{message}");
        return (true, message);
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
    public (UITextExtraction Extraction, string Note) ExtractWithGuard(InstalledPluginEntry entry)
    {
        if (UITextRules.IsDoNotLocalize(entry.InternalName))
        {
            return (new UITextExtraction
            {
                Error = "识别为中文插件（由朋友维护），FireGaze 不汉化它。",
            }, string.Empty);
        }

        var sources = this.ExtractionSourceOf(entry, out var note, out var searchDirectories);
        var extraction = UIStringExtractor.ExtractMany(sources, searchDirectories);
        if (extraction.Error is not null || !LooksLikePatchedDLL(extraction, entry.InternalName))
        {
            return (extraction, note);
        }

        var state = this.LoadStateOrAdopt(entry);
        var backups = new List<string>();
        if (state is not null)
        {
            foreach (var file in state.EffectiveFiles)
            {
                if (file.HasBackup)
                {
                    backups.Add(file.BackupPath!);
                }
            }
        }

        if (backups.Count > 0)
        {
            var retry = UIStringExtractor.ExtractMany(backups, searchDirectories);
            if (retry.Error is null && !LooksLikePatchedDLL(retry, entry.InternalName))
            {
                note = (note.Length > 0 ? note + "；" : string.Empty) + "盘上像是汉化补丁，已改从原始备份抽取";
                Plugin.Log?.Information($"[内部文本] {entry.InternalName}：抽取体检发现「译文###原文」形态，已改从原始备份抽取");
                return (retry, note);
            }
        }

        // 最后一条自救：拿**当前盘上**的文件（不是备份）反向还原出原文——
        // 没有记录时（或备份本身也是打过的 DLL：记录丢失时把补丁后的文件当成了原文存下来）这条路都能走。
        // 2026-10-02 AutoHook 实测：旧补丁 + 丢记录 → 备份里也是打了补丁的文件，
        // 只靠「改读备份」会一直卡在「找不到可还原的原始备份」，只能让用户重装。
        var diskPaths = UITextRules.ResolveCompanions(entry.DLLPath ?? string.Empty, entry.InternalName);
        if (diskPaths.Count == 0 && !string.IsNullOrEmpty(entry.DLLPath))
        {
            diskPaths.Add(entry.DLLPath);
        }

        var recovered = this.TryRecoverPatchedDLL(entry, diskPaths, searchDirectories, out var recoveryNote);
        if (recovered is not null)
        {
            note = (note.Length > 0 ? note + "；" : string.Empty) + recoveryNote;
            Plugin.Log?.Information($"[内部文本] {entry.InternalName}：{recoveryNote}");
            return (recovered, note);
        }

        Plugin.Log?.Warning($"[内部文本] {entry.InternalName}：抽取结果里有一批「译文###原文」形态的文本，反向还原也救不回来——已停下（避免污染译文包）");
        return (new UITextExtraction
        {
            AssemblyPath = extraction.AssemblyPath,
            Entries = extraction.Entries,
            Resources = extraction.Resources,
            Attributes = extraction.Attributes,
            Error = "这个 DLL 上已经有一批我们打过的汉化补丁，而且找不到可还原的原始备份（backups 目录为空或被清过）。先停下，避免把假原文灌进译文包。恢复办法：在插件安装器里把这个插件重新安装一次（回到原版），再点「一键汉化」——译文包与公共库里的译文都还在。",
        }, note);
    }

    /// <summary>
    ///     盘上是我们打的补丁、但没有任何补丁记录时的自救：
    ///     用译文包反向把补丁还原成原文（存成新备份 + 补一条状态），再从还原出的原文抽取。
    /// </summary>
    /// <remarks>
    ///     还原量太少（&lt; 3 条，或不到包里译文数的一半）就认为不可信，返回 null，让调用方走「停下」的提示。
    ///     宁可让用户重装插件，也不拿一份拼凑出来的「原文」去重打。
    /// </remarks>
    private UITextExtraction? TryRecoverPatchedDLL(
        InstalledPluginEntry entry,
        List<string> sources,
        List<string> searchDirectories,
        out string note)
    {
        note = string.Empty;
        var pack = this.packs.Load(entry.InternalName);
        if (pack is null || pack.TranslatedCount == 0)
        {
            return null;
        }

        if (!this.TryRebuildPatchRecord(entry, sources, pack, this.LoadStateOrAdopt(entry)?.EffectiveFiles, searchDirectories, out var recoveredPaths, out var tempDirectory, out note))
        {
            return null;
        }

        // 还原出来的原文在临时目录里，抽完才能删——TryRebuildPatchRecord 成功时把目录交出来由调用方清理
        // （2026-10-02 血教训：在它内部 finally 删掉，返回后抽取就报「Could not open file」）。
        try
        {
            return UIStringExtractor.ExtractMany(recoveredPaths, searchDirectories);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>
    ///     把「盘上是我们打的补丁、但记录丢了」的插件补回补丁记录：
    ///     用译文包反向还原出原文（存备份 + 写状态 + 落持久清单）。成功时 
    ///     <paramref name="recoveredPaths" /> 是还原出来的原文文件（临时目录，调用方用完即弃）。
    ///     <paramref name="carry" /> 是本轮不重新还原、但要继续留在记录里的其它文件（如本地化文件）。
    /// </summary>
    /// <remarks>
    ///     还原量太少（&lt; 3 条，或不到包里译文数的一半）就认为不可信，拒绝——
    ///     宁可让用户重装插件，也不拿一份拼凑出来的「原文」去重打。
    /// </remarks>
    private bool TryRebuildPatchRecord(
        InstalledPluginEntry entry,
        IReadOnlyList<string> sources,
        UITextPack pack,
        IReadOnlyList<UITextPatchFile>? carry,
        IReadOnlyList<string>? searchDirectories,
        out List<string> recoveredPaths,
        out string tempDirectory,
        out string note)
    {
        note = string.Empty;
        recoveredPaths = [];
        tempDirectory = string.Empty;
        if (pack.TranslatedCount == 0)
        {
            return false;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "firegaze-recover-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var fileStates = new List<UITextPatchFile>();
            var totalReverted = 0;
            foreach (var source in sources)
            {
                if (!File.Exists(source))
                {
                    continue;
                }

                var recovered = Path.Combine(tempDir, Path.GetFileName(source));
                var outcome = UITextPatcher.Revert(source, recovered, pack, searchDirectories);
                if (!outcome.Ok || outcome.PatchedTotal == 0)
                {
                    continue;
                }

                var recoveredHash = UITextPatchStore.HashOf(recovered);
                var currentHash = UITextPatchStore.HashOf(source);
                if (recoveredHash.Length == 0 || currentHash.Length == 0)
                {
                    continue;
                }

                var backup = this.store.Backup(entry.InternalName, recovered, recoveredHash);
                if (backup is null)
                {
                    continue;
                }

                recoveredPaths.Add(recovered);
                fileStates.Add(new UITextPatchFile
                {
                    Path = source,
                    SourceHash = recoveredHash,
                    PatchedHash = currentHash,
                    BackupPath = backup,
                });
                totalReverted += outcome.PatchedTotal;
            }

            if (fileStates.Count == 0 || totalReverted < 3 || totalReverted < pack.TranslatedCount / 2)
            {
                return false;
            }

            // 本轮不碰、但盘上还是我们补丁的其它文件（多半是本地化文件）继续留在记录里
            if (carry is not null)
            {
                var known = new HashSet<string>(fileStates.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var previous in carry)
                {
                    if (string.IsNullOrEmpty(previous.Path)
                        || known.Contains(previous.Path)
                        || !File.Exists(previous.Path)
                        || !string.Equals(UITextPatchStore.HashOf(previous.Path), previous.PatchedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    fileStates.Add(previous);
                }
            }

            var main = fileStates[0];
            var recoveredState = new UITextPatchState
            {
                InternalName = entry.InternalName,
                DLLPath = main.Path,
                PluginVersion = entry.Version,
                SourceHash = main.SourceHash,
                PatchedHash = main.PatchedHash,
                PatchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                BackupPath = main.BackupPath,
                AppliedEntries = totalReverted,
                Files = fileStates,
                PendingVerify = false,
            };
            this.store.Save(recoveredState);
            // 还原出来的基线也落一份清单：这根链路上配置再被重置一次也还能认领
            this.store.PublishManifest(entry.InternalName, entry.Version, recoveredState.PatchedAt, totalReverted, fileStates, Path.GetDirectoryName(main.Path) ?? string.Empty);

            note = $"盘上还留着我们打过的补丁但丢了记录，已从补丁反向还原出原文文件（{totalReverted} 处）并恢复了补丁记录";
            tempDirectory = tempDir; // 成功：交给调用方用完再删
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, $"[内部文本] {entry.InternalName}：反向还原失败");
            return false;
        }
        finally
        {
            if (tempDirectory.Length == 0)
            {
                TryDeleteDirectory(tempDir);
            }
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

    /// <summary>
    ///     便宜的预检：盘上的文件里有没有我们打过的补丁痕迹（只扫字节，不动 dnlib）。
    ///     从包里抽最多 64 条有译文的条目，找 <c>###原文</c>（保留 ID 的写法）或译文本身（纯译文写法）的 UTF-16 字节。
    /// </summary>
    /// <remarks>
    ///     命中不一定真是补丁（译文碰巧本来就是界面文本）——调用方拿到 true 后还要走
    ///     <see cref="TryRebuildPatchRecord" /> 的还原量阀值校验。宁可多跑一次反向还原，不能把打过补丁的 DLL 当原文。
    /// </remarks>
    internal static bool MayContainOurPatch(IReadOnlyList<string> paths, UITextPack pack)
    {
        var probes = new List<byte[]>();
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

            probes.Add(System.Text.Encoding.Unicode.GetBytes(UITextText.IDSeparator + original));
            probes.Add(System.Text.Encoding.Unicode.GetBytes(translated));
            if (probes.Count >= 128)
            {
                break;
            }
        }

        if (probes.Count == 0)
        {
            return false;
        }

        foreach (var path in paths)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var span = bytes.AsSpan();
                foreach (var probe in probes)
                {
                    if (span.IndexOf(probe) >= 0)
                    {
                        return true;
                    }
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

    /// <summary>备份目录当前的全部备份文件（给「失败后清理新备份」用）。</summary>
    private HashSet<string> SnapshotBackupFiles()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var directory = Path.Combine(this.plugin.ConfigDirectory, "uitrans", "backups");
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.GetFiles(directory, "*.dll"))
                {
                    result.Add(file);
                }
            }
        }
        catch (Exception)
        {
            // 列不出来就不清理（宁可留垃圾也不误删）
        }

        return result;
    }

    /// <summary>
    ///     删掉「这次新出现、又没有任何状态引用」的备份文件（打补丁失败后的退路清理）。
    ///     已被某个状态引用的（可能是唯一的原始件）绝不碰。
    /// </summary>
    private void DeleteUnreferencedBackups(HashSet<string> before)
    {
        try
        {
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var state in this.store.ListAll())
            {
                foreach (var file in state.EffectiveFiles)
                {
                    if (!string.IsNullOrEmpty(file.BackupPath))
                    {
                        referenced.Add(file.BackupPath);
                    }
                }
            }

            foreach (var path in this.SnapshotBackupFiles())
            {
                if (!before.Contains(path) && !referenced.Contains(path))
                {
                    TryDelete(path);
                }
            }
        }
        catch (Exception)
        {
            // 清理是尽力而为
        }
    }

    /// <summary>打补丁失败时清掉这次新建、又没有状态引用的持久目录备份（原始件绝不误删）。</summary>
    private void DeleteUnreferencedOriginals(HashSet<string> before)
    {
        try
        {
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var state in this.store.ListAll())
            {
                foreach (var file in state.EffectiveFiles)
                {
                    if (!string.IsNullOrEmpty(file.BackupPath))
                    {
                        referenced.Add(file.BackupPath);
                    }
                }
            }

            foreach (var path in this.store.SnapshotOriginals())
            {
                if (!before.Contains(path) && !referenced.Contains(path))
                {
                    TryDelete(path);
                }
            }
        }
        catch (Exception)
        {
            // 清理是尽力而为
        }
    }

    /// <summary>
    ///     一次性迁移：把以前留在配置目录里的原始备份搬进持久目录（重置插件配置后不再丢）。
    ///     后台跑；每个状态写回前再读一遍，发现被动过就跳过（避免盖掉正在进行的补丁）。
    /// </summary>
    private void MigrateLegacyBackups()
    {
        try
        {
            var moved = 0;
            foreach (var state in this.store.ListAll())
            {
                if (state.PendingVerify || state.LastError is { Length: > 0 })
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

                this.store.Save(current with
                {
                    Files = files,
                    BackupPath = files.Count > 0 ? files[0].BackupPath : current.BackupPath,
                });
                this.store.PublishManifest(state.InternalName, state.PluginVersion, state.PatchedAt, state.AppliedEntries, files, Path.GetDirectoryName(state.DLLPath) ?? string.Empty);

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

    /// <summary>
    ///     还原成原始 DLL。
    /// </summary>
    public (bool Ok, string Message) Restore(InstalledPluginEntry entry, string? reason = null)
    {
        var state = this.LoadStateOrAdopt(entry);
        if (state is null)
        {
            return (false, "这个插件没有打过补丁的记录。");
        }

        var restored = 0;
        foreach (var file in state.EffectiveFiles)
        {
            if (!file.HasBackup)
            {
                continue;
            }

            // 插件更新会换版本目录：记录的路径不在了、但名字还是主程序集的话，还原到当前主程序集路径
            var target = file.Path;
            if (!File.Exists(target)
                && !string.IsNullOrEmpty(entry.DLLPath)
                && string.Equals(Path.GetFileName(file.Path), Path.GetFileName(entry.DLLPath), StringComparison.OrdinalIgnoreCase))
            {
                target = entry.DLLPath;
            }

            if (!File.Exists(target))
            {
                continue;
            }

            try
            {
                File.Copy(file.BackupPath!, target, overwrite: true);
                restored++;
            }
            catch (Exception e)
            {
                this.store.Save(state with { LastError = "还原失败：" + e.Message });
                return (false, $"还原 {Path.GetFileName(target)} 失败：{e.Message}");
            }
        }

        if (restored == 0)
        {
            this.store.Save(state with { LastError = "找不到备份文件，无法自动还原" });
            return (false, "找不到备份文件（备份目录被清过？）——需要手动重装这个插件。");
        }

        this.store.Delete(entry.InternalName);
        this.store.RemoveOriginals(state);
        var suffix = reason is null ? string.Empty : $"（{reason}）";
        Plugin.Log?.Information($"[内部文本] {entry.InternalName}：已还原原始 DLL{suffix}（{restored} 个文件）");
        return (true, "已还原成原始文件" + suffix + "；重载插件后恢复英文。");
    }

    /// <summary>
    ///     写补丁 + 自动重载：「应用汉化」就调它——重载是可合并的中间步骤，不单独给用户看。
    /// </summary>
    public async Task<(bool Ok, string Message)> ApplyAndReloadAsync(InstalledPluginEntry entry)
    {
        var (ok, message) = await Task.Run(() => this.Apply(entry)).ConfigureAwait(false);
        if (!ok)
        {
            return (false, message);
        }

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
    public async Task<(bool Ok, string Message)> RestoreAndReloadAsync(InstalledPluginEntry entry, string? reason = null)
    {
        var (ok, message) = await Task.Run(() => this.Restore(entry, reason)).ConfigureAwait(false);
        if (!ok)
        {
            return (false, message);
        }

        message = message.Replace("；重载插件后恢复英文。", string.Empty, StringComparison.Ordinal);
        if (!entry.IsLoaded)
        {
            return (true, message + "；插件当前未加载，下次加载自动恢复英文。");
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
            var state = this.LoadStateOrAdopt(entry);
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

        // 一次性迁移旧备份（后台跑，别占渲染线程）：以前备份在配置目录里，重置一把就没了
        if (!this.migrationStarted && (DateTime.Now - this.processStartedAt).TotalSeconds > 20)
        {
            this.migrationStarted = true;
            _ = Task.Run(this.MigrateLegacyBackups);
        }

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
        var state = this.LoadStateOrAdopt(entry);
        if (state is null || state.PendingVerify || string.IsNullOrEmpty(entry.DLLPath) || !File.Exists(entry.DLLPath))
        {
            return;
        }

        // 主程序集 + 伴生程序集：任意一个「不是我们打过的那个内容」就要重打
        var needs = false;
        foreach (var file in state.EffectiveFiles)
        {
            if (string.IsNullOrEmpty(file.Path) || !File.Exists(file.Path))
            {
                needs = true;
                break;
            }

            var hash = UITextPatchStore.HashOf(file.Path);
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

            // 补丁是「插件未加载」时打上的（ReloadAttempts 没增过）：插件现在加载了，
            // 读的就是打好的补丁文件——已经生效，直接确认。不能干等「下次启动」：
            // 2026-10-02 用户实测（ActionTimelineReborn 先禁用后启用，打补丁时索引还没刷新到已加载）
            // 会卡在 PendingReload，行按钮永远不变「打开」。
            if (state.ReloadAttempts == 0 && entry.IsLoaded)
            {
                this.MarkVerified(state.InternalName);
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
