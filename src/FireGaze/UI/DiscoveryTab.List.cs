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
        => ImGui.GetStyle().CellPadding.X * 2f + 8f + MathF.Max(40f, ImGui.GetFontSize() * 2.6f) + 12f
            + MaxLabelWidth("加入自己的库", "启用") + 10f
            + MaxLabelWidth("一键安装", "安装中…", "去汉化", "安装器") + ImGui.GetStyle().ItemSpacing.X;

    private static float MaxLabelWidth(params string[] labels) => labels.Max(UiHelpers.LabelWidth);

    /// <summary>列表：两栏（左内容 / 右操作），ins 风卡片行，整行点击展开。</summary>
    private void DrawList(float height)
    {
        this.RefreshInstalledActions();
        RebuildFiltered();

        if (filtered.Count == 0)
        {
            ImGui.TextDisabled(index is { Available: true }
                ? "没有匹配的插件 —— 换个关键词，或清掉筛选试试。"
                : "云端插件库暂时是空的。");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;

        var columns = PluginListLayout.MeasureLegacyColumns(ImGui.GetContentRegionAvail().X,
            ImGui.GetStyle().ScrollbarSize, ActionsColumnWidth());
        var rowHeight = FoldedRowHeight();

        InsStyle.PushRounded();
        if (!ImGui.BeginTable("###DiscoveryList", 2, flags, new Vector2(0, height)))
        {
            InsStyle.PopRounded();
            return;
        }
        ImGui.TableSetupColumn("plugin", ImGuiTableColumnFlags.WidthFixed, columns.Content);
        ImGui.TableSetupColumn("actions", ImGuiTableColumnFlags.WidthFixed, columns.Actions);
        void DrawClosedRows(int start, int count)
        {
            if (count == 0) return;
            var clipper = new ImGuiListClipper();
            clipper.Begin(count, rowHeight + ImGui.GetStyle().CellPadding.Y * 2f);
            while (clipper.Step())
            {
                NoteVisibleForIcons(start + clipper.DisplayStart, start + clipper.DisplayEnd);
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    DrawRow(filtered[start + i], rowHeight);
            }
        }
        // Variable-height details must not participate in the folded-row clipper.
        var ranges = PluginListLayout.SplitRows(filtered.Count,
            filtered.FindIndex(e => e.InternalName == expandedEntry));
        DrawClosedRows(0, ranges.BeforeCount);
        if (ranges.ExpandedIndex >= 0)
        {
            var entry = filtered[ranges.ExpandedIndex];
            DrawRow(entry, rowHeight);
            if (expandedEntry == entry.InternalName)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.PushID(entry.InternalName);
                DrawExpanded(entry);
                ImGui.PopID();
                ImGui.TableNextColumn();
            }
        }
        DrawClosedRows(ranges.AfterStart, ranges.AfterCount);
        ImGui.EndTable();
        InsStyle.PopRounded();
    }

    private void DrawRow(TranslationIndexEntry entry, float rowHeight)
    {
        var isOpen = string.Equals(expandedEntry, entry.InternalName, StringComparison.Ordinal);
        ImGui.PushID(entry.InternalName);
        ImGui.TableNextRow();

        // 整行点击展开详情（与「插件汉化」同一写法）：Selectable 铺在行底层、跨所有列、允许按钮覆盖。
        // 必须先切到列 0 再取行内坐标——TableNextRow 之后光标还停在上一行结束的位置，
        // 拿它当行起点会把后面画的东西整条画到窗口外（2026-10-07 用户看到的 "点赞旁黑条"）。
        ImGui.TableSetColumnIndex(0);
        var rowStartY = ImGui.GetCursorPosY();
        if (ImGui.Selectable("##row", isOpen,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
                new Vector2(0, rowHeight)))
        {
            expandedEntry = isOpen ? null : entry.InternalName;
        }

        ImGui.SameLine(0, 0);

        // 删掉元信息行后仍是两行文字：行高保持原来三行的节奏，内容在行内垂直居中
        const float iconSize = PluginListLayout.IconSize;
        var textHeight = (ImGui.GetTextLineHeight() * 2f) + ImGui.GetStyle().ItemSpacing.Y;
        var contentHeight = MathF.Max(iconSize, textHeight);
        var padY = MathF.Max(2f, (rowHeight - contentHeight) * 0.5f);
        ImGui.SetCursorPosY(rowStartY + padY);

        if (!TryDrawIcon(entry, iconSize))
        {
            InsStyle.DrawLetterAvatar(entry.DisplayName, iconSize);
        }

        ImGui.SameLine(0, 12f);
        ImGui.BeginGroup();
        {
            UiHelpers.Fitted(entry.DisplayName, entry.DisplayName);
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
            if (entry.InternalName != this.installingName && this.discoveryInstalled?.All.Any(e => e.InternalName == entry.InternalName) == true)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("[已安装]");
            }

            var punchline = DisplayPunchline(entry);
            if (!string.IsNullOrWhiteSpace(punchline))
            {
                UiHelpers.Fitted(punchline.Replace('\n', ' '), punchline);
            }
        }

        ImGui.EndGroup();

        // 按原格式让操作组贴右、垂直居中，保留新增安装与汉化入口。
        ImGui.TableNextColumn();
        ImGui.SetCursorPosY(rowStartY + ((rowHeight - ImGui.GetFrameHeight()) * 0.5f));

        var url = entry.RepositoryURL is { Length: > 0 } candidate ? candidate : null;
        var addAction = !entry.IsOfficial && url is not null && !entry.RepositoryKnown;
        var enableAction = !entry.IsOfficial && url is not null && entry.RepositoryKnown && !entry.RepositoryEnabled;

        var installedKnown = this.discoveryInstalled is { Available: true };
        var installing = entry.InternalName == this.installingName;
        var installed = installedKnown && !installing && this.discoveryInstalled!.All.Any(e => e.InternalName == entry.InternalName);
        var navigate = installed || entry.IsOfficial || (entry.RepositoryKnown && entry.RepositoryEnabled);
        var navigateLabel = installed ? "去汉化" : installing ? "安装中…" : installedKnown ? "一键安装" : "安装器";
        var navigateWidth = MaxLabelWidth("一键安装", "安装中…", "去汉化", "安装器");
        var libraryWidth = addAction ? InsStyle.PinkButtonWidth("加入自己的库") : enableAction ? UiHelpers.LabelWidth("启用") : 0f;
        var actionOrigin = ImGui.GetCursorScreenPos();
        var right = MathF.Min(actionOrigin.X + ImGui.GetContentRegionAvail().X,
            ImGui.GetWindowDrawList().GetClipRectMax().X) - ImGui.GetStyle().CellPadding.X;
        var positions = PluginListLayout.MeasureDiscoveryActions(right,
            MathF.Max(40f, ImGui.GetFontSize() * 2.6f), navigate ? navigateWidth : 0f, libraryWidth,
            ImGui.GetStyle().ItemSpacing.X);

        if (addAction)
        {
            ImGui.SetCursorScreenPos(new Vector2(positions.LibraryX, actionOrigin.Y));
            const string cta = "加入自己的库";
            if (InsStyle.PinkButton(cta + "###add", InsStyle.PinkButtonWidth(cta)))
            {
                AddRepoFromRow(url!, entry.InternalName);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("把这条库加进你的第三方插件列表（会先自动备份）");
            }
        }
        else if (enableAction)
        {
            ImGui.SetCursorScreenPos(new Vector2(positions.LibraryX, actionOrigin.Y));
            UiHelpers.PushEnableButton();
            if (ImGui.Button("启用###enable"))
            {
                EnableRepoFromRow(url!);
            }

            UiHelpers.PopEnableButton();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("保留链接、把它重新启用；启用后卫月会去抓它的插件");
            }
        }

        if (navigate)
        {
            ImGui.SetCursorScreenPos(new Vector2(positions.NavigateX, actionOrigin.Y));
            ImGui.PushStyleColor(ImGuiCol.Button, installed ? new Vector4(0.23f, 0.29f, 0.28f, 1f) : new Vector4(0.27f, 0.27f, 0.32f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, installed ? new Vector4(0.30f, 0.37f, 0.34f, 1f) : new Vector4(0.34f, 0.34f, 0.41f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, installed ? new Vector4(0.19f, 0.25f, 0.23f, 1f) : new Vector4(0.23f, 0.23f, 0.28f, 1f));
            ImGui.BeginDisabled(!installed && this.installTask is not null);
            if (ImGui.Button(navigateLabel + "###discovery-next", new Vector2(navigateWidth, 0)))
            {
                if (installed) this.plugin.OpenTranslationFor(entry.InternalName);
                else if (installedKnown) this.StartInstallation(entry);
                else this.OpenInstallationFallback(entry.InternalName);
            }
            ImGui.EndDisabled();
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(installed ? "进入插件汉化，把这个插件放到列表第一行。"
                    : this.installTask is not null ? "卫月正在安装插件，请完成后再安装另一个。"
                    : installedKnown ? "直接用卫月安装这个插件；成功后可以去汉化，安装成功才计入推荐次数。"
                    : "暂时读不到已安装插件，请在卫月安装器确认。");
        }
        // Explicit slots keep all actions on the same baseline without wrap-dependent row heights.
        ImGui.SetCursorScreenPos(new Vector2(positions.LikeX, actionOrigin.Y));
        DrawLikeButton(entry, "row");

        ImGui.PopID();
    }

    /// <summary>展开区：描述 + 作者/更新/推荐/点赞 + 库链操作（不再重复一行简介与内部名，标签也不写灰色小字）。</summary>
    private void DrawExpanded(TranslationIndexEntry entry)
    {
        // 缩进对齐到图标右侧的文字列（40px 图标 + 间距），详情和名字同一视线
        var detailInset = 40f + ImGui.GetStyle().ItemSpacing.X + 4f;
        ImGui.Indent(detailInset);

        if (this.installFallbackName == entry.InternalName)
        {
            if (ImGui.SmallButton("打开安装器###installation-fallback"))
                this.OpenInstallationFallback(entry.InternalName);
            ImGui.Spacing();
        }

        var description = DisplayDescription(entry);
        if (!string.IsNullOrWhiteSpace(description))
        {
            ImGui.TextWrapped(description);
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

        DrawKeyValue("推荐", $"{recommends} 次");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("从插件发现选择并成功安装这个插件的次数。旧统计包含历史加库记录，不代表完整的下载量。");
        DrawKeyValue("点赞", discoveryStats is null ? "统计暂不可用（点赞会先记在本机）" : total.ToString());

        if (entry.IsOfficial)
        {
            ImGui.TextDisabled("来自卫月官方主库（Dip17）：本来就在你的库里，不用加库。");
            ImGui.Unindent(detailInset);
            return;
        }

        if (entry.RepositoryURL is not { Length: > 0 } url)
        {
            ImGui.TextDisabled("来源未知：旧词表没有记这条插件的库链。");
            ImGui.Unindent(detailInset);
            return;
        }

        ImGui.TextDisabled("库链");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(url);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("复制###copy"))
        {
            ImGui.SetClipboardText(url);
            SetStatus("已复制库链地址", isError: false);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(url);
        }

        if (entry.RepositoryKnown && !entry.RepositoryEnabled)
        {
            // 已加库但停用：详情里给一个启用入口（未加库的加库动作只在行尾，详情不重复）
            UiHelpers.SameLineOrWrap(UiHelpers.LabelWidth("启用"));
            UiHelpers.PushEnableButton();
            if (ImGui.SmallButton("启用###enable-expanded"))
            {
                EnableRepoFromRow(url);
            }

            UiHelpers.PopEnableButton();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(url);
            }
        }
        else if (entry.RepositoryKnown)
        {
            // 已在库：给出状态，不再给「加库」（加了也是重复）——用户 2026-10-07 点名要看得到这个状态
            ImGui.SameLine();
            ImGui.TextDisabled("已在库");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(url);
            }
        }

        ImGui.Unindent(detailInset);
    }

    private static void DrawKeyValue(string label, string value)
    {
        var startX = ImGui.GetCursorPosX();
        ImGui.TextDisabled(label);
        ImGui.SameLine(startX + ImGui.GetFontSize() * 5.2f);
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

        if (!entry.DeclaresIcon)
        {
            return false;
        }

        var installed = ToIconEntry(entry);

        // 本地缓存（盘上 / 已建纹理）或卫月内存缓存里有就画；纹理还在解码时这一帧先画字母块
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

        // 还没拿到：交给视口队列去下（下过 / 失败过的不重复排）
        this.QueueDiscoveryIcon(entry);

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
}
