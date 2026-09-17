using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>
/// 「记住安装器列表浏览位置」的实验版实现 + 诊断探针（<b>不使用任何钩子</b>）。
/// </summary>
/// <remarks>
/// 列表是安装器窗口里的一层子窗口（<c>InstallerCategories</c> → <c>ScrollingPlugins</c>），
/// ImGui 不持久化 child window（<c>dalamudUI.ini</c> 里没有它的记录），所以这里每帧把它找出来、
/// 直接读/写它的 <c>Scroll</c>。
///
/// <para>上一轮实测 <c>ImGuiContext.Windows</c> 读出 16553 个假窗口（结构体偏移错位），所以这一版
/// **先做布局自检**（用函数版的 <c>FrameCount</c> / <c>CurrentWindow</c> 跟结构体里的值对拍，
/// 两个都对得上才枚举），布局不可信时退回「名字 × seed」ID 穷举。</para>
///
/// <para>另外附带一个实验：重载仓库时把 <c>PluginManager.repoRefreshTask</c> 换成已完成 Task，
/// 让安装器不切到「正在加载插件…」，从而不再把列表（和滚动位置）顶掉。</para>
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

    /// <summary>列表外层的分类子窗口（ImRaii.Child("InstallerCategories")）。</summary>
    private const string CategoriesChildId = "InstallerCategories";

    /// <summary>真正滚动的那层子窗口（ImRaii.Child("ScrollingPlugins")）。</summary>
    private const string ListChildId = "ScrollingPlugins";

    // ------------------------------------------------------------------ 探针状态（页签显示用）

    /// <summary>ImGui 上下文结构体自检结论。</summary>
    public string CtxVerdict { get; private set; } = "尚未检查";

    /// <summary>结构体布局是否可信（FrameCount 与 CurrentWindow 都对得上）。</summary>
    public bool CtxLayoutOk { get; private set; }

    /// <summary>上下文里枚举到的窗口数（-1 = 还没查）。</summary>
    public int CtxWindowCount { get; private set; } = -1;

    /// <summary>这一帧真的在画的窗口数（其余是积压的旧窗口）。</summary>
    public int LiveWindows { get; private set; } = -1;

    /// <summary>列表子窗口的探测结论。</summary>
    public string ListVerdict { get; private set; } = "尚未检查";

    /// <summary>最近一帧读到的列表滚动值。</summary>
    public float LastScrollY { get; private set; }

    /// <summary>已屏蔽「重载把列表顶掉」的次数 / 帧数。</summary>
    public long MaskedRuns { get; private set; }

    public long MaskedFrames { get; private set; }

    /// <summary>屏蔽实验的当前状态。</summary>
    public string MaskVerdict { get; private set; } = "还没有重载在跑";

    /// <summary>窗口清单 dump 文件路径。</summary>
    public string DumpPath { get; private set; } = "—";

    // ------------------------------------------------------------------ 内部状态

    private bool ctxChecked;
    private bool dumpedThisOpen;
    private bool loggedListId;
    private bool wasOpen;
    private bool pendingRestore;
    private int restoreGraceFrames;
    private DateTime lastDump = DateTime.MinValue;
    private ImGuiWindowPtr listWindow = ImGuiWindowPtr.Null;

    // PluginManager 反射句柄（一次缓存）
    private static object? pluginManager;
    private static PropertyInfo? pluginsReadyProp;
    private static PropertyInfo? reposReadyProp;
    private static FieldInfo? repoRefreshTaskField;
    private static MethodInfo? reloadReposMethod;

    /// <summary>每帧调用（挂在 <c>UiBuilder.Draw</c> 上）。</summary>
    public void Tick(Configuration config, Action saveConfig, Func<bool> isInstallerOpen)
    {
        try
        {
            var ctx = ImGui.GetCurrentContext();

            if (!this.ctxChecked)
            {
                this.CheckContext(ctx);
            }

            // 安装器这帧有没有在画（LastFrameActive 每帧刷新；WasActive 关窗后会一直留着 true）
            var installer = FindInstallerWindow();
            var frame = ImGui.GetFrameCount();
            var open = !installer.IsNull && installer.LastFrameActive >= frame - 1;

            if (!open)
            {
                this.wasOpen = false;
                this.pendingRestore = false;
                this.listWindow = ImGuiWindowPtr.Null;
                this.dumpedThisOpen = false;
                return;
            }

            // 找列表子窗口：先看缓存，再枚举（只在结构体可信时），最后退回 ID 穷举
            var how = "—";
            var list = ImGuiWindowPtr.Null;

            if (!this.listWindow.IsNull && this.listWindow.LastFrameActive >= frame - 1)
            {
                list = this.listWindow;
                how = "缓存";
            }

            if (list.IsNull && this.CtxLayoutOk)
            {
                list = FindListWindowEnumerated(ctx, out how, out var liveCount);
                this.LiveWindows = liveCount;
            }

            if (list.IsNull)
            {
                list = FindListWindowByHashes(installer, out var hashHow);
                if (!list.IsNull)
                {
                    how = hashHow;
                }
            }

            if (!list.IsNull)
            {
                this.listWindow = list;
                this.ListVerdict = $"已找到（{how}）";
            }
            else
            {
                this.listWindow = ImGuiWindowPtr.Null;
                this.ListVerdict = this.CtxLayoutOk
                    ? "未找到（枚举 + ID 穷举都没命中）"
                    : "未找到（结构体不可信，只做了 ID 穷举）";
            }

            if (!this.dumpedThisOpen)
            {
                this.dumpedThisOpen = true;
                this.DumpWindows(ctx);
            }

            if (!this.listWindow.IsNull)
            {
                this.RememberScroll(config, saveConfig);
            }

            this.MaskReloadEffect(config.RememberListScroll, isInstallerOpen);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "[FireGaze] 安装器探针出错（本次忽略）");
        }
    }

    /// <summary>测试用：反射调一次 <c>PluginManager.ReloadAllReposAsync()</c>（真实重载仓库）。</summary>
    public bool TriggerRepoReload()
    {
        try
        {
            var manager = ResolvePluginManager();
            if (manager is null || reloadReposMethod is null)
            {
                return false;
            }

            _ = reloadReposMethod.Invoke(manager, null);
            Plugin.Log.Information("[FireGaze] 测试：已触发一次仓库重载");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "[FireGaze] 触发仓库重载失败");
            return false;
        }
    }

    // ------------------------------------------------------------------ 自检

    /// <summary>
    /// 结构体布局自检：用函数版的 FrameCount / CurrentWindow 跟结构体里的字段对拍。
    /// （Windows 字段夹在这两者之间，两个都对得上才认为枚举安全。）
    /// </summary>
    private unsafe void CheckContext(ImGuiContextPtr ctx)
    {
        try
        {
            var frameFunction = ImGui.GetFrameCount();
            var frameStruct = ctx.FrameCount;
            var currentFunction = ImGuiP.GetCurrentWindow();
            var currentStruct = ctx.CurrentWindow;

            var functionPointer = (nint)currentFunction.Handle;
            var structPointer = (nint)currentStruct.Handle;
            var framesOk = frameFunction == frameStruct;
            var currentOk = functionPointer == structPointer;
            this.CtxLayoutOk = framesOk && currentOk;
            this.CtxWindowCount = this.CtxLayoutOk ? ctx.Windows.Size : -1;

            this.CtxVerdict = this.CtxLayoutOk
                ? $"一致（帧 {frameFunction}；窗口数 {this.CtxWindowCount}）→ 可以安全枚举"
                : $"不一致（FrameCount 函数={frameFunction} / 结构体={frameStruct}；"
                  + $"CurrentWindow 函数=0x{functionPointer:x} / 结构体=0x{structPointer:x}）→ 不枚举";

            this.ctxChecked = true;
            Plugin.Log.Information($"[FireGaze] ImGui 结构体自检：{this.CtxVerdict}");
        }
        catch (Exception e)
        {
            this.ctxChecked = true;
            this.CtxLayoutOk = false;
            this.CtxVerdict = "自检失败：" + e.Message;
            Plugin.Log.Warning(e, "[FireGaze] ImGui 结构体自检失败");
        }
    }

    // ------------------------------------------------------------------ 找窗口

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

    /// <summary>枚举上下文里的窗口，找**活着的**列表子窗口（结构体可信时才调用）。</summary>
    /// <remarks>
    /// 两个坑都在这里处理：
    /// ① 子窗口的 ImGui 名字是「父窗口路径 + 名字 + _ID 后缀」（如
    ///    <c>插件安装器###XlPluginInstaller/InstallerCategories_514EEA57/ScrollingPlugins_70AB139F</c>），
    ///    所以只能用 Contains 匹配，不能用等号；
    /// ② 窗口表里会积压大量早就不画的旧窗口（实测 2 万+），必须用 LastFrameActive 挑出
    ///    「这一帧真的在画」的那个，否则会读到陈旧的 Scroll。
    /// </remarks>
    private static ImGuiWindowPtr FindListWindowEnumerated(ImGuiContextPtr ctx, out string how, out int liveCount)
    {
        how = "枚举（活窗口）";
        liveCount = 0;

        try
        {
            var size = ctx.Windows.Size;
            var frame = ImGui.GetFrameCount();

            for (var i = 0; i < size; i++)
            {
                var window = ctx.Windows[i];
                if (window.IsNull)
                {
                    continue;
                }

                // 先做廉价的「这帧在画吗」过滤，再读名字（否则要对 2 万多个窗口做字符串读取）
                if (window.LastFrameActive < frame - 1)
                {
                    continue;
                }

                liveCount++;

                var name = WindowName(window);
                if (name is null)
                {
                    continue;
                }

                if (name.Contains(ListChildId, StringComparison.Ordinal)
                    && !name.Contains("/plugin_child_", StringComparison.Ordinal))
                {
                    how = $"枚举命中（活窗口 {liveCount} 个中）";
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

    /// <summary>把「可能的名字 × 可能的 seed」全试一遍（结构体不可信时的退路）。</summary>
    private static ImGuiWindowPtr FindListWindowByHashes(ImGuiWindowPtr installer, out string how)
    {
        var installerId = ImGuiP.ImHashStr("###XlPluginInstaller");
        var seeds = new (string Label, uint Seed)[]
        {
            ("父ID种子", installerId),
            ("无种子", 0u),
            ("DalamudCore+父ID", ImGuiP.ImHashStr("DalamudCore", installerId)),
            ("DalamudCore", ImGuiP.ImHashStr("DalamudCore")),
        };

        foreach (var categoriesName in new[] { CategoriesChildId, "###" + CategoriesChildId })
        {
            foreach (var (seedLabel, seed) in seeds)
            {
                var categoriesId = ImGuiP.ImHashStr(categoriesName, seed);
                var categories = ImGuiP.FindWindowByID(categoriesId);
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
                        if (!list.IsNull)
                        {
                            how = $"ID 穷举 {categoriesName}/{seedLabel} → {listName}/{seed2Label}";
                            return list;
                        }
                    }
                }
            }
        }

        how = "—";
        return ImGuiWindowPtr.Null;
    }

    // ------------------------------------------------------------------ 位置记忆

    private void RememberScroll(Configuration config, Action saveConfig)
    {
        var list = this.listWindow;
        var scroll = list.Scroll.Y;
        var hasContent = list.ScrollMax.Y > 0f || scroll > 0f;

        this.LastScrollY = scroll;

        if (!this.loggedListId)
        {
            this.loggedListId = true;
            var parentId = list.ParentWindow.IsNull ? 0u : list.ParentWindow.ID;
            var computed = ImGuiP.ImHashStr(ListChildId, parentId);
            Plugin.Log.Information(
                $"[FireGaze] 列表子窗口：name={WindowName(list) ?? "?"} id=0x{list.ID:X8}"
                + $"；父 id=0x{parentId:X8}；hash(名字, 父id)=0x{computed:X8}（{(computed == list.ID ? "一致" : "不一致")}）"
                + $"；Scroll={scroll:F1}/{list.ScrollMax.Y:F1}");
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

    // ------------------------------------------------------------------ 防「重载顶飞」

    /// <summary>
    /// 实验：重载仓库期间把 <c>repoRefreshTask</c> 换成已完成 Task，让 <c>ReposReady</c> 保持 true，
    /// 安装器就不会切到「正在加载插件…」、列表子窗口也不会被 ImGui 回收 → 滚动位置得以保住。
    /// 只在「确实有一个重载在跑」时才动手。
    /// </summary>
    private void MaskReloadEffect(bool enabled, Func<bool> isInstallerOpen)
    {
        if (!enabled)
        {
            this.MaskVerdict = "关（上面的开关没勾时不做屏蔽，方便 A/B 对比）";
            return;
        }

        var manager = ResolvePluginManager();
        if (manager is null)
        {
            this.MaskVerdict = "拿不到 PluginManager";
            return;
        }

        var pluginsReady = pluginsReadyProp?.GetValue(manager) as bool? ?? false;
        var reposReady = reposReadyProp?.GetValue(manager) as bool? ?? false;

        if (pluginsReady && reposReady)
        {
            this.MaskVerdict = $"就绪（没有重载在跑；累计屏蔽 {this.MaskedRuns} 次）";
            return;
        }

        var task = repoRefreshTaskField?.GetValue(manager) as Task;
        if (task is null || task.IsCompleted)
        {
            this.MaskVerdict =
                $"未就绪但没有在跑的重载（PluginsReady={pluginsReady} ReposReady={reposReady}；"
                + $"卫月说安装器开着={isInstallerOpen()}）";
            return;
        }

        repoRefreshTaskField!.SetValue(manager, Task.CompletedTask);
        this.MaskedRuns++;
        this.MaskedFrames++;
        this.MaskVerdict = $"已屏蔽第 {this.MaskedRuns} 次重载（PluginsReady={pluginsReady} ReposReady={reposReady}）";
        Plugin.Log.Information($"[FireGaze] 已屏蔽一次重载对列表的影响（第 {this.MaskedRuns} 次）");
    }

    // ------------------------------------------------------------------ 诊断 dump

    /// <summary>把当前 ImGui 窗口清单写进文件（只在布局自检通过时做，避免读野指针）。</summary>
    private void DumpWindows(ImGuiContextPtr ctx)
    {
        if (!this.CtxLayoutOk)
        {
            Plugin.Log.Warning("[FireGaze] 结构体布局不可信，跳过窗口清单 dump");
            return;
        }

        try
        {
            var size = ctx.Windows.Size;
            var frame = ImGui.GetFrameCount();
            var lines = new List<string>(2048)
            {
                $"frame={frame} windows={size}（列表里会积压旧窗口，下面只写「这一帧还在画」的）",
            };

            var live = 0;
            for (var i = 0; i < size; i++)
            {
                var window = ctx.Windows[i];
                if (window.IsNull || window.LastFrameActive < frame - 1)
                {
                    continue;
                }

                live++;
                var name = WindowName(window) ?? "(unreadable)";
                var parent = window.ParentWindow;
                lines.Add(
                    $"[{i}] name={name} id=0x{window.ID:X8}"
                    + $" parent={(parent.IsNull ? "-" : WindowName(parent) ?? "?")}"
                    + $" parentId={(parent.IsNull ? 0u : parent.ID):X8}"
                    + $" scroll={window.Scroll.Y:F1}/{window.ScrollMax.Y:F1}"
                    + $" lastFrame={window.LastFrameActive}");
            }

            lines.Insert(1, $"live={live}");

            var path = Path.Combine(Plugin.ConfigDirectoryForDiagnostics, "installer-window-dump.txt");
            File.WriteAllLines(path, lines);
            this.DumpPath = path;
            Plugin.Log.Information($"[FireGaze] 活窗口 {live} 个 / 窗口表 {size} 个，已写进 {path}");

            foreach (var line in lines.Where(l =>
                         l.Contains("Installer", StringComparison.OrdinalIgnoreCase) ||
                         l.Contains("Scrolling", StringComparison.OrdinalIgnoreCase)))
            {
                Plugin.Log.Information("[FireGaze]   窗口：" + line);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "[FireGaze] dump 窗口清单失败");
        }
    }

    private static unsafe string? WindowName(ImGuiWindowPtr window)
    {
        var name = window.Name;
        return name == null ? null : Marshal.PtrToStringUTF8((nint)name);
    }

    // ------------------------------------------------------------------ PluginManager 反射

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
            reloadReposMethod = managerType.GetMethod("ReloadAllReposAsync", flags);
            return pluginManager;
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "[FireGaze] 解析 PluginManager 失败");
            return null;
        }
    }
}
