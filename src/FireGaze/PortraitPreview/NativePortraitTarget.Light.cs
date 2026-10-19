using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal unsafe interface IPortraitLightNativeApi
{
    void SyncDirectionalAngles(AgentBannerEditorState* state, short vertical, short horizontal);
}

internal sealed unsafe partial class NativePortraitTarget
{
    /// <summary>Only these two live fields are owned; an earlier editor snapshot is never reapplied.</summary>
    public bool SetDirectionalLightAngles(PortraitIdentity identity, PortraitNativeLightAngles expected, PortraitNativeLightAngles desired)
    {
        if (!expected.IsValid || !desired.IsValid || api is not IPortraitEditorNativeApi native
            || !TryResolve(out var current,out _,out _) || current!=identity) return false;
        var view=(CharaViewPortrait*)identity.Model;
        ExportedPortraitData exported=default;
        try
        {
            using (var phase=PortraitLightInputTrace.Measure(PortraitLightTraceStage.NativeExportBefore))
            {
                native.Export(view,&exported);
                phase.Complete();
            }
            if (!IsCurrent(identity) || Angles(exported)!=expected) return false;
            if (expected==desired) return true;
            using (var phase=PortraitLightInputTrace.Measure(PortraitLightTraceStage.NativeSetAngle))
            {
                native.SetDirectionalAngle(view,desired.Vertical,desired.Horizontal);
                phase.Complete();
            }
            if (!IsCurrent(identity)) return false;
            using (var phase=PortraitLightInputTrace.Measure(PortraitLightTraceStage.NativeExportAfter))
            {
                native.Export(view,&exported);
                phase.Complete();
            }
            if (!IsCurrent(identity) || Angles(exported)!=desired) return false;
            var state=(AgentBannerEditorState*)identity.State;
            using (var phase=PortraitLightInputTrace.Measure(PortraitLightTraceStage.BannerEntryWrite))
            {
                state->BannerEntry.DirectionalLightingVerticalAngle=desired.Vertical;
                state->BannerEntry.DirectionalLightingHorizontalAngle=desired.Horizontal;
                phase.Complete();
            }
            if (api is IPortraitLightNativeApi controls && IsCurrent(identity))
            {
                using var phase=PortraitLightInputTrace.Measure(PortraitLightTraceStage.NativeSyncControls);
                controls.SyncDirectionalAngles(state,desired.Vertical,desired.Horizontal);
                phase.Complete();
            }
            return IsCurrent(identity);
        }
        catch(Exception exception)
        {
            // A native hook can change other fields before throwing. Broad rollback would
            // overwrite that newer state, so stop this gesture without replaying old data.
            LastError="主光方向暂时无法调整："+exception.Message;
            return false;
        }
    }

    private static PortraitNativeLightAngles Angles(ExportedPortraitData data)
        =>new(data.DirectionalLightingVerticalAngle,data.DirectionalLightingHorizontalAngle);

    private sealed partial class LiveApi : IPortraitLightNativeApi
    {
        public void SyncDirectionalAngles(AgentBannerEditorState* state,short vertical,short horizontal)
        {
            var addon=CurrentAddon(state);
            if (addon==null) return;
            if (addon->DirectionalLightingVerticalAngleSlider!=null)
            {
                using var phase=PortraitLightInputTrace.Measure(PortraitLightTraceStage.VerticalSliderSetValue);
                addon->DirectionalLightingVerticalAngleSlider->SetValue(vertical,false);
                phase.Complete();
            }
            addon=CurrentAddon(state);
            if (addon==null) return;
            if (addon->DirectionalLightingHorizontalAngleSlider!=null)
            {
                using var phase=PortraitLightInputTrace.Measure(PortraitLightTraceStage.HorizontalSliderSetValue);
                addon->DirectionalLightingHorizontalAngleSlider->SetValue(horizontal,false);
                phase.Complete();
            }
            if (CurrentAddon(state)!=null)
            {
                using var phase=PortraitLightInputTrace.Measure(PortraitLightTraceStage.SetHasChanged);
                state->SetHasChanged(true);
                phase.Complete();
            }
        }
    }
}
