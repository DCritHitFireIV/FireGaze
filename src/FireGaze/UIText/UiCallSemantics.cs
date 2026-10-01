namespace FireGaze.UIText;

/// <summary>
///     外部调用的语义判定：这个调用是不是在画 UI、是不是字符串加工、是不是「离 UI 很远」的危险语境。
/// </summary>
internal static class UICallSemantics
{
    /// <summary>
    ///     类型名不含 ImGui 字样、但确实在画界面的包装器（前缀匹配）。
    /// </summary>
    private static readonly string[] KnownWrapperPrefixes =
    [
        "Dalamud.Interface.Components.",
        "Dalamud.Interface.Utility.",
        "ECommons.ImGuiMethods.",
        "OmenTools.ImGuiOm.",
    ];

    /// <summary>
    ///     这些 ImGui 调用只是把文字画出来，不拿这个字符串当控件 ID。
    /// </summary>
    private static readonly HashSet<string> PlainTextCalls = new(StringComparer.Ordinal)
    {
        "Text",
        "TextUnformatted",
        "TextWrapped",
        "TextDisabled",
        "TextColored",
        "TextV",
        "BulletText",
        "SetTooltip",
        "SetItemTooltip",
        "SetTooltipUnformatted",
        "LabelText",
        "CalcTextSize",
        "GetTextLineHeight",
        "PushTextWrapPos",
        // Dalamud 的「(?) 悬停说明」：文本只画在 tooltip 里，不当控件 ID（ID 来自那个 "(?)" 前缀）
        "HelpMarker",
    };

    /// <summary>
    ///     ImGui 里拿字符串当 ID 用的调用：翻完要保留 <c>###原文</c> 后缀。
    /// </summary>
    private static readonly HashSet<string> IDCalls = new(StringComparer.Ordinal)
    {
        "Button",
        "SmallButton",
        "InvisibleButton",
        "ArrowButton",
        "Checkbox",
        "RadioButton",
        "Selectable",
        "MenuItem",
        "TreeNode",
        "TreeNodeEx",
        "CollapsingHeader",
        "Begin",
        "BeginChild",
        "BeginPopup",
        "BeginPopupModal",
        "BeginTabItem",
        "BeginMenu",
        "BeginCombo",
        "Combo",
        "ListBox",
        "InputText",
        "InputTextMultiline",
        "InputFloat",
        "InputInt",
        "InputDouble",
        "ColorEdit3",
        "ColorEdit4",
        "DragFloat",
        "DragInt",
        "SliderFloat",
        "SliderInt",
        "PushID",
        "PopID",
        "GetID",
        "OpenPopup",
        "OpenPopupOnItemClick",
        "IsPopupOpen",
        "ImageButton",
        "ColorButton",
        "ProgressBar",
        "TextLink",
        "TextLinkOpenURL",
    };

    /// <summary>
    ///     类型名前缀命中的「危险语境」：字符串进这些地方基本都不是拿来显示的。
    /// </summary>
    private static readonly string[] DangerousTypePrefixes =
    [
        "System.IO.",
        "System.Net.",
        "System.Reflection.",
        "System.Text.RegularExpressions.",
        "System.Text.Json.",
        "System.Text.Encoding",
        "Newtonsoft.Json.",
        "System.Diagnostics.",
        "System.Environment",
        "System.Runtime.InteropServices.",
        "System.Security.",
        "System.Globalization.",
        "System.Xml.",
        "System.Data.",
    ];

    /// <summary>
    ///     方法名命中的「危险语境」：比较、解析、查找。
    /// </summary>
    private static readonly HashSet<string> DangerousMethodNames = new(StringComparer.Ordinal)
    {
        // 注意：Equals / GetHashCode 不在这里。记录（record）的自动生成代码会对每个字段调它们，
        // 一放进来所有「存在 record 里再画出来」的文本都会被打成灰名单（2026-10-01 实测 ARSR 的教程文本）。
        // 真正的「当键名」由字典调用（IsCollectionKeyCall）和 op_Equality / CompareTo / StartsWith 这些兜住。
        "op_Equality",
        "op_Inequality",
        "CompareTo",
        "CompareOrdinal",
        "StartsWith",
        "EndsWith",
        "Contains",
        "IndexOf",
        "IndexOfAny",
        "LastIndexOf",
        "ToLower",
        "ToLowerInvariant",
        "ToUpper",
        "ToUpperInvariant",
        "Trim",
        "TrimStart",
        "TrimEnd",
        "Split",
        "Replace",
        "Remove",
        "Insert",
        "Substring",
        "Parse",
        "TryParse",
        "Add",
        "TryAdd",
        "ContainsKey",
        "TryGetValue",
        "GetValueOrDefault",
        "Invoke",
        "GetMethod",
        "GetField",
        "GetProperty",
        "GetEvent",
        "GetMember",
        "GetType",
        "GetTypes",
        "GetConstructor",
        "SendCommand",
        "ProcessCommand",
        "AddHandler",
        "RemoveHandler",
        "GetIpcProvider",
        "GetIpcSubscriber",
        "GetIpcCaller",
        "Subscribe",
        "Unsubscribe",
        "Broadcast",
        "Path",
        "Exists",
        "WriteLine",
        "WriteAllText",
        "ReadAllText",
        "AppendLine",
        "Append",
    };

    /// <summary>
    ///     字符串加工：结果的字符串由入参拼出来；用于把 <c>string.Format</c> / 插值 / 拼接串起来。
    /// </summary>
    private static readonly HashSet<string> StringProducerNames = new(StringComparer.Ordinal)
    {
        "Format",
        "Concat",
        "Join",
        "Intern",
        "Copy",
    };

    /// <summary>
    ///     Dalamud 窗口基类的构造函数：第 0 个参数就是窗口标题，会显示在标题栏上。
    /// </summary>
    /// <remarks>
    ///     子类构造函数里的 <c>base("标题")</c> 编译成 <c>call Window::.ctor(string)</c>（不是 newobj），
    ///    以前会落到「外部方法、语义不明」整条排除——2026-10-01 实测 AnoMech 的「AnoMech Settings」、
    ///     BazookaLens 的三个窗口标题都在这儿漏掉。
    /// </remarks>
    public static bool IsWindowTitleCall(string typeFullName, string methodName) =>
        methodName == ".ctor" && typeFullName.EndsWith("Dalamud.Interface.Windowing.Window", StringComparison.Ordinal);

    /// <summary>
    ///     插件自己写的 UI 包装方法（名字一看就是在画界面的）：类型不在上面几个前缀里，但第一个字符串参数
    ///     肯定是给玩家看的标签，点了就拿去画（ARSR 的 `RotationSolver.UI.ImGuiHelper.SelectableCombo` 这样）。
    /// </summary>
    private static readonly HashSet<string> UIHelperMethodNames = new(StringComparer.Ordinal)
    {
        "SelectableCombo",
        "SelectableButton",
        "DrawSectionTitle",
        "DrawHeader",
        "DrawLabel",
        "SectionTitle",
        "DrawTooltip",
    };

    /// <summary>
    ///     服务器信息栏（DTR）：`Get("名字")` 的那个名字会显示在服务器信息栏的悬停说明里。
    /// </summary>
    public static bool IsDtrCall(string typeFullName, string methodName) =>
        methodName is "Get" or "TryGet" && typeFullName.EndsWith("IDtrBar", StringComparison.Ordinal);

    /// <summary>
    ///     是不是「在画 UI」的调用。
    /// </summary>
    public static bool IsUICall(string typeFullName, string methodName)
    {
        // 窗口标题：即使类型名字里没有 ImGui，也是给玩家看的
        if (IsWindowTitleCall(typeFullName, methodName))
        {
            return true;
        }

        // 服务器信息栏的标题
        if (IsDtrCall(typeFullName, methodName))
        {
            return true;
        }

        // ImU8String 只是字符串构造器（<c>AppendLiteral</c> / <c>AppendFormatted</c>），
        // 碰它的时候还没画到界面上；真正的 UI 调用是后面接住它的那个（Checkbox 要 ID、TextColored 不要）。
        // 2026-10-01 实测：Orbwalker 的 "Movement" 经 AppendLiteral 后才进 ImGui.TextColored，
        // 在构造器上误判成「用 ID」，补丁写成「移动###Movement」，界面上原样漏出后缀。
        if (typeFullName.Contains("ImU8String", StringComparison.Ordinal))
        {
            return false;
        }

        if (typeFullName.Contains("ImGui", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var prefix in KnownWrapperPrefixes)
        {
            if (typeFullName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // 插件自己写的 UI 包装方法（名字一看就是在画界面的）
        if (UIHelperMethodNames.Contains(methodName))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     这个 ImGui 调用会不会把字符串当控件 ID 用（决定补丁要不要保留 <c>###原文</c>）。
    /// </summary>
    /// <summary>
    ///     这个参数位置上的字符串会不会被当控件 ID 用（决定补丁要不要保留 ###原文 后缀）。
    /// </summary>
    /// <remarks>
    ///     ImGui 的惯例：只有第 0 个字符串参数是控件标签（拿它算 ID），后面的字符串参数基本都是「只显示的」：
    ///     InputTextWithHint 的 hint（输入框里的灰字占位）、BeginCombo 的 preview、MenuItem 的快捷键、
    ///     InputFloat 的 format……这些字符串的绘制路径不处理 ##，所以绝不能给它们加 ###原文。
    ///     2026-10-01 实测：FriendlyFire 的输入框灰字显示成「角色名称（例如苹果汽水）###Character Name (e.g., Apple Soda)」就是这个原因。
    /// </remarks>
    /// <summary>
    ///     本地化查表调用：第 0 个参数是查表用的 key，翻了就查不到译文（跟字典键名同一类危险）。
    /// </summary>
    /// <remarks>
    ///     这类方法常常在另一个程序集里（HaselTweaks 的 HaselCommon.TextService.Translate(key)、
    ///     SimpleTweaks 的 Loc.Localize(key, fallback)），我们只扫主 DLL，看不见里面的字典查找，
    ///     key 就会被当成普通界面文本翻掉（2026-10-01 实测：HaselTweaks 包里 32 条译文其实是它的 key）。
    ///     这里按方法名兜底：Translate / Localize 这类名字 + 第 0 个参数是字符串 => 当 key 处理。
    /// </remarks>
    public static bool IsLocalizationKeyCall(string typeFullName, string methodName) => methodName is
        "Translate" or "Localize" or "GetTranslation" or "TryGetTranslation" or "GetLocalized" or "Localized";

    public static bool UsesStringAsIDForArgument(string typeFullName, string methodName, int argIndex) => argIndex == 0;

    public static bool UsesStringAsID(string typeFullName, string methodName)
    {
        // 窗口标题：ImGui 拿它算窗口 ID，翻完要保留 ###原文（标题自带 ###ID 时 BuildPatched 只换显示段）
        if (IsWindowTitleCall(typeFullName, methodName))
        {
            return true;
        }

        // 字符串构造器从来不当控件 ID 用（真正的判定在接住它的 UI 调用上）
        if (typeFullName.Contains("ImU8String", StringComparison.Ordinal))
        {
            return false;
        }

        if (!typeFullName.Contains("ImGui", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (PlainTextCalls.Contains(methodName))
        {
            return false;
        }

        // ImGui 里的控件标签（Button / Checkbox / TreeNode / Begin …）几乎都把标签当 ID 用；
        // 宁可多留一个 ###原文 后缀（显示不受影响），也不要让译文撞 ID。
        return true;
    }

    /// <summary>
    ///     把调用目标压成给人看的短名（<c>Dalamud.Bindings.ImGui.ImGui</c> + <c>Text</c> → <c>ImGui.Text</c>）。
    /// </summary>
    public static string ShortTarget(string typeFullName, string methodName)
    {
        if (IsWindowTitleCall(typeFullName, methodName))
        {
            return "窗口标题";
        }

        var dot = typeFullName.LastIndexOf('.');
        var shortType = dot >= 0 ? typeFullName[(dot + 1)..] : typeFullName;
        return $"{shortType}.{methodName}";
    }

    /// <summary>
    ///     是不是字符串加工调用（<c>string.Format</c> 之类）。
    /// </summary>
    public static bool IsStringProducer(string typeFullName, string methodName)
    {
        if (IsStringConversionShim(typeFullName, methodName))
        {
            return true;
        }

        // Dalamud 新版绑定的 UTF-8 字符串构造器：AppendLiteral / AppendFormatted 之后
        // 字面量还在这个缓冲区里，等接住它的 UI 调用再判定（value 流继承入参）。
        if (typeFullName.Contains("ImU8String", StringComparison.Ordinal))
        {
            return true;
        }

        if (typeFullName is "System.String" or "System.Text.StringBuilder")
        {
            return StringProducerNames.Contains(methodName);
        }

        return false;
    }

    /// <summary>
    ///     字符串转换垫片：Dalamud 新版绑定用 <c>ImU8String.op_Implicit(string)</c> 把字面量转成 UTF-8 串，
    ///     真正的 UI 调用在下一步；这里当「字符串加工」，把字面量带到真正的调用点再判定。
    /// </summary>
    public static bool IsStringConversionShim(string typeFullName, string methodName) =>
        methodName is "op_Implicit" or "op_Explicit"
        && (typeFullName.Contains("ImU8String", StringComparison.OrdinalIgnoreCase)
            || typeFullName.Contains("ImGui", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     这个调用是不是「把字符串当集合键名用」——翻它一定会破坏查找（本地化 key 就是这么被误翻的）。
    /// </summary>
    public static bool IsCollectionKeyCall(string typeFullName, string methodName) =>
        (typeFullName.Contains("Dictionary", StringComparison.Ordinal)
         || typeFullName.Contains("HashSet", StringComparison.Ordinal)
         || typeFullName.Contains("SortedSet", StringComparison.Ordinal)
         || typeFullName.Contains("KeyedCollection", StringComparison.Ordinal))
        && methodName is "get_Item" or "set_Item" or "ContainsKey" or "TryGetValue" or "TryAdd" or "Add" or "Remove";

    /// <summary>
    ///     判断「这个参数位置是不是字符串」时用的宽松版：泛型参数（<c>!0</c>/<c>!!0</c>）和 object 也算，
    ///     因为 <c>SortedDictionary&lt;TKey, TValue&gt;.get_Item(TKey)</c> 这种签名读出来是泛型参数，
    ///     严格判断会把「拿字符串当键名」整条漏掉（2026-10-01 的 key 误翻就是这么来的）。
    /// </summary>
    public static bool IsStringLikeOrGeneric(string typeName) =>
        typeName.Length == 0
        || typeName.Contains("String", StringComparison.Ordinal)
        || typeName.Contains("Char", StringComparison.Ordinal)
        || typeName.Contains("ReadOnlySpan", StringComparison.Ordinal)
        || typeName.StartsWith("!", StringComparison.Ordinal)
        || typeName is "System.Object";

    /// <summary>
    ///     对象初始化器 / 字段赋值里的「给人看的字符串」：目前只认命令帮助。
    /// </summary>
    public static string? DescribeUIField(string declaringType, string fieldName)
    {
        if (declaringType.Contains("CommandInfo", StringComparison.Ordinal) && fieldName is "HelpMessage")
        {
            return "CommandInfo.HelpMessage";
        }

        return null;
    }

    /// <summary>
    ///     属性 setter 版的「给人看的字段」：对象初始化器会编译成 <c>set_XXX</c> 调用。
    /// </summary>
    public static string? DescribeUISetter(string declaringType, string methodName)
    {
        if (methodName is "set_HelpMessage" && declaringType.Contains("CommandInfo", StringComparison.Ordinal))
        {
            return "CommandInfo.HelpMessage";
        }

        return null;
    }

    /// <summary>
    ///     是不是日志调用。
    /// </summary>
    public static bool IsLogCall(string typeFullName, string methodName)
    {
        if (typeFullName.Contains("Log", StringComparison.Ordinal))
        {
            return true;
        }

        if (typeFullName.StartsWith("Serilog", StringComparison.Ordinal))
        {
            return true;
        }

        return methodName is "WriteLine" or "Print" or "Information" or "Debug" or "Warning" or "Error" or "Verbose" or "Fatal";
    }

    /// <summary>
    ///     是不是「危险语境」——字符串进这里多半不是给人看的。
    /// </summary>
    public static bool IsDangerousCall(string typeFullName, string methodName)
    {
        if (IsLogCall(typeFullName, methodName))
        {
            return true;
        }

        if (IsStringProducer(typeFullName, methodName))
        {
            return false;
        }

        foreach (var prefix in DangerousTypePrefixes)
        {
            if (typeFullName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // 字典 / 集合当键用
        if (typeFullName.Contains("Dictionary", StringComparison.Ordinal)
            || typeFullName.Contains("HashSet", StringComparison.Ordinal)
            || typeFullName.Contains("SortedSet", StringComparison.Ordinal)
            || typeFullName.Contains("KeyedCollection", StringComparison.Ordinal))
        {
            return methodName is "Add" or "Remove" or "ContainsKey" or "TryGetValue" or "TryAdd" or "RemoveWhere" or "get_Item" or "set_Item";
        }

        // 命令名 / IPC 名 / 反射名：只拦真正会把字符串当标识符用的那几个入口，
        // 否则 `CommandInfo` 之类名字里带 Command 的类型会被整片误判
        if (typeFullName.Contains("IPc", StringComparison.OrdinalIgnoreCase)
            && methodName is "GetIpcProvider" or "GetIpcSubscriber" or "GetIpcCaller" or "Subscribe" or "Unsubscribe" or "Broadcast" or "InvokeFunc" or "InvokeAction")
        {
            return true;
        }

        if ((typeFullName.EndsWith("ICommandManager", StringComparison.Ordinal)
             || typeFullName.EndsWith("CommandManager", StringComparison.Ordinal))
            && methodName is "AddHandler" or "RemoveHandler")
        {
            return true;
        }

        if (methodName is "SendCommand" or "ProcessCommand")
        {
            return true;
        }

        // 字符串自身的比较与查找（扩展方法也会挂在 System.String 上）
        if (typeFullName is "System.String" or "System.MemoryExtensions")
        {
            return DangerousMethodNames.Contains(methodName);
        }

        return DangerousMethodNames.Contains(methodName) && typeFullName.StartsWith("System.", StringComparison.Ordinal);
    }
}
