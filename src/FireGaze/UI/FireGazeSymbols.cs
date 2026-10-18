using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>Small vector assets and object states, independent of downloaded textures or icon fonts.</summary>
internal static class FireGazeSymbols
{
    public static void DrawMark(Vector2 position, float size)
    {
        var draw = ImGui.GetWindowDrawList();
        var unit = size / 24f;
        var ink = ImGui.GetColorU32(FireGazePalette.Text);
        var line = Math.Max(1f, 1.7f * unit);
        draw.AddLine(position + new Vector2(3, 12) * unit, position + new Vector2(3, 21) * unit, ink, line);
        draw.AddLine(position + new Vector2(3, 21) * unit, position + new Vector2(12, 21) * unit, ink, line);
        draw.AddLine(position + new Vector2(12, 3) * unit, position + new Vector2(21, 3) * unit, ink, line);
        draw.AddLine(position + new Vector2(21, 3) * unit, position + new Vector2(21, 12) * unit, ink, line);
        draw.AddRectFilled(position + new Vector2(11, 10) * unit, position + new Vector2(15, 14) * unit,
            ImGui.GetColorU32(FireGazePalette.BrandEmber));
    }

    public static void DrawSelection(Vector2 min, Vector2 max, bool selected, bool focused = false)
    {
        var scale = ImGui.GetFontSize() / 17f;
        var draw = ImGui.GetWindowDrawList();
        if (selected)
        {
            draw.AddRect(min, max, ImGui.GetColorU32(FireGazePalette.Text), 4 * scale, ImDrawFlags.None, 2 * scale);
            draw.AddRect(min + new Vector2(2 * scale), max - new Vector2(2 * scale),
                ImGui.GetColorU32(FireGazePalette.White), 2 * scale, ImDrawFlags.None, 1 * scale);
            var corner = new Vector2(max.X - 15 * scale, min.Y + 2 * scale);
            draw.AddRectFilled(corner, corner + new Vector2(13 * scale), ImGui.GetColorU32(FireGazePalette.Text), 2 * scale);
            var white = ImGui.GetColorU32(FireGazePalette.White);
            draw.AddLine(corner + new Vector2(3, 6) * scale, corner + new Vector2(5, 9) * scale, white, 1.6f * scale);
            draw.AddLine(corner + new Vector2(5, 9) * scale, corner + new Vector2(10, 3) * scale, white, 1.6f * scale);
        }
        if (focused) DrawFocus(min, max);
    }

    public static void DrawFocus(Vector2 min, Vector2 max)
    {
        var scale = ImGui.GetFontSize() / 17f;
        ImGui.GetWindowDrawList().AddRect(min - new Vector2(2 * scale), max + new Vector2(2 * scale),
            ImGui.GetColorU32(FireGazePalette.Action), 6 * scale, ImDrawFlags.None, 2 * scale);
    }

    public static bool NeutralButton(string label, Vector2 size = default, bool selected = false)
    {
        if (selected) ImGui.PushStyleColor(ImGuiCol.Button, FireGazePalette.Selected);
        try
        {
            var clicked = ImGui.Button(label, size);
            if (ImGui.IsItemFocused()) DrawFocus(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            return clicked;
        }
        finally
        {
            if (selected) ImGui.PopStyleColor();
        }
    }
}
