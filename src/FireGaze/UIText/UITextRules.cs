using System.Text.Json;
using System.Text.Json.Serialization;

namespace FireGaze.UIText;

/// <summary>
///     插件侧的静态规则（随插件打包的 <c>uit-rules.json</c>；插件端与 CI 共用同一份）：
///     ① 多程序集插件的**伴生程序集**；② 自定义特性里哪些特性算界面文本。
/// </summary>
/// <remarks>
///     伴生程序集：有些插件把界面代码拆在第二个 DLL 里（ARSR：主 DLL 只有壳，
///     配置窗口文字在 <c>RotationSolver.Basic.dll</c>），只扫主 DLL 会漏一大半。
///     规则 = 同目录 + （文件名为 <c>&lt;主DLL名&gt;.*.dll</c> 或名单点名）。
///     **不要**按「主程序集引用了的」来筛——2026-10-02 实测会把 SixLabors / NAudio / SharpDX / MessagePack
///     这类第三方库全捞进来。
///
///     属性字符串：不少插件把配置界面的说明放在自定义特性参数里（ARSR 的 <c>UIAttribute</c>、
///     SimpleTweaks 的 <c>TweakNameAttribute</c>/<c>TweakDescriptionAttribute</c>/<c>TweakConfigOptionAttribute</c>），
///     这些字符串不在 <c>ldstr</c> 里，普通抽取看不到。识别规则：
///       · 内建：<c>System.ComponentModel</c> 的 Description / Category / Display / Tooltip / Label；
///       · 内建：短名以 <c>UI</c>/<c>Ui</c> 开头且以 <c>Attribute</c> 结尾（UIAttribute / UiTextAttribute…）；
///       · 名单：<c>uiAttributes</c> 里点名（SimpleTweaks 那套）。
///     只翻**构造函数参数**（命名参数常是键/ID，不碰），且值要过 <c>LooksTranslatable</c>。
/// </remarks>
internal static class UITextRules
{
    /// <summary>插件目录（和 FFXIVGlossary 一样，启动时由 Plugin 告诉它）。</summary>
    public static string PluginDirectory { get; set; } = string.Empty;

    /// <summary>日志出口（插件里由 Plugin 接上；探针/测试里为 null，不记）。</summary>
    public static Action<Exception, string>? WarningSink { get; set; }

    /// <summary>调试日志出口（同上）。</summary>
    public static Action<Exception, string>? DebugSink { get; set; }

    private static Dictionary<string, string[]>? companions;
    private static HashSet<string>? uiAttributes;
    private static bool loaded;

    /// <summary>
    ///     解析出一个插件要处理的全部 DLL：主程序集 + 伴生程序集（文件存在才返回，主永远第一）。
    /// </summary>
    public static List<string> ResolveCompanions(string? mainDLLPath, string internalName)
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
            EnsureLoaded();
            if (companions!.TryGetValue(internalName, out var listed))
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
            DebugSink?.Invoke(e, "[内部文本] 查找伴生程序集失败（只处理主程序集）");
            result.RemoveAll(p => !string.Equals(p, mainDLLPath, StringComparison.OrdinalIgnoreCase));
            result.Insert(0, mainDLLPath);
        }

        return result;
    }

    /// <summary>
    ///     这个自定义特性里的字符串算不算界面文本。只看**构造函数参数**由调用方保证。
    /// </summary>
    public static bool IsUIAttribute(string typeFullName)
    {
        if (string.IsNullOrEmpty(typeFullName))
        {
            return false;
        }

        var name = typeFullName;
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }

        if (name is "DescriptionAttribute" or "CategoryAttribute" or "DisplayAttribute" or "TooltipAttribute" or "LabelAttribute")
        {
            return true;
        }

        // 插件自己写的 UI 特性（UIAttribute / UiTextAttribute / UiStringAttribute…）
        if (name.EndsWith("Attribute", StringComparison.Ordinal)
            && (name.StartsWith("UI", StringComparison.Ordinal) || name.StartsWith("Ui", StringComparison.Ordinal)))
        {
            return true;
        }

        EnsureLoaded();
        return uiAttributes!.Contains(name);
    }

    /// <summary>
    ///     自定义特性的**命名参数**（Property / Field）里哪些名字算界面文本。
    /// </summary>
    /// <remarks>
    ///     ARSR 的 <c>[RotationConfig(CombatType.PvE, Name = "…")]</c> 就是这种形态（2026-10-02 用户实测漏翻）。
    ///     其余名字（Path / Id / Command / Version…）多是键与标识，一律不碰。
    /// </remarks>
    private static readonly string[] UINamedArgumentNames =
    [
        "Name",
        "Text",
        "Label",
        "Title",
        "Caption",
        "Heading",
        "Description",
        "Tooltip",
        "Hint",
        "DisplayName",
        "DisplayText",
    ];

    /// <summary>命名参数名是否算界面文本（大小写不敏感）。</summary>
    public static bool IsUINamedArgument(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var candidate in UINamedArgumentNames)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>测试与诊断用：当前加载进来的伴生名单。</summary>
    public static IReadOnlyDictionary<string, string[]> LoadedCompanions
    {
        get
        {
            EnsureLoaded();
            return companions!;
        }
    }

    /// <summary>测试与诊断用：当前生效的额外界面特性名单。</summary>
    public static IReadOnlyCollection<string> LoadedUIAttributes
    {
        get
        {
            EnsureLoaded();
            return uiAttributes!;
        }
    }

    /// <summary>强制重新读一次规则文件（测试用）。</summary>
    public static void Reload()
    {
        loaded = false;
        EnsureLoaded();
    }

    private static void EnsureLoaded()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        companions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        uiAttributes = new HashSet<string>(StringComparer.Ordinal);

        var path = string.IsNullOrEmpty(PluginDirectory) ? null : Path.Combine(PluginDirectory, "uit-rules.json");
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RulesFile>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (parsed?.Companions is not null)
            {
                foreach (var (key, value) in parsed.Companions)
                {
                    var cleaned = value?.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray() ?? [];
                    if (cleaned.Length > 0)
                    {
                        companions[key] = cleaned;
                    }
                }
            }

            if (parsed?.UIAttributes is { } attributeNames)
            {
                foreach (var name in attributeNames)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        uiAttributes.Add(name);
                    }
                }
            }
        }
        catch (Exception e)
        {
            WarningSink?.Invoke(e, "[内部文本] 读 uit-rules.json 失败（本次只按内建规则）");
        }
    }

    private sealed class RulesFile
    {
        [JsonPropertyName("companions")]
        public Dictionary<string, string[]>? Companions { get; set; }

        [JsonPropertyName("uiAttributes")]
        public string[]? UIAttributes { get; set; }
    }
}
