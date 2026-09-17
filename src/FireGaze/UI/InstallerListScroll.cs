using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>
/// 插件安装器增强（<b>全程不使用任何钩子</b>）：
/// ① 记住列表的浏览位置（下次打开接着看）；
/// ② 拦住自动刷新：重载仓库期间不让安装器把列表换成「正在加载插件…」，从而不把列表和位置顶掉。
/// </summary>
/// <remarks>
/// 列表是安装器窗口里的一层子窗口（<c>InstallerCategories</c> → <c>ScrollingPlugins</c>）。
/// 它有几个坑，都是实测踩出来的：
/// <list type="bullet">
/// <item>子窗口**不在** ImGui 的 <c>WindowsById</c> 表里，<c>FindWindowByID/FindWindowByName</c> 永远查不到 → 只能遍历窗口表；</item>
/// <item>子窗口的 ImGui 名字是「父窗口路径 + 名字 + _ID 后缀」（如
/// <c>插件安装器###XlPluginInstaller/InstallerCategories_514EEA57/ScrollingPlugins_70AB139F</c>）→ 只能用 Contains 匹配；</item>
/// <item>窗口表里会积压大量早就没在画的旧窗口（实测 2 万+）→ 必须用 <c>LastFrameActive</c> 挑出「这帧真的在画」的那个，
/// 否则会读到陈旧的滚动值。</item>
/// </list>
/// 写滚动位置用 ImGui 自己的语义（<c>ImGui::SetScrollY()</c> 的写法：设 <c>ScrollTarget</c> + 居中比 0 + 边缘吸附 0），
/// 并连写几帧——因为列表子窗口刚出现时 ImGui 会先把滚动清零一次。
/// </remarks>
internal sealed class InstallerListScroll
{
    /// <summary>安装器窗口的可能名字（卫月用的是 "插件安装器###XlPluginInstaller"）。</summary>
    private static readonly string[] InstallerNameCandidates =
    [
        "###XlPluginInstaller",
        "插件安装器###XlPluginInstaller",
        "Plugin Installer###XlPluginInstaller",
        "XlPluginInstaller",
    ];

    /// <summary>真正滚动的那层子窗口（ImRaii.Child("ScrollingPlugins")）。</summary>
    private const string ListChildId = "ScrollingPlugins";

    /// <summary>连续多少帧没画才算安装器关掉了（折叠/切窗会让个别帧画不到）。</summary>
    private const int CloseAfterMissedFrames = 5;

    /// <summary>恢复时允许的偏差（像素）。</summary>
    private const float RestoreTolerance = 30f;

    /// <summary>最多连写多少帧。</summary>
    private const int RestoreMaxFrames = 30;

    /// <summary>写够这么多帧且没跑偏就算恢复完成。</summary>
    private const int RestoreSettleFrames = 6;

    /// <summary>位置恢复的小状态机。</summary>
    private enum RestorePhase
    {
        /// <summary>不恢复（没存过 / 开关关了）。</summary>
        Idle,

        /// <summary>已下令恢复，等列表把内容铺开（ScrollMax &gt; 0）。</summary>
        WaitingContent,

        /// <summary>正在写 ScrollTarget，直到被 ImGui 采纳。</summary>
        Applying,

        /// <summary>已到位。</summary>
        Done,

        /// <summary>写不进去（被 ImGui 重置 / 被用户接管）。</summary>
        GivenUp,
    }

    // ---------------- 运行状态 ----------------

    private bool layoutChecked;
    private bool layoutOk;
    private bool installerWasOpen;
    private bool sawListContent;
    private bool loggedFind;
    private bool loggedMask;
    private int missedFrames;
    private int restoreGraceFrames;
    private long maskedRuns;
    private DateTime lastMaskUtc;
    private ImGuiWindowPtr listWindow = ImGuiWindowPtr.Null;
    private RestorePhase phase = RestorePhase.Idle;
    private float restoreTarget;
    private int restoreFrames;
    private int offTargetFrames;

    // PluginManager 反射句柄（一次缓存）
    private static object? pluginManager;
    private static PropertyInfo? pluginsReadyProp;
    private static PropertyInfo? reposReadyProp;
    private static FieldInfo? repoRefreshTaskField;

    /// <summary>每帧调用（挂在 <c>UiBuilder.Draw</c> 上）。</summary>
    public void Tick(Configuration config, Action saveConfig)
    {
        try
        {
            var ctx = ImGui.GetCurrentContext();
            if (!this.layoutChecked)
            {
                this.CheckLayout(ctx);
            }

            var installer = FindInstallerWindow();
            var frame = ImGui.GetFrameCount();
            var open = !installer.IsNull && installer.LastFrameActive >= frame - 1;

            if (!open)
            {
                if (++this.missedFrames >= CloseAfterMissedFrames)
                {
                    this.OnClosed();
                }

                return;
            }

            this.missedFrames = 0;

            if (!this.installerWasOpen)
            {
                this.installerWasOpen = true;
                this.sawListContent = false;
                this.restoreFrames = 0;
                this.offTargetFrames = 0;
                this.phase = RestorePhase.Idle;

                if (config.RememberListScroll && config.ListScrollY is { } saved && saved > 1f)
                {
                    this.restoreTarget = saved;
                    this.phase = RestorePhase.WaitingContent;
                }
            }

            var list = this.FindListWindow(ctx, frame);
            if (!list.IsNull)
            {
                this.listWindow = list;
                this.HandleListWindow(config, saveConfig);
            }

            // 列表铺开过、安装器还开着 → 这时候才值得拦自动刷新（否则会把「正在加载插件…」也藏掉）
            if (config.BlockInstallerAutoRefresh && this.sawListContent)
            {
                this.KeepListWhileReloading();
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "[FireGaze] 安装器增强出错（本次忽略）");
        }
    }

    /// <summary>窗口关掉（或连续多帧没画）时复位状态。</summary>
    private void OnClosed()
    {
        this.installerWasOpen = false;
        this.sawListContent = false;
        this.listWindow = ImGuiWindowPtr.Null;
        this.phase = RestorePhase.Idle;
        this.restoreFrames = 0;
        this.offTargetFrames = 0;
    }

    // ------------------------------------------------------------------ 找回那个子窗口

    /// <summary>按候选名字找安装器主窗口。</summary>
    private static ImGuiWindowPtr FindInstallerWindow()
    {
        foreach (var candidate in InstallerNameCandidates)
        {
            var window = ImGuiP.FindWindowByName(candidate);
            if (!window.IsNull)
            {
                return window;
            }
        }

        return ImGuiWindowPtr.Null;
    }

    /// <summary>拿列表子窗口：先用缓存（每帧验证一次“还活着”），失效了就重新枚举。</summary>
    private ImGuiWindowPtr FindListWindow(ImGuiContextPtr ctx, int frame)
    {
        if (!this.listWindow.IsNull && this.listWindow.LastFrameActive >= frame - 1)
        {
            return this.listWindow;
        }

        this.listWindow = ImGuiWindowPtr.Null;

        if (!this.layoutOk)
        {
            return ImGuiWindowPtr.Null;
        }

        var found = EnumerateListWindow(ctx, frame);
        if (!found.IsNull && !this.loggedFind)
        {
            this.loggedFind = true;
            Plugin.Log.Debug($"[FireGaze] 已找到安装器列表子窗口：{WindowName(found) ?? "?"}");
        }

        return found;
    }

    /// <summary>遍历 ImGui 窗口表，找这帧真在画的列表子窗口。</summary>
    private static ImGuiWindowPtr EnumerateListWindow(ImGuiContextPtr ctx, int frame)
    {
        try
        {
            var size = ctx.Windows.Size;
            for (var i = 0; i < size; i++)
            {
                var window = ctx.Windows[i];
                if (window.IsNull || window.LastFrameActive < frame - 1)
                {
                    continue;   // 廉价的活窗口过滤（表里积压着两万多个旧窗口）
                }

                var name = WindowName(window);
                if (name is not null
                    && name.Contains(ListChildId, StringComparison.Ordinal)
                    && !name.Contains("/plugin_child_", StringComparison.Ordinal))
                {
                    return window;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "[FireGaze] 枚举 ImGui 窗口失败");
        }

        return ImGuiWindowPtr.Null;
    }

    // ------------------------------------------------------------------ 位置记忆

    private void HandleListWindow(Configuration config, Action saveConfig)
    {
        var list = this.listWindow;
        var scroll = list.Scroll.Y;
        var max = list.ScrollMax.Y;
        var hasContent = max > 0f || scroll > 0f;

        if (hasContent)
        {
            this.sawListContent = true;
        }

        switch (this.phase)
        {
            case RestorePhase.WaitingContent:
                if (!hasContent)
                {
                    return;   // 列表还在加载：现在写会被夹回 0，等它把内容铺开
                }

                this.phase = RestorePhase.Applying;
                goto case RestorePhase.Applying;

            case RestorePhase.Applying:
                if (!hasContent)
                {
                    return;
                }

                // 写进去之后又跑回来了，而且连续两帧都跑 → 放弃（用户自己滚了，或 ImGui 不买账）
                if (this.restoreFrames > 0 && Math.Abs(scroll - this.restoreTarget) > RestoreTolerance)
                {
                    if (++this.offTargetFrames >= 2)
                    {
                        this.phase = RestorePhase.GivenUp;
                        Plugin.Log.Information($"[FireGaze] 放弃恢复列表位置：写入后回到 {scroll:F0}（目标 {this.restoreTarget:F0}）");
                        return;
                    }
                }
                else
                {
                    this.offTargetFrames = 0;
                }

                // ImGui::SetScrollY() 的写法：ScrollTarget + 居中比 0 + 边缘吸附 0，并连写几帧
                list.ScrollTarget = new Vector2(list.ScrollTarget.X, this.restoreTarget);
                list.ScrollTargetCenterRatio = new Vector2(list.ScrollTargetCenterRatio.X, 0f);
                list.ScrollTargetEdgeSnapDist = new Vector2(list.ScrollTargetEdgeSnapDist.X, 0f);
                list.Scroll = new Vector2(list.Scroll.X, this.restoreTarget);
                this.restoreFrames++;

                if (this.restoreFrames >= RestoreSettleFrames && Math.Abs(scroll - this.restoreTarget) <= RestoreTolerance)
                {
                    this.phase = RestorePhase.Done;
                    this.restoreGraceFrames = 3;
                    Plugin.Log.Information($"[FireGaze] 已恢复列表浏览位置：{scroll:F0}");
                }
                else if (this.restoreFrames >= RestoreMaxFrames)
                {
                    this.phase = RestorePhase.GivenUp;
                    Plugin.Log.Information($"[FireGaze] 恢复列表位置超时：停在 {scroll:F0}（目标 {this.restoreTarget:F0}）");
                }

                return;

            default:
                break;   // Idle / Done / GivenUp → 记录
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

    // ------------------------------------------------------------------ 拦住自动刷新

    /// <summary>
    /// 重载仓库期间把 <c>PluginManager.repoRefreshTask</c> 换成已完成 Task，让 <c>ReposReady</c> 保持 true：
    /// 安装器就不会切到「正在加载插件…」，列表子窗口不会被 ImGui 回收，滚动位置也就保住了。
    /// 重载本身照常在后台跑（数据照常更新），我们只是不让它把 UI 掀翻。
    /// 只在「确实有一个重载在跑」时才动手。
    /// </summary>
    private void KeepListWhileReloading()
    {
        var manager = ResolvePluginManager();
        if (manager is null || pluginsReadyProp is null || reposReadyProp is null || repoRefreshTaskField is null)
        {
            return;
        }

        var pluginsReady = pluginsReadyProp.GetValue(manager) as bool? ?? false;
        var reposReady = reposReadyProp.GetValue(manager) as bool? ?? false;
        if (pluginsReady && reposReady)
        {
            return;   // 没有重载在跑
        }

        if (repoRefreshTaskField.GetValue(manager) is not Task { IsCompleted: false })
        {
            return;   // 重载任务为空或已完成——不插手
        }

        try
        {
            repoRefreshTaskField.SetValue(manager, Task.CompletedTask);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "[FireGaze] 拦住自动刷新失败");
            return;
        }

        this.maskedRuns++;
        this.lastMaskUtc = DateTime.Now;
        if (!this.loggedMask)
        {
            this.loggedMask = true;
            Plugin.Log.Information("[FireGaze] 已拦住一次自动刷新（重载照常进行，只是不再把列表顶掉）");
        }
        else
        {
            Plugin.Log.Debug($"[FireGaze] 已拦住第 {this.maskedRuns} 次自动刷新");
        }
    }

    /// <summary>给设置页用的一行状态（用户可见）。</summary>
    public string StatusText()
    {
        if (this.layoutChecked && !this.layoutOk)
        {
            return "本次已停用（与当前卫月版本不兼容，详见日志）";
        }

        if (this.phase == RestorePhase.GivenUp)
        {
            return "已生效 · 上次打开时的列表位置没能恢复（详见日志）";
        }

        return this.maskedRuns > 0
            ? $"已生效 · 本次已拦下 {this.maskedRuns} 次自动刷新（最近 {this.lastMaskUtc:HH:mm}）"
            : "已生效 · 本会话还没遇到后台自动刷新";
    }

    // ------------------------------------------------------------------ 结构体自检 / 反射

    /// <summary>
    /// 布局自检：用函数版的 FrameCount / CurrentWindow 跟结构体里的字段对拍。
    /// （<c>Windows</c> 字段夹在这两者之间，两个都对得上才敢遍历；对不上就整个不碰，
    /// 免得读到野指针把游戏带崩。）正常情况下静默，对不上才记一条警告。
    /// </summary>
    private unsafe void CheckLayout(ImGuiContextPtr ctx)
    {
        this.layoutChecked = true;

        try
        {
            var currentFunction = ImGuiP.GetCurrentWindow();
            var currentStruct = ctx.CurrentWindow;
            var match = ImGui.GetFrameCount() == ctx.FrameCount
                        && (nint)currentFunction.Handle == (nint)currentStruct.Handle;

            this.layoutOk = match;

            if (!match)
            {
                Plugin.Log.Warning(
                    "[FireGaze] ImGui 结构体布局与预期不一致（可能是卫月/ImGui 升级了），"
                    + "本次会话不再遍历窗口：位置记忆与自动刷新拦截将自动停用。");
            }
        }
        catch (Exception e)
        {
            this.layoutOk = false;
            Plugin.Log.Warning(e, "[FireGaze] ImGui 结构体自检失败");
        }
    }

    private static unsafe string? WindowName(ImGuiWindowPtr window)
    {
        var name = window.Name;
        return name == null ? null : Marshal.PtrToStringUTF8((nint)name);
    }

    private static object? ResolvePluginManager()
    {
        if (pluginManager is not null)
        {
            return pluginManager;
        }

        try
        {
            var dalamud = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;
            var serviceOpen = dalamud.GetType("Dalamud.Service`1", throwOnError: false);
            var managerType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: false);
            if (serviceOpen is null || managerType is null)
            {
                return null;
            }

            var get = serviceOpen.MakeGenericType(managerType).GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
            pluginManager = get?.Invoke(null, null);
            if (pluginManager is null)
            {
                return null;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            pluginsReadyProp = managerType.GetProperty("PluginsReady", flags);
            reposReadyProp = managerType.GetProperty("ReposReady", flags);
            repoRefreshTaskField = managerType.GetField("repoRefreshTask", flags);
            return pluginManager;
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "[FireGaze] 解析 PluginManager 失败");
            return null;
        }
    }
}
