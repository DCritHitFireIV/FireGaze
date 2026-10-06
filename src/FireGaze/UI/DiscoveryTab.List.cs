using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.Diagnostics;
using FireGaze.Discovery;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

/// <summary>
///     插件发现：卡片式列表（对齐「插件汉化」的两栏布局与整行展开）。
/// </summary>
internal sealed partial class DiscoveryTab
{
    /// <summary>
    ///     折叠行的固定行高：删掉元信息行（第三行）后仍保留原来三行的节奏与列距，
    ///     不够高的话两行会显得又扁又挤（用户 2026-10-06：「列距还要保持类似原来三行那样的宽度」）。
    /// </summary>
    private static float FoldedRowHeight()
        => MathF.Max(ImGui.GetFrameHeight() + 16f, ImGui.GetFontSize() * 3.2f);

    /// <summary>右栏按钮列宽：按当前字体 / UI 缩放实算（写死 176 在缩放 >1 时会把按钮切掉）。</summary>
    private static float ActionsColumnWidth()
    {
        var width = (ImGui.GetStyle().CellPadding.X * 2f) + 8f;
        width += MathF.Max(40f, ImGui.GetFontSize() * 2.6f) + 12f;   // ♥ 自绘最小命中区
        width += MaxLabelWidth("加入自己的库", "启用") + 10f;
        return width;
    }

    private static float MaxLabelWidth(params string[] labels) => labels.Max(UiHelpers.LabelWidth);

    /// <summary>列表：两栏（左内容 / 右操作），ins 风卡片行，整行点击展开。</summary>
    private void DrawList(float height)
    {
        RebuildFiltered();

        if (filtered.Count == 0)
        {
            ImGui.TextDisabled(index is { Available: true }
                ? "没有匹配的插件 —— 换个关键词，或清掉筛选试试。"
                : "云端插件库暂时是空的。");
            return;
        }

        // 卡片之间靠色差与间距分开，不用行条纹（ins 风：层级靠留白与明度，不靠线）
        const ImGuiTableFlags flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        var actionsWidth = ActionsColumnWidth();
        var listWidth = MathF.Max(240f, ImGui.GetContentRegionAvail().X - 2f);

        InsStyle.PushRounded();
        if (!ImGui.BeginTable("###DiscoveryList", 2, flags, new Vector2(0, height)))
        {
            InsStyle.PopRounded();
            return;
        }

        // 左列自适应、右列按按钮实宽固定：列宽贴合窗口，右侧不会留一条空缝
        ImGui.TableSetupColumn("plugin", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("actions", ImGuiTableColumnFlags.WidthFixed, actionsWidth);

        // 卡片行比一个文本行高得多（~54px）：不给 clipper 正确行高，滚动范围与可见区估算会偏好几倍
        var clipper = new ImGuiListClipper();
        clipper.Begin(filtered.Count, FoldedRowHeight());
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                if (i >= 0 && i < filtered.Count)
                {
                    DrawRow(filtered[i], listWidth);
                }
            }
        }

        ImGui.EndTable();
        InsStyle.PopRounded();
    }

    private void DrawRow(TranslationIndexEntry entry, float listWidth)
    {
        var isOpen = string.Equals(expandedEntry, entry.InternalName, StringComparison.Ordinal);
        ImGui.PushID(entry.InternalName);
        ImGui.TableNextRow();

        // ins 风：先铺圆角卡片底（行与行靠色差 + 空隙分开），再画内容。
        // 跨列绘制必须用 PushClipRect(..., intersect: false) 顶掉单元格裁剪，否则右半被裁掉。
        var rowStartY = ImGui.GetCursorPosY();
        var rowHeight = FoldedRowHeight();
        var detailExtra = isOpen && detailHeights.TryGetValue(entry.InternalName, out var knownDetail) ? knownDetail : 0f;
        var cardMin = ImGui.GetCursorScreenPos();
        var cardMax = new Vector2(cardMin.X + listWidth, cardMin.Y + rowHeight + detailExtra - 5f);
        ImGui.GetWindowDrawList().PushClipRect(cardMin, cardMax, false);
        InsStyle.DrawCard(cardMin, cardMax, isOpen, rowHovered.Contains(entry.InternalName));
        ImGui.GetWindowDrawList().PopClipRect();

        // 整行点击展开详情：Selectable 只当命中区用，视觉全交给卡片（Header 色推成透明）
        ImGui.TableSetColumnIndex(0);
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0f, 0f, 0f, 0f));
        var clicked = ImGui.Selectable("##row", isOpen,
            ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
            new Vector2(0, rowHeight));
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
        {
            rowHovered.Add(entry.InternalName);
        }
        else
        {
            rowHovered.Remove(entry.InternalName);
        }

        if (clicked)
        {
            expandedEntry = isOpen ? null : entry.InternalName;
        }

        ImGui.SameLine(0, 0);

        // 删掉元信息行后仍是两行文字：行高保持原来三行的节奏，内容在行内垂直居中
        const float iconSize = 40f;
        var textHeight = (ImGui.GetTextLineHeight() * 2f) + ImGui.GetStyle().ItemSpacing.Y;
        var contentHeight = MathF.Max(iconSize, textHeight);
        var padY = MathF.Max(2f, (rowHeight - contentHeight) * 0.5f);
        ImGui.SetCursorPosY(rowStartY + padY);

        if (!TryDrawIcon(entry, iconSize))
        {
            DrawLetterIcon(entry.DisplayName, iconSize);
        }

        ImGui.SameLine(0, 12f);
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
        }

        ImGui.EndGroup();

        // 右栏：♥ + 库操作（SetCursorPosY 用窗口局部坐标——用屏幕坐标会把按钮画到下一行去）
        ImGui.TableNextColumn();
        ImGui.SetCursorPosY(rowStartY + ((rowHeight - ImGui.GetFrameHeight()) * 0.5f));
        DrawLikeButton(entry, "row");

        if (!entry.IsOfficial && entry.RepositoryURL is { Length: > 0 } url)
        {
            if (!entry.RepositoryKnown)
            {
                ImGui.SameLine();
                var cta = "加入自己的库";
                if (InsStyle.PinkButton(cta + "###add", InsStyle.PinkButtonWidth(cta)))
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
                UiHelpers.PushEnableButton();
                if (ImGui.Button("启用###enable"))
                {
                    EnableRepoFromRow(url);
                }

                UiHelpers.PopEnableButton();

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

            // 量展开区高度：下一帧卡片底把它一起盖住（首帧先按 0 算）
            detailHeights[entry.InternalName] = Math.Max(0f, ImGui.GetItemRectMax().Y - (cardMin.Y + rowHeight));
        }

        ImGui.PopID();
    }

    /// <summary>展开区：描述 + 作者/更新/推荐/点赞 + 库链操作（不再重复一行简介与内部名，标签也不写灰色小字）。</summary>
    private void DrawExpanded(TranslationIndexEntry entry)
    {
        // 缩进对齐到图标右侧的文字列（40px 图标 + 间距），详情和名字同一视线
        ImGui.Indent(40f + ImGui.GetStyle().ItemSpacing.X + 4f);

        if (!string.IsNullOrWhiteSpace(entry.OriginalDescription))
        {
            ImGui.TextWrapped(entry.OriginalDescription);
            ImGui.Spacing();
        }

        var total = discoveryStats?.TotalOf(entry.InternalName) ?? 0;
        var recommends = discoveryStats?.RecommendsOf(entry.InternalName) ?? 0;
        if (!string.IsNullOrWhiteSpace(entry.Author))
        {
            DrawKeyValue("作者", entry.Author);
        }

        if (entry.Updated is { } stamp)
        {
            DrawKeyValue("最后更新", DateTimeOffset.FromUnixTimeSeconds(stamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        }

        DrawKeyValue("推荐", $"{recommends} 次（从云端加进自己库）");
        DrawKeyValue("点赞", discoveryStats is null ? "统计暂不可用（点赞会先记在本机）" : total.ToString());

        if (entry.IsOfficial)
        {
            ImGui.TextDisabled("来自卫月官方主库（Dip17）：本来就在你的库里，不用加库。");
            ImGui.Unindent(40f + ImGui.GetStyle().ItemSpacing.X + 4f);
            return;
        }

        if (entry.RepositoryURL is not { Length: > 0 } url)
        {
            ImGui.TextDisabled("来源未知：旧词表没有记这条插件的库链。");
            ImGui.Unindent(40f + ImGui.GetStyle().ItemSpacing.X + 4f);
            return;
        }

        ImGui.TextDisabled("库链");
        ImGui.SameLine();
        if (ImGui.SmallButton("复制###copy"))
        {
            ImGui.SetClipboardText(url);
            SetStatus("已复制库链地址", isError: false);
        }

        if (!entry.RepositoryKnown)
        {
            UiHelpers.SameLineOrWrap(InsStyle.PinkButtonWidth("加入自己的库"));
            if (InsStyle.PinkButton("加入自己的库###add-expanded", InsStyle.PinkButtonWidth("加入自己的库")))
            {
                AddRepoFromRow(url);
            }
        }
        else if (!entry.RepositoryEnabled)
        {
            UiHelpers.SameLineOrWrap(UiHelpers.LabelWidth("启用"));
            UiHelpers.PushEnableButton();
            if (ImGui.SmallButton("启用###enable-expanded"))
            {
                EnableRepoFromRow(url);
            }

            UiHelpers.PopEnableButton();
        }

        ImGui.TextWrapped(url);
        ImGui.Unindent(40f + ImGui.GetStyle().ItemSpacing.X + 4f);
    }

    private static void DrawKeyValue(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(ImGui.GetFontSize() * 5.2f);
        ImGui.TextWrapped(value);
    }

    /// <summary>行尾的 ♥：不显示数字（用户 2026-10-06 定），计数在 tooltip 里；本机清过的一周内变灰。</summary>
    private void DrawLikeButton(TranslationIndexEntry entry, string suffix)
    {
        // 本地算北京时间的 ISO 周：统计拉不到时也能正确变灰（中继还没部署 v8 的时段）
        var localWeek = DiscoveryWeek.Current();
        var serverWeek = discoveryStats?.Week ?? string.Empty;
        var liked = discoveryState.LikedThisWeek(entry.InternalName, localWeek)
                    || (serverWeek.Length > 0 && discoveryState.LikedThisWeek(entry.InternalName, serverWeek));
        var unsynced = discoveryState.HasPending(entry.InternalName);
        var hasStats = discoveryStats is not null;
        var weekly = discoveryStats?.WeeklyOf(entry.InternalName) ?? 0;
        var total = discoveryStats?.TotalOf(entry.InternalName) ?? 0;

        // ins 风红心：已赞 #ED4956、未赞中性、悬停提亮；命中区不小于 40px（自绘，不走 ImGui 默认按钮底）
        var likeWidth = MathF.Max(40f, ImGui.GetFontSize() * 2.6f);
        if (InsStyle.HeartButton($"like-{suffix}", liked, unsynced, likeWidth))
        {
            MarkLike(entry);
        }

        if (ImGui.IsItemHovered())
        {
            var lines = new List<string>
            {
                hasStats ? $"总共 {total} 赞 · 本周 {weekly} 赞" : "统计暂不可用（点赞会先记在本机，恢复后显示计数）",
                liked ? "这周你已经点过赞了（下周可以再点）" : "点一下为这个插件点赞（一周一次，匿名上报）",
            };
            if (unsynced)
            {
                lines.Add("* 有点赞还没同步到云端，会自动重试");
            }

            ImGui.SetTooltip(string.Join("\n", lines));
        }
    }

    /// <summary>本机点赞：先记下来（支持离线/失败重试），再后台上报；成功用云端真值回填。</summary>
    private void MarkLike(TranslationIndexEntry entry)
    {
        discoveryState.MarkLiked(entry.InternalName, DiscoveryWeek.Current());
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
            var (ok, total, weekly, error) = await DiscoveryRelay.LikeAsync(name, CancellationToken.None).ConfigureAwait(false);
            if (!ok)
            {
                // 留在待重试里，不谎报成功；补报成功时会另记一条 Info
                ActivityLog.Warning("插件发现", $"{name}：点赞上报失败（{error ?? "unknown"}）；已排队自动重试");
                return;
            }

            discoveryState.CompleteLike(name);
            likeResults.Enqueue((name, total, weekly));
        });
    }

    /// <summary>尝试画缓存里的图标；没有就返回 false（由调用方画首字母占位）。</summary>
    private bool TryDrawIcon(TranslationIndexEntry entry, float size)
    {
        if (iconHandles.TryGetValue(entry.InternalName, out var cached))
        {
            return !cached.IsNull && DrawRoundIcon(cached, size);
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
            return DrawRoundIcon(handle, size);
        }

        if (PluginIconLookup.TryPeekHandle(installed, out var peeked) && !peeked.IsNull)
        {
            iconHandles[entry.InternalName] = peeked;
            return DrawRoundIcon(peeked, size);
        }

        iconMisses.Add(entry.InternalName);
        return false;
    }

    /// <summary>圆形头像（ins 风：头像是完整的圆）；纹理为空返回 false 交字母占位。</summary>
    private static bool DrawRoundIcon(ImTextureID texture, float size)
    {
        if (!InsStyle.DrawRoundIcon(texture, ImGui.GetCursorScreenPos(), size))
        {
            return false;
        }

        ImGui.Dummy(new Vector2(size, size));
        return true;
    }

    /// <summary>没有图标时的字母占位（ins 风：也是正圆）。</summary>
    private static void DrawLetterIcon(string name, float size)
    {
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            start,
            start + new Vector2(size, size),
            ImGui.GetColorU32(new Vector4(0.22f, 0.25f, 0.31f, 1f)),
            size / 2f);

        var initial = string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
        var textSize = ImGui.CalcTextSize(initial);
        drawList.AddText(
            start + ((new Vector2(size, size) - textSize) * 0.5f),
            ImGui.GetColorU32(new Vector4(0.80f, 0.86f, 0.96f, 1f)),
            initial);

        ImGui.Dummy(new Vector2(size, size));
    }
}
