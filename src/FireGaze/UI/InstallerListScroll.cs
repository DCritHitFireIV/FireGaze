using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>
/// 记住插件安装器列表的浏览位置 —— <b>不使用任何钩子</b>。
/// </summary>
/// <remarks>
/// 列表是安装器窗口里的一层子窗口（<c>InstallerCategories</c> → <c>ScrollingPlugins</c>），
/// 而 ImGui 不持久化 child window（实测 <c>dalamudUI.ini</c> 里只有父窗口 <c>###XlPluginInstaller</c>），
/// 所以这里每帧从 ImGui 里把那层子窗口找出来，直接读/写它的 <c>Scroll</c>：
///   · 打开安装器时（窗口从"没在画"变成"在画"）记下待恢复标记；
///   · 等列表内容出来（仓库重载期间列表会被「正在加载插件仓库中…」替掉，此时没有内容），
///     且用户还没自己滚过，就把上次的位置写回去；
///   · 平时每帧读当前位置存进配置（内存每帧、落盘走 SaveConfig 的节流）。
/// 全程只用 Dalamud 公开的 ImGui 绑定（<c>ImGuiP</c>），找不到窗口就静默跳过——
/// 失效只会"记不住位置"，不会影响游戏、也不会碰别的插件。
/// </remarks>
internal sealed class InstallerListScroll
{
    /// <summary>安装器窗口名里的稳定标记（"插件安装器###XlPluginInstaller"，### 之后那段跨语言固定）。</summary>
    private const string InstallerWindowMark = "###XlPluginInstaller";

    /// <summary>列表外层的分类子窗口。</summary>
    private const string CategoriesChildId = "InstallerCategories";

    /// <summary>真正滚动的那层子窗口（名字里带这个就跑不掉）。</summary>
    private const string ListChildId = "ScrollingPlugins";

    private bool wasOpen;
    private bool pendingRestore;
    private int restoreGraceFrames;
    private bool warnedLookup;
    private bool loggedLookup;
    private bool loggedStart;
    private long frames;
    private DateTime lastHeartbeat = DateTime.UtcNow;
    private DateTime lastDump = DateTime.MinValue;
    private int lastWindowCount = -1;
    private bool sawInstallerWindow;
    private bool loggedInstaller;

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

            this.TickCore(config, saveConfig, isInstallerOpen);

            // 心跳：证明"还在跑"，并暴露我们这一帧能看到多少个 ImGui 窗口
            this.frames++;
            if ((DateTime.UtcNow - this.lastHeartbeat).TotalSeconds >= 60)
            {
                this.lastHeartbeat = DateTime.UtcNow;
                var n = ImGui.GetCurrentContext().Windows.Size;
                Plugin.Log.Information(
                    $"[FireGaze] 列表位置记忆心跳：{this.frames} 帧；本帧可见 ImGui 窗口 {n} 个；找到安装器窗口={(this.sawInstallerWindow ? "是" : "否")}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "[FireGaze] 列表位置记忆出错（本次忽略）");
        }
    }

    private void TickCore(Configuration config, Action saveConfig, Func<bool> isInstallerOpen)
    {
        var installer = FindInstallerWindow();
        if (installer.IsNull)
        {
            this.wasOpen = false;
            this.pendingRestore = false;

            // 窗口数一变（多半就是安装器刚被画出来 / 别的插件开窗）就 dump 一次——
            // 不再依赖 IsPluginInstallerOpen 那个反射，免得它不灵就没日志。
            var windowCount = ImGui.GetCurrentContext().Windows.Size;
            if (windowCount != this.lastWindowCount)
            {
                this.lastWindowCount = windowCount;

                if ((DateTime.UtcNow - this.lastDump).TotalSeconds >= 30)
                {
                    this.lastDump = DateTime.UtcNow;
                    Plugin.Log.Information(
                        $"[FireGaze] 窗口数变成 {windowCount} 个但仍没找到安装器窗口（卫月说开着={isInstallerOpen()})，清单如下：");
                    DumpWindows();
                }
            }

            return;
        }

        // 安装器这帧有没有在画：看 LastFrameActive（每帧刷新），不要用 WasActive——
        // 窗口关掉之后 WasActive 会一直留着 true，那样就检测不到「下一次打开」。
        var frame = ImGui.GetFrameCount();
        var drawnThisFrame = installer.LastFrameActive >= frame - 1;
        if (!drawnThisFrame)
        {
            this.wasOpen = false;
            this.pendingRestore = false;
            return;
        }

        this.sawInstallerWindow = true;

        if (!this.loggedInstaller)
        {
            this.loggedInstaller = true;
            Plugin.Log.Information($"[FireGaze] 找到安装器窗口：{WindowName(installer) ?? "(名字读不到)"}");
        }

        if (!this.wasOpen)
        {
            this.wasOpen = true;
            this.pendingRestore = config.RememberListScroll && config.ListScrollY is not null;

            if (this.pendingRestore)
            {
                Plugin.Log.Debug($"[FireGaze] 安装器已打开，准备恢复列表位置：{config.ListScrollY:F0}");
            }
        }

        var list = FindListWindow(installer);
        if (list.IsNull)
        {
            if (!this.warnedLookup)
            {
                this.warnedLookup = true;
                Plugin.Log.Information("[FireGaze] 暂未找到安装器列表子窗口，下面是当前 ImGui 窗口清单（供排查）：");
                DumpWindows();
            }

            return;
        }

        if (!this.loggedLookup)
        {
            this.loggedLookup = true;
            Plugin.Log.Information($"[FireGaze] 已找到安装器列表窗口：{WindowName(list) ?? "(名字读不到)"}");
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
                // 列表还在加载（或本来就放得下）→ 现在写会被夹回 0，等内容出来再说
                return;
            }

            if (scroll > 1f)
            {
                // 用户自己先滚了：不抢
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

    /// <summary>
    /// 找列表子窗口：主路径按 ID 逐层算（ImGui 的 child ID = 父窗口 GetID(名字)），
    /// 兜底按名字扫一遍当前上下文里的所有窗口。
    /// </summary>
    /// <summary>按窗口名找安装器窗口（"…###XlPluginInstaller"）。</summary>
    private static ImGuiWindowPtr FindInstallerWindow()
    {
        foreach (var window in ImGui.GetCurrentContext().Windows)
        {
            if (window.IsNull)
            {
                continue;
            }

            var name = WindowName(window);
            if (name is null)
            {
                continue;
            }

            if (name.Contains(InstallerWindowMark, StringComparison.Ordinal) ||
                name.Contains("XlPluginInstaller", StringComparison.Ordinal))
            {
                return window;
            }
        }

        return ImGuiWindowPtr.Null;
    }

    /// <summary>按窗口名找列表子窗口（名字里带 ScrollingPlugins，且属于安装器那棵树）。</summary>
    private static ImGuiWindowPtr FindListWindow(ImGuiWindowPtr installer)
    {
        var installerName = WindowName(installer);
        ImGuiWindowPtr loose = ImGuiWindowPtr.Null;

        foreach (var window in ImGui.GetCurrentContext().Windows)
        {
            if (window.IsNull)
            {
                continue;
            }

            var name = WindowName(window);
            if (name is null || !name.Contains(ListChildId, StringComparison.Ordinal))
            {
                continue;
            }

            // 名字形如 "<安装器窗口名>/InstallerCategories/ScrollingPlugins_XXXXXXXX"
            if (installerName is not null && name.StartsWith(installerName, StringComparison.Ordinal))
            {
                return window;
            }

            loose = window;   // 兜底：任何叫这个的都先记着
        }

        return loose;
    }

    /// <summary>排查用：把当前上下文里的窗口名打出来（只打名字里带关键字的，最多 20 个）。</summary>
    private static void DumpWindows()
    {
        var count = 0;
        foreach (var window in ImGui.GetCurrentContext().Windows)
        {
            if (window.IsNull)
            {
                continue;
            }

            var name = WindowName(window);
            if (name is null)
            {
                continue;
            }

            var interesting = name.Contains("Scrolling", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("Installer", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("Categories", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("插件", StringComparison.Ordinal);

            Plugin.Log.Information(
                $"[FireGaze]   窗口{(interesting ? "*" : " ")}: {name}  (Scroll={window.Scroll.Y:F0}, Max={window.ScrollMax.Y:F0})");

            if (++count >= 40)
            {
                break;
            }
        }

        if (count == 0)
        {
            Plugin.Log.Information("[FireGaze]   （没有任何名字含 Scrolling/Installer/Categories 的窗口）");
        }
    }

    private static unsafe string? WindowName(ImGuiWindowPtr window)
    {
        var name = window.Name;
        return name == null ? null : Marshal.PtrToStringUTF8((nint)name);
    }
}
