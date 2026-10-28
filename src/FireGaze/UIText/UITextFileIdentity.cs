namespace FireGaze.UIText;

/// <summary>
/// Maps recorded file identities across an installation-directory move.
/// This validates lexical path ownership only; callers still verify bytes and
/// backup provenance before reading an original or changing an installed file.
/// </summary>
internal static class UITextFileIdentity
{
    public static bool TryMap(
        UITextPatchState state,
        string currentMainPath,
        out Dictionary<string, UITextPatchFile> mapped,
        out string error)
    {
        mapped = new Dictionary<string, UITextPatchFile>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        try
        {
            if (state is null || !IsAbsolutePath(state.DLLPath) || !IsAbsolutePath(currentMainPath))
            {
                error = "补丁记录或当前主程序集路径为空或不是绝对路径。";
                return false;
            }

            var recordedMain = Path.GetFullPath(state.DLLPath);
            var currentMain = Path.GetFullPath(currentMainPath);
            var recordedRoot = Path.GetDirectoryName(recordedMain);
            var currentRoot = Path.GetDirectoryName(currentMain);
            if (string.IsNullOrEmpty(recordedRoot) || string.IsNullOrEmpty(currentRoot))
            {
                error = "补丁记录或当前主程序集没有有效的安装目录。";
                return false;
            }

            var candidates = new Dictionary<string, UITextPatchFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in state.EffectiveFiles)
            {
                if (file is null || !IsAbsolutePath(file.Path))
                {
                    error = "补丁记录里有空的或非绝对文件路径。";
                    return false;
                }

                var recordedPath = Path.GetFullPath(file.Path);
                var relative = Path.GetRelativePath(recordedRoot, recordedPath);
                if (!IsDescendant(relative))
                {
                    error = "补丁记录里的文件不在记录主程序集目录内。";
                    return false;
                }

                var target = Path.GetFullPath(Path.Combine(currentRoot, relative));
                if (!IsDescendant(Path.GetRelativePath(currentRoot, target)))
                {
                    error = "补丁记录里的目标路径跑出当前插件目录。";
                    return false;
                }

                // Moving a directory preserves the main file's relative
                // identity. A different main filename requires a new record.
                if (string.Equals(recordedPath, recordedMain, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(target, currentMain, StringComparison.OrdinalIgnoreCase))
                {
                    error = "记录主程序集与当前主程序集的相对文件身份不同。";
                    return false;
                }

                if (!candidates.TryAdd(target, file))
                {
                    error = "补丁记录里有重复的规范目标文件。";
                    return false;
                }
            }

            mapped = candidates;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or IOException or System.Security.SecurityException)
        {
            error = "补丁记录里的路径无效：" + exception.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>When the write set excludes the main DLL, its unchanged hash anchors the plugin version.</summary>
    public static bool MatchesUnmodifiedMain(UITextPatchState state, string currentMainPath,
        IReadOnlyDictionary<string, UITextPatchFile> mapped)
    {
        if (mapped.ContainsKey(Path.GetFullPath(currentMainPath))) return true;
        return Guid.TryParse(state.OperationID, out _)
            && string.Equals(Path.GetFileName(state.DLLPath), Path.GetFileName(currentMainPath), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(state.SourceHash)
            && string.Equals(state.SourceHash, state.PatchedHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(UITextPatchStore.HashOf(currentMainPath), state.SourceHash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAbsolutePath(string? path) => !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path);

    private static bool IsDescendant(string relative) => relative.Length > 0
        && !relative.Equals(".", StringComparison.Ordinal)
        && !relative.Equals("..", StringComparison.Ordinal)
        && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
        && !Path.IsPathRooted(relative);
}
