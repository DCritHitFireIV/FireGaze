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

    /// <summary>
    ///     「不汉化」名单（用户 2026-10-02 指定）：这些插件本身就是中文、由朋友维护，
    ///     汉化只会增加维护负担；FireGaze 识别为中文插件直接跳过，不允许抽取 / 翻译 / 打补丁 / 上传。
    ///     按内部名匹配，大小写不敏感。
    /// </summary>
    private static readonly HashSet<string> DoNotLocalize = new(StringComparer.OrdinalIgnoreCase)
    {
        "AEAssistV3",
        "AEAssist",
        "DailyRoutines",
        "OmniToolbox",
        "XSZToolbox",
        "KodakkuAssist",
        "PromeRotation",
        "NyaDraw",
        "I-Ching-GL",
        "MissFisher",
        "LightlessSync",
        "LightlessCN",
        "SillyToolbox",
        "pvpauto",
        "BOCCHI",
        "AetherphoneCN",
        "AvantGardeCN",
        "BossModRebornCN",
        "Brio-CN",
        "DalamudCNAdapter",
        "DamageMeter-CN",
        "ExtraChat-CN",
        "FuckDalamudCN",
        "HyperboreaCN",
        "ICECN",
        "InventoryToolsCN",
        "ItemVendorLocationCN",
        "Mahjong.Plugin.CN",
        "MalmstoneCN",
        "MarketRouteCN",
        "OCNFarmer",
        "PVPLOGSCN",
        "PriceInsight-CN",
        "PvpStatsCN",
        "RetainerRepricerCN",
        "TidyChatCN",
        "VFXEditorCN",
        "XIVComboExpandedCN",
        "visland_cn",
        "KeitaToolbox",
        "PFClassifier",
        "PFRadar",
        "PvPSelector",
        "UntarnishedHeart",
        "AutoHook",
        "DCTravelerX",
        "ProxyPlugin",
        // Siren（extrant）的插件（2026-10-03 用户要求：Siren 做的插件都是中文插件）
        "Zhouyi",
        "Coyote-FFXiv",
        "FFXIVNetworkPacketAnalysisTool",
        "GposeResizer",
        "CleanWindow",
        "AutoTriadC",
        "TTC_Siren",
        "CoordImporter",
    };

    /// <summary>这个插件是不是「中文插件、不汉化」（名单见 <see cref="DoNotLocalize" />）。</summary>
    public static bool IsDoNotLocalize(string? internalName) =>
        !string.IsNullOrEmpty(internalName) && DoNotLocalize.Contains(internalName);

    private static Dictionary<string, string[]>? companions;
    private static HashSet<string>? uiAttributes;
    private static Dictionary<string, HashSet<int>>? attributeSkipArgs;
    private static Dictionary<string, int>? commandAttributes;
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
    ///     这个特性的第 N 个构造参数是不是「功能字符串」（搜索标签之类）——抽取时跳过。
    ///     名单在 <c>uit-rules.json</c> 的 <c>attributeSkipArgs</c>（例：BossMod 的 PropertyDisplay
    ///     第 5 个参数是搜索标签，翻了会让英文搜索失效）。
    /// </summary>
    public static bool IsAttributeArgumentSkipped(string typeFullName, int index)
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

        EnsureLoaded();
        return attributeSkipArgs!.TryGetValue(name, out var skip) && skip.Contains(index);
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
        if (uiAttributes!.Contains(name))
        {
            return true;
        }

        // 命令类特性（ECommons 的 Cmd/SubCmd、卫月的 Command）：特性本身算 UI（帮助文本要翻），
        // 但第 0 个参数是命令名/子命令名，抽取与打补丁都要跳过——见 CommandHelpStartIndex。
        return commandAttributes!.ContainsKey(name);
    }

    /// <summary>
    ///     命令类特性的帮助文本从第几个构造参数开始（前面的是命令名 / 别名，绝不能翻）。
    ///     不是命令类特性时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    ///     例：<c>[SubCmd("unlock", "Unlock all windows")]</c> → 1（0 是子命令名）；
    ///     ECommons 的 <c>CmdAttribute(command, helpMessage, ...)</c>、卫月的 <c>[Command("/x", "help")]</c> 同理。
    /// </remarks>
    public static int? CommandHelpStartIndex(string typeFullName)
    {
        if (string.IsNullOrEmpty(typeFullName))
        {
            return null;
        }

        var name = typeFullName;
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }

        EnsureLoaded();
        return commandAttributes!.TryGetValue(name, out var start) ? start : null;
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

    /// <summary>测试与诊断用：命令类特性 → 帮助文本起始参数下标。</summary>
    public static IReadOnlyDictionary<string, int> LoadedCommandAttributes
    {
        get
        {
            EnsureLoaded();
            return commandAttributes!;
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
        attributeSkipArgs = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        commandAttributes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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

            if (parsed?.AttributeSkipArgs is { } skipMap)
            {
                foreach (var (key, value) in skipMap)
                {
                    if (!string.IsNullOrWhiteSpace(key) && value is { Length: > 0 })
                    {
                        attributeSkipArgs[key] = [.. value];
                    }
                }
            }

            if (parsed?.CommandAttributes is { } commandMap)
            {
                foreach (var (key, value) in commandMap)
                {
                    if (!string.IsNullOrWhiteSpace(key) && value >= 0)
                    {
                        commandAttributes[key] = value;
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

        [JsonPropertyName("commandAttributes")]
        public Dictionary<string, int>? CommandAttributes { get; set; }

        [JsonPropertyName("attributeSkipArgs")]
        public Dictionary<string, int[]>? AttributeSkipArgs { get; set; }
    }
}
