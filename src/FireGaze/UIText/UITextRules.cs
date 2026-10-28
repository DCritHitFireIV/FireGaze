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
///     这些字符串不在 <c>ldstr</c> 里，普通抽取看不到。准入由
///     <see cref="UITextAttributeContracts" /> 根据完整类型、构造器与消费者上下文决定。
///     旧规则文件的短名列表保留读取兼容，不授予未知类型或参数写入权限。
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
    ///     「中文插件」名单（用户 2026-10-02 指定）：这些插件本身就是中文，
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
        // Siren（extrant）的插件（2026-10-03 用户要求：Siren 做的插件都是中文插件；Zhouyi 是 I-Ching 曾用名，不单列）
        "Coyote-FFXiv",
        "FFXIVNetworkPacketAnalysisTool",
        "GposeResizer",
        "CleanWindow",
        "AutoTriadC",
        "TTC_Siren",
        "CoordImporter",
        // 2026-10-03 用户要求：XTeleport / 琉璃的宝石 / Anmi 的插件 / VanillaPlus / Inverse Kinematics
        "Teleport",        // XTeleport（XTeleportOnline）
        "ruri",            // 琉璃的宝石（kano）
        "ArenaPilot",      // 斗兽塔助手（Anmi）
        "Beastmaster",     // 驯兽师助手（Anmi）
        "Chronicler",      // 新月岛史官（Anmi）
        "Phantom",         // 肝武助手（Anmi）
        "DalamudACT",      // DPS统计（Shiyuvi、Anmi 合作）
        "VanillaPlus",     // 自带 de/fr/ja/zh 本地化目录
        "FootIk",          // Inverse Kinematics（BSoD38）
    };

    /// <summary>这个插件是不是中文插件（名单见 <see cref="DoNotLocalize" />）。</summary>
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
    ///     已知的精确属性类型。具体参数的抽取、写入及恢复检测还须通过
    ///     <see cref="UITextAttributeContracts" /> 的完整合同，不能仅据此方法放行。
    /// </summary>
    public static bool IsUIAttribute(string typeFullName) => UITextAttributeContracts.IsKnownType(typeFullName);

    /// <summary>测试与诊断用：当前加载进来的伴生名单。</summary>
    public static IReadOnlyDictionary<string, string[]> LoadedCompanions
    {
        get
        {
            EnsureLoaded();
            return companions!;
        }
    }

    /// <summary>测试与诊断用：旧规则文件里的界面特性名单；不作为参数准入依据。</summary>
    public static IReadOnlyCollection<string> LoadedUIAttributes
    {
        get
        {
            EnsureLoaded();
            return uiAttributes!;
        }
    }

    /// <summary>测试与诊断用：旧规则文件里的命令帮助下标；不作为参数准入依据。</summary>
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
