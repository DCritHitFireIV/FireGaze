using Dalamud.Configuration;

namespace FireGaze;

/// <summary>后台自动刷新的拦截模式。</summary>
public enum BlockMode
{
    /// <summary>只要不是卫月自己发起的刷新，一律拦掉（默认）。</summary>
    Always = 0,

    /// <summary>只在「插件安装器」窗口开着的时候拦（没在看列表就照常刷新）。</summary>
    InstallerOpenOnly = 1,

    /// <summary>关闭拦截。</summary>
    Off = 2,
}

/// <summary>某个字段的展示方式。</summary>
public enum DisplayMode
{
    /// <summary>英语原版。</summary>
    Original = 0,

    /// <summary>中文覆盖（无译文时回退原文）。</summary>
    Chinese = 1,

    /// <summary>双语：简介/详情为「中文 + 空行 + 原文」，名字为「中文 (English)」。</summary>
    Both = 2,
}

/// <summary>一条被撤销记录里的单个仓库。</summary>
public sealed class UndoEntry
{
    public string Url { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public int Index { get; set; }
}

/// <summary>一次「停用 / 删除」操作的撤销记录。</summary>
public sealed class UndoRecord
{
    /// <summary>disable 或 delete。</summary>
    public string Action { get; set; } = string.Empty;

    public DateTime TimeUtc { get; set; }

    public List<UndoEntry> Entries { get; set; } = [];

    public string? BackupPath { get; set; }

    public int Count => this.Entries.Count;

    /// <summary>给按钮/提示用的简短描述：类型 + 数量 + 时间。</summary>
    public string Describe()
    {
        var action = this.Action == "delete" ? "删除" : this.Action == "rename" ? "修正链接" : "停用";
        return $"{action} {this.Count} 个 · {this.TimeUtc.ToLocalTime():HH:mm}";
    }
}

/// <summary>插件配置。</summary>
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ---------------- 列表自动刷新拦截 ----------------

    public BlockMode BlockerMode { get; set; } = BlockMode.Off;

    public bool BlockerWriteLog { get; set; } = true;

    public int BlockedCount { get; set; }

    /// <summary>最近被拦下的来源（新的在前，最多 <see cref="MaxRecentBlocked"/> 条）。</summary>
    public List<string> RecentBlockedSources { get; set; } = [];

    public const int MaxRecentBlocked = 20;

    // ---------------- 简介汉化 ----------------

    public bool TranslateEnabled { get; set; }

    /// <summary>每两周自动从 GitHub 检查一次词表更新（汉化启用时才生效）。</summary>
    public bool AutoUpdateTable { get; set; } = true;

    /// <summary>上次自动检查词表的时间（UTC）。</summary>
    public DateTime LastTableUpdateCheckUtc { get; set; }

    public DisplayMode NameMode { get; set; } = DisplayMode.Original;

    public DisplayMode PunchlineMode { get; set; } = DisplayMode.Chinese;

    public DisplayMode DescriptionMode { get; set; } = DisplayMode.Both;

    // ---------------- 仓库体检 ----------------

    /// <summary>是否连已停用的库一起扫描（默认是）。</summary>
    public bool ScanIncludeDisabled { get; set; } = true;

    /// <summary>首次运行标记：安装本插件之前就存在的库没有时间记录。</summary>
    public bool RepoFirstRunDone { get; set; }

    /// <summary>仓库 URL → 首次被本插件看到的时间（ISO 8601 字符串，空字符串表示「安装前已有」）。</summary>
    public Dictionary<string, string> RepoFirstSeen { get; set; } = new(StringComparer.Ordinal);

    /// <summary>最近几次「停用 / 删除」操作，最新的在最后（最多 <see cref="MaxUndo"/> 条）。</summary>
    public List<UndoRecord> UndoHistory { get; set; } = [];

    public const int MaxUndo = 10;

    public DateTime LastScanUtc { get; set; }
}
