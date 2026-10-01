using System.Text.Json;
using System.Text.Json.Serialization;

namespace FireGaze.UIText;

/// <summary>
///     多程序集插件：把「插件自己的其它程序集」找出来（主 DLL 之外还要一起抽取/打补丁的文件）。
/// </summary>
/// <remarks>
///     背景：有些插件把界面代码拆在伴生程序集里（例：ARSR 的主 DLL 只有壳，
///     配置窗口的文字在 <c>RotationSolver.Basic.dll</c>），只扫主 DLL 会漏掉一大半。
///     只收**插件自己的**程序集，不能收同目录的第三方库（图片/音频/协议库…）——
///     2026-10-02 实测：光按「主程序集引用了的」筛会把 SixLabors / NAudio / SharpDX 全捞进来。
///     两条规则：
///       · 通用：文件名叫 <c>&lt;主DLL名&gt;.*.dll</c>（同一作者自己的分层命名）；
///       · 名单：<c>uit-companions.json</c>（随插件打包；与 CI 共用同一份）里点名。
///     打开规则见 <see cref="Resolve" />；主程序集永远排第一。
/// </remarks>
internal static class PluginAssemblies
{
    /// <summary>插件目录（和 FFXIVGlossary 一样，启动时由 Plugin 告诉它）。</summary>
    public static string PluginDirectory { get; set; } = string.Empty;

    private static Dictionary<string, string[]>? map;
    private static bool mapLoaded;

    /// <summary>
    ///     解析出一个插件要处理的全部 DLL：主程序集 + 伴生程序集（都存在才返回）。
    /// </summary>
    public static List<string> Resolve(string? mainDLLPath, string internalName)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(mainDLLPath) || !File.Exists(mainDLLPath))
        {
            return result;
        }

        result.Add(mainDLLPath);
        try
        {
            var directory = Path.GetDirectoryName(mainDLLPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return result;
            }

            var stem = Path.GetFileNameWithoutExtension(mainDLLPath);
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(directory, "*.dll"))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, Path.GetFileName(mainDLLPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 通用规则：主程序集自己的分层（Main.Something.dll）
                if (name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                {
                    wanted.Add(name);
                }
            }

            // 名单规则：名字完全不同的伴生程序集（uid 为内部名）
            if (LookupCompanions(internalName) is { Length: > 0 } listed)
            {
                foreach (var name in listed)
                {
                    if (File.Exists(Path.Combine(directory, name)))
                    {
                        wanted.Add(name);
                    }
                }
            }

            foreach (var name in wanted)
            {
                var full = Path.Combine(directory, name);
                if (!result.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(full);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.Debug(e, "[内部文本] 查找伴生程序集失败（只处理主程序集）");
            result.RemoveAll(p => !string.Equals(p, mainDLLPath, StringComparison.OrdinalIgnoreCase));
            result.Insert(0, mainDLLPath);
        }

        return result;
    }

    /// <summary>测试与诊断用：当前加载进来的伴生名单。</summary>
    public static IReadOnlyDictionary<string, string[]> LoadedMap
    {
        get
        {
            EnsureMap();
            return map!;
        }
    }

    /// <summary>强制重新读一次名单（改完文件不用重启游戏，测试也在用）。</summary>
    public static void Reload()
    {
        mapLoaded = false;
        EnsureMap();
    }

    private static string[]? LookupCompanions(string internalName)
    {
        EnsureMap();
        return map!.TryGetValue(internalName, out var list) ? list : null;
    }

    private static void EnsureMap()
    {
        if (mapLoaded)
        {
            return;
        }

        mapLoaded = true;
        map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var path = string.IsNullOrEmpty(PluginDirectory) ? null : Path.Combine(PluginDirectory, "uit-companions.json");
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<CompanionFile>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (parsed?.Plugins is null)
            {
                return;
            }

            foreach (var (key, value) in parsed.Plugins)
            {
                var cleaned = value?.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray() ?? [];
                if (cleaned.Length > 0)
                {
                    map[key] = cleaned;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 读 uit-companions.json 失败（本次只处理主程序集）");
        }
    }

    private sealed class CompanionFile
    {
        [JsonPropertyName("plugins")]
        public Dictionary<string, string[]>? Plugins { get; set; }
    }
}
