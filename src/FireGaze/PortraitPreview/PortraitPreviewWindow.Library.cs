using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

/// <summary>Image-first library; the view reads detached entries and queues Framework actions.</summary>
internal sealed partial class PortraitPreviewWindow
{
    private bool showFavorites;
    private bool showGlamourPlates;
    private bool showFavoriteTrash;
    private string favoriteSearch = "";
    private readonly HashSet<string> favoriteTagFilter = new(StringComparer.Ordinal);
    private Guid? editingFavorite;
    private Guid? deletingFavorite;
    private Guid? selectedFavorite;
    private bool selectedFavoriteInTrash;
    private bool openFavoriteActions;
    private bool openFavoriteEdit;
    private string editingFavoriteName = "";
    private string editingFavoriteTags = "";

    public void OpenFavorites()
    {
        if (!showFavorites) favoriteGallery.ResetWindow();
        showFavorites = true;
        showFavoriteTrash = false;
    }

    private void DrawLibraryPopups()
    {
        DrawFavoritesWindow();
        DrawGlamourPlatesWindow();
        DrawAnalysisWindow();
        DrawLightWindow();
        DrawLightingSpaceWindow();
        DrawLightingFitWindow();
    }

    private void DrawFavoritesWindow()
    {
        var observation = favoriteGallery.Begin(FavoriteGalleryContext, showFavorites, showFavoriteTrash);
        if (!showFavorites) return;
        FireGazeWindowPlacement.Prepare("FireGazePortraitFavorites", new Vector2(720, 700) * Scale, Scale, "FireGazePortraitPreview");
        FireGazeWindowPlacement.Constrain(new Vector2(430, 300) * Scale, new Vector2(1040, 920) * Scale, Scale);
        if (!FireGazeWindowPlacement.Begin("肖像收藏###FireGazePortraitFavorites", ref showFavorites, "FireGazePortraitFavorites"))
        {
            ImGui.End();
            favoriteGallery.Complete(observation, FavoriteGalleryContext, showFavorites, showFavoriteTrash, false, []);
            return;
        }

        if (FireGazeSymbols.NeutralButton($"我的收藏 · {feature.Favorites.Count}", default, !showFavoriteTrash)) showFavoriteTrash = false;
        ImGui.SameLine();
        if (FireGazeSymbols.NeutralButton($"回收站 · {feature.FavoriteTrash.Count}", default, showFavoriteTrash)) showFavoriteTrash = true;
        ImGui.Spacing();
        ImGui.TextWrapped(showFavoriteTrash ? "已删除的收藏可在这里恢复。永久删除前会再次确认。"
            : "在同步预览工作台点击“收藏”保存当前效果。点击照片选择操作。");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###PortraitFavoriteSearch", "搜索名称、标签或来源角色", ref favoriteSearch, 120);
        var renderedTrash = showFavoriteTrash;
        var source = renderedTrash ? feature.FavoriteTrash : feature.Favorites;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("###PortraitFavoriteTagFilter", favoriteTagFilter.Count == 0 ? "全部标签" : $"标签 · {favoriteTagFilter.Count}"))
        {
            foreach (var tag in source.SelectMany(entry => entry.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var selected = favoriteTagFilter.Contains(tag);
                var checkbox = PortraitLibraryPresentation.TagCheckboxPresentation(tag);
                if (ImGui.Checkbox(checkbox.ID, ref selected))
                { if (selected) favoriteTagFilter.Add(tag); else favoriteTagFilter.Remove(tag); }
                ImGui.SameLine();
                ImGui.TextUnformatted(checkbox.VisibleText);
            }
            ImGui.EndCombo();
        }
        if (favoriteTagFilter.Count > 0 && ImGui.SmallButton("清除标签筛选")) favoriteTagFilter.Clear();
        if (!string.IsNullOrWhiteSpace(feature.FavoritesStatus)) ImGui.TextWrapped(feature.FavoritesStatus);
        ImGui.Separator();
        ImGui.Spacing();
        // Child owns scrolling while navigation, filters and status remain in place.
        var observedCards = new List<PortraitFavoriteGalleryCard>(PortraitFavoriteGalleryPublisher.MaximumCards + 1);
        if (ImGui.BeginChild("###PortraitFavoriteGallery", Vector2.Zero, false))
        {
            var visible = PortraitLibraryPresentation.Filter(source, favoriteSearch, favoriteTagFilter);
            if (visible.Count == 0)
                ImGui.TextWrapped(source.Count == 0
                    ? showFavoriteTrash ? "回收站是空的。" : "还没有收藏。请在同步预览工作台点击“收藏”。"
                    : "没有符合条件的收藏。");
            else DrawFavoriteMasonry(visible, observedCards);
        }
        ImGui.EndChild();
        DrawFavoriteCardActions();
        DrawFavoriteMetadataEditor();
        ImGui.End();
        // Navigation can reenter through a draw boundary. A changed tab cannot
        // relabel rows already submitted from the previous collection.
        if (showFavoriteTrash != renderedTrash) return;
        favoriteGallery.Complete(observation, FavoriteGalleryContext, showFavorites, renderedTrash, true, observedCards);
    }

    private void DrawFavoriteMasonry(IReadOnlyList<PortraitFavoriteEntry> entries,
        List<PortraitFavoriteGalleryCard> observedCards)
    {
        var layout = PortraitFavoriteMasonryLayout.For(ImGui.GetContentRegionAvail().X, Scale);
        var inset = 8 * Scale;
        var textWidth = Math.Max(1, layout.CardWidth - inset * 2);
        var imageSize = new Vector2(textWidth, textWidth * 1.6f);
        var heights = entries.Select(entry => inset * 3 + imageSize.Y + ImGui.GetStyle().ItemSpacing.Y
            + ImGui.CalcTextSize(entry.Name, false, textWidth).Y + ImGui.GetTextLineHeight()).ToArray();
        var positions = layout.Place(heights);
        var origin = ImGui.GetCursorScreenPos();
        var bottom = 0f;
        var photoRequests = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            var placement = positions[index];
            bottom = Math.Max(bottom, placement.Y + placement.Height);
            var min = origin + new Vector2(placement.X, placement.Y);
            var max = min + new Vector2(layout.CardWidth, placement.Height);
            var visible = ImGui.IsRectVisible(min, max);
            var entry = entries[index];
            ImGui.PushID(entry.ID.ToString("N", CultureInfo.InvariantCulture));
            ImGui.SetCursorScreenPos(min);
            var clicked = FireGazeSymbols.NeutralButton("###PortraitFavoriteCard", new(layout.CardWidth, placement.Height),
                selectedFavorite == entry.ID && selectedFavoriteInTrash == showFavoriteTrash);
            var hovered = ImGui.IsItemHovered();
            if (clicked)
            {
                selectedFavorite = entry.ID;
                selectedFavoriteInTrash = showFavoriteTrash;
                deletingFavorite = null;
                openFavoriteActions = true;
            }
            // Submit off-screen buttons to keyboard navigation too, while only visible
            // cards can request or draw a GPU texture.
            if (!visible) { ImGui.PopID(); continue; }
            var imageSubmitted = false;
            var imageOrigin = min + new Vector2(inset);
            ImGui.SetCursorScreenPos(imageOrigin);
            if (photoRequests++ < 32 && photos?.GetSnapshot(entry.SourceContentID, entry.Snapshot, entry.Source) is { } texture)
            {
                ImGui.Image(texture.Handle, imageSize);
                imageSubmitted = true;
            }
            else
            {
                ImGui.Dummy(imageSize);
                var draw = ImGui.GetWindowDrawList();
                draw.AddRectFilled(imageOrigin, imageOrigin + imageSize, ImGui.GetColorU32(FireGazePalette.Raised));
                var status = photos?.FavoritePhotoStatus(entry.SourceContentID, entry.Snapshot, entry.Source) ?? PortraitFavoritePhotoStatus.Missing;
                var label = PortraitHistoryPhotoPresentation.FavoriteLabel(status);
                // Wrap within this card, including storage errors; never bleed into its neighbor.
                ImGui.SetCursorScreenPos(imageOrigin + new Vector2(inset, imageSize.Y * .44f));
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(1, textWidth - inset * 2));
                ImGui.TextUnformatted(label);
                ImGui.PopTextWrapPos();
            }
            ImGui.SetCursorScreenPos(new(min.X + inset, imageOrigin.Y + imageSize.Y + inset));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + textWidth);
            ImGui.TextUnformatted(entry.Name);
            ImGui.PopTextWrapPos();
            ImGui.PushStyleColor(ImGuiCol.Text, FireGazePalette.Muted);
            ImGui.TextUnformatted($"{entry.UpdatedAt.LocalDateTime:yyyy-MM-dd}");
            ImGui.PopStyleColor();
            if (hovered) ImGui.SetTooltip($"{entry.Name}\n{entry.SourceName}\n{string.Join(" · ", entry.Tags)}");
            ImGui.PopID();
            // Keep one overflow row solely to report truncation. Missing rows
            // are unknown coverage, never evidence that a card does not exist.
            if (observedCards.Count <= PortraitFavoriteGalleryPublisher.MaximumCards)
                observedCards.Add(new(entry.ID, entry.SourceContentID, true, imageSubmitted));
        }
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(1, bottom + layout.Gap));
    }

    private void DrawFavoriteCardActions()
    {
        if (openFavoriteActions)
        { ImGui.OpenPopup("###PortraitFavoriteCardActions"); openFavoriteActions = false; }
        ImGui.SetNextWindowSizeConstraints(new Vector2(240, 0) * Scale, new Vector2(420, 640) * Scale);
        if (!ImGui.BeginPopup("###PortraitFavoriteCardActions")) return;
        var entry = (selectedFavoriteInTrash ? feature.FavoriteTrash : feature.Favorites)
            .FirstOrDefault(value => value.ID == selectedFavorite);
        if (entry is null) ImGui.CloseCurrentPopup();
        else
        {
            ImGui.TextWrapped(entry.Name);
            ImGui.Separator();
            if (selectedFavoriteInTrash)
            {
                if (ImGui.MenuItem("恢复收藏")) { feature.RestoreDeletedFavorite(entry.ID); ImGui.CloseCurrentPopup(); }
                if (deletingFavorite == entry.ID)
                {
                    ImGui.TextWrapped("永久删除后无法从回收站恢复。");
                    if (ImGui.Button("确认永久删除")) { feature.PermanentlyRemoveFavorite(entry.ID); deletingFavorite = null; ImGui.CloseCurrentPopup(); }
                    ImGui.SameLine();
                    if (ImGui.Button("取消")) deletingFavorite = null;
                }
                else if (ImGui.Selectable("永久删除…", false, ImGuiSelectableFlags.DontClosePopups)) deletingFavorite = entry.ID;
            }
            else
            {
                var reason = feature.FavoriteAvailability(entry.ID);
                using (UiHelpers.Disabled(!string.IsNullOrWhiteSpace(reason)))
                    if (ImGui.MenuItem("载入当前预览")) feature.RestoreFavorite(entry.ID);
                if (!string.IsNullOrWhiteSpace(reason)) ImGui.TextWrapped(reason);
                if (ImGui.MenuItem("编辑名称与标签"))
                {
                    editingFavorite = entry.ID;
                    editingFavoriteName = entry.Name;
                    editingFavoriteTags = string.Join(", ", entry.Tags);
                    openFavoriteEdit = true;
                }
                if (ImGui.MenuItem("删除 · 移入回收站"))
                { editingFavorite = null; feature.RemoveFavorite(entry.ID); ImGui.CloseCurrentPopup(); }
            }
        }
        ImGui.EndPopup();
    }

    private void DrawFavoriteMetadataEditor()
    {
        if (openFavoriteEdit)
        { ImGui.OpenPopup("###PortraitFavoriteEdit"); openFavoriteEdit = false; }
        ImGui.SetNextWindowSizeConstraints(new Vector2(300, 0) * Scale, new Vector2(480, 480) * Scale);
        if (!ImGui.BeginPopup("###PortraitFavoriteEdit")) return;
        if (editingFavorite is not { } id || !feature.Favorites.Any(entry => entry.ID == id))
        { editingFavorite = null; ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        ImGui.TextUnformatted("编辑收藏");
        ImGui.Spacing();
        ImGui.TextUnformatted("名称");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("###PortraitFavoriteEditName", ref editingFavoriteName, 121);
        ImGui.TextUnformatted("标签");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###PortraitFavoriteEditTags", "标签，用逗号分隔", ref editingFavoriteTags, 512);
        if (ImGui.Button("保存修改"))
        {
            feature.EditFavorite(id, editingFavoriteName.Trim(), editingFavoriteTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            editingFavorite = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("取消")) { editingFavorite = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private void DrawGlamourPlatesWindow()
    {
        if (!showGlamourPlates) return;
        FireGazeWindowPlacement.Prepare("FireGazePortraitGlamourPlates", new Vector2(440, 600) * Scale, Scale, "FireGazePortraitPreview");
        FireGazeWindowPlacement.Constrain(new Vector2(380, 220) * Scale, new Vector2(760, 720) * Scale, Scale);
        if (!FireGazeWindowPlacement.Begin("投影模板###FireGazePortraitGlamourPlates", ref showGlamourPlates, "FireGazePortraitGlamourPlates"))
        {
            ImGui.End();
            return;
        }

        var view = feature.GlamourPlateView;
        if (view is null)
        {
            ImGui.TextWrapped("尚未读取投影模板。打开投影模板后即可读取，FireGaze 会保存本地读取记录。");
            ImGui.End();
            return;
        }
        ImGui.TextWrapped(PortraitGlamourPlateGuidance.ForPlayer(view.Reason));
        if (view.Status != OwnedReadStatus.Current)
            ImGui.TextDisabled(view.Status == OwnedReadStatus.PossiblyStale ? "使用已保存记录；重新打开投影模板可刷新。" : "模板尚未在当前登录中读取。" );
        ImGui.Separator();
        ImGui.BeginChild("###PortraitGlamourPlateEntries", new Vector2(0, 0), false);
        for (var index = 0; index < 20; index++)
        {
            var plate = view.Plates.FirstOrDefault(item => item.Index == index);
            var filled = plate?.Pieces.Count(piece => piece.ItemID != 0) ?? 0;
            ImGui.PushID(index);
            ImGui.TextUnformatted($"投影模板 {index + 1:00}");
            ImGui.SameLine();
            ImGui.TextDisabled(filled == 0 ? "空" : $"{filled} 个装备部位");
            using (UiHelpers.Disabled(plate is null || filled == 0))
            {
                if (ImGui.SmallButton("预览")) feature.ApplyGlamourPlate(index);
            }
            ImGui.PopID();
        }
        ImGui.EndChild();
        ImGui.End();
    }
}
