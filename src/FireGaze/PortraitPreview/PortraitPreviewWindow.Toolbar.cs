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
        ImGui.EndGroup();
        ContinueToolbar(right, 260 * Scale);
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
        using (UiHelpers.Disabled(!feature.CanUndo))
        {
            if (FireGazeSymbols.IconButton("↶###PortraitUndo", "撤回", feature.CanUndo)) feature.Undo();
        }
        ImGui.SameLine();
        using (UiHelpers.Disabled(!feature.CanRedo))
        {
            if (FireGazeSymbols.IconButton("↷###PortraitRedo", "重做", feature.CanRedo)) feature.Redo();
        }
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
        ContinueToolbar(right, ImGui.CalcTextSize("收藏").X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetFrameHeight());
        ImGui.BeginGroup();
        if (ImGui.Button("收藏###PortraitFavoriteCurrent"))
            feature.SaveFavorite($"肖像 {DateTime.Now:yyyy-MM-dd HH:mm:ss}", []);
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip("收藏当前预览，之后可以重新载入");
        ImGui.SameLine(0, 0);
        if (ImGui.ArrowButton("###PortraitFavoriteMenu", ImGuiDir.Down)) ImGui.OpenPopup("###PortraitFavoriteActions");
        if (ImGui.BeginPopup("###PortraitFavoriteActions"))
        {
            if (ImGui.MenuItem("我的收藏")) showFavorites = true;
            ImGui.EndPopup();
        }
        ImGui.EndGroup();
    }

    private void DrawGlamourPlateEntry()
    {
        var width = MathF.Min(136 * Scale, MathF.Max(1, ImGui.GetContentRegionAvail().X));
        if (ImGui.Button("投影模板###PortraitPlateSwitcher", new Vector2(width, ImGui.GetFrameHeight())))
            showGlamourPlates = true;
    }

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
