using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private void DrawSourceRecords()
    {
        if (ImGui.SmallButton("存储位置")) ImGui.OpenPopup("###PortraitSourceRecords");
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip("查看背包、兵装库、投影台与雇员的真实读取状态");
        var viewport = ImGui.GetWindowViewport();
        if (viewport.IsNull) viewport = ImGui.GetMainViewport();
        var sourceWorkArea = viewport.WorkSize;
        ImGui.SetNextWindowSizeConstraints(Vector2.Zero, PortraitPopupLayout.MaximumSize(sourceWorkArea, Scale));
        ImGui.SetNextWindowSize(PortraitPopupLayout.PreferredSize(new Vector2(540 * Scale, 0), sourceWorkArea, Scale), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("###PortraitSourceRecords")) return;
        ImGui.TextUnformatted("物品来源");
        ImGui.Separator();
        foreach (var source in feature.SourceSummary) ImGui.TextWrapped(source);
        if (feature.SourceSummary.Count == 0) ImGui.TextWrapped("尚未读取物品。请先打开对应的背包、投影台或雇员背包。");
        ImGui.EndPopup();
    }
}
