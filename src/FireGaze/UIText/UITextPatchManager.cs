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
        var state = this.store.Load(entry.InternalName);
        if (state?.PatchedAt is not { Length: > 0 } text || !DateTime.TryParse(text, out var at))
        {
            return null;
        }

        return at;
    }

    /// <summary>
    ///     这个插件有没有可还原的备份。
    /// </summary>
    public bool HasBackup(InstalledPluginEntry entry) => this.store.Load(entry.InternalName)?.HasBackup == true;

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

        var state = this.store.Load(entry.InternalName);
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
    public (bool Ok, string Message) Apply(InstalledPluginEntry entry)
    {
        var backupsBefore = this.SnapshotBackupFiles();
        var (ok, message) = this.ApplyCore(entry);
        if (!ok)
        {
            this.DeleteUnreferencedBackups(backupsBefore);
        }

        return (ok, message);
    }

    /// <summary>打补丁的主体（失败清理包在外面，见 <see cref="Apply" />）。</summary>
    private (bool Ok, string Message) ApplyCore(InstalledPluginEntry entry)
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

        var existing = this.store.Load(entry.InternalName);
        var files = UITextRules.ResolveCompanions(dllPath, entry.InternalName);
        if (files.Count == 0)
        {
            files.Add(dllPath);
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

        var pack = this.packs.Load(entry.InternalName);
        if (pack.Entries.Count == 0 && pack.Resources.Count == 0 && pack.Attributes.Count == 0)
        {
            return (false, "这个插件还没有本地译文包。");
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
                foreach (var (_, pending) in staged)
                {
                    TryDelete(pending);
                }

                TryDelete(newPath);
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

        var main = fileStates[0];
        this.store.Save(new UITextPatchState
        {
            InternalName = entry.InternalName,
            DLLPath = main.Path,
            PluginVersion = entry.Version,
            SourceHash = main.SourceHash,
            PatchedHash = main.PatchedHash,
            PatchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            BackupPath = main.BackupPath,
            AppliedEntries = patchedTotal,
            Files = fileStates,
            PendingVerify = true,
            PendingSince = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        });

        var message = $"已写入 {patchedTotal} 处译文";
        if (fileStates.Count > 1)
        {
            message += $"（跨 {fileStates.Count} 个程序集）";
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

        var state = this.store.Load(entry.InternalName);
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
        else
        {
            // 没有补丁记录、备份也指望不上（历史上的失败退路会在盘上留下「当前文件的快照」）——
            // 试试从已打的补丁反向还原出原文（2026-10-02 ActionTimelineReborn / DailyRoutines 实测）。
            var recovered = this.TryRecoverPatchedDLL(entry, sources, searchDirectories, out var recoveryNote);
            if (recovered is not null)
            {
                note = (note.Length > 0 ? note + "；" : string.Empty) + recoveryNote;
                Plugin.Log?.Information($"[内部文本] {entry.InternalName}：{recoveryNote}");
                return (recovered, note);
            }
        }

        Plugin.Log?.Warning($"[内部文本] {entry.InternalName}：抽取结果里有一批「译文###原文」形态的文本，且没有可用备份——已停下（避免污染译文包）");
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

        var tempDirectory = Path.Combine(Path.GetTempPath(), "firegaze-recover-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDirectory);
            var recoveredPaths = new List<string>();
            var fileStates = new List<UITextPatchFile>();
            var totalReverted = 0;
            foreach (var source in sources)
            {
                if (!File.Exists(source))
                {
                    continue;
                }

                var recovered = Path.Combine(tempDirectory, Path.GetFileName(source));
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
                return null;
            }

            var main = fileStates[0];
            this.store.Save(new UITextPatchState
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
            });

            note = $"盘上还留着我们打过的补丁但丢了记录，已从补丁反向还原出原文文件（{totalReverted} 处）并恢复了补丁记录";
            return UIStringExtractor.ExtractMany(recoveredPaths, searchDirectories);
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, $"[内部文本] {entry.InternalName}：反向还原失败");
            return null;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
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

        var patched = 0;
        foreach (var item in extraction.Entries)
        {
            // 不再只看 UI：中文判定会把「译文###原文」当成「已是中文」排掉，
            // 那种条目恰恰是最硬的补丁证据（2026-10-02 修）。
            var mark = item.Original.IndexOf(UITextText.IDSeparator, StringComparison.Ordinal);
            if (mark <= 0)
            {
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

    /// <summary>
    ///     还原成原始 DLL。
    /// </summary>
    public (bool Ok, string Message) Restore(InstalledPluginEntry entry, string? reason = null)
    {
        var state = this.store.Load(entry.InternalName);
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
