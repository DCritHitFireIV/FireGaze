using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private bool showDyePalette;

    private void DrawSlots(PortraitLook current)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var size = PortraitPreviewLayout.GridEdge((ImGui.GetContentRegionAvail().X - spacing) * .5f, 80f * Scale);
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

    private void DrawDyePalette()
    {
        if (!showDyePalette) return;
        var look = feature.Current;
        var piece = look is null ? default : look.Pieces[(int)dyeSlot];
        var item = feature.GetItem(dyeSlot, piece.ItemID);
        if (!feature.IsActive || item is null || item.DyeChannels <= dyeChannel || dyeSlot == PortraitSlot.Facewear)
        {
            showDyePalette = false;
            return;
        }

        var style = ImGui.GetStyle();
        var edge = 32f * Scale;
        var defaultWidth = edge * 12 + style.ItemSpacing.X * 11 + style.WindowPadding.X * 2 + style.ScrollbarSize;
        // BeginPopup always adds AlwaysAutoResize: a remaining-space child then measures
        // itself back to a tiny popup. A normal window keeps native manual resizing.
        FireGazeWindowPlacement.Prepare("PortraitDyePalette", new Vector2(defaultWidth, 620 * Scale), Scale, "FireGazePortraitPreview");
        FireGazeWindowPlacement.Constrain(new Vector2(260, 240) * Scale, FireGazeWindowPlacement.PreparedWorkSize, Scale);
        try
        {
            if (!FireGazeWindowPlacement.Begin($"{SlotName(dyeSlot)} · 染色 {dyeChannel + 1}###PortraitDyePalette", ref showDyePalette, "PortraitDyePalette")) return;
            ImGui.TextUnformatted($"{SlotName(dyeSlot)} · 染色 {dyeChannel + 1}");
            var currentStain = dyeChannel == 0 ? piece.Stain0 : piece.Stain1;
            ImGui.TextUnformatted($"当前：{(currentStain == 0 ? "无染色" : feature.Dyes.FirstOrDefault(dye => dye.ID == currentStain)?.Name ?? $"#{currentStain}")}");
            ImGui.TextDisabled("染色仅用于肖像预览，不消耗染剂");
            DrawUndoRedo();
            if (ImGui.Button("清除染色"))
            {
                feature.Dye(dyeSlot, dyeChannel, 0);
                showDyePalette = false;
            }
            ImGui.Separator();
            ImGui.BeginChild("###PortraitStainColors", new Vector2(0, 0), false);
            try
            {
                var columns = PortraitPreviewLayout.Columns(ImGui.GetContentRegionAvail().X, edge, style.ItemSpacing.X);
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
            }
            finally { ImGui.EndChild(); }
        }
        finally { ImGui.End(); }
    }
}
