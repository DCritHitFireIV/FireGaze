using System.Text;
using System.Text.Json;

namespace FireGaze.UIText;

internal sealed partial class UITextPatchStore
{
    public void CleanupUnreferencedOriginals(IReadOnlySet<string> before)
    {
        if (this.originalsDirectory is null) return;
        lock (this.gate)
        {
            try
            {
                if (!Directory.Exists(this.originalsDirectory)) return;
                // Legacy config records could reference another plugin's
                // durable directory. Preserve that global reference union.
                if (!this.TryCollectConfigReferences(out var configReferences)) return;
                foreach (var directory in Directory.EnumerateDirectories(this.originalsDirectory))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    var name = Path.GetFileName(directory);
                    if (!this.TryCollectReferencedBackups(name, out var referenced)) continue;
                    referenced.UnionWith(configReferences);
                    foreach (var path in Directory.EnumerateFiles(directory, "*.orig", SearchOption.TopDirectoryOnly))
                        if (!before.Contains(path) && !referenced.Contains(Path.GetFullPath(path))) TryDeleteFile(path);
                }
            }
            catch (Exception) { /* An incomplete inventory is never permission to delete. */ }
        }
    }

    private bool TryCollectConfigReferences(out HashSet<string> references)
    {
        references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(this.stateDirectory)) return !File.Exists(this.stateDirectory);
            foreach (var path in Directory.EnumerateFiles(this.stateDirectory, "*.json"))
            {
                if (!TryRead(path, out var json) || json is null) return false;
                var state = JsonSerializer.Deserialize<UITextPatchState>(json, Options);
                if (state is null || !state.IsKnownWritePhase) return false;
                foreach (var file in state.EffectiveFiles)
                {
                    if (string.IsNullOrEmpty(file.BackupPath) || !Path.IsPathFullyQualified(file.BackupPath)) return false;
                    references.Add(Path.GetFullPath(file.BackupPath));
                }
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    private bool TryCollectReferencedBackups(string directoryName, out HashSet<string> referenced)
    {
        referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = this.OriginalsDirectoryOf(directoryName)!;
        var records = 0;
        try
        {
            foreach (var path in new[] { this.PathOf(directoryName), Path.Combine(directory, "pending-operation.json") })
            {
                if (!TryRead(path, out var json)) return false;
                if (json is null) continue;
                records++;
                var state = JsonSerializer.Deserialize<UITextPatchState>(json, Options);
                if (state is null || Sanitize(state.InternalName) != directoryName || !state.IsKnownWritePhase
                    || !UITextFileIdentity.TryMap(state, state.DLLPath, out var files, out _)) return false;
                foreach (var file in files.Values)
                {
                    if (string.IsNullOrEmpty(file.BackupPath) || !Path.IsPathFullyQualified(file.BackupPath)) return false;
                    var backup = Path.GetFullPath(file.BackupPath);
                    if (!TryResolveRelative(directory, Path.GetRelativePath(directory, backup), out _)
                        && !TryResolveRelative(this.backupDirectory, Path.GetRelativePath(this.backupDirectory, backup), out _)) return false;
                    found.Add(backup);
                }
            }
            if (!TryRead(Path.Combine(directory, "manifest.json"), out var manifestJson)) return false;
            if (manifestJson is not null)
            {
                records++;
                var manifest = JsonSerializer.Deserialize<UITextOriginalsManifest>(manifestJson, Options);
                if (manifest is null || manifest.Format != 1 || Sanitize(manifest.InternalName) != directoryName
                    || manifest.Files.Count == 0) return false;
                foreach (var file in manifest.Files)
                {
                    if (!TryResolveRelative(directory, file.Backup, out var backup)) return false;
                    found.Add(backup);
                }
            }
            // No record is not evidence that an interrupted operation never wrote.
            if (records == 0) return false;
            referenced = found;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool TryRead(string path, out string? json)
    {
        json = null;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            json = File.ReadAllText(path, Encoding.UTF8);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception) { return false; }
    }
}
