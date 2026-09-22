using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

internal sealed partial class RepoAuditTab
{
    /// <summary>
    ///     读/刷新「已安装插件」索引；读不到时界面必须显示 `—`（不能显示假 0）。
    ///     构建放到后台：185 个插件的反射 + 图标缓存读取在主线程会顶出 80–100ms 的掉帧（实测过）。
    /// </summary>
    private void EnsureInstalledIndex()
    {
        if (installedIndexBuild is not null)
        {
            if (installedIndexBuild.IsCompleted)
            {
                var built = installedIndexBuild.Status == TaskStatus.RanToCompletion
                    ? installedIndexBuild.Result
                    : null;

                installedIndexBuild = null;

                if (built is not null)
                {
                    installedIndex = built;
                    installedIndexStale = false;
                    installedCountsDirty = true;
                    snapshotDirty = true;
                    iconHandles.Clear();
                    iconPeekMisses.Clear();

                    // 不可用时过几秒再试一次（卫月可能还在启动）；界面一律按「不可用」渲染
                    installedIndexRetryAfter = built.Available
                        ? DateTime.MaxValue
                        : DateTime.Now.AddSeconds(5);
                }
            }

            return;
        }

        if (!installedIndexStale || DateTime.Now < installedIndexRetryAfter)
        {
            return;
        }

        installedIndexBuild = Task.Run(InstalledPluginsIndex.Build);
    }

    /// <summary>
    ///     把索引结果填到每一行（不可用 → -1，不参与任何「0 个」的断言）。
    /// </summary>
    private void FillInstalledCounts()
    {
        if (!installedCountsDirty)
        {
            return;
        }

        installedCountsDirty = false;
        var index = installedIndex;

        lock (gate)
        {
            foreach (var item in items)
            {
                if (index is { Available: true })
                {
                    item.InstalledPlugins = index.TryGetInstalledByNormalized(item.NormalizedURL, out var plugins) ? plugins : [];
                    item.InstalledCount = item.InstalledPlugins.Count;
                }
                else
                {
                    item.InstalledPlugins = [];
                    item.InstalledCount = -1;
                }
            }
        }

        installedInUseCache = items.Count(x => x.InstalledCount > 0);
        installedUnusedCache = items.Count(x => x.InstalledCount == 0);
        snapshotDirty = true;
    }

    /// <summary>
    ///     「已安装」单元格：一律放数字（0 也是数字）；数据不可用显示 `—`。
    /// </summary>
    private void DrawInstalledCell(RepoAuditItem item, bool indexReady)
    {
        if (!indexReady || item.InstalledCount < 0)
        {
            ImGui.TextDisabled("—");
        }
        else if (item.InstalledCount == 0)
        {
            ImGui.TextDisabled("0 个");
        }
        else
        {
            ImGui.TextUnformatted($"{item.InstalledCount} 个");
        }

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        if (!indexReady)
        {
            ImGui.SetTooltip("读不到卫月的已装插件列表，可能是卫月升级改了内部字段；本次无法判断。");
            return;
        }

        var lines = new List<string>
        {
            $"从这条库链装了 {item.InstalledCount} 个插件。",
            "这条库一共提供多少个插件，把鼠标放在仓库地址上看",
        };

        if (item.InstalledPlugins.Count > 0)
        {
            var names = string.Join("、", item.InstalledPlugins.Take(8).Select(Describe));
            lines.Add(item.InstalledPlugins.Count > 8 ? $"{names}…（共 {item.InstalledPlugins.Count} 个）" : names);
        }
        else
        {
            lines.Add("只表示没从它装过插件：可能你在别的机器装过、它只是备用源、或插件是手动/开发版装的。");
        }

        if (item.InstalledPlugins.Any(x => iconDead.Contains(x.InternalName)))
        {
            lines.Add("带「缺图标」的是这次检查没缓存到的，可以点「下载图标」；带「图标地址失效」的要作者更新清单。图标只影响列表里的小图，不影响插件运行。");
        }

        ImGui.SetTooltip(string.Join('\n', lines));
        return;

        string Describe(InstalledPluginEntry entry)
        {
            // 只标「确认失效」这一类；「还没下到」是常态（卫月不落盘缓存），不在这里断言
            return iconDead.Contains(entry.InternalName)
                ? entry.DisplayName + " · 图标地址失效"
                : entry.DisplayName;
        }
    }

    /// <summary>
    ///     只读地把可见行里已在缓存中的图标取出来显示。
    /// </summary>
    /// <remarks>
    /// <b>这里绝不能触发下载</b>：曾经写过“每帧 12 个”的自动预取，185 个插件十几帧内全撒出去，
    /// 又经 FastDalamudCN 三线路竞速 → 一秒上千行失败日志、网络栈被拖死、游戏未响应。
    /// 下载一律走「下载图标」按钮那条限速通道。
    /// </remarks>
    private void PeekVisibleIcons(RepoAuditItem item)
    {
        if (!plugin.Config.ShowInstalledIcons || item.InstalledCount <= 0)
        {
            return;
        }

        foreach (var entry in item.InstalledPlugins)
        {
            if (iconHandles.ContainsKey(entry.InternalName) || iconPeekMisses.Contains(entry.InternalName))
            {
                continue;
            }

            // 先试本地落盘缓存（重开游戏后也能直接出图），再试卫月内存里的
            if (plugin.Icons.TryGetHandle(entry, out var cached))
            {
                iconHandles[entry.InternalName] = cached;
            }
            else if (PluginIconLookup.TryPeekHandle(entry, out var handle) && !handle.IsNull)
            {
                iconHandles[entry.InternalName] = handle;
            }
            else
            {
                iconPeekMisses.Add(entry.InternalName);   // 本帧不再重复查
            }
        }
    }

    /// <summary>
    ///     图标条：有图标的在前、缺图标的最后（首字母占位），组内保持原顺序。
    ///     每个图标悬停显示**它自己**的名字（占位格额外说明图标未缓存）；鼠标停在空白处则一次列出全部名字，
    ///     而且顺序与图标排列**完全一致**（之前名字按原序列、图标却重排过，所以对不上）。
    /// </summary>
    private void DrawInstalledIcons(RepoAuditItem item)
    {
        var plugins = item.InstalledPlugins;
        if (plugins.Count == 0)
        {
            return;
        }

        // 画序：先有图标的（原序），再缺图标的（原序）
        var sequence = new List<(InstalledPluginEntry Entry, ImTextureID? Handle)>(plugins.Count);
        var missing = new List<(InstalledPluginEntry Entry, ImTextureID? Handle)>();

        foreach (var entry in plugins)
        {
            if (iconHandles.TryGetValue(entry.InternalName, out var cached) && !cached.IsNull)
            {
                sequence.Add((entry, cached));
            }
            else
            {
                missing.Add((entry, null));
            }
        }

        sequence.AddRange(missing);

        const float iconSize = 22f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;

        ImGui.BeginGroup();

        var available = ImGui.GetContentRegionAvail().X;
        var maxIcons = Math.Clamp((int)((available + spacing) / (iconSize + spacing)), 1, 8);

        var drawn = 0;
        var hoveredIcon = false;

        foreach (var (entry, handle) in sequence)
        {
            if (drawn >= maxIcons)
            {
                break;
            }

            if (drawn > 0)
            {
                ImGui.SameLine();
            }

            if (handle is { } texture)
            {
                ImGui.Image(texture, new Vector2(iconSize, iconSize));
            }
            else
            {
                DrawIconPlaceholder(entry, iconSize);
            }

            if (ImGui.IsItemHovered())
            {
                hoveredIcon = true;

                var tooltip = handle is not null
                    ? entry.DisplayName
                    : iconDead.Contains(entry.InternalName)
                        ? entry.DisplayName + "\n图标地址已失效，需要作者更新清单"
                        : entry.DeclaresIcon
                            ? entry.DisplayName + "\n图标还没缓存下来；只影响列表里的小图，不影响插件运行"
                            : entry.DisplayName + "\n这个插件没有提供图标；只影响列表里的小图，不影响插件运行";

                ImGui.SetTooltip(tooltip);
            }

            drawn++;
        }

        var hidden = sequence.Count - drawn;
        if (hidden > 0)
        {
            if (drawn > 0)
            {
                ImGui.SameLine();
            }

            ImGui.TextDisabled($"+{hidden}");

            if (ImGui.IsItemHovered())
            {
                hoveredIcon = true;
                ImGui.SetTooltip("另有：" + string.Join("、", sequence.Skip(drawn).Select(x => x.Entry.DisplayName)));
            }
        }

        ImGui.EndGroup();

        // 停在图标条空白处：一次列出全部名字，顺序与图标排列一致
        if (!hoveredIcon && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"已安装 {plugins.Count} 个：" + string.Join("、", sequence.Select(x => x.Entry.DisplayName)));
        }
    }
}
