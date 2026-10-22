using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private void DrawWorkspaceHeader()
    {
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        ImGui.BeginGroup();
        if (DrawTab("装备", !appearanceTab && !showDesignShowcase, true))
            appearanceTab = showDesignShowcase = false;
        ImGui.SameLine();
        if (DrawTab("美容师", appearanceTab && !showDesignShowcase, true))
        { appearanceTab = true; showDesignShowcase = false; }
        ImGui.SameLine();
        if (DrawTab("造型展示", showDesignShowcase, true))
        { showDesignShowcase = true; appearanceTab = false; }
        ImGui.SameLine();
        DrawPortraitVisibilityToggle();
        ImGui.EndGroup();
        ContinueToolbar(right, HeaderActionSize("投影模板").X + ImGui.GetStyle().ItemSpacing.X + HeaderActionSize("收藏").X + 44 * Scale);
        ImGui.BeginGroup();
        DrawGlamourPlateEntry();
        DrawLibraryToolbar();
        ImGui.EndGroup();
        ImGui.Separator();
    }

    private void DrawWorkspaceFooter()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var right = ImGui.GetCursorScreenPos().X + width;
        var stacked = width / Scale < 980;
        ImGui.PushID("PortraitEditingToolbar");
        DrawUndoRedo();
        ContinueToolbar(right, ImGui.CalcTextSize("历史记录").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button("历史记录")) showHistory = !showHistory;
        if (!stacked) ContinueToolbar(right, ImGui.CalcTextSize("铭牌同步预览").X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        var sync = feature.SyncPlate;
        if (ImGui.Checkbox("铭牌同步预览", ref sync)) feature.SetSyncPlate(sync);
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused())
            ImGui.SetTooltip("同步自己看到的铭牌画面。结束预览后还原，不会保存临时外观。");
        ContinueToolbar(right, ImGui.CalcTextSize("结束预览").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button("结束预览###PortraitEndPreview")) feature.ClosePreview();
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip("结束试换并还原进入预览时的外观");
        ImGui.PopID();
    }

    private void DrawUndoRedo()
    {
        if (DrawHistoryArrow("###PortraitUndo", "撤回", feature.CanUndo, false)) feature.Undo();
        ImGui.SameLine();
        if (DrawHistoryArrow("###PortraitRedo", "重做", feature.CanRedo, true)) feature.Redo();
    }

    private static bool DrawHistoryArrow(string id, string tooltip, bool enabled, bool redo)
    {
        using var disabled = UiHelpers.Disabled(!enabled);
        var edge = ImGui.GetFrameHeight();
        var clicked = ImGui.Button(id, new Vector2(edge));
        var min = ImGui.GetItemRectMin();
        var draw = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(enabled ? FireGazePalette.Text : FireGazePalette.Muted);
        Vector2 Point(float x, float y) => min + new Vector2(redo ? 1 - x : x, y) * edge;
        draw.PathLineTo(Point(.76f, .76f));
        draw.PathLineTo(Point(.84f, .60f));
        draw.PathLineTo(Point(.80f, .43f));
        draw.PathLineTo(Point(.65f, .29f));
        draw.PathLineTo(Point(.31f, .29f));
        draw.PathStroke(color, ImDrawFlags.None, 1.6f * Scale);
        draw.PathLineTo(Point(.47f, .13f));
        draw.PathLineTo(Point(.31f, .29f));
        draw.PathLineTo(Point(.47f, .45f));
        draw.PathStroke(color, ImDrawFlags.None, 1.6f * Scale);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) || ImGui.IsItemFocused())
            ImGui.SetTooltip(enabled ? tooltip : $"{tooltip} · 暂无可{tooltip}的操作");
        if (ImGui.IsItemFocused()) FireGazeSymbols.DrawFocus(min, ImGui.GetItemRectMax());
        return enabled && clicked;
    }

    private static void ContinueToolbar(float right, float nextWidth)
    {
        var gap = ImGui.GetStyle().ItemSpacing.X;
        if (PortraitPreviewLayout.FitsInline(right, ImGui.GetItemRectMax().X, nextWidth, gap))
            ImGui.SameLine(0, gap);
    }

    private void DrawLibraryToolbar()
    {
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        ContinueToolbar(right, HeaderActionSize("收藏").X + 44 * Scale);
        ImGui.BeginGroup();
        if (HeaderActionButton("收藏###PortraitFavoriteCurrent"))
            feature.SaveFavorite($"肖像 {DateTime.Now:yyyy-MM-dd HH:mm:ss}", []);
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip("收藏当前预览，之后可以重新载入");
        ImGui.SameLine(0, 8 * Scale);
        if (ImGui.Button("###PortraitFavoriteMenu", new Vector2(36 * Scale))) ImGui.OpenPopup("###PortraitFavoriteActions");
        var menuCenter = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * .5f;
        ImGui.GetWindowDrawList().AddTriangleFilled(menuCenter + new Vector2(-4, -2) * Scale,
            menuCenter + new Vector2(4, -2) * Scale, menuCenter + new Vector2(0, 3) * Scale,
            ImGui.GetColorU32(FireGazePalette.Text));
        if (ImGui.BeginPopup("###PortraitFavoriteActions"))
        {
            if (ImGui.MenuItem("我的收藏")) OpenFavorites();
            ImGui.EndPopup();
        }
        ImGui.EndGroup();
    }

    private void DrawGlamourPlateEntry()
    {
        if (HeaderActionButton("投影模板###PortraitPlateSwitcher")) showGlamourPlates = true;
    }

    private static Vector2 HeaderActionSize(string label)
    {
        var separator = label.IndexOf("##", StringComparison.Ordinal);
        var text = separator < 0 ? label : label[..separator];
        return new(MathF.Max(64 * Scale, ImGui.CalcTextSize(text).X + 24 * Scale), 36 * Scale);
    }

    private static bool HeaderActionButton(string label)
        => FireGazeSymbols.NeutralButton(label, HeaderActionSize(label));

    private bool DrawTab(string text, bool active, bool enabled)
    {
        var size = new Vector2(88, 36) * Scale;
        bool clicked;
        using (UiHelpers.Disabled(!enabled))
        {
            clicked = FireGazeSymbols.NeutralButton($"{text}###PortraitTab{text}", size, active);
            FireGazeSymbols.DrawSelection(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), active);
        }
        return enabled && clicked;
    }
}
