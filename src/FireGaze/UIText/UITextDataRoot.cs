namespace FireGaze.UIText;

/// <summary>
///     FireGaze 的持久数据目录：放在启动器根目录下（<c>...\XIVLauncherCN\FireGazeData\</c>），
///     与插件配置目录分开——用户重置 / 清理 FireGaze 插件配置时不会连它一起删掉。
/// </summary>
/// <remarks>
///     2026-10-02 用户实测：重置把 <c>uitrans/state</c> 与 <c>uitrans/backups</c> 清空，而打过补丁的 DLL 还在，
///     补丁记录与原始文件都没了，只能靠反向还原自救。备份额外写进这里之后，同样的重置也能字节级恢复；
///     译文包也镜像到这里（JSON 很小，但丢了就真没了）。
/// </remarks>
internal static class UITextDataRoot
{
    /// <summary>持久目录的文件夹名（启动器根目录下）。</summary>
    public const string FolderName = "FireGazeData";

    /// <summary>
    ///     解析持久目录。按 <c>&lt;启动器根&gt;/pluginConfigs/&lt;插件名&gt;</c> 的常规布局往上推两级；
    ///     推不出来（非常规配置目录）就退回配置目录——行为和加这个目录之前一致，不会把功能弄坏。
    /// </summary>
    public static string Resolve(string configDirectory)
    {
        try
        {
            var pluginConfigs = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(configDirectory));
            var launcherRoot = pluginConfigs is null ? null : Path.GetDirectoryName(pluginConfigs);
            if (string.IsNullOrEmpty(launcherRoot) || !Directory.Exists(launcherRoot))
            {
                return configDirectory;
            }

            return Path.Combine(launcherRoot, FolderName);
        }
        catch (Exception)
        {
            return configDirectory;
        }
    }

    /// <summary>解析出来的目录是不是真的独立于配置目录（退回时相等，只用于日志说明）。</summary>
    public static bool IsDedicated(string root, string configDirectory) =>
        !string.Equals(root, configDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>补丁原始备份目录（每个插件一个子目录，里面有 manifest.json 与 *.orig）。</summary>
    public static string Originals(string root) => Path.Combine(root, "uit-originals");

    /// <summary>译文包镜像目录（<c>&lt;内部名&gt;.json</c>）。</summary>
    public static string PackMirror(string root) => Path.Combine(root, "uit-packs");
}
