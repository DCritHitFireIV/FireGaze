using System.Text;
using System.Text.Json;

namespace FireGaze.UIText;

internal sealed partial class UITextPatchStore
{
    private string? PendingPath(string internalName) => this.OriginalsDirectoryOf(internalName) is { } directory
        ? Path.Combine(directory, "pending-operation.json") : null;

    public bool HasPendingOperation(string internalName) => this.PendingPath(internalName) is { } path
        && (File.Exists(path) || Directory.Exists(path));

    // A complete immutable plan survives a later config reset or failed progress
    // save. Hashes, not a progress counter, determine what actually reached disk.
    public bool SavePendingOperation(UITextPatchState state)
    {
        var path = this.PendingPath(state.InternalName);
        if (path is null || !Guid.TryParse(state.OperationID, out _) || !state.IsKnownWritePhase) return false;
        try
        {
            lock (this.gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, Options) + Environment.NewLine, new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
            return true;
        }
        catch (Exception error)
        {
            Plugin.Log?.Warning(error, "[内部文本] 写恢复操作记录失败：{Name}", state.InternalName);
            return false;
        }
    }

    public bool ClearPendingOperation(string internalName)
    {
        var path = this.PendingPath(internalName);
        if (path is null) return true;
        try
        {
            lock (this.gate)
            {
                if (File.Exists(path)) File.Delete(path);
                return !File.Exists(path) && !Directory.Exists(path);
            }
        }
        catch (Exception) { return false; }
    }

    public UITextPatchState? ReadPendingOperation(string internalName, string currentMainPath)
    {
        var path = this.PendingPath(internalName);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<UITextPatchState>(File.ReadAllText(path, Encoding.UTF8), Options);
            if (state is null || !state.InternalName.Equals(internalName, StringComparison.Ordinal)
                || !Guid.TryParse(state.OperationID, out _) || !state.IsKnownWritePhase
                || state.WritePhase is null || state.Files is not { Count: > 0 }
                || !UITextFileIdentity.TryMap(state, currentMainPath, out var mapped, out _)
                || !UITextFileIdentity.MatchesUnmodifiedMain(state, currentMainPath, mapped)) return null;
            var directory = this.OriginalsDirectoryOf(internalName)!;
            var backups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in mapped.Values)
            {
                if (string.IsNullOrEmpty(file.BackupPath) || !Path.IsPathFullyQualified(file.BackupPath)
                    || !TryResolveRelative(directory, Path.GetRelativePath(directory, file.BackupPath), out var backup)
                    || !backups.Add(backup) || string.IsNullOrEmpty(file.SourceHash)
                    || string.IsNullOrEmpty(file.PatchedHash) || string.IsNullOrEmpty(file.BeforeHash)) return null;
            }
            // Keep unknown bytes visible to Restore, which validates the entire
            // plan before writing. Never fall back to an older success manifest.
            return state with
            {
                DLLPath = Path.GetFullPath(currentMainPath),
                Files = mapped.Select(pair => pair.Value with { Path = pair.Key }).ToList(),
                WritePhase = state.WritePhase == "Restored" ? "Restored" : "RecoveryRequired",
                PendingVerify = false,
                LastError = state.LastError ?? "检测到未完成的文件操作，请先还原。",
            };
        }
        catch (Exception) { return null; }
    }

    // Used for manifest ownership, not for the display text of paths.
    private static bool TryResolveRelative(string root, string? relative, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
        try
        {
            var canonicalRoot = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relative));
            var normalized = Path.GetRelativePath(canonicalRoot, candidate);
            if (normalized is "." or ".." || Path.IsPathRooted(normalized)
                || normalized.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || normalized.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) return false;
            full = candidate;
            return true;
        }
        catch (Exception) { return false; }
    }
}
