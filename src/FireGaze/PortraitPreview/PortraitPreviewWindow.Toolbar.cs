using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private void DrawWorkspaceHeader()
    {
        DrawEditingToolbar();
        DrawLibraryToolbar();
        DrawPreviewSettings();
        DrawWorkspaceTools();
        ImGui.Separator();

        ImGui.BeginGroup();
        if (DrawTab("装备", !appearanceTab, true)) appearanceTab = false;
        ImGui.SameLine();
        if (DrawTab("美容师", appearanceTab, true)) appearanceTab = true;
        ImGui.EndGroup();
        ImGui.Separator();

        DrawNativeLiveSetting();
        ContinueToolbar(ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X,
            ImGui.CalcTextSize("仅本地 · 关闭后还原").X);
        ImGui.TextDisabled("仅本地 · 关闭后还原");
        DrawNativeLivePreview();
    }

    private void DrawUndoRedo()
    {
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        using (UiHelpers.Disabled(!feature.CanUndo))
        {
            if (ImGui.Button("撤回")) feature.Undo();
        }
        ContinueToolbar(right, ImGui.CalcTextSize("重做").X + ImGui.GetStyle().FramePadding.X * 2);
        using (UiHelpers.Disabled(!feature.CanRedo))
        {
            if (ImGui.Button("重做")) feature.Redo();
        }
    }

    private static void ContinueToolbar(float right, float nextWidth)
    {
        var gap = ImGui.GetStyle().ItemSpacing.X;
        if (PortraitPreviewLayout.FitsInline(right, ImGui.GetItemRectMax().X, nextWidth, gap))
            ImGui.SameLine(0, gap);
    }

    private void DrawEditingToolbar()
    {
        ImGui.PushID("PortraitEditingToolbar");
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        DrawUndoRedo();
        ContinueToolbar(right, ImGui.CalcTextSize("历史记录").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button("历史记录")) showHistory = !showHistory;
        ImGui.PopID();
    }

    private void DrawPreviewSettings()
    {
        // A saved look and its local plate preview form one task. Keep the pair
        // intact when the separate workspace-tool group moves to another row.
        ImGui.BeginGroup();
        DrawGlamourPlateEntry();
        ImGui.SameLine();
        ImGui.PushID("PortraitEditingToolbar");
        var sync = feature.SyncPlate;
        if (ImGui.Checkbox("同步铭牌预览", ref sync)) feature.SetSyncPlate(sync);
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip("仅同步自己看到的铭牌预览。关闭预览后还原，不会保存到铭牌。");
        ImGui.PopID();
        ImGui.EndGroup();
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

    private void DrawWorkspaceTools()
    {
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var groupWidth = ImGui.CalcTextSize("空间参照").X + ImGui.CalcTextSize("画面分析").X
            + ImGui.GetStyle().FramePadding.X * 4 + ImGui.GetStyle().ItemSpacing.X;
        ContinueToolbar(right, groupWidth);
        ImGui.BeginGroup();
        if (ImGui.Button("空间参照")) showSpaceLight = true;
        ImGui.SameLine();
        if (ImGui.Button("画面分析")) showAnalysis = true;
        ImGui.EndGroup();
    }

    private void DrawGlamourPlateEntry()
    {
        var syncWidth = ImGui.CalcTextSize("同步铭牌预览").X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        var minimum = ImGui.CalcTextSize("投影模板").X + ImGui.GetStyle().FramePadding.X * 2;
        var width = Math.Clamp(ImGui.GetContentRegionAvail().X - syncWidth - ImGui.GetStyle().ItemSpacing.X,
            minimum, 198 * Scale);
        UiHelpers.PushPrimaryButton();
        try
        {
            if (ImGui.Button("投影模板###PortraitPlateSwitcher", new Vector2(width, ImGui.GetFrameHeight())))
                showGlamourPlates = true;
        }
        finally { UiHelpers.PopPrimaryButton(); }
    }

    private bool DrawTab(string text, bool active, bool enabled)
    {
        var size = new Vector2(88, 44) * Scale;
        bool clicked;
        using (UiHelpers.Disabled(!enabled))
        {
            clicked = FireGazeSymbols.NeutralButton($"{text}###PortraitTab{text}", size, active);
            FireGazeSymbols.DrawSelection(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), active);
        }
        return enabled && clicked;
    }
}
