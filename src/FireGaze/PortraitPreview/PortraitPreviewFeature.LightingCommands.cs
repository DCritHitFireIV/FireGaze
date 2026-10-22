using System.Globalization;
using System.Numerics;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    // Development verification follows the same Framework-owned entry points as the UI.
    private void LightingFitCommand(string[] parts)
    {
        if (!pluginInterface.IsDev) return;
        if (parts is ["light-fit","window"]) { Mutate(()=>window.OpenLightingFit());return; }
        if (parts is ["light-space","window"]) { Mutate(()=>window.OpenLightingSpace());return; }
        if (parts.Length != 15 || parts[1] != "start") { Error("用法：light-fit start R暗 R中 R亮 G暗 G中 G亮 B暗 B中 B亮 左 上 右 下");return; }
        var values=new int[9];var rect=new float[4];
        for(var index=0;index<9;index++)
            if(!int.TryParse(parts[index+2],NumberStyles.Integer,CultureInfo.InvariantCulture,out values[index]))return;
        for(var index=0;index<4;index++)
            if(!float.TryParse(parts[index+11],NumberStyles.Float,CultureInfo.InvariantCulture,out rect[index]))return;
        if(!PortraitColorToneTarget.TryCreate(values[..3],values[3..6],values[6..9],out var target)
            ||!PortraitSampleRegion.TryFromDrag(new Vector2(rect[0],rect[1]),new Vector2(rect[2],rect[3]),out var region))
        { Error("曲线或取样区域无效。");return; }
        StartLightingFit(target!,region);
    }

    private void SetPortraitPlayback(bool paused) => Mutate(()=>
    {
        if(!CancelLightingFitForEdit()||!session.IsActive||!target.TryResolve(out var identity,out _,out _)
            ||target.CaptureEditorState(identity) is not{} before)return;
        if(before.Composition.Paused==paused)return;
        HandleHistoryBatchResult(historyBatch.Flush());
        if(!target.SetAnimationPaused(identity,paused))
        { Error("暂时无法切换动作播放状态。");return; }
        if(paused)RecordDirectChange("定格动作");
    });
}
