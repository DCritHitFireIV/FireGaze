using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

/// <summary>The window reads snapshots; the feature owns Framework-thread changes and editor identity checks.</summary>
internal interface IPortraitPreviewViewModel
{
    bool SourceCensusBlocksOperations => false;
    bool IsActive { get; }
    PortraitLook? Current { get; }
    string Status { get; }
    IReadOnlyList<string> SourceSummary { get; }
    IReadOnlyList<PortraitDye> Dyes { get; }
    IReadOnlyList<PortraitBrowserItem> GetItems(PortraitSlot slot);
    IReadOnlyList<OwnedSourceView> Sources => [];
    OwnedCharacterKey? SourceCharacter => null;
    OwnedRetainerRoster? RetainerRoster => null;
    string SourceGuideKey => string.Empty;
    IReadOnlyList<string> StorageLocations(uint id) => [];
    void EquipFromSources(PortraitSlot slot, uint id, IReadOnlyCollection<OwnedSourceKind> sources) => Equip(slot, id);
    PortraitBrowserItem? GetItem(PortraitSlot slot, uint id);
    void Equip(PortraitSlot slot, uint id);
    void Clear(PortraitSlot slot);
    void Dye(PortraitSlot slot, int channel, byte stain);
    void SetVisibility(bool head, bool weapon, bool visor);
    void ClosePreview();
    bool SyncPlate { get; }
    void SetSyncPlate(bool enabled);
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Undo();
    void Redo();
    IReadOnlyList<PortraitHistoryListItem> History { get; }
    string HistoryStatus { get; }
    void RestoreHistory(long id);
    IReadOnlyList<PortraitAppearanceGroup> AppearanceGroups { get; }
    void ChangeAppearance(string groupID, int value);

    // Portrait library surfaces are optional so older view models and offline fixtures remain valid.
    PortraitGlamourPlateView? GlamourPlateView => null;
    void ApplyGlamourPlate(int index) { }
    IReadOnlyList<PortraitFavoriteEntry> Favorites => [];
    string FavoritesStatus => string.Empty;
    string FavoriteAvailability(Guid id) => string.Empty;
    void SaveFavorite(string name, IReadOnlyCollection<string> tags) { }
    void RestoreFavorite(Guid id) { }
    void RenameFavorite(Guid id, string name) { }
    void TagFavorite(Guid id, IReadOnlyCollection<string> tags) { }
    void RemoveFavorite(Guid id) { }
    PortraitCapturedImage? AcquireCurrentLiveImage() => null;
    bool NativeLiveEnabled => false;
    void SetNativeLiveEnabled(bool enabled) { }
    PortraitNativeLiveBorrow? TryBorrowNativeLive() => null;
    PortraitImageScope LiveCaptureScope => PortraitImageScope.Portrait;
    void SetLiveCaptureScope(PortraitImageScope scope) { }
    PortraitDirectionalLightView? DirectionalLight => null;
    PortraitComposition? CurrentComposition => null;
    PortraitLightingSpaceBinding? CurrentLightingSpaceBinding => null;
    void ApplyLightingSpace(PortraitLightingSpaceBinding expected, PortraitComposition desired) { }
    void BeginLightDrag(long token) { }
    void MoveLightDrag(long token, float vertical, float horizontal) { }
    void EndLightDrag(long token, float vertical, float horizontal) { }
    void CancelLightDrag(long token) { }
    PortraitLightingFitView? LightingFit => null;
    void StartLightingFit(PortraitColorToneTarget target, PortraitSampleRegion region) { }
    void StartLightingFit(PortraitColorToneTarget target, PortraitSampleRegion region, string? expectedFixedStateKey)
        => StartLightingFit(target, region);
    void CancelLightingFit() { }
    bool OwnedViewportVisible => false;
    bool OwnedViewportExperimentActive => false;
    string OwnedViewportStatus => string.Empty;
    PortraitOwnedSceneObserverCameraSnapshot? OwnedObserver => null;
    PortraitOwnedSceneTextureLifetime<IDalamudTextureWrap>.Borrow? TryBorrowOwnedViewport() => null;
    void SetOwnedObserver(PortraitOwnedSceneObserverCameraSnapshot camera) { }
    void NudgeOwnedObserver(Vector3 move) { }
    void ResetOwnedObserver() { }
    void CloseOwnedViewportView() { }
}

/// <summary>Independent wardrobe UI using game-local textures, without Dresser code or plugin IPC.</summary>
internal sealed partial class PortraitPreviewWindow : Window, IDisposable
{
    private static readonly PortraitSlot[] Slots =
    [
        PortraitSlot.MainHand, PortraitSlot.OffHand,
        PortraitSlot.Head, PortraitSlot.Ears,
        PortraitSlot.Body, PortraitSlot.Neck,
        PortraitSlot.Hands, PortraitSlot.Wrists,
        PortraitSlot.Legs, PortraitSlot.RightRing,
        PortraitSlot.Feet, PortraitSlot.LeftRing,
        PortraitSlot.Facewear,
    ];

    private readonly IPortraitPreviewViewModel feature;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ITextureProvider textures;
    private readonly PortraitHistoryPhotos? photos;
    private readonly Dictionary<string, UldWrapper?> ulds = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Uld, string Texture, int Part), IDalamudTextureWrap?> parts = new();
    private PortraitSlot selectedSlot = PortraitSlot.Head;
    private PortraitSlot dyeSlot;
    private int dyeChannel;
    private string search = string.Empty;
    private bool disposed;
    private bool appearanceTab;
    private string selectedAppearance = "";
    private bool showHistory;
    private PortraitBrowserItem? inspectedItem;
    private readonly HashSet<OwnedSourceKind> selectedSources = [];
    private readonly HashSet<byte> selectedDyes = [];
    private PortraitEquipmentSort equipmentSort;
    private IReadOnlyList<PortraitBrowserItem>? cachedCatalog;
    private IReadOnlyList<PortraitBrowserItem> filteredItems = [];
    private string cachedSearch = "";
    private bool filtersChanged = true;
    private bool showSourceGuide;
    private readonly PortraitSourceGuideState sourceGuide = new();
    private string lastSourceOwner = "";
    private PortraitEditorBounds? ownerBounds;

    public void SetOwnerBounds(PortraitEditorBounds bounds) => ownerBounds = bounds;

    public PortraitPreviewWindow(IPortraitPreviewViewModel feature, IDalamudPluginInterface pluginInterface, ITextureProvider textures, PortraitHistoryPhotos? photos = null)
        : base("肖像同步预览###FireGazePortraitPreview", ImGuiWindowFlags.None)
    {
        this.feature = feature;
        this.pluginInterface = pluginInterface;
        this.textures = textures;
        this.photos = photos;
        Size = new Vector2(920, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(700, 540) };
    }

    public override void OnOpen()
    {
        selectedSlot = PortraitSlot.Head;
        search = string.Empty;
        if (lastSourceOwner != feature.SourceGuideKey)
        {
            lastSourceOwner = feature.SourceGuideKey;
            selectedSources.Clear();
            selectedDyes.Clear();
            cachedCatalog = null;
            filtersChanged = true;
        }
        showSourceGuide = sourceGuide.ShouldShow(feature.SourceGuideKey,
            feature.Sources.Any(source => source.Status == OwnedReadStatus.Unread));
        ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
    }

    public override void OnClose()
    {
        feature.ClosePreview();
        if (!feature.IsActive) CloseAuxiliaryWindows();
    }

    public void CloseAuxiliaryWindows()
    {
        showHistory = showFavorites = showGlamourPlates = showAnalysis = showLight = showLightingFit = showSpaceLight = showSourceGuide = false;
        BindLightingSubject(null);
        editingFavorite = deletingFavorite = null;
        // Feature lifetime cleanup owns its gesture; do not resample a retiring editor from a UI callback.
        lightPointerToken = 0;
        analysis.Invalidate();
        ReleaseAnalysisPreview();
        analysisRecord = 0;
        analysisInputStatus = "";
        ClearAnalysisComparison();
    }

    public override void Draw()
    {
        if (disposed) return;
        FireGazeWindowPlacement.Remember("FireGazePortraitPreview");
        var current = feature.Current;
        if (!feature.IsActive || current is null)
        {
            CancelLightInteraction();
            analysis.Invalidate();
            ReleaseAnalysisPreview();
            ClearAnalysisComparison();
            ImGui.TextWrapped("请先打开肖像编辑器，再点击旁边的「FireGaze预览」。");
            return;
        }

        if (feature.OwnedViewportExperimentActive)
        {
            ImGui.TextWrapped(feature.OwnedViewportStatus);
            DrawLightingSpaceWindow();
            return;
        }
        DrawWorkspaceHeader();

        if (appearanceTab)
        {
            DrawAppearance();
            DrawHistoryPopup();
            DrawLibraryPopups();
            return;
        }

        var summaryHeight = ImGui.GetTextLineHeightWithSpacing() * 3.2f;
        var mainHeight = Math.Max(220f * Scale, ImGui.GetContentRegionAvail().Y - summaryHeight);
        var leftWidth = PortraitPreviewLayout.SideWidth(ImGui.GetContentRegionAvail().X, Scale);
        ImGui.BeginChild("###PortraitEquipment", new Vector2(leftWidth, mainHeight), false);
        DrawSlots(current);
        ImGui.Separator();
        DrawEquipmentControls(current);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("###PortraitOwnedBrowser", new Vector2(0, mainHeight), false);
        DrawBrowser(current);
        ImGui.EndChild();

        if (ImGui.SmallButton("存储位置")) ImGui.OpenPopup("###PortraitSourceRecords");
        var sourceStatus = feature.SourceSummary.Count == 0 ? "尚未读取来源记录" : "查看已读取的物品来源";
        ContinueToolbar(ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X,
            ImGui.CalcTextSize(sourceStatus).X);
        ImGui.TextUnformatted(sourceStatus);
        var sourceWorkArea = ImGui.GetMainViewport().WorkSize;
        ImGui.SetNextWindowSizeConstraints(Vector2.Zero, PortraitPopupLayout.MaximumSize(sourceWorkArea, Scale));
        ImGui.SetNextWindowSize(PortraitPopupLayout.PreferredSize(new Vector2(540 * Scale, 0), sourceWorkArea, Scale), ImGuiCond.Appearing);
        if (ImGui.BeginPopup("###PortraitSourceRecords"))
        {
            ImGui.TextUnformatted("物品来源");
            ImGui.Separator();
            foreach (var source in feature.SourceSummary) ImGui.TextWrapped(source);
            if (feature.SourceSummary.Count == 0) ImGui.TextUnformatted("尚未读取物品。请先打开对应的背包、投影台或雇员背包。");
            ImGui.EndPopup();
        }
        ImGui.TextWrapped("请先关闭预览，再保存肖像。");
        if (!string.IsNullOrWhiteSpace(feature.Status)) ImGui.TextWrapped(feature.Status);
        DrawHistoryPopup();
        DrawLibraryPopups();
    }

    private static float Scale => ImGui.GetFontSize() / 17f;

    private void DrawSlots(PortraitLook current)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var size = Math.Min(70f * Scale, (ImGui.GetContentRegionAvail().X - spacing) * 0.5f);
        for (var i = 0; i < Slots.Length; i++)
        {
            var slot = Slots[i];
            var piece = current.Pieces[(int)slot];
            var item = feature.GetItem(slot, piece.ItemID);
            ImGui.PushID((int)slot);
            ImGui.BeginGroup();
            if (DrawItemIcon("###Slot", item, size, slot == selectedSlot, slot)) selectedSlot = slot;
            var name = SlotName(slot);
            var labelWidth = ImGui.CalcTextSize(name).X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (size - labelWidth) / 2));
            ImGui.TextUnformatted(name);
            ImGui.EndGroup();
            if ((i & 1) == 0 && i + 1 < Slots.Length) ImGui.SameLine();
            ImGui.PopID();
        }
    }

    private void DrawEquipmentControls(PortraitLook current)
    {
        var piece = current.Pieces[(int)selectedSlot];
        var item = feature.GetItem(selectedSlot, piece.ItemID);
        ImGui.TextUnformatted(SlotName(selectedSlot));
        if (item is not null) ImGui.TextWrapped(item.Name);
        if (ImGui.Button("清空当前部位", new Vector2(-1, 0))) feature.Clear(selectedSlot);
        for (var channel = 0; channel < 2; channel++)
        {
            var available = item is not null && item.DyeChannels > channel && selectedSlot != PortraitSlot.Facewear;
            var stain = channel == 0 ? piece.Stain0 : piece.Stain1;
            var dye = feature.Dyes.FirstOrDefault(dye => dye.ID == stain);
            var color = dye?.Color ?? 0x808080;
            var position = ImGui.GetCursorScreenPos();
            var extent = new Vector2(28) * Scale;
            if (ImGui.ColorButton($"染色 {channel + 1}###PortraitStain{channel}", available ? DyeColor(color) : FireGazePalette.Raised,
                    ImGuiColorEditFlags.NoTooltip, extent) && available)
            {
                dyeSlot = selectedSlot;
                dyeChannel = channel;
                ImGui.OpenPopup("###PortraitDyePalette");
            }
            var hovered = ImGui.IsItemHovered();
            var focused = ImGui.IsItemFocused();
            FireGazeSymbols.DrawSelection(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), false, focused);
            if (!available) DrawUnavailableMarker(position);
            ImGui.SameLine();
            ImGui.TextWrapped($"染色 {channel + 1} · {(available ? stain == 0 ? "无染色" : dye?.Name ?? $"#{stain}" : "不支持")}");
            if (hovered || focused)
                ImGui.SetTooltip(available ? $"选择染色 {channel + 1}，不消耗染剂" : "当前装备不支持此染色通道");
        }
        if (item is null || item.DyeChannels == 0 || selectedSlot == PortraitSlot.Facewear)
            ImGui.TextWrapped("当前部位不支持染色。");
        else if (item.DyeChannels == 1) ImGui.TextWrapped("当前装备仅支持一种染色。");

        var head = current.HeadVisible;
        var weapon = current.WeaponVisible;
        var visor = current.Visor;
        var changed = ImGui.Checkbox("显示头部装备", ref head);
        changed |= ImGui.Checkbox("显示武器", ref weapon);
        changed |= ImGui.Checkbox("开合面罩", ref visor);
        if (changed) feature.SetVisibility(head, weapon, visor);
        DrawDyePalette();
    }

    private void DrawBrowser(PortraitLook current)
    {
        DrawSourceGuide();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###PortraitEquipmentSearch", "搜索装备名称", ref search, 100);
        DrawEquipmentFilters(selectedSlot == PortraitSlot.Facewear);
        var needle = search.Trim();
        var catalog = feature.GetItems(selectedSlot);
        if (filtersChanged || !ReferenceEquals(cachedCatalog, catalog) || cachedSearch != needle)
        {
            var query = PortraitLibraryPresentation.ForSlot(new(needle, selectedSources, selectedDyes, equipmentSort), selectedSlot);
            filteredItems = PortraitEquipmentFilter.Apply(catalog, query);
            cachedCatalog = catalog;
            cachedSearch = needle;
            filtersChanged = false;
        }
        var items = filteredItems;
        ImGui.TextDisabled($"{SlotName(selectedSlot)} · 已拥有 {items.Count} 项");
        ImGui.Separator();
        inspectedItem = null;
        var detailsHeight = ImGui.GetTextLineHeightWithSpacing() * 3.4f;
        var gridHeight = Math.Max(56 * Scale, ImGui.GetContentRegionAvail().Y - detailsHeight);
        ImGui.BeginChild("###PortraitOwnedGrid", new Vector2(0, gridHeight), false);
        if (items.Count == 0)
        {
            ImGui.TextWrapped(needle.Length > 0 || selectedSources.Count > 0 || selectedDyes.Count > 0 ? "没有符合条件的装备。" : "尚未读取到此部位的装备。");
        }
        else
        {
            var cell = PortraitPreviewLayout.GridEdge(ImGui.GetContentRegionAvail().X, 82f * Scale);
            var gap = ImGui.GetStyle().ItemSpacing;
            var columns = PortraitPreviewLayout.Columns(ImGui.GetContentRegionAvail().X, cell, gap.X);
            var rows = (items.Count + columns - 1) / columns;
            var clipper = new ImGuiListClipper();
            clipper.Begin(rows, cell + gap.Y);
            while (clipper.Step())
            {
                for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                {
                    for (var col = 0; col < columns; col++)
                    {
                        var index = row * columns + col;
                        if (index >= items.Count) break;
                        if (col != 0) ImGui.SameLine();
                        var item = items[index];
                        ImGui.PushID(index);
                        if (DrawItemIcon("###OwnedItem", item, cell, current.Pieces[(int)selectedSlot].ItemID == item.ID, null) && item.Available)
                        {
                            if (selectedSlot == PortraitSlot.Facewear) feature.Equip(selectedSlot, item.ID);
                            else feature.EquipFromSources(selectedSlot, item.ID, selectedSources.ToArray());
                        }
                        ImGui.PopID();
                    }
                }
            }
            clipper.End();
        }
        ImGui.EndChild();
        ImGui.Separator();
        var detail = inspectedItem ?? feature.GetItem(selectedSlot, current.Pieces[(int)selectedSlot].ItemID);
        if (detail is not null)
        {
            ImGui.TextWrapped(detail.Name);
            var locations = feature.StorageLocations(detail.ID);
            if (locations.Count > 0) ImGui.TextWrapped("存储位置：" + string.Join("、", locations));
            if (!detail.Available) ImGui.TextWrapped(string.IsNullOrWhiteSpace(detail.Reason) ? "当前角色无法预览此物品。" : detail.Reason);
        }
        else ImGui.TextWrapped("选择物品即可预览。");
    }

    private bool DrawItemIcon(string id, PortraitBrowserItem? item, float size, bool selected, PortraitSlot? emptySlot)
    {
        var position = ImGui.GetCursorScreenPos();
        var extent = new Vector2(size);
        var clicked = ImGui.Button(id, extent);
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        var focused = ImGui.IsItemFocused();
        var draw = ImGui.GetWindowDrawList();
        var dimmed = item is { Available: false } && emptySlot is null;
        var tint = dimmed ? new Vector4(0.65f, 0.65f, 0.65f, 1f) : Vector4.One;
        if (item is { Icon: > 0 } && textures.GetFromGameIcon(new GameIconLookup(item.Icon)).TryGetWrap(out var icon, out _))
        {
            var inset = new Vector2(size * 0.06f);
            draw.AddImage(icon.Handle, position + inset, position + extent - inset, Vector2.Zero, Vector2.One, ImGui.GetColorU32(tint));
        }
        else if (emptySlot is { } slot)
        {
            var part = EmptySlotPart(slot);
            var placeholder = part < 0 ? null : GetPart("ui/uld/Character.uld", "ui/uld/Character_hr1.tex", part);
            if (placeholder is not null)
            {
                var inset = new Vector2(size * 0.18f);
                draw.AddImage(placeholder.Handle, position + inset, position + extent - inset);
            }
            else
            {
                var text = SlotName(slot);
                draw.AddText(position + (extent - ImGui.CalcTextSize(text)) / 2, ImGui.GetColorU32(FireGazePalette.Muted), text);
            }
        }
        FireGazeSymbols.DrawSelection(position, position + extent, selected, focused);
        if (dimmed) DrawUnavailableMarker(position);
        if (hovered || focused)
        {
            if (emptySlot is null) inspectedItem = item;
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(item?.Name ?? (emptySlot is { } slot ? $"{SlotName(slot)} · 空" : "未读取装备信息"));
            if (item is not null)
            {
                foreach (var location in feature.StorageLocations(item.ID)) ImGui.TextDisabled(location);
                if (item.DyeChannels > 0) ImGui.TextDisabled($"支持 {item.DyeChannels} 处染色");
                if (!item.Available && !string.IsNullOrWhiteSpace(item.Reason)) ImGui.TextWrapped(item.Reason);
            }
            ImGui.EndTooltip();
        }
        return clicked;
    }

    private static void DrawUnavailableMarker(Vector2 position)
    {
        var marker = position + new Vector2(3 * Scale);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(marker, marker + new Vector2(16 * Scale), ImGui.GetColorU32(FireGazePalette.Raised), 2 * Scale);
        draw.AddLine(marker + new Vector2(4, 4) * Scale, marker + new Vector2(12, 12) * Scale,
            ImGui.GetColorU32(FireGazePalette.Muted), 1.5f * Scale);
        draw.AddLine(marker + new Vector2(12, 4) * Scale, marker + new Vector2(4, 12) * Scale,
            ImGui.GetColorU32(FireGazePalette.Muted), 1.5f * Scale);
    }

    private void DrawDyePalette()
    {
        var workArea = ImGui.GetMainViewport().WorkSize;
        ImGui.SetNextWindowSizeConstraints(Vector2.Zero, PortraitPopupLayout.MaximumSize(workArea, Scale));
        ImGui.SetNextWindowSize(PortraitPopupLayout.PreferredSize(new Vector2(570, 620) * Scale, workArea, Scale), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("###PortraitDyePalette")) return;
        var look = feature.Current;
        var piece = look is null ? default : look.Pieces[(int)dyeSlot];
        var item = feature.GetItem(dyeSlot, piece.ItemID);
        var supported = feature.IsActive && item is not null && item.DyeChannels > dyeChannel && dyeSlot != PortraitSlot.Facewear;
        if (!supported)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        ImGui.TextUnformatted($"{SlotName(dyeSlot)} · 染色 {dyeChannel + 1}");
        var currentStain = dyeChannel == 0 ? piece.Stain0 : piece.Stain1;
        ImGui.TextUnformatted($"当前：{(currentStain == 0 ? "无染色" : feature.Dyes.FirstOrDefault(dye => dye.ID == currentStain)?.Name ?? $"#{currentStain}")}");
        ImGui.TextDisabled("染色仅用于肖像预览，不消耗染剂");
        DrawUndoRedo();
        if (ImGui.Button("清除染色"))
        {
            feature.Dye(dyeSlot, dyeChannel, 0);
            ImGui.CloseCurrentPopup();
        }
        ImGui.Separator();
        ImGui.BeginChild("###PortraitStainColors", new Vector2(0, 0), false);
        var edge = 32f * Scale;
        var columns = Math.Clamp((int)((ImGui.GetContentRegionAvail().X + ImGui.GetStyle().ItemSpacing.X) / (edge + ImGui.GetStyle().ItemSpacing.X)), 1, 12);
        foreach (var group in PortraitDyePalette.Group(feature.Dyes))
        {
            ImGui.Spacing();
            ImGui.TextUnformatted(group.Name);
            var index = 0;
            foreach (var dye in group.Dyes)
            {
                if (index++ % columns != 0) ImGui.SameLine();
                var selected = (dyeChannel == 0 ? piece.Stain0 : piece.Stain1) == dye.ID;
                var position = ImGui.GetCursorScreenPos();
                var clicked = ImGui.ColorButton($"###PortraitDye{dye.ID}", DyeColor(dye.Color), ImGuiColorEditFlags.NoTooltip, new Vector2(edge));
                FireGazeSymbols.DrawSelection(position, position + new Vector2(edge), selected, ImGui.IsItemFocused());
                if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip(dye.Name);
                if (clicked) feature.Dye(dyeSlot, dyeChannel, dye.ID);
            }
        }
        ImGui.EndChild();
        ImGui.EndPopup();
    }

    private IDalamudTextureWrap? GetPart(string uldName, string textureName, int part)
    {
        var key = (uldName, textureName, part);
        if (parts.TryGetValue(key, out var cached)) return cached;
        IDalamudTextureWrap? result = null;
        try
        {
            if (!ulds.TryGetValue(uldName, out var uld))
            {
                uld = pluginInterface.UiBuilder.LoadUld(uldName);
                ulds.Add(uldName, uld);
            }
            if (uld is { Valid: true }) result = uld.LoadTexturePart(textureName, part);
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "肖像预览界面资源不可用: {Resource}, part {Part}", textureName, part);
        }
        parts.Add(key, result);
        return result;
    }

    private static Vector4 DyeColor(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);

    private static int EmptySlotPart(PortraitSlot slot) => slot switch
    {
        PortraitSlot.MainHand => 17, PortraitSlot.OffHand => 18, PortraitSlot.Head => 19,
        PortraitSlot.Body => 20, PortraitSlot.Hands => 21, PortraitSlot.Legs => 23, PortraitSlot.Feet => 24,
        PortraitSlot.Ears => 25, PortraitSlot.Neck => 26, PortraitSlot.Wrists => 27,
        PortraitSlot.RightRing or PortraitSlot.LeftRing => 28, _ => -1,
    };

    private static string SlotName(PortraitSlot slot) => slot switch
    {
        PortraitSlot.MainHand => "主手", PortraitSlot.OffHand => "副手", PortraitSlot.Head => "头部",
        PortraitSlot.Body => "身体", PortraitSlot.Hands => "手部", PortraitSlot.Legs => "腿部", PortraitSlot.Feet => "脚部",
        PortraitSlot.Ears => "耳饰", PortraitSlot.Neck => "项链", PortraitSlot.Wrists => "手镯",
        PortraitSlot.RightRing => "右戒指", PortraitSlot.LeftRing => "左戒指", PortraitSlot.Facewear => "眼镜", _ => "装备",
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CancelLightInteraction();
        DisposeAnalysisResources();
        comparisonReference.Dispose();
        foreach (var part in parts.Values) part?.Dispose();
        foreach (var uld in ulds.Values) uld?.Dispose();
        parts.Clear();
        ulds.Clear();
    }
}
