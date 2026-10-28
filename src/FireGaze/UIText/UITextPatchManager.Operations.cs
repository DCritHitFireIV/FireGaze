using System.Text;
using FireGaze.RepoAudit;

namespace FireGaze.UIText;

internal sealed partial class UITextPatchManager
{
    private (bool Ok, string Message) ApplyPrepared(InstalledPluginEntry entry, UITextPack pack,
        IReadOnlyList<string> inputs, UITextPatchState? existing, string pluginDirectory, bool safetyRepair,
        out bool statePersisted)
    {
        statePersisted = false;
        var oldFiles = new Dictionary<string, UITextPatchFile>(StringComparer.OrdinalIgnoreCase);
        if (existing is not null
            && !UITextFileIdentity.TryMap(existing, entry.DLLPath!, out oldFiles, out var pathError)) return (false, pathError);
        var staged = new List<(UITextPatchFile State, string NewPath)>();
        var files = new Dictionary<string, UITextPatchFile>(StringComparer.OrdinalIgnoreCase);
        var temporary = new List<string>();
        UITextPatchState? operation = null;
        var translated = 0;
        var localizationWritten = 0;
        var placeholders = 0;
        var protectedIdentities = 0;
        var pdbDropped = false;
        var mainPath = Path.GetFullPath(entry.DLLPath!);
        var initialMainHash = UITextPatchStore.HashOf(mainPath);
        var missingSets = new List<IReadOnlyCollection<string>>();
        try
        {
            foreach (var input in inputs)
            {
                var path = Path.GetFullPath(input);
                var before = UITextPatchStore.HashOf(path);
                if (before.Length == 0) throw new IOException("读不到目标文件的完整字节：" + path);
                oldFiles.TryGetValue(path, out var old);
                var isOldPatch = old is not null && HashEquals(before, old.PatchedHash);
                var sourceHash = isOldPatch ? old!.SourceHash : before;
                var backup = isOldPatch ? old!.BackupPath : this.store.Backup(entry.InternalName, path, sourceHash);
                VerifyOriginal(backup, sourceHash);
                var newPath = path + ".fguitext.new";
                temporary.Add(newPath);
                // Resolve dependency contracts from the actual install directory,
                // even when the source bytes live in the durable originals store.
                var outcome = UITextPatcher.Patch(backup!, newPath, pack, entry.Version,
                    includeAmbiguous: this.plugin.Config.UITextTranslateGreyList,
                    allowEmptyPatch: true, searchDirectories: [pluginDirectory]);
                if (!outcome.Ok) throw new IOException(outcome.Error ?? "无法生成完整补丁副本。");
                translated += outcome.PatchedTotal;
                placeholders += outcome.PlaceholderSkipped;
                protectedIdentities += outcome.FunctionalIdentifierSkipped;
                pdbDropped |= outcome.PdbDropped;
                missingSets.Add(outcome.Missing);
                if (outcome.PatchedTotal == 0)
                {
                    // Removing the last translation must restore exact original
                    // bytes, without treating a rewritten empty assembly as a patch.
                    if (!isOldPatch) continue;
                    File.Copy(backup!, newPath, overwrite: true);
                }
                var file = new UITextPatchFile { Path = path, SourceHash = sourceHash,
                    BeforeHash = before, PatchedHash = UITextPatchStore.HashOf(newPath), BackupPath = backup };
                files.Add(path, file);
                staged.Add((file, newPath));
            }

            var previous = oldFiles.Select(pair => new UITextLocalizationPreviousFile(pair.Key,
                pair.Value.SourceHash, pair.Value.PatchedHash, pair.Value.BackupPath)).ToList();
            UITextLocalizationFiles.TryWrite(pluginDirectory, pack, previous, out var writes, out var fileError);
            if (!string.IsNullOrEmpty(fileError)) throw new IOException(fileError);
            foreach (var write in writes)
            {
                var path = Path.GetFullPath(write.Path);
                var before = write.CurrentBeforeHash;
                if (!HashEquals(UITextPatchStore.HashOf(path), before)) throw new IOException("本地化文件在准备期间发生变化。");
                var backup = write.NeedsBackup ? this.store.Backup(entry.InternalName, path, write.SourceHash) : write.ReuseBackup;
                VerifyOriginal(backup, write.SourceHash);
                var newPath = path + ".fguitext.tmp";
                temporary.Add(newPath);
                File.WriteAllText(newPath, write.Content, new UTF8Encoding(false));
                var file = new UITextPatchFile { Path = path, SourceHash = write.SourceHash,
                    BeforeHash = before, PatchedHash = UITextPatchStore.HashOf(newPath), BackupPath = backup };
                files.Add(path, file);
                staged.Add((file, newPath));
                localizationWritten += write.Count;
            }

            foreach (var (path, old) in oldFiles)
            {
                if (files.ContainsKey(path)) continue;
                var current = UITextPatchStore.HashOf(path);
                if (HashEquals(current, old.SourceHash)) continue;
                if (!HashEquals(current, old.PatchedHash))
                    throw new IOException("此前修改的文件已变化，先核对原始包再应用汉化。");
                VerifyOriginal(old.BackupPath, old.SourceHash);
                files.Add(path, old with { Path = path, BeforeHash = current });
            }
            if (files.Count == 0) return (false, "没有匹配的可写译文，目标文件保持原样。");
            foreach (var file in files.Values)
            {
                VerifyOriginal(file.BackupPath, file.SourceHash);
                if (string.IsNullOrEmpty(file.PatchedHash)
                    || !HashEquals(UITextPatchStore.HashOf(file.Path), file.BeforeHash))
                    throw new IOException("目标文件在准备期间发生变化，尚未提交。");
            }
            files.TryGetValue(mainPath, out var main);
            if (main is null && !HashEquals(UITextPatchStore.HashOf(mainPath), initialMainHash))
                throw new IOException("主程序集在准备本地化文件期间发生变化。");
            operation = new UITextPatchState
            {
                InternalName = entry.InternalName, DLLPath = mainPath, PluginVersion = entry.Version,
                OperationID = Guid.NewGuid().ToString("N"), WritePhase = "Prepared",
                SourceHash = main?.SourceHash ?? initialMainHash,
                PatchedHash = main?.PatchedHash ?? initialMainHash, BackupPath = main?.BackupPath,
                Files = files.Values.ToList(), AppliedEntries = translated + localizationWritten,
                PatchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), PendingVerify = false,
            };
            // Both records precede any target mutation. The durable copy is a
            // full plan, so no per-file progress save can lose recovery authority.
            if (!this.store.SavePendingOperation(operation)) return (false, "保存恢复操作失败，未修改目标文件。");
            if (!this.store.Save(operation)) return (false, "保存补丁状态失败，未修改目标文件；恢复凭据已保留。");
            foreach (var (file, newPath) in staged)
            {
                if (main is null && !HashEquals(UITextPatchStore.HashOf(mainPath), initialMainHash))
                    throw new IOException("主程序集在提交本地化文件期间发生变化，停止后续提交。");
                if (!HashEquals(UITextPatchStore.HashOf(file.Path), file.BeforeHash)
                    || !HashEquals(UITextPatchStore.HashOf(newPath), file.PatchedHash))
                    throw new IOException("目标或临时副本已经变化，停止后续提交。");
                if (!HashEquals(file.BeforeHash, file.PatchedHash)) File.Move(newPath, file.Path, overwrite: true);
                if (!HashEquals(UITextPatchStore.HashOf(file.Path), file.PatchedHash))
                    throw new IOException("写入结果未能确认，停止后续提交。");
            }
            if ((main is null && !HashEquals(UITextPatchStore.HashOf(mainPath), initialMainHash))
                || files.Values.Any(file => !HashEquals(UITextPatchStore.HashOf(file.Path), file.PatchedHash)))
                throw new IOException("提交后的完整文件集合发生变化。");
            var committed = operation with { WritePhase = "Committed", PendingVerify = true,
                PendingSince = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            if (!this.store.Save(committed)) throw new IOException("文件已提交，但完成状态保存失败。");
            if (!this.store.PublishManifest(entry.InternalName, entry.Version, committed.PatchedAt,
                    committed.AppliedEntries, committed.EffectiveFiles, pluginDirectory, mainPath, committed.OperationID, committed.PatchedHash))
                throw new IOException("文件已提交，但原始清单发布失败。");
            if (!this.store.ClearPendingOperation(entry.InternalName))
                throw new IOException("完成记录已保存，但恢复操作尚未清理。");
            statePersisted = true;
            this.ClearHashCache();
            var message = $"已写入 {translated} 处译文";
            if (localizationWritten > 0) message += $"，另补了本地化文件 {localizationWritten} 条";
            if (protectedIdentities > 0) message += $"（保留了 {protectedIdentities} 条插件 / IPC 标识符）";
            if (placeholders > 0) message += $"（{placeholders} 条占位符对不上，没写进去）";
            if (pdbDropped) message += "（调试符号未保留）";
            var missing = IntersectMissing(missingSets);
            if (missing.Count > 0) message += $"（{missing.Count} 条在 DLL 里没找到）";
            return (true, message + "；重载插件后生效。");
        }
        catch (Exception error)
        {
            if (operation is not null && this.store.HasPendingOperation(entry.InternalName))
            {
                var failed = operation with { WritePhase = "RecoveryRequired", LastError = error.GetBaseException().Message };
                this.store.SavePendingOperation(failed);
                this.store.Save(failed);
            }
            return (false, "写入未完成，已保留恢复凭据：" + error.GetBaseException().Message);
        }
        finally
        {
            foreach (var path in temporary) TryDelete(path);
        }

        void VerifyOriginal(string? path, string hash)
        {
            if (path is null || !this.store.IsDurableBackup(path)
                || !HashEquals(UITextPatchStore.HashOf(path), hash))
                throw new IOException("未取得可校验的持久原件，未允许目标写入。");
        }
    }

    private static bool HashEquals(string? left, string? right) => !string.IsNullOrEmpty(left)
        && !string.IsNullOrEmpty(right) && left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
