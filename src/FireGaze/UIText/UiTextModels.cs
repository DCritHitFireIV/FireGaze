namespace FireGaze.UIText;

/// <summary>
///     一条字面量的判定结果。
/// </summary>
public enum UITextRole
{
    /// <summary>
    ///     只流向 UI 调用：默认认为可以翻译。
    /// </summary>
    UI,

    /// <summary>
    ///     既流向 UI 调用、又出现在比较 / 键名 / 日志之类的功能语境里：默认不翻，编辑器里要提示原因。
    /// </summary>
    Ambiguous,

    /// <summary>
    ///     明确不是 UI 文本（日志、命令、签名、键名……）：不进候选。
    /// </summary>
    Excluded,
}

/// <summary>
///     抽取出来的一条字符串字面量。
/// </summary>
public sealed class UITextEntry
{
    /// <summary>
    ///     原文（字面量的值）。
    /// </summary>
    public string Original { get; init; } = string.Empty;

    /// <summary>
    ///     代码位置：<c>类型.方法</c>。
    /// </summary>
    public string Context { get; init; } = string.Empty;

    /// <summary>
    ///     判定。
    /// </summary>
    public UITextRole Role { get; init; }

    /// <summary>
    ///     判定依据（人话，会显示在编辑器里）。
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    ///     这个字面量被 ImGui 当作控件 ID 用（Button / Selectable / TreeNode 之类）。
    ///     打补丁时要写成 <c>译文###原文</c>，把 ID 留在原文上，避免译文相同的控件互相撞 ID。
    /// </summary>
    public bool PreserveID { get; init; }
}

/// <summary>
///     从插件 DLL 内嵌的本地化资源容器（<c>.resources</c>）里读到的一条界面文字。
/// </summary>
/// <remarks>
///     和 <see cref="UITextEntry" />（<c>ldstr</c> 字面量）不同：资源条目的身份是「容器名 + key」，
///     原文是容器里的值。打补丁时按「容器 + key」把值改成译文，不去碰卫星程序集（官中优先）。
/// </remarks>
public sealed class UITextResourceItem
{
    /// <summary>资源容器名：<c>AutoHook.Resources.Localization.UIStrings.resources</c>。</summary>
    public string Container { get; init; } = string.Empty;

    /// <summary>容器里的 entry 名（查表 key）。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>当前的英文值。</summary>
    public string Value { get; init; } = string.Empty;
}

/// <summary>
///     自定义特性（Attribute）参数里的一条界面文字。
/// </summary>
/// <remarks>
///     身份用「值」本身（与字面量一致）：同一个值出现在多个特性里，译文共享。
///     例：ARSR 的 <c>[UIAttribute("Make /rotation Manual a toggle command.")]</c>、
///     SimpleTweaks 的 <c>[TweakName("Auto Lock Action Bars")]</c>。
/// </remarks>
public sealed class UITextAttributeItem
{
    /// <summary>宿主：<c>类型::成员</c>（只给人工核对用）。</summary>
    public string Owner { get; init; } = string.Empty;

    /// <summary>特性短名（<c>UIAttribute</c>）。</summary>
    public string Attribute { get; init; } = string.Empty;

    /// <summary>参数里的字符串值。</summary>
    public string Value { get; init; } = string.Empty;
}

/// <summary>
///     一次抽取的结果。
/// </summary>
public sealed class UITextExtraction
{
    /// <summary>
    ///     被抽取的程序集路径。
    /// </summary>
    public string AssemblyPath { get; init; } = string.Empty;

    /// <summary>
    ///     读不出来时的失败原因（加壳 / 加密 / 不是 .NET 程序集）。
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    ///     全部字面量（含排除的，编辑器里可以按判定筛选）。
    /// </summary>
    public List<UITextEntry> Entries { get; init; } = [];

    /// <summary>
    ///     从内嵌 <c>.resources</c> 容器里读到的界面文字（资源型本地化）。
    /// </summary>
    public List<UITextResourceItem> Resources { get; init; } = [];

    /// <summary>
    ///     自定义特性参数里的界面文字（ARSR 的 <c>UIAttribute</c>、SimpleTweaks 的 Tweak* 特性）。
    /// </summary>
    public List<UITextAttributeItem> Attributes { get; init; } = [];

    /// <summary>
    ///     这个插件里「界面文字放在本地化资源（.resx / ResourceManager）」的字面量个数。
    ///     这些是查表 key、不能翻（翻了查不到资源）；它们的值是 <see cref="Resources" />，那条通道翻值、不翻 key。
    /// </summary>
    public int ResourceKeyCount { get; init; }

    /// <summary>
    ///     UI 候选条数。
    /// </summary>
    public int UICount => Entries.Count(e => e.Role == UITextRole.UI);

    /// <summary>
    ///     灰名单条数（既像 UI 又像功能串）。
    /// </summary>
    public int AmbiguousCount => Entries.Count(e => e.Role == UITextRole.Ambiguous);

    /// <summary>
    ///     被判「已是中文」排除的条数（没东西可翻时用它把原因说清楚：不是没抽到，是本来就中文）。
    /// </summary>
    public int ChineseExcludedCount => Entries.Count(
        e => e.Role == UITextRole.Excluded && e.Reason.StartsWith("已是中文", StringComparison.Ordinal));
}
