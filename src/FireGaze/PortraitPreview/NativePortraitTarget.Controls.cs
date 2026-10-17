using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class NativePortraitTarget
{
    private sealed partial class LiveApi
    {
        private AddonBannerEditor* CurrentAddon(AgentBannerEditorState* state)
        {
            var editor=GetEditor();
            if(editor==null || editor->EditorState!=state || state->AgentBannerEditor!=editor) return null;
            var addon=gameGUI.GetAddonByName<AddonBannerEditor>("BannerEditor");
            return addon!=null && addon->Id==editor->AddonId && GetEditor()==editor
                && editor->EditorState==state && state->AgentBannerEditor==editor ? addon : null;
        }
        public void SyncControls(AgentBannerEditorState* state,PortraitComposition composition)
        {
            var addon=CurrentAddon(state);
            if(addon==null) return;
            if(addon->PlayAnimationCheckbox!=null) addon->PlayAnimationCheckbox->AtkComponentButton.IsChecked=!composition.Paused;
            if(addon->HeadFacingCameraCheckbox!=null) addon->HeadFacingCameraCheckbox->AtkComponentButton.IsChecked=composition.HeadFacingCamera;
            if(addon->EyesFacingCameraCheckbox!=null) addon->EyesFacingCameraCheckbox->AtkComponentButton.IsChecked=composition.EyesFacingCamera;
            // UI changes use setters without dispatch: importing history must not re-run user actions.
            for(var index=0;index<12;index++)
            {
                addon=CurrentAddon(state);if(addon==null) return;
                var slider=index switch {
                    0=>addon->AmbientLightingColorRedSlider,1=>addon->AmbientLightingColorGreenSlider,2=>addon->AmbientLightingColorBlueSlider,
                    3=>addon->AmbientLightingBrightnessSlider,4=>addon->DirectionalLightingColorRedSlider,5=>addon->DirectionalLightingColorGreenSlider,
                    6=>addon->DirectionalLightingColorBlueSlider,7=>addon->DirectionalLightingBrightnessSlider,8=>addon->DirectionalLightingVerticalAngleSlider,
                    9=>addon->DirectionalLightingHorizontalAngleSlider,10=>addon->CameraZoomSlider,_=>addon->ImageRotation};
                var value=index switch {
                    0=>composition.AmbientRed,1=>composition.AmbientGreen,2=>composition.AmbientBlue,3=>composition.AmbientBrightness,
                    4=>composition.DirectionalRed,5=>composition.DirectionalGreen,6=>composition.DirectionalBlue,7=>composition.DirectionalBrightness,
                    8=>composition.DirectionalVerticalAngle,9=>composition.DirectionalHorizontalAngle,10=>composition.CameraZoom,_=>composition.ImageRotation};
                if(slider!=null) slider->SetValue(value,false);
            }
            for(var index=1;index<6;index++)
            {
                addon=CurrentAddon(state);if(addon==null) return;
                var dataset=index switch {1=>&state->Backgrounds,2=>&state->Frames,3=>&state->Accents,4=>&state->Poses,_=>&state->Expressions};
                var row=index switch {1=>composition.Background,2=>composition.Frame,3=>composition.Decoration,4=>composition.BannerTimeline,_=>composition.Expression};
                var selected=FindRow(dataset,row,index!=5);
                var dropdown=addon->Dropdowns[index].Dropdown;
                if(selected>=0 && dropdown!=null) dropdown->SelectItem(selected);
            }
            addon=CurrentAddon(state);if(addon==null) return;
            var preset=state->GetPresetIndex(composition.Background,composition.Frame,composition.Decoration);
            addon=CurrentAddon(state);if(addon==null) return;
            if(preset>=0 && addon->Dropdowns[0].Dropdown!=null) addon->Dropdowns[0].Dropdown->SelectItem(preset);
            if(CurrentAddon(state)!=null) state->SetHasChanged(true);
        }
        private static int FindRow(AgentBannerEditorState.Dataset* dataset,ushort row,bool unlocked)
        {
            var entries=unlocked ? dataset->UnlockedEntries : dataset->SortedEntries;
            var count=unlocked ? dataset->UnlockedEntriesCount : dataset->SortedEntriesCount;
            // The editor owns these bounded arrays. No row pointers are saved in history.
            if(entries==null || count>10000) return -1;
            for(var index=0;index<count;index++)
                if(entries[index]!=null && entries[index]->Row!=0 && entries[index]->RowId==row) return index;
            return -1;
        }
    }
}
