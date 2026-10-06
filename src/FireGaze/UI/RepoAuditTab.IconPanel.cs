using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>
///     图标缓存维护区：启用缓存 / 检查缺图标 / 下载图标 + 状态行。
/// </summary>
/// <remarks>
///     2026-10-07 用户要求：整体从「仓库体检」搬到「插件安装器」页——图标本来就是安装器里看得见的东西。
///     逻辑仍住在体检页的 partial（复用它算好的已装索引与下载队列），这里只负责画；
///     状态行也是本面板私有的，不再借用体检页底部那条。
/// </remarks>
internal sealed partial class RepoAuditTab
{
    /// <summary>面板自己的状态行（体检页的状态行只留给扫描 / 停用 / 删除那些操作）。</summary>
    private string? iconPanelStatus;
    private bool iconPanelStatusIsError;

    private void SetIconPanelStatus(string? message, bool isError)
    {
        iconPanelStatus = message;
        iconPanelStatusIsError = isError;
    }

    /// <summary>安装器页每帧调它推进下载（体检页也保留自己的推进，切回去能接着下）。</summary>
    internal void TickIconMaintenance()
    {
        // 面板可能先于体检页开：已装索引在这里也要能建（否则按钮会一直显示「插件数据不可用」）
        EnsureInstalledIndex();
        TickIconDownload();
    }

    /// <summary>画整个图标缓存维护区（由「插件安装器」页调用）。</summary>
    internal void DrawIconMaintenance()
    {
        var indexReady = installedIndex is { Available: true };

        var iconCacheEnabled = plugin.Config.IconCacheEnabled;
        if (ImGui.Checkbox("启用图标缓存###IconCache", ref iconCacheEnabled))
        {
            plugin.Config.IconCacheEnabled = iconCacheEnabled;
            plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "把下到的图标存到本地（配置目录 /icons），重开游戏不用重下；\n"
                + "并分批挂回卫月的图标缓存，插件安装器里也能直接用本地图。\n"
                + "关掉后回到旧行为：只借卫月内存缓存，每次重启都要重新下载。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("│");
        ImGui.SameLine();

        var canCheck = indexReady && !scanning && !iconDownloadRunning;
        if (!canCheck)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(iconCheckDone ? "重新检查缺图标###IconCheck" : "检查缺图标###IconCheck"))
        {
            RunIconCheck();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                (indexReady ? string.Empty : "插件数据不可用，暂时不能检查。\n")
                + "哪些已装插件声明了图标、但本地（含落盘缓存）还没有。\n"
                + "范围是已安装的插件，与体检页的筛选和勾选无关；这一步不下载任何东西。");
        }

        if (!canCheck)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();

        // 第二步：看过清单后由用户决定下不下
        var canDownload = indexReady && !scanning && iconCheckDone;
        if (!canDownload)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(iconDownloadRunning
                ? "停止下载###IconDownload"
                : $"下载图标（{iconMissing.Count}）###IconDownload"))
        {
            if (iconDownloadRunning)
            {
                FinishIconDownload();
            }
            else
            {
                StartIconDownload();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                (indexReady ? string.Empty : "插件数据不可用，暂时不能下载。\n")
                + (iconCheckDone ? string.Empty : "先点「检查缺图标」，拿到清单再决定。\n")
                + "把上一步查出来缺的那些图标下下来（直连 + 镜像竞速，同时最多 8 个）。\n"
                + "下到的图标会存到本地（配置目录 /icons），重开游戏不用重下，\n"
                + "插件安装器里也能直接用本地图。\n"
                + "最多等 120 秒；只剩最后几个时会收尾（再等 4 秒就报结果）。中途可点「停止下载」。");
        }

        if (!canDownload)
        {
            ImGui.EndDisabled();
        }

        // 状态：下载进行中给进度条，其余给文字（含「缺图标」名单悬停展开）
        if (iconDownloadRunning && iconDownloadTotal > 0)
        {
            ImGui.ProgressBar(
                (float)iconDownloadGot / iconDownloadTotal,
                new Vector2(120, 0));
            ImGui.SameLine();
            UiHelpers.ColoredWrapped(UiHelpers.Muted, iconDownloadLine ?? string.Empty);
        }
        else if (!string.IsNullOrEmpty(iconPanelStatus))
        {
            UiHelpers.ColoredWrapped(iconPanelStatusIsError ? UiHelpers.Bad : UiHelpers.Muted, iconPanelStatus);

            // 缺图标名单在状态行里只能给前几个，悬停看全部（IC2-06）
            if (iconMissingNames is { Count: > 0 } names && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"缺图标 {names.Count} 个：\n" + string.Join("、", names));
            }
        }
    }
}
