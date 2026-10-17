using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitBrowserItem(uint ID, string Name, uint Icon, byte DyeChannels, bool Available, string Reason);
internal sealed record PortraitDye(byte ID, string Name, uint Color);

/// <summary>The window reads snapshots; the feature owns Framework-thread changes and editor identity checks.</summary>
internal interface IPortraitPreviewViewModel
{
    bool IsActive { get; }
    PortraitLook? Current { get; }
    string Status { get; }
    IReadOnlyList<string> SourceSummary { get; }
    IReadOnlyList<PortraitDye> Dyes { get; }
    IReadOnlyList<PortraitBrowserItem> GetItems(PortraitSlot slot);
    PortraitBrowserItem? GetItem(PortraitSlot slot, uint id);
    void Equip(PortraitSlot slot, uint id);
    void Clear(PortraitSlot slot);
    void Dye(PortraitSlot slot, int channel, byte stain);
    void SetVisibility(bool head, bool weapon, bool visor);
    void ClosePreview();
}

/// <summary>Independent wardrobe UI using game-local textures, without Dresser code or plugin IPC.</summary>
internal sealed class PortraitPreviewWindow : Window, IDisposable
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
    private readonly Dictionary<string, UldWrapper?> ulds = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Uld, string Texture, int Part), IDalamudTextureWrap?> parts = new();
    private PortraitSlot selectedSlot = PortraitSlot.Head;
    private PortraitSlot dyeSlot;
    private int dyeChannel;
    private string search = string.Empty;
    private bool disposed;

    public PortraitPreviewWindow(IPortraitPreviewViewModel feature, IDalamudPluginInterface pluginInterface, ITextureProvider textures)
        : base("肖像实时预览###FireGazePortraitPreview", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.feature = feature;
        this.pluginInterface = pluginInterface;
        this.textures = textures;
        Size = new Vector2(920, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(700, 540) };
    }

    public override void OnOpen()
    {
        selectedSlot = PortraitSlot.Head;
        search = string.Empty;
        ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
    }

    public override void OnClose() => feature.ClosePreview();

    public override void Draw()
    {
        if (disposed) return;
        var current = feature.Current;
        if (!feature.IsActive || current is null)
        {
            ImGui.TextWrapped("请先打开肖像编辑器，再点击旁边的「实时预览」。");
            return;
        }

        DrawTab("装备", true, true);
        ImGui.SameLine();
        DrawTab("美容师", false, false);
        ImGui.SameLine();
        ImGui.TextDisabled("仅预览当前肖像，关闭后还原");
        ImGui.Separator();

        var summaryHeight = ImGui.GetTextLineHeightWithSpacing() * 2.6f;
        var mainHeight = Math.Max(260f * Scale, ImGui.GetContentRegionAvail().Y - summaryHeight);
        var leftWidth = 198f * Scale;
        ImGui.BeginChild("###PortraitEquipment", new Vector2(leftWidth, mainHeight), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        var slotHeight = Math.Max(1, ImGui.GetContentRegionAvail().Y - 220f * Scale);
        ImGui.BeginChild("###PortraitEquipmentSlots", new Vector2(0, slotHeight), false);
        DrawSlots(current);
        ImGui.EndChild();
        ImGui.Separator();
        DrawEquipmentControls(current);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("###PortraitOwnedBrowser", new Vector2(0, mainHeight), true);
        DrawBrowser(current);
        ImGui.EndChild();

        if (ImGui.SmallButton("来源记录")) ImGui.OpenPopup("###PortraitSourceRecords");
        var showSourceTooltip = ImGui.IsItemHovered();
        ImGui.SameLine();
        ImGui.TextUnformatted("已拥有物品 · 关闭实时预览后才能保存肖像");
        if (showSourceTooltip)
        {
            ImGui.BeginTooltip();
            foreach (var source in feature.SourceSummary) ImGui.TextUnformatted(source);
            ImGui.EndTooltip();
        }
        if (ImGui.BeginPopup("###PortraitSourceRecords"))
        {
            foreach (var source in feature.SourceSummary) ImGui.TextUnformatted(source);
            if (feature.SourceSummary.Count == 0) ImGui.TextDisabled("尚未读取来源记录");
            ImGui.EndPopup();
        }
        if (!string.IsNullOrWhiteSpace(feature.Status)) ImGui.TextWrapped(feature.Status);
    }

    private static float Scale => ImGui.GetFontSize() / 17f;

    private void DrawTab(string text, bool active, bool enabled)
    {
        var size = new Vector2(116, 38) * Scale;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.BeginDisabled(!enabled);
        ImGui.InvisibleButton($"###PortraitTab{text}", size);
        var texture = GetPart("ui/uld/MiragePrismPlate.uld", "ui/uld/MiragePrismPlate2_hr1.tex", active ? 5 : 4);
        var draw = ImGui.GetWindowDrawList();
        if (texture is not null)
        {
            var tint = enabled ? Vector4.One : new Vector4(1, 1, 1, 0.4f);
            draw.AddImage(texture.Handle, origin, origin + size, Vector2.Zero, Vector2.One, ImGui.GetColorU32(tint));
        }
        else
        {
            draw.AddRectFilled(origin, origin + size,
                ImGui.GetColorU32(active ? new Vector4(0.70f, 0.58f, 0.31f, 1) : new Vector4(0.29f, 0.29f, 0.32f, 1)), 5 * Scale);
        }
        var textSize = ImGui.CalcTextSize(text);
        draw.AddText(origin + (size - textSize) * 0.5f,
            ImGui.GetColorU32(enabled ? new Vector4(0.16f, 0.14f, 0.10f, 1) : new Vector4(0.70f, 0.70f, 0.70f, 1)), text);
        ImGui.EndDisabled();
        if (!enabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("美容师功能将在后续版本提供。");
    }

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
        if (ImGui.Button("清空当前部位", new Vector2(-1, 0))) feature.Clear(selectedSlot);
        for (var channel = 0; channel < 2; channel++)
        {
            if (channel != 0) ImGui.SameLine();
            var available = item is not null && item.DyeChannels > channel && selectedSlot != PortraitSlot.Facewear;
            ImGui.BeginDisabled(!available);
            var stain = channel == 0 ? piece.Stain0 : piece.Stain1;
            var color = feature.Dyes.FirstOrDefault(dye => dye.ID == stain)?.Color ?? 0x808080;
            if (ImGui.ColorButton($"染色 {channel + 1}###PortraitStain{channel}", DyeColor(color), ImGuiColorEditFlags.NoTooltip,
                    new Vector2(28, 24) * Scale))
            {
                dyeSlot = selectedSlot;
                dyeChannel = channel;
                ImGui.OpenPopup("###PortraitDyePalette");
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(available ? $"选择染色 {channel + 1}，不消耗染剂" : "当前装备不支持此染色通道");
        }
        ImGui.SameLine();
        ImGui.TextDisabled("染色 1 / 2");

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
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###PortraitEquipmentSearch", "搜索已拥有装备名称或编号", ref search, 100);
        var needle = search.Trim();
        var items = feature.GetItems(selectedSlot);
        if (needle.Length > 0)
        {
            var isID = uint.TryParse(needle, NumberStyles.None, CultureInfo.InvariantCulture, out var id);
            items = items.Where(item => item.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) || isID && item.ID == id).ToArray();
        }
        ImGui.TextDisabled($"{SlotName(selectedSlot)} · 已拥有 {items.Count} 项");
        ImGui.Separator();
        ImGui.BeginChild("###PortraitOwnedGrid", Vector2.Zero, false);
        if (items.Count == 0)
        {
            ImGui.TextWrapped(needle.Length > 0 ? "没有找到符合名称或编号的已拥有装备。" : "尚未读取到此部位的已拥有装备。");
        }
        else
        {
            var cell = 82f * Scale;
            var gap = ImGui.GetStyle().ItemSpacing;
            var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + gap.X) / (cell + gap.X)));
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
                        ImGui.BeginDisabled(!item.Available);
                        if (DrawItemIcon("###OwnedItem", item, cell, current.Pieces[(int)selectedSlot].ItemID == item.ID, null) && item.Available)
                            feature.Equip(selectedSlot, item.ID);
                        ImGui.EndDisabled();
                        ImGui.PopID();
                    }
                }
            }
            clipper.End();
        }
        ImGui.EndChild();
    }

    private bool DrawItemIcon(string id, PortraitBrowserItem? item, float size, bool selected, PortraitSlot? emptySlot)
    {
        var position = ImGui.GetCursorScreenPos();
        var extent = new Vector2(size);
        var clicked = ImGui.InvisibleButton(id, extent);
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        var draw = ImGui.GetWindowDrawList();
        var dimmed = item is { Available: false } && emptySlot is null;
        var tint = dimmed ? new Vector4(0.45f, 0.45f, 0.45f, 0.65f) : Vector4.One;
        draw.AddRectFilled(position, position + extent, ImGui.GetColorU32(new Vector4(0.20f, 0.20f, 0.22f, 0.95f)), size * 0.12f);
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
                draw.AddText(position + (extent - ImGui.CalcTextSize(text)) / 2, ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 1)), text);
            }
        }
        var frame = GetPart("ui/uld/MiragePrismPlate.uld", "ui/uld/MiragePrismBoxIcon_hr1.tex", 2);
        if (frame is not null) draw.AddImage(frame.Handle, position, position + extent, Vector2.Zero, Vector2.One, ImGui.GetColorU32(tint));
        if (selected || hovered)
        {
            var highlight = GetPart("ui/uld/Character.uld", "ui/uld/IconA_Frame_hr1.tex", 16);
            if (highlight is not null) draw.AddImage(highlight.Handle, position - new Vector2(2 * Scale), position + extent + new Vector2(2 * Scale));
            else draw.AddRect(position, position + extent, ImGui.GetColorU32(new Vector4(1, 0.86f, 0.45f, 1)), size * 0.12f, ImDrawFlags.None, 2 * Scale);
        }
        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(item?.Name ?? (emptySlot is { } slot ? $"{SlotName(slot)} · 空" : "未读取装备信息"));
            if (item is not null)
            {
                ImGui.TextDisabled($"#{item.ID}");
                if (item.DyeChannels > 0) ImGui.TextDisabled($"支持 {item.DyeChannels} 个染色通道");
                if (!item.Available && !string.IsNullOrWhiteSpace(item.Reason)) ImGui.TextWrapped(item.Reason);
            }
            ImGui.EndTooltip();
        }
        return clicked;
    }

    private void DrawDyePalette()
    {
        ImGui.SetNextWindowSize(new Vector2(390, 400) * Scale, ImGuiCond.Appearing);
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
        ImGui.TextDisabled("全部原生颜色，仅作用于肖像，不消耗染剂");
        if (ImGui.Button("清除染色"))
        {
            feature.Dye(dyeSlot, dyeChannel, 0);
            ImGui.CloseCurrentPopup();
        }
        ImGui.Separator();
        ImGui.BeginChild("###PortraitStainColors", new Vector2(0, 0), false);
        var edge = 25f * Scale;
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + ImGui.GetStyle().ItemSpacing.X) / (edge + ImGui.GetStyle().ItemSpacing.X)));
        var index = 0;
        foreach (var dye in feature.Dyes)
        {
            if (dye.ID == 0) continue;
            if (index++ % columns != 0) ImGui.SameLine();
            var selected = (dyeChannel == 0 ? piece.Stain0 : piece.Stain1) == dye.ID;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1, 0.86f, 0.45f, 1));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, selected ? 2f * Scale : 0f);
            var clicked = ImGui.ColorButton($"###PortraitDye{dye.ID}", DyeColor(dye.Color), ImGuiColorEditFlags.NoTooltip, new Vector2(edge));
            ImGui.PopStyleVar();
            if (selected) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(dye.Name);
            if (clicked)
            {
                feature.Dye(dyeSlot, dyeChannel, dye.ID);
                ImGui.CloseCurrentPopup();
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
        foreach (var part in parts.Values) part?.Dispose();
        foreach (var uld in ulds.Values) uld?.Dispose();
        parts.Clear();
        ulds.Clear();
    }
}
