using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using FireGaze.LocalPreview.Interaction;
using FireGaze.UI;
using MenuKind = FireGaze.LocalPreview.Interaction.PreviewKind;

namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    private string mountSearch = string.Empty;
    private string accessorySearch = string.Empty;

    private void DrawMountCollection()
    {
        DrawCatalogSearchAndScale("###LocalPreviewMountSearch", "搜索坐骑", ref mountSearch,
            "###LocalPreviewStopAllMounts");
        DrawMountCatalogViews();
        var view = ReadCatalogControls().MountView;
        var currentCatalog = catalog;
        var allMounts = currentCatalog.SearchMounts(view, "");
        var query = mountSearch.Trim();
        var mounts = currentCatalog.SearchMounts(view, query);
        var presentation = ReadMountPresentation();
        var page = TrialCatalogPresentation.Create(presentation, allMounts.Count, mounts.Count, query);
        DrawTrialCatalogSummary(page);
        DrawTrialCatalogContent("###LocalPreviewMountContent", mounts, presentation, page.EmptyText);
    }

    private void DrawMountCatalogViews()
    {
        const string regularLabel = "常规坐骑";
        const string specialLabel = "剧情与特殊";
        var left = ImGui.GetCursorPosX();
        var availableWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var regularWidth = ImGui.CalcTextSize(regularLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
        var specialWidth = ImGui.CalcTextSize(specialLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
        if (FireGazeSymbols.NeutralButton(regularLabel + "###LocalPreviewMountRegular",
            new Vector2(MathF.Min(regularWidth, availableWidth), ImGui.GetFrameHeight()),
            selected: ReadCatalogControls().MountView == MountCatalogView.Regular))
            UpdateCatalogControls(TrialCatalogAction.Regular);
        if (regularWidth + specialWidth + ImGui.GetStyle().ItemSpacing.X <= availableWidth)
            ImGui.SameLine();
        else
            ImGui.SetCursorPosX(left);
        if (FireGazeSymbols.NeutralButton(specialLabel + "###LocalPreviewMountSpecial",
            new Vector2(MathF.Min(specialWidth, availableWidth), ImGui.GetFrameHeight()),
            selected: ReadCatalogControls().MountView == MountCatalogView.Special))
            UpdateCatalogControls(TrialCatalogAction.Special);
    }

    private void DrawAccessoryCollection()
    {
        var allAccessories = catalog.Search(MenuKind.Accessory, "");
        var query = accessorySearch.Trim();
        var accessories = catalog.Search(MenuKind.Accessory, query);
        var presentation = ReadAccessoryPresentation();
        var page = TrialCatalogPresentation.Create(presentation, allAccessories.Count, accessories.Count, query, "时尚配饰");
        DrawCatalogSearchAndScale("###LocalPreviewAccessorySearch", "搜索时尚配饰", ref accessorySearch,
            "###LocalPreviewStopAllAccessories");
        DrawTrialCatalogSummary(page);
        DrawTrialCatalogContent("###LocalPreviewAccessoryContent", accessories, presentation, page.EmptyText);
    }

    private void DrawTrialCatalogContent(string id, IReadOnlyList<PreviewCatalogEntry> entries,
        TrialPresentation presentation, string emptyText)
    {
        var visible = ImGui.BeginChild(id, Vector2.Zero, false);
        try
        {
            if (!visible) return;
            if (entries.Count == 0)
            {
                ImGui.TextDisabled(emptyText);
                return;
            }
            DrawTrialIconTable(id + "Masonry", entries,
                (entry, _) => { if (presentation.CanStart) Request(entry.Kind, entry.Id); },
                isSelected: (entry, _) => presentation.Phase == TrialPhase.Active
                    && entry.Id == presentation.ActiveId,
                isDisabled: (_, _) => !presentation.CanStart);
        }
        finally { ImGui.EndChild(); }
    }

    private static void DrawTrialCatalogSummary(TrialCatalogPresentation page)
    {
        var left = ImGui.GetCursorPosX();
        var available = ImGui.GetContentRegionAvail().X;
        var countWidth = ImGui.CalcTextSize(page.CountText).X;
        if (page.StatusText.Length != 0)
        {
            var statusColor = page.Phase switch
            {
                TrialPhase.Active => FireGazePalette.GoodText,
                TrialPhase.Switching or TrialPhase.Stopping => FireGazePalette.Warn,
                TrialPhase.Unknown => FireGazePalette.BadText,
                _ => FireGazePalette.Muted,
            };
            UiHelpers.ColoredWrapped(statusColor, page.StatusText);
            if (ImGui.CalcTextSize(page.StatusText).X + ImGui.GetStyle().ItemSpacing.X + countWidth <= available)
                ImGui.SameLine(left + available - countWidth);
        }
        ImGui.TextDisabled(page.CountText);
    }

    private void DrawCatalogSearchAndScale(string id, string hint, ref string search)
        => DrawCatalogSearchAndScale(id, hint, ref search, null);

    private void DrawCatalogSearchAndScale(string id, string hint, ref string search, string? stopButtonID)
    {
        var left = ImGui.GetCursorPosX();
        var availableWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var scaleWidth = ImGui.GetFrameHeight() * 2f + ImGui.GetStyle().ItemSpacing.X;
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var textScale = ImGui.GetFontSize() / 17f;
        var stopWidth = stopButtonID is null ? 0f
            : ImGui.CalcTextSize("结束全部试用").X + ImGui.GetStyle().FramePadding.X * 2f;
        var minimumSearchWidth = MathF.Min(180f * textScale, availableWidth);
        var scaleInline = availableWidth >= minimumSearchWidth + scaleWidth + gap;
        var stopInline = stopButtonID is not null
            && availableWidth >= minimumSearchWidth + scaleWidth + stopWidth + gap * 2f;
        var inputWidth = MathF.Min(320f * textScale, scaleInline
            ? availableWidth - scaleWidth - gap - (stopInline ? stopWidth + gap : 0f)
            : availableWidth);
        ImGui.SetNextItemWidth(MathF.Max(1f, inputWidth));
        if (ImGui.InputTextWithHint(id, hint, ref search, 96)) search = search.TrimStart();
        if (scaleInline) ImGui.SameLine();
        else ImGui.SetCursorPosX(left);
        DrawTrialIconControls();
        if (stopButtonID is not null)
        {
            var stopLeft = left + MathF.Max(0f, availableWidth - stopWidth);
            if (stopInline || !scaleInline && availableWidth >= scaleWidth + stopWidth + gap)
                ImGui.SameLine(stopLeft);
            else ImGui.SetCursorPosX(stopLeft);
            DrawStopAllAction(stopButtonID);
        }
    }

    private void DrawTrialCollectionHeader(string buttonID)
    {
        const string label = "结束全部试用";
        var contentLeft = ImGui.GetCursorPosX();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var contentRight = contentLeft + availableWidth;
        var buttonWidth = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SetCursorPosX(MathF.Max(contentLeft, contentRight - buttonWidth));
        DrawStopAllAction(buttonID);
    }

    private void DrawStopAllAction(string buttonID)
    {
        const string label = "结束全部试用";
        var mountState = ReadMountPresentation();
        var accessoryState = ReadAccessoryPresentation();
        var stopping = mountState.Phase == TrialPhase.Stopping || accessoryState.Phase == TrialPhase.Stopping;
        var hasTrials = mountState.CanStop || accessoryState.CanStop || ReadActiveEffects().Length != 0
            || System.Threading.Volatile.Read(ref petPresentation).Leases.Length != 0;
        using (UiHelpers.Disabled(stopping || !hasTrials))
        {
            UiHelpers.PushPrimaryButton();
            try
            {
                if (ImGui.Button(label + buttonID, new Vector2(0, ImGui.GetFrameHeight()))) Cancel();
            }
            finally
            {
                UiHelpers.PopPrimaryButton();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) || ImGui.IsItemFocused())
            ImGui.SetTooltip(stopping ? "正在结束试用" : !hasTrials ? "当前没有试用"
                : "结束坐骑、宠物和时尚配饰的全部试用");
    }

    private void DrawTrialIconControls()
    {
        var trialIconSize = ReadCatalogControls().IconSize;
        if (FireGazeSymbols.StrokeButton("###LocalPreviewIconSmaller",
                trialIconSize <= 40f ? "图标已是最小尺寸" : "缩小图标", trialIconSize > 40f, plus: false))
            UpdateCatalogControls(TrialCatalogAction.Smaller);
        ImGui.SameLine();
        if (FireGazeSymbols.StrokeButton("###LocalPreviewIconLarger",
                trialIconSize >= 112f ? "图标已是最大尺寸" : "放大图标", trialIconSize < 112f, plus: true))
            UpdateCatalogControls(TrialCatalogAction.Larger);
    }

    private void DrawTrialIconTable(string id, IReadOnlyList<PreviewCatalogEntry> entries,
        Action<PreviewCatalogEntry, int> onLeftClick, Action<PreviewCatalogEntry, int>? onRightClick = null,
        Func<PreviewCatalogEntry, int, bool>? isSelected = null,
        Func<PreviewCatalogEntry, int, bool>? isDisabled = null)
    {
        if (entries.Count == 0)
        {
            ImGui.TextDisabled("暂时没有可试用的项目。");
            return;
        }

        ImGui.PushID(id);
        var contentWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var layout = PreviewIconMasonryLayout.For(contentWidth, ReadCatalogControls().IconSize, ImGui.GetIO().FontGlobalScale);
        var placements = layout.Place(Enumerable.Repeat(layout.CardWidth, entries.Count).ToArray());
        var origin = ImGui.GetCursorScreenPos();
        var bottom = 0f;
        for (var index = 0; index < entries.Count; index++)
        {
            var placement = placements[index];
            var position = origin + new Vector2(placement.X, placement.Y);
            bottom = MathF.Max(bottom, placement.Y + placement.Height);
            if (!ImGui.IsRectVisible(position, position + new Vector2(layout.CardWidth, placement.Height))) continue;
            ImGui.SetCursorScreenPos(position);
            ImGui.PushID($"{entries[index].Kind}:{entries[index].Id}:{index}");
            var entry = entries[index];
            var clicked = DrawTrialIcon(entry, isSelected?.Invoke(entry, index) == true,
                layout.CardWidth, () => onRightClick?.Invoke(entry, index),
                isDisabled?.Invoke(entry, index) == true);
            if (clicked) onLeftClick(entries[index], index);
            ImGui.PopID();
        }
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(contentWidth, bottom + layout.Gap));
        ImGui.PopID();
    }

    private bool DrawTrialIcon(PreviewCatalogEntry entry, bool selected = false, float? requestedSize = null,
        Action? onRightClick = null, bool disabled = false)
    {
        var iconSize = Math.Max(1f, requestedSize ?? ReadCatalogControls().IconSize);
        var size = new Vector2(iconSize);
        var position = ImGui.GetCursorScreenPos();
        bool clicked;
        using (UiHelpers.Disabled(disabled)) clicked = ImGui.Button("###TrialIcon", size);
        if (!disabled && ImGui.IsItemClicked(ImGuiMouseButton.Right)) onRightClick?.Invoke();
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        var focused = ImGui.IsItemFocused();
        var draw = ImGui.GetWindowDrawList();
        var inset = new Vector2(iconSize * 0.06f);
        var drawn = false;
        var loading = false;
        if (entry.Icon > 0 && textures is { } provider
            && provider.TryGetFromGameIcon(new GameIconLookup(entry.Icon), out var texture))
        {
            if (texture.TryGetWrap(out var icon, out var error))
            {
                draw.AddImage(icon.Handle, position + inset, position + size - inset, Vector2.Zero, Vector2.One);
                drawn = true;
            }
            else if (error is null) loading = true;
            // An in-flight shared texture is already loading. Ask for the same
            // icon at standard resolution only after an explicit HD failure.
            else if (provider.TryGetFromGameIcon(new GameIconLookup(entry.Icon, hiRes: false), out var standard))
            {
                if (standard.TryGetWrap(out icon, out error))
                {
                    draw.AddImage(icon.Handle, position + inset, position + size - inset, Vector2.Zero, Vector2.One);
                    drawn = true;
                }
                else loading = error is null;
            }
        }
        var unnamed = entry.Kind == MenuKind.Mount && entry.Name.StartsWith("未命名坐骑", StringComparison.Ordinal);
        if (!drawn)
        {
            var lineHeight = MathF.Min(ImGui.GetFontSize(), (iconSize - inset.Y * 2f) / 2f);
            var textScale = lineHeight / ImGui.GetFontSize();
            string Fit(string value)
            {
                if (ImGui.CalcTextSize(value).X * textScale <= iconSize - inset.X * 2f) return value;
                while (value.Length > 0 && ImGui.CalcTextSize(value + "…").X * textScale > iconSize - inset.X * 2f)
                    value = value[..^1];
                return value + "…";
            }
            var mark = Fit(loading ? "加载中" : "无图标");
            var label = Fit(unnamed ? $"#{entry.Id}" : entry.Name);
            var top = position.Y + (iconSize - lineHeight * 2f) / 2f;
            draw.AddText(ImGui.GetFont(), lineHeight,
                new(position.X + (iconSize - ImGui.CalcTextSize(mark).X * textScale) / 2f, top),
                ImGui.GetColorU32(FireGazePalette.Muted), mark);
            draw.AddText(ImGui.GetFont(), lineHeight,
                new(position.X + (iconSize - ImGui.CalcTextSize(label).X * textScale) / 2f, top + lineHeight),
                ImGui.GetColorU32(FireGazePalette.Muted), label);
        }
        FireGazeSymbols.DrawSelection(position, position + size, selected, focused);
        if (hovered || focused)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(unnamed ? $"{entry.Name} · #{entry.Id}" : entry.Name);
            if (!drawn) ImGui.TextUnformatted(loading ? "图标加载中" : "暂无图标");
            ImGui.EndTooltip();
        }
        return clicked;
    }
}
