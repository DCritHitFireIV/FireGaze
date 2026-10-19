using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
            // Keep the native angle setter narrow. Calling Atk slider setters from inside this
            // transaction adds a synchronous BannerEditor notification boundary. Its role in
            // the prior AppHang remains unproven. The next
            // framework sample exports the accepted angle for our detached UI.
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
            var verticalSlider=OwnedDirectionalSlider(addon,addon->DirectionalLightingVerticalAngleSlider);
            var horizontalSlider=OwnedDirectionalSlider(addon,addon->DirectionalLightingHorizontalAngleSlider);
            if (verticalSlider==null || horizontalSlider==null) return;
            {
                using var phase=PortraitLightInputTrace.Measure(PortraitLightTraceStage.VerticalSliderSetValue);
                verticalSlider->SetValue(vertical,false);
                phase.Complete();
            }
            // Keep this synchronization on one addon/control allocation. Reacquiring a replacement
            // with the same name/Id must not continue a gesture that began on the previous addon.
            if (CurrentAddon(state)!=addon || addon->DirectionalLightingVerticalAngleSlider!=verticalSlider
                || addon->DirectionalLightingHorizontalAngleSlider!=horizontalSlider
                || OwnedDirectionalSlider(addon,verticalSlider)==null || OwnedDirectionalSlider(addon,horizontalSlider)==null) return;
            {
                using var phase=PortraitLightInputTrace.Measure(PortraitLightTraceStage.HorizontalSliderSetValue);
                horizontalSlider->SetValue(horizontal,false);
                phase.Complete();
            }
            if (CurrentAddon(state)==addon && addon->DirectionalLightingVerticalAngleSlider==verticalSlider
                && addon->DirectionalLightingHorizontalAngleSlider==horizontalSlider
                && OwnedDirectionalSlider(addon,verticalSlider)!=null && OwnedDirectionalSlider(addon,horizontalSlider)!=null)
            {
                using var phase=PortraitLightInputTrace.Measure(PortraitLightTraceStage.SetHasChanged);
                state->SetHasChanged(true);
                phase.Complete();
            }
        }

        private static AtkComponentSlider* OwnedDirectionalSlider(AddonBannerEditor* addon,AtkComponentSlider* slider)
        {
            if (slider==null || addon->UldManager.NodeList==null || addon->UldManager.NodeListCount==0
                || addon->UldManager.NodeListCount>4096 || addon->UldManager.NodeListCount>addon->UldManager.NodeListSize)
                return null;
            // Find the component through the live addon before dereferencing its cached slider
            // pointer. Component node types are >=1000; the exact component kind lives in ULD.
            for (var index=0;index<addon->UldManager.NodeListCount;index++)
            {
                var node=addon->UldManager.NodeList[index];
                if (node==null || (ushort)node->Type<1000) continue;
                var componentNode=(AtkComponentNode*)node;
                if (componentNode->Component!=(AtkComponentBase*)slider) continue;
                const AtkUldManagerResourceFlag readyFlags = AtkUldManagerResourceFlag.Initialized | AtkUldManagerResourceFlag.ArraysAllocated;
                if (slider->OwnerNode!=componentNode || slider->UldManager.BaseType!=AtkUldManagerBaseType.Component
                    || (slider->UldManager.ResourceFlags & readyFlags)!=readyFlags
                    || slider->UldManager.LoadedState!=AtkLoadState.Loaded || slider->UldManager.Objects==null
                    || ((AtkUldComponentInfo*)slider->UldManager.Objects)->ComponentType!=ComponentType.Slider)
                    return null;
                return slider;
            }
            return null;
        }
    }
}
