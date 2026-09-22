using Dalamud.Configuration;
using Newtonsoft.Json;

namespace FireGaze;

/// <summary>
///     某个字段的展示方式。
/// </summary>
public enum DisplayMode
{
    /// <summary>
    ///     英语原版。
    /// </summary>
    Original = 0,

    /// <summary>
    ///     中文覆盖（无译文时回退原文）。
    /// </summary>
    Chinese = 1,

    /// <summary>
    ///     双语：简介/详情为「中文 + 空行 + 原文」，名字为「中文 (English)」。
    /// </summary>
    Both = 2,
}

/// <summary>
///     一条被撤销记录里的单个仓库。
/// </summary>
public sealed class UndoEntry
{
    [JsonProperty("Url")]
    public string URL { get; set; } = string.Empty;

    [JsonProperty("IsEnabled")]
    public bool IsEnabled { get; set; }

    [JsonProperty("Index")]
    public int Index { get; set; }
}

/// <summary>
///     一次「停用 / 删除」操作的撤销记录。
/// </summary>
public sealed class UndoRecord
{
    /// <summary>
    ///     disable 或 delete。
    /// </summary>
    [JsonProperty("Action")]
    public string Action { get; set; } = string.Empty;

    [JsonProperty("TimeUtc")]
    public DateTime TimeUTC { get; set; }

    [JsonProperty("Entries")]
    public List<UndoEntry> Entries { get; set; } = [];

    [JsonProperty("BackupPath")]
    public string? BackupPath { get; set; }

    public int Count => Entries.Count;

    /// <summary>
    ///     给按钮/提示用的简短描述：类型 + 数量 + 时间。
    /// </summary>
    public string Describe()
    {
        var action = Action == "delete" ? "删除"
            : Action == "rename" ? "修正链接"
            : Action == "add" ? "添加"
            : "停用";
        return $"{action} {Count} 个 · {TimeUTC.ToLocalTime():HH:mm}";
    }
}

/// <summary>
///     插件配置。
///     落盘属性全部显式标注 <see cref="JsonPropertyAttribute"/>：C# 属性名可以改（如 DR0009 缩写对齐），
///     JSON 键名必须稳定——老用户更新后要能读回之前的设置。
/// </summary>
public sealed class Configuration : IPluginConfiguration
{
    [JsonProperty("Version")]
    public int Version { get; set; } = 1;

    // ---------------- 插件安装器（位置记忆 + 拦住自动刷新） ----------------

    /// <summary>
    ///     是否记住插件安装器列表的浏览位置（下次打开接着看）。默认关，由用户自己打开。
    /// </summary>
    [JsonProperty("RememberListScroll")]
    public bool RememberListScroll { get; set; }

    /// <summary>
    ///     上次离开时列表的滚动位置（像素）。
    /// </summary>
    [JsonProperty("ListScrollY")]
    public float? ListScrollY { get; set; }

    /// <summary>
    ///     防止打开插件管理器时自动刷新（刷新照常跑，只是不再把列表和位置顶掉）。默认关。
    /// </summary>
    [JsonProperty("BlockInstallerAutoRefresh")]
    public bool BlockInstallerAutoRefresh { get; set; }

    // ---------------- 简介汉化 ----------------

    [JsonProperty("TranslateEnabled")]
    public bool TranslateEnabled { get; set; }

    /// <summary>
    ///     每两周自动从 GitHub 检查一次词表更新（汉化启用时才生效）。
    /// </summary>
    [JsonProperty("AutoUpdateTable")]
    public bool AutoUpdateTable { get; set; } = true;

    /// <summary>
    ///     上次自动检查词表的时间（UTC）。
    /// </summary>
    [JsonProperty("LastTableUpdateCheckUtc")]
    public DateTime LastTableUpdateCheckUTC { get; set; }

    /// <summary>
    ///     上次真的把词表换成新版的本地时间（自动检查或手动都算）。
    /// </summary>
    [JsonProperty("LastTableUpdateUtc")]
    public DateTime LastTableUpdateUTC { get; set; }

    [JsonProperty("NameMode")]
    public DisplayMode NameMode { get; set; } = DisplayMode.Original;

    [JsonProperty("PunchlineMode")]
    public DisplayMode PunchlineMode { get; set; } = DisplayMode.Chinese;

    [JsonProperty("DescriptionMode")]
    public DisplayMode DescriptionMode { get; set; } = DisplayMode.Both;

    // ---------------- 参与翻译 ----------------

    /// <summary>
    ///     「参与翻译」窗口是否在列表里显示插件图标。默认关；只显示本地已缓存的，不会为此下载。
    /// </summary>
    [JsonProperty("ShowIconsInContribute")]
    public bool ShowIconsInContribute { get; set; }

    /// <summary>
    ///     译文提交的推送地址（server3 酱）：玩家点「一键提交」时把这一批译文直接推给维护者。
    ///     留空则不发推送（只能导出文件自己发）。
    /// </summary>
    // 旧版本这里存的是 server3 推送地址（含密钥）。现在密钥只在仓库 secret 里，
    // 由工作流在收到 GitHub issue 后通知维护者；客户端不再持有任何推送凭据。
    [JsonProperty("PushUrl")]
    public string PushURL { get; set; } = string.Empty;

    /// <summary>
    ///     「参与翻译」是否连已停用仓库里的插件一起列出（默认是）。
    /// </summary>
    [JsonProperty("ContributeShowDisabled")]
    public bool ContributeShowDisabled { get; set; } = true;

    // ---------------- 仓库体检 ----------------

    /// <summary>
    ///     是否连已停用的库一起扫描（默认是）。
    /// </summary>
    [JsonProperty("ScanIncludeDisabled")]
    public bool ScanIncludeDisabled { get; set; } = true;
    /// <summary>
    ///     仓库体检：是否在每条库链下面展开「本机已装插件」的图标（默认关，打开后列表每行会变高）。
    /// </summary>
    [JsonProperty("ShowInstalledIcons")]
    public bool ShowInstalledIcons { get; set; }

    /// <summary>
    ///     图标落盘缓存（默认开）：下载的图标存本地、重开游戏不重下，并分批注入回卫月的图标缓存。
    ///     关掉后回到「只用卫月内存缓存」的旧行为（排查问题时可以一键关掉）。
    /// </summary>
    [JsonProperty("IconCacheEnabled")]
    public bool IconCacheEnabled { get; set; } = true;

    /// <summary>
    ///     首次运行标记：安装本插件之前就存在的库没有时间记录。
    /// </summary>
    [JsonProperty("RepoFirstRunDone")]
    public bool RepoFirstRunDone { get; set; }

    /// <summary>
    ///     仓库 URL → 首次被本插件看到的时间（ISO 8601 字符串，空字符串表示「安装前已有」）。
    /// </summary>
    [JsonProperty("RepoFirstSeen")]
    public Dictionary<string, string> RepoFirstSeen { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     最近几次「停用 / 删除」操作，最新的在最后（最多 <see cref="MaxUndo"/> 条）。
    /// </summary>
    [JsonProperty("UndoHistory")]
    public List<UndoRecord> UndoHistory { get; set; } = [];

    public const int MaxUndo = 10;

    [JsonProperty("LastScanUtc")]
    public DateTime LastScanUTC { get; set; }
}
