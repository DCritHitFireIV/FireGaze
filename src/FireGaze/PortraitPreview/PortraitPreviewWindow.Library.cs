using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

/// <summary>Small, opt-in library surfaces for durable favorites and saved glamour plates.</summary>
internal sealed partial class PortraitPreviewWindow
{
    private bool showFavorites;
    private bool showGlamourPlates;
    private string favoriteName = "未命名肖像";
    private string favoriteTags = "";
    private string favoriteSearch = "";
    private readonly HashSet<string> favoriteTagFilter = new(StringComparer.Ordinal);
    private Guid? editingFavorite;
    private Guid? deletingFavorite;
    private string editingFavoriteName = "";
    private string editingFavoriteTags = "";

    public void OpenFavorites() => showFavorites = true;

    private void DrawLibraryToolbar()
    {
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        ContinueToolbar(right, ImGui.CalcTextSize("收藏当前").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button("收藏当前"))
        {
            feature.SaveFavorite($"肖像 {DateTime.Now:yyyy-MM-dd HH:mm:ss}", []);
            showFavorites = true;
        }
        ContinueToolbar(right, ImGui.CalcTextSize("收藏库").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button("收藏库")) showFavorites = true;
        ContinueToolbar(right, ImGui.CalcTextSize("投影模板").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button("投影模板")) showGlamourPlates = true;
        ContinueToolbar(right, ImGui.CalcTextSize("分析").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button("分析")) showAnalysis = true;
        ContinueToolbar(right, ImGui.CalcTextSize("主光方向").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button("主光方向")) showLight = true;
        ContinueToolbar(right, ImGui.CalcTextSize("空间灯光").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button("空间灯光")) showSpaceLight = true;
        ContinueToolbar(right, ImGui.CalcTextSize("色彩曲线").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button("色彩曲线")) { feature.SetLiveCaptureScope(PortraitImageScope.Portrait); showLightingFit = true; }
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
        if (!showFavorites) return;
        ImGui.SetNextWindowSizeConstraints(new Vector2(420, 240) * Scale, new Vector2(760, 840) * Scale);
        if (!ImGui.Begin("肖像收藏###FireGazePortraitFavorites", ref showFavorites))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("收藏保存完整的本地预览快照。它不保存游戏肖像，也不会改变真实装备。");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###PortraitFavoriteName", "收藏名称", ref favoriteName, 121);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###PortraitFavoriteTags", "标签，用逗号分隔", ref favoriteTags, 512);
        if (ImGui.Button("收藏当前肖像"))
        {
            var name = string.IsNullOrWhiteSpace(favoriteName)
                ? $"肖像 {DateTime.Now:yyyy-MM-dd HH:mm}"
                : favoriteName.Trim();
            var tags = favoriteTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            feature.SaveFavorite(name, tags);
        }
        if (!string.IsNullOrWhiteSpace(feature.FavoritesStatus))
            ImGui.TextWrapped(feature.FavoritesStatus);
        ImGui.Separator();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###PortraitFavoriteSearch", "搜索名称、标签或来源角色", ref favoriteSearch, 120);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("###PortraitFavoriteTagFilter", favoriteTagFilter.Count == 0 ? "全部标签" : $"标签 · {favoriteTagFilter.Count}"))
        {
            foreach (var tag in feature.Favorites.SelectMany(entry => entry.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var selected = favoriteTagFilter.Contains(tag);
                var checkbox = PortraitLibraryPresentation.TagCheckboxPresentation(tag);
                var changed = ImGui.Checkbox(checkbox.ID, ref selected);
                ImGui.SameLine();
                ImGui.TextUnformatted(checkbox.VisibleText);
                if (changed)
                {
                    if (selected) favoriteTagFilter.Add(tag); else favoriteTagFilter.Remove(tag);
                }
            }
            ImGui.EndCombo();
        }
        if (favoriteTagFilter.Count > 0 && ImGui.SmallButton("清除标签筛选")) favoriteTagFilter.Clear();
        DrawFavoriteMetadataEditor();

        var visible = PortraitLibraryPresentation.Filter(feature.Favorites, favoriteSearch, favoriteTagFilter);
        if (feature.Favorites.Count == 0)
            ImGui.TextDisabled("还没有收藏。满意时保存当前肖像，之后可从这里恢复。");
        else if (visible.Count == 0)
            ImGui.TextDisabled("没有符合条件的收藏。");
        else
        {
            ImGui.BeginChild("###PortraitFavoriteEntries", new Vector2(0, 0), false);
            var spacing = ImGui.GetStyle().ItemSpacing;
            float ButtonWidth(string label) => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
            var buttonWidths = new[] { ButtonWidth("载入当前预览"), ButtonWidth("编辑名称与标签"), ButtonWidth("确认删除"), ButtonWidth("保留") };
            // Reserve the widest confirmation state for every row, so clipper pitch never changes
            // as one row expands its delete controls or its explanation becomes longer.
            var layout = PortraitFavoriteRowLayout.For(ImGui.GetContentRegionAvail().X, Scale,
                ImGui.GetTextLineHeight(), ImGui.GetTextLineHeightWithSpacing(), spacing, buttonWidths);
            var rowHeight = layout.RowHeight;
            var clipper = new ImGuiListClipper();
            clipper.Begin(visible.Count, rowHeight);
            var photoRequests = 0;
            while (clipper.Step())
            for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var entry = visible[index];
                ImGui.PushID(entry.ID.ToString("N", CultureInfo.InvariantCulture));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
                ImGui.BeginChild("###PortraitFavoriteRow", new Vector2(0, rowHeight - spacing.Y), false);
                var origin = ImGui.GetCursorScreenPos();
                var imageSize = new Vector2(layout.PhotoWidth, layout.PhotoHeight);
                if (photoRequests++ < 32 && photos?.GetSnapshot(entry.SourceContentID, entry.Snapshot) is { } texture)
                    ImGui.Image(texture.Handle, imageSize);
                else
                {
                    ImGui.Dummy(imageSize);
                    var draw = ImGui.GetWindowDrawList();
                    draw.AddRectFilled(origin, origin + imageSize, ImGui.GetColorU32(FireGazePalette.Panel));
                    draw.AddText(origin + new Vector2(6, 50) * Scale, ImGui.GetColorU32(FireGazePalette.Muted), "暂无照片");
                }
                ImGui.SetCursorScreenPos(origin + new Vector2(layout.PhotoWidth + spacing.X, 0));
                ImGui.BeginChild("###PortraitFavoriteMetadata", new Vector2(layout.TextWidth, layout.MetadataHeight), false);
                ImGui.PushTextWrapPos(0);
                ImGui.TextUnformatted(entry.Name);
                ImGui.PushStyleColor(ImGuiCol.Text, FireGazePalette.Muted);
                ImGui.TextUnformatted($"{entry.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {entry.SourceName}");
                if (entry.Tags.Length > 0) ImGui.TextUnformatted("标签：" + string.Join("、", entry.Tags));
                var reason = feature.FavoriteAvailability(entry.ID);
                if (!string.IsNullOrWhiteSpace(reason)) ImGui.TextUnformatted(reason);
                ImGui.PopStyleColor();
                ImGui.PopTextWrapPos();
                ImGui.EndChild();

                bool ActionButton(int actionIndex, string label)
                {
                    ImGui.SetCursorScreenPos(origin + layout.Actions[actionIndex].Offset);
                    return ImGui.SmallButton(label);
                }
                using (UiHelpers.Disabled(!string.IsNullOrWhiteSpace(reason)))
                {
                    if (ActionButton(0, "载入当前预览")) feature.RestoreFavorite(entry.ID);
                }
                if (ActionButton(1, "编辑名称与标签"))
                {
                    editingFavorite = entry.ID;
                    editingFavoriteName = entry.Name;
                    editingFavoriteTags = string.Join(", ", entry.Tags);
                }
                if (deletingFavorite == entry.ID)
                {
                    if (ActionButton(2, "确认删除")) { feature.RemoveFavorite(entry.ID); deletingFavorite = null; }
                    if (ActionButton(3, "保留")) deletingFavorite = null;
                }
                else if (ActionButton(2, "删除")) deletingFavorite = entry.ID;
                ImGui.EndChild();
                ImGui.PopStyleVar();
                ImGui.PopID();
            }
            clipper.End();
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private void DrawFavoriteMetadataEditor()
    {
        if (editingFavorite is not { } id) return;
        ImGui.Separator();
        ImGui.TextUnformatted("名称与标签");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("###PortraitFavoriteEditName", ref editingFavoriteName, 121);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###PortraitFavoriteEditTags", "标签，用逗号分隔", ref editingFavoriteTags, 512);
        if (ImGui.Button("保存名称")) feature.RenameFavorite(id, editingFavoriteName.Trim());
        ImGui.SameLine();
        if (ImGui.Button("保存标签")) feature.TagFavorite(id,
            editingFavoriteTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        ImGui.SameLine();
        if (ImGui.Button("完成")) editingFavorite = null;
        ImGui.Separator();
    }

    private void DrawGlamourPlatesWindow()
    {
        if (!showGlamourPlates) return;
        ImGui.SetNextWindowSizeConstraints(new Vector2(380, 220) * Scale, new Vector2(760, 720) * Scale);
        if (!ImGui.Begin("投影模板###FireGazePortraitGlamourPlates", ref showGlamourPlates))
        {
            ImGui.End();
            return;
        }

        var view = feature.GlamourPlateView;
        if (view is null)
        {
            ImGui.TextWrapped("尚未读取投影模板。打开一次投影台后回来，FireGaze 会保存本地读取记录。");
            ImGui.End();
            return;
        }
        ImGui.TextWrapped(view.Reason);
        if (view.Status != OwnedReadStatus.Current)
            ImGui.TextDisabled(view.Status == OwnedReadStatus.PossiblyStale ? "使用已保存记录；重新打开投影台可刷新。" : "模板尚未在当前登录中读取。" );
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
