using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>
/// 记住插件安装器列表的浏览位置 —— <b>不使用任何钩子</b>。
/// </summary>
/// <remarks>
/// 列表是安装器窗口里的一层子窗口（<c>InstallerCategories</c> → <c>ScrollingPlugins</c>），
/// 而 ImGui 不持久化 child window（实测 <c>dalamudUI.ini</c> 里没有它的记录），
/// 所以这里每帧把那个子窗口找出来、直接读/写它的 <c>Scroll</c>。
///
/// <para><b>只走函数调用，不读 ImGui 结构体</b>：早期版本用
/// <c>ImGui.GetCurrentContext().Windows</c> 遍历窗口，实测读出来 16553 个窗口（绑定与运行时
/// ImGui 的 <c>ImGuiContext</c> 偏移不一致 → 全是垃圾数据），所以改成只用
/// <c>ImGuiP.FindWindowByName</c> / <c>FindWindowByID</c> / <c>GetID</c> 这些函数调用。</para>
///
/// 找不到窗口就静默跳过——失效只会"记不住位置"，不会影响游戏、也不会碰别的插件。
/// </remarks>
internal sealed class InstallerListScroll
{
    /// <summary>安装器窗口的可能名字（卫月用的是 "插件安装器###XlPluginInstaller"；带 ### 的写法各版本可能不同）。</summary>
    private static readonly string[] InstallerNameCandidates =
    [
        "###XlPluginInstaller",
        "XlPluginInstaller",
        "插件安装器###XlPluginInstaller",
        "Plugin Installer###XlPluginInstaller",
    ];

    /// <summary>列表外层的分类子窗口（ImRaii.Child("InstallerCategories")）。</summary>
    private const string CategoriesChildId = "InstallerCategories";

    /// <summary>真正滚动的那层子窗口（ImRaii.Child("ScrollingPlugins")）。</summary>
    private const string ListChildId = "ScrollingPlugins";

    private bool wasOpen;
    private bool pendingRestore;
    private int restoreGraceFrames;
    private bool loggedStart;
    private bool loggedInstaller;
    private bool loggedList;

    private static bool loggedChain;
    private DateTime lastDump = DateTime.MinValue;
    private long frames;
    private DateTime lastHeartbeat = DateTime.UtcNow;

    /// <summary>每帧调用（挂在 <c>UiBuilder.Draw</c> 上）。</summary>
    public void Tick(Configuration config, Action saveConfig, Func<bool> isInstallerOpen)
    {
        try
        {
            if (!this.loggedStart)
            {
                this.loggedStart = true;
                Plugin.Log.Information("[FireGaze] 列表位置记忆已启动（每帧检查安装器窗口，不用钩子）");
            }

            this.TickCore(config, saveConfig);

            this.frames++;
            if ((DateTime.UtcNow - this.lastHeartbeat).TotalSeconds >= 60)
            {
                this.lastHeartbeat = DateTime.UtcNow;
                var probe = string.Join(
                    " / ",
                    InstallerNameCandidates.Select(c => c + "=" + (ImGuiP.FindWindowByName(c).IsNull ? "x" : "v")));
                Plugin.Log.Information(
                    $"[FireGaze] 列表位置记忆心跳：{this.frames} 帧；安装器窗口={(this.loggedInstaller ? "找到" : "未找到")}；列表窗口={(this.loggedList ? "找到" : "未找到")}；卫月说开着={isInstallerOpen()}；候选探测：{probe}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "[FireGaze] 列表位置记忆出错（本次忽略）");
        }
    }

    private void TickCore(Configuration config, Action saveConfig)
    {
        var installer = FindInstallerWindow(out var installerHit);
        if (installer.IsNull)
        {
            this.wasOpen = false;
            this.pendingRestore = false;
            this.DumpOnce();
            return;
        }

        if (!this.loggedInstaller)
        {
            this.loggedInstaller = true;
            var expectId = ImGuiP.ImHashStr("###XlPluginInstaller");
            Plugin.Log.Information($"[FireGaze] 找到安装器窗口：{WindowName(installer) ?? "(读不到名字)"}（候选命中：{installerHit}）");
            var stackSize = installer.IDStack.Size;
            uint stackBack = 0;
            unsafe { if (stackSize > 0) stackBack = *installer.IDStack.Back; }
            var idFromStack = ImGuiP.ImHashStr(CategoriesChildId, stackBack);
            Plugin.Log.Information(
                $"[FireGaze] IDStack 探针：Size={stackSize}；Back=0x{stackBack:X8}（窗口 ID=0x{installer.ID:X8}）"
                + $"；用它算 categories=0x{idFromStack:X8}（FindWindowByID={(ImGuiP.FindWindowByID(idFromStack).IsNull ? "未命中" : "命中")}）");
            Plugin.Log.Information(
                $"[FireGaze] 窗口结构自检：ID=0x{installer.ID:X8}（按名字算是 0x{expectId:X8}）"
                + $"；Scroll=({installer.Scroll.X:F0},{installer.Scroll.Y:F0})"
                + $"；ScrollMax=({installer.ScrollMax.X:F0},{installer.ScrollMax.Y:F0})"
                + $"；LastFrameActive={installer.LastFrameActive}（当前帧 {ImGui.GetFrameCount()}）");
        }

        // 安装器这帧有没有在画：LastFrameActive 每帧刷新（WasActive 关窗后会一直留着 true，不能用）
        var frame = ImGui.GetFrameCount();
        if (installer.LastFrameActive < frame - 1)
        {
            this.wasOpen = false;
            this.pendingRestore = false;
            return;
        }

        if (!this.loggedList)
        {
            var probe = FindListWindow(installer, out var listHow);
            if (!probe.IsNull)
            {
                this.loggedList = true;
                Plugin.Log.Information(
                    $"[FireGaze] 已找到安装器列表窗口：{WindowName(probe) ?? "(读不到名字)"}（方式：{listHow}）");
            }
            else
            {
                this.DumpOnce();
            }
        }

        if (!this.wasOpen)
        {
            this.wasOpen = true;
            this.pendingRestore = config.RememberListScroll && config.ListScrollY is not null;

            if (this.pendingRestore)
            {
                Plugin.Log.Information($"[FireGaze] 安装器已打开，准备恢复列表位置：{config.ListScrollY:F0}");
            }
        }

        var list = FindListWindow(installer, out _);
        if (list.IsNull)
        {
            return;
        }

        var scroll = list.Scroll.Y;
        var hasContent = list.ScrollMax.Y > 0f || scroll > 0f;

        if (this.pendingRestore)
        {
            if (config.ListScrollY is not { } target)
            {
                this.pendingRestore = false;
                return;
            }

            if (!hasContent)
            {
                return;   // 列表还在加载：现在写会被夹回 0
            }

            if (scroll > 1f)
            {
                this.pendingRestore = false;
                Plugin.Log.Information("[FireGaze] 列表已被手动滚动，放弃本次位置恢复");
                return;
            }

            list.Scroll = new Vector2(list.Scroll.X, target);
            this.pendingRestore = false;
            this.restoreGraceFrames = 2;
            Plugin.Log.Information($"[FireGaze] 已恢复列表浏览位置：{target:F0}");
            return;
        }

        if (!config.RememberListScroll || !hasContent)
        {
            return;
        }

        if (this.restoreGraceFrames > 0)
        {
            this.restoreGraceFrames--;
            return;
        }

        if (config.ListScrollY is { } current && Math.Abs(current - scroll) < 0.5f)
        {
            return;
        }

        config.ListScrollY = scroll;
        saveConfig();
    }

    /// <summary>按候选名字找安装器窗口（只调函数，不读结构体）。</summary>
    private static ImGuiWindowPtr FindInstallerWindow(out string? hit)
    {
        foreach (var candidate in InstallerNameCandidates)
        {
            var window = ImGuiP.FindWindowByName(candidate);
            if (!window.IsNull)
            {
                hit = candidate;
                return window;
            }
        }

        hit = null;
        return ImGuiWindowPtr.Null;
    }

    /// <summary>
    /// 找列表子窗口：把「可能的名字 × 可能的 seed」全试一遍，命中即用。
    /// （子窗口 ID = ImGui 在 BeginChild 时按「当前窗口 ID 栈 + 名字」算出来的哈希；
    /// 不同绑定/版本对 seed 的处理可能不同，所以这里穷举，并把命中的组合写进日志。）
    /// </summary>
    private static ImGuiWindowPtr FindListWindow(ImGuiWindowPtr installer, out string? how)
    {
        var installerId = ImGuiP.ImHashStr("###XlPluginInstaller");
        var seeds = new (string Label, uint Seed)[]
        {
            ("父ID种子", installerId),
            ("无种子", 0u),
            ("DalamudCore+父ID", ImGuiP.ImHashStr("DalamudCore", installerId)),
            ("DalamudCore", ImGuiP.ImHashStr("DalamudCore")),
        };

        var report = new List<string>();

        foreach (var categoriesName in new[] { CategoriesChildId, "###" + CategoriesChildId })
        {
            foreach (var (seedLabel, seed) in seeds)
            {
                var categoriesId = ImGuiP.ImHashStr(categoriesName, seed);
                var categories = ImGuiP.FindWindowByID(categoriesId);
                report.Add($"categories[{categoriesName}/{seedLabel}]={(categories.IsNull ? "x" : "v")}");
                if (categories.IsNull)
                {
                    continue;
                }

                foreach (var listName in new[] { ListChildId, "###" + ListChildId })
                {
                    foreach (var (seed2Label, seed2) in seeds)
                    {
                        var listId = ImGuiP.ImHashStr(listName, seed2);
                        var list = ImGuiP.FindWindowByID(listId);
                        report.Add($"list[{listName}/{seed2Label}]={(list.IsNull ? "x" : "v")}");
                        if (!list.IsNull)
                        {
                            LogChainOnce(report);
                            how = $"ID 探测命中：{categoriesName}/{seedLabel} → {listName}/{seed2Label}";
                            return list;
                        }
                    }
                }

                LogChainOnce(report);
                how = $"ID 探测命中（父级）：{categoriesName}/{seedLabel}（列表层未命中）";
                return ImGuiWindowPtr.Null;
            }
        }

        LogChainOnce(report);
        how = null;
        return ImGuiWindowPtr.Null;
    }

    private static void LogChainOnce(List<string> report)
    {
        if (loggedChain)
        {
            return;
        }

        loggedChain = true;
        Plugin.Log.Information("[FireGaze] 子窗口 ID 探测结果：" + string.Join(" ", report));
    }

    /// <summary>排查用：把 dalamudUI.ini 里的窗口名打出来（读文件，不碰 ImGui 结构体）。</summary>
    private void DumpOnce()
    {
        if ((DateTime.UtcNow - this.lastDump).TotalSeconds < 30)
        {
            return;
        }

        this.lastDump = DateTime.UtcNow;

        try
        {
            var ini = Path.GetFullPath(Path.Combine(Plugin.ConfigDirectoryForDiagnostics, "..", "..", "dalamudUI.ini"));
            if (!File.Exists(ini))
            {
                Plugin.Log.Information($"[FireGaze] 找不到 {ini}，无法列出窗口名");
                return;
            }

            var keys = File.ReadAllLines(ini)
                .Where(l => l.StartsWith("[Window][", StringComparison.Ordinal))
                .Select(l => l.Substring("[Window][".Length).TrimEnd(']'))
                .ToList();

            Plugin.Log.Information($"[FireGaze] 未找到安装器窗口；dalamudUI.ini 里共 {keys.Count} 个窗口，相关的是：");
            foreach (var key in keys.Where(k =>
                         k.Contains("Installer", StringComparison.OrdinalIgnoreCase) ||
                         k.Contains("插件", StringComparison.Ordinal) ||
                         k.Contains("Scrolling", StringComparison.OrdinalIgnoreCase)))
            {
                Plugin.Log.Information($"[FireGaze]   窗口: {key}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "[FireGaze] 列出窗口名失败");
        }
    }

    private static unsafe string? WindowName(ImGuiWindowPtr window)
    {
        var name = window.Name;
        return name == null ? null : Marshal.PtrToStringUTF8((nint)name);
    }
}
