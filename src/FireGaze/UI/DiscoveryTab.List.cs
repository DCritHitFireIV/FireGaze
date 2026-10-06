using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.Discovery;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

/// <summary>
///     插件发现：卡片式列表（对齐「插件汉化」的两栏布局与整行展开）。
/// </summary>
internal sealed partial class DiscoveryTab
{
    private const float ActionsWidth = 176f;

    /// <summary>列表：两栏（左内容 / 右操作），卡片式行，整行点击展开。</summary>
    private void DrawList(float height)
    {
        RebuildFiltered();

        if (filtered.Count == 0)
        {
            ImGui.TextDisabled(index is { Available: true } ? "当前筛选下没有插件。" : "没有插件。");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        var slack = ImGui.GetStyle().ScrollbarSize + 48f;
        var contentWidth = Math.Max(240f, ImGui.GetContentRegionAvail().X - ActionsWidth - slack);

        if (!ImGui.BeginTable("###DiscoveryList", 2, flags, new Vector2(0, height)))
        {
            return;
        }

        ImGui.TableSetupColumn("plugin", ImGuiTableColumnFlags.WidthFixed, contentWidth);
        ImGui.TableSetupColumn("actions", ImGuiTableColumnFlags.WidthFixed, ActionsWidth);

        var clipper = new ImGuiListClipper();
        clipper.Begin(filtered.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                if (i >= 0 && i < filtered.Count)
                {
                    DrawRow(filtered[i]);
                }
            }
        }

        ImGui.EndTable();
    }

    private void DrawRow(TranslationIndexEntry entry)
    {
        var isOpen = string.Equals(expandedEntry, entry.InternalName, StringComparison.Ordinal);
        ImGui.PushID(entry.InternalName);
        ImGui.TableNextRow();

        // 整行点击展开详情：Selectable 铺底、跨两栏、允许被按钮覆盖（和「插件汉化」同一做法）
        var rowHeight = rowHeights.TryGetValue(entry.InternalName, out var knownHeight) ? knownHeight : 52f;
        ImGui.TableSetColumnIndex(0);
        if (ImGui.Selectable("##row", isOpen,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
                new Vector2(0, rowHeight)))
        {
            expandedEntry = isOpen ? null : entry.InternalName;
        }

        ImGui.SameLine(0, 0);
        var rowTop = ImGui.GetCursorScreenPos().Y;

        ImGui.TextDisabled(isOpen ? "▾" : "▸");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(isOpen ? "点这一行收起。" : "点这一行展开详情。");
        }

        ImGui.SameLine(0, 6);

        const float iconSize = 40f;
        if (!TryDrawIcon(entry, iconSize))
        {
            DrawLetterIcon(entry.DisplayName, iconSize);
        }

        ImGui.SameLine();
        ImGui.BeginGroup();
        {
            ImGui.TextUnformatted(entry.DisplayName);
            if (entry.IsOfficial)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("[官方]");
            }

            if (entry.RepositoryKnown && entry.RepositoryEnabled && !entry.IsOfficial)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("[已在库]");
            }

            var punchline = FirstNonEmpty(entry.OriginalPunchline, entry.OriginalDescription);
            if (!string.IsNullOrWhiteSpace(punchline))
            {
                UiHelpers.Fitted(punchline.Replace('\n', ' '), punchline);
            }

            // meta 行：作者 · 来源库 · 更新 · 推荐
            var meta = new List<string>(4);
            if (!string.IsNullOrWhiteSpace(entry.Author))
            {
                meta.Add(entry.Author);
            }

            if (entry.IsOfficial)
            {
                meta.Add("官方主库");
            }
            else if (entry.RepositoryURL is { Length: > 0 } repo)
            {
                meta.Add(RepoShort(repo));
            }

            if (entry.Updated is { } stamp)
            {
                meta.Add(DateTimeOffset.FromUnixTimeSeconds(stamp).ToLocalTime().ToString("yyyy-MM-dd"));
            }

            var recommends = discoveryStats?.RecommendsOf(entry.InternalName) ?? 0;
            if (recommends > 0)
            {
                meta.Add("推荐 " + recommends);
            }

            if (meta.Count > 0)
            {
                ImGui.TextDisabled(string.Join(" · ", meta));
            }
        }

        ImGui.EndGroup();

        // 量一下这一行实际多高，下一帧整行点击的 Selectable 用它（首帧用估算值）
        rowHeights[entry.InternalName] = Math.Max(20f, ImGui.GetItemRectMax().Y - rowTop);

        // 右栏：♥ + 库操作
        ImGui.TableNextColumn();
        ImGui.SetCursorPosY(rowTop + 4f);
        DrawLikeButton(entry, "row");

        if (!entry.IsOfficial && entry.RepositoryURL is { Length: > 0 } url)
        {
            if (!entry.RepositoryKnown)
            {
                ImGui.SameLine();
                if (ImGui.Button("加库###add"))
                {
                    AddRepoFromRow(url);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("把这条库加进你的第三方插件列表（会先自动备份）");
                }
            }
            else if (!entry.RepositoryEnabled)
            {
                ImGui.SameLine();
                if (ImGui.Button("启用###enable"))
                {
                    EnableRepoFromRow(url);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("保留链接、把它重新启用；启用后卫月会去抓它的插件");
                }
            }
        }

        if (isOpen)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawExpanded(entry);
        }

        ImGui.PopID();
    }

    /// <summary>展开区：完整简介/详情 + 作者/更新/推荐/点赞 + 库链操作。</summary>
    private void DrawExpanded(TranslationIndexEntry entry)
    {
        ImGui.Indent(ImGui.GetFontSize() + 8f);

        if (!string.IsNullOrWhiteSpace(entry.OriginalPunchline))
        {
            ImGui.TextDisabled("一行简介");
            ImGui.SameLine();
            ImGui.TextWrapped(entry.OriginalPunchline.Replace('\n', ' '));
        }

        if (!string.IsNullOrWhiteSpace(entry.OriginalDescription))
        {
            ImGui.TextDisabled("详情");
            ImGui.Spacing();
            ImGui.TextWrapped(entry.OriginalDescription);
        }

        ImGui.Spacing();

        var weekly = discoveryStats?.WeeklyOf(entry.InternalName) ?? 0;
        var total = discoveryStats?.TotalOf(entry.InternalName) ?? 0;
        var recommends = discoveryStats?.RecommendsOf(entry.InternalName) ?? 0;
        DrawKeyValue("内部名", entry.InternalName);
        if (!string.IsNullOrWhiteSpace(entry.Author))
        {
            DrawKeyValue("作者", entry.Author);
        }

        if (entry.Updated is { } stamp)
        {
            DrawKeyValue("最后更新", DateTimeOffset.FromUnixTimeSeconds(stamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        }

        DrawKeyValue("推荐", $"{recommends} 次（从云端加进自己库）");
        DrawKeyValue("点赞", $"本周 {weekly} · 总共 {total}");

        if (entry.IsOfficial)
        {
            ImGui.TextDisabled("来自卫月官方主库（Dip17）：本来就在你的库里，不用加库。");
            ImGui.Unindent(ImGui.GetFontSize() + 8f);
            return;
        }

        if (entry.RepositoryURL is not { Length: > 0 } url)
        {
            ImGui.TextDisabled("来源未知：旧词表没有记这条插件的库链。");
            ImGui.Unindent(ImGui.GetFontSize() + 8f);
            return;
        }

        ImGui.TextDisabled("库链");
        ImGui.SameLine();
        if (ImGui.SmallButton("复制###copy"))
        {
            ImGui.SetClipboardText(url);
            SetStatus("已复制库链地址", isError: false);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("在浏览器打开###open"))
        {
            OpenInBrowser(url);
        }

        if (!entry.RepositoryKnown)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("加库###add-expanded"))
            {
                AddRepoFromRow(url);
            }
        }
        else if (!entry.RepositoryEnabled)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("启用###enable-expanded"))
            {
                EnableRepoFromRow(url);
            }
        }

        ImGui.TextWrapped(url);
        ImGui.Unindent(ImGui.GetFontSize() + 8f);
    }

    private static void DrawKeyValue(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(96f);
        ImGui.TextWrapped(value);
    }

    /// <summary>行尾的 ♥：本周/总共；本机清过的一周内变灰，下周可以再点。</summary>
    private void DrawLikeButton(TranslationIndexEntry entry, string suffix)
    {
        var week = discoveryStats?.Week ?? string.Empty;
        var liked = week.Length > 0 && discoveryState.LikedThisWeek(entry.InternalName, week);
        var unsynced = discoveryState.HasPending(entry.InternalName);
        var weekly = discoveryStats?.WeeklyOf(entry.InternalName) ?? 0;
        var total = discoveryStats?.TotalOf(entry.InternalName) ?? 0;

        if (liked)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"♥ {ShortCount(weekly)}/{ShortCount(total)}###like-{suffix}"))
        {
            MarkLike(entry);
        }

        if (liked)
        {
            ImGui.EndDisabled();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var lines = new List<string>
            {
                $"本周 {weekly} 赞 · 总共 {total} 赞",
                liked ? "这周你已经点过赞了（下周可以再点）" : "点一下为这个插件点赞（一周一次，匿名上报）",
            };
            if (unsynced)
            {
                lines.Add("有点赞还没同步到云端，会自动重试");
            }

            ImGui.SetTooltip(string.Join("\n", lines));
        }
    }

    /// <summary>本机点赞：先记下来（支持离线/失败重试），再后台上报；成功用云端真值回填。</summary>
    private void MarkLike(TranslationIndexEntry entry)
    {
        var week = discoveryStats?.Week ?? string.Empty;
        discoveryState.MarkLiked(entry.InternalName, week);
        if (discoveryStats is not null)
        {
            discoveryStats.WeeklyLikes[entry.InternalName] = discoveryStats.WeeklyOf(entry.InternalName) + 1;
            discoveryStats.TotalLikes[entry.InternalName] = discoveryStats.TotalOf(entry.InternalName) + 1;
        }

        SetStatus("已点赞，正在同步到云端…", isError: false);
        rebuildPending = true;
        var name = entry.InternalName;
        _ = Task.Run(async () =>
        {
            var (ok, total, weekly, _) = await DiscoveryRelay.LikeAsync(name, CancellationToken.None).ConfigureAwait(false);
            if (!ok)
            {
                return;   // 留在待重试里，不谎报成功
            }

            discoveryState.CompleteLike(name);
            likeResults.Enqueue((name, total, weekly));
        });
    }

    private static string ShortCount(int value) => value > 999 ? "999+" : value.ToString();

    /// <summary>尝试画缓存里的图标；没有就返回 false（由调用方画首字母占位）。</summary>
    private bool TryDrawIcon(TranslationIndexEntry entry, float size)
    {
        if (iconHandles.TryGetValue(entry.InternalName, out var cached))
        {
            if (cached.IsNull)
            {
                return false;
            }

            ImGui.Image(cached, new Vector2(size, size));
            return true;
        }

        if (!entry.DeclaresIcon || iconMisses.Contains(entry.InternalName))
        {
            return false;
        }

        var installed = new InstalledPluginEntry
        {
            InternalName = entry.InternalName,
            DisplayName = entry.DisplayName,
            IconURL = entry.IconURL,
            RawPlugin = entry.RawPlugin!,
            Manifest = entry.Manifest,
            IsThirdParty = true,
        };

        if (plugin.Icons.TryGetHandle(installed, out var handle) && !handle.IsNull)
        {
            iconHandles[entry.InternalName] = handle;
            ImGui.Image(handle, new Vector2(size, size));
            return true;
        }

        if (PluginIconLookup.TryPeekHandle(installed, out var peeked) && !peeked.IsNull)
        {
            iconHandles[entry.InternalName] = peeked;
            ImGui.Image(peeked, new Vector2(size, size));
            return true;
        }

        iconMisses.Add(entry.InternalName);
        return false;
    }

    /// <summary>没有图标时的首字母占位块（和「插件汉化」一致）。</summary>
    private static void DrawLetterIcon(string name, float size)
    {
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            start,
            start + new Vector2(size, size),
            ImGui.GetColorU32(new Vector4(0.22f, 0.25f, 0.31f, 1f)),
            5f);

        var initial = string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
        var textSize = ImGui.CalcTextSize(initial);
        drawList.AddText(
            start + ((new Vector2(size, size) - textSize) * 0.5f),
            ImGui.GetColorU32(new Vector4(0.80f, 0.86f, 0.96f, 1f)),
            initial);

        ImGui.Dummy(new Vector2(size, size));
    }
}
