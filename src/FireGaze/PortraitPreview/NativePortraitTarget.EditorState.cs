using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Common.Math;

namespace FireGaze.PortraitPreview;

internal unsafe interface IPortraitEditorNativeApi
{
    void SyncControls(AgentBannerEditorState* state, PortraitComposition composition) { }
    void Export(CharaViewPortrait* view, ExportedPortraitData* data);
    void SetPoseTimed(CharaViewPortrait* view, ushort timeline, float time);
    void SetExpression(CharaViewPortrait* view, byte expression);
    void SetHeadDirection(CharaViewPortrait* view, float x, float y);
    void SetEyeDirection(CharaViewPortrait* view, float x, float y);
    void SetCameraZoom(CharaViewPortrait* view, byte zoom);
    void SetBackground(CharaViewPortrait* view, ushort background);
    void SetDirectionalColor(CharaViewPortrait* view, byte red, byte green, byte blue);
    void SetDirectionalBrightness(CharaViewPortrait* view, byte brightness);
    void SetDirectionalAngle(CharaViewPortrait* view, short vertical, short horizontal);
    void SetAmbientColor(CharaViewPortrait* view, byte red, byte green, byte blue);
    void SetAmbientBrightness(CharaViewPortrait* view, byte brightness);
    bool IsPaused(CharaViewPortrait* view);
    void SetPaused(CharaViewPortrait* view, bool paused);
    void SetCharacterVisible(CharaViewPortrait* view, bool visible);
    void ApplyCamera(CharaViewPortrait* view);
    void SetFrame(AgentBannerEditorState* state, ushort frame);
    void SetAccent(AgentBannerEditorState* state, ushort accent);
    bool UpdateCustomization(CharaViewPortrait* view, Character* character, byte[] customize);
    void SetEars(Character* character, bool hideVieraEars);
}

internal sealed unsafe partial class NativePortraitTarget
{
    public string SnapshotGameBuild { get; set; } = "FFCS:" + typeof(CharaViewPortrait).Assembly.GetName().Version;

    public PortraitEditorSnapshot? CaptureEditorState(PortraitIdentity identity)
    {
        if (api is not IPortraitEditorNativeApi native || !TryResolve(out var resolved, out _, out _) || resolved != identity) return null;
        var view=(CharaViewPortrait*)identity.Model;
        // SetPoseTimed can replace its timeline asynchronously. Its queued pause state identifies
        // a native transition, not an editable zero-progress state for the history recorder.
        if (view->IsAnimationPauseStatePending) return null;
        ExportedPortraitData data=default;
        native.Export(view,&data);
        if (!IsCurrent(identity)) return null;
        var paused=native.IsPaused(view);
        if (!IsCurrent(identity)) return null;
        var state=(AgentBannerEditorState*)identity.State;
        var follow=((Character*)identity.Character)->LookAt.BannerCameraFollowFlag;
        if (!IsCurrent(identity)) return null;
        var snapshot=new PortraitEditorSnapshot {
            GameBuild=SnapshotGameBuild, EntryPoint=(byte)state->OpenType, Look=ReadLook(view),
            Customize=view->ModelData.CustomizeData.Data.ToArray(), HideVieraEars=view->PortraitCharacterData.HideVieraEars,
            Composition=ReadComposition(view,state,&data,paused) with {
                HeadFacingCamera=(follow&LookAtContainer.BannerCameraFollowFlags.Head)!=0,
                EyesFacingCamera=(follow&LookAtContainer.BannerCameraFollowFlags.Eyes)!=0}
        };
        return IsCurrent(identity) && snapshot.IsValid ? snapshot : null;
    }

    public bool ApplyEditorState(PortraitIdentity identity, PortraitEditorSnapshot snapshot)
    {
        if (snapshot is null || !snapshot.IsValid || api is not IPortraitEditorNativeApi native) return false;
        var before=CaptureEditorState(identity);
        if (before is null || !snapshot.IsCompatibleWith(before)) return false;
        var compositionChanged = snapshot.Composition != before.Composition;
        var compositionAttempted = false;
        try {
            if (!Apply(identity,snapshot.Look) || !ApplyCustomization(identity,snapshot.Customize,snapshot.HideVieraEars)
                || !IsCurrent(identity))
            { if (!IsCurrent(identity)) return false; throw new InvalidOperationException("当前模型未接受临时外观。"); }
            // ImportPortraitData restarts SetPoseTimed, even for an identical pose. Beauty and
            // wardrobe edits must leave a paused timeline untouched instead of creating a
            // transient zero-progress state before the requested pose settles next frame.
            if (compositionChanged)
            {
                compositionAttempted = true;
                ApplyComposition(identity,snapshot.Composition,before.Composition,native);
            }
            return IsCurrent(identity);
        }
        catch(Exception ex) {
            LastError="肖像历史还原失败："+ex.Message;
            try {
                if (IsCurrent(identity)) {
                    Apply(identity,before.Look);
                    if (IsCurrent(identity)) ApplyCustomization(identity,before.Customize,before.HideVieraEars);
                    if (compositionAttempted && IsCurrent(identity)) ApplyComposition(identity,before.Composition,null,native);
                }
            } catch(Exception rollback) {LastError+="；临时状态还原失败："+rollback.Message;}
            error(LastError); return false;
        }
    }

    public bool RestoreOriginal(PortraitIdentity identity, PortraitLook look)
    {
        if (original is null || originalIdentity != identity || !IsCurrent(identity)) return false;
        try
        {
            if (api is IPortraitEditorNativeApi && !ApplyCustomization(identity, original.ModelData.CustomizeData.Data.ToArray(), original.CharacterData.HideVieraEars)) return false;
            return Apply(identity, look);
        }
        catch (Exception ex) { LastError="肖像原始外观还原失败："+ex.Message; return false; }
    }

    public bool ApplyCustomization(PortraitIdentity identity, byte[] customize, bool hideVieraEars)
    {
        if(customize is not {Length:26} || api is not IPortraitEditorNativeApi native || !IsCurrent(identity)) return false;
        var view=(CharaViewPortrait*)identity.Model;
        var current=view->ModelData.CustomizeData.Data;
        // A history or beauty operation cannot morph the portrait into another character model.
        if(current[0]!=customize[0] || current[1]!=customize[1] || current[2]!=customize[2] || current[4]!=customize[4]) return false;
        if(current.SequenceEqual(customize) && view->PortraitCharacterData.HideVieraEars==hideVieraEars) return true;
        if(!native.UpdateCustomization(view,(Character*)identity.Character,customize) || !IsCurrent(identity)) return false;
        native.SetEars((Character*)identity.Character, hideVieraEars);
        if(!IsCurrent(identity)) return false;
        customize.AsSpan().CopyTo(((Character*)identity.Character)->DrawData.CustomizeData.Data);
        customize.AsSpan().CopyTo(view->ModelData.CustomizeData.Data);
        customize.AsSpan().CopyTo(view->PortraitCharacterData.CustomizeData.Data);
        view->PortraitCharacterData.HideVieraEars=hideVieraEars;
        return IsCurrent(identity);
    }

    private void ApplyComposition(PortraitIdentity identity, PortraitComposition composition, PortraitComposition? previous, IPortraitEditorNativeApi native)
    {
        if(!IsCurrent(identity)) return;
        var view=(CharaViewPortrait*)identity.Model;var state=(AgentBannerEditorState*)identity.State;
        var data=ToNative(composition);
        // Presentation setters do not replace the timeline. A camera/light/frame restoration
        // must not reset a paused pose or issue a new asynchronous pause request.
        var poseChanged=previous is null || composition.BannerTimeline!=previous.BannerTimeline || composition.PoseClassJob!=previous.PoseClassJob
            || (composition.Paused || previous.Paused) && composition.AnimationProgress!=previous.AnimationProgress;
        view->PoseClassJob=composition.PoseClassJob;
        if(poseChanged)
        {
            // CN timeline setup seeks to max(0, requestedTime - 0.001f). Export reports that
            // actual time, so replaying an exported positive value requires the inverse input.
            // Zero retains the native start request; this is not history comparison tolerance.
            var time=composition.AnimationProgress>0 ? composition.AnimationProgress+0.001f : 0;
            native.SetPoseTimed(view,composition.BannerTimeline,time);
            if(!IsCurrent(identity)) return;
        }
        if(previous is null || composition.Expression!=previous.Expression)
        { native.SetExpression(view,composition.Expression); if(!IsCurrent(identity)) return; }
        if(previous is null || composition.HeadDirection!=previous.HeadDirection || composition.HeadFacingCamera!=previous.HeadFacingCamera)
        { native.SetHeadDirection(view,composition.HeadDirection.X,composition.HeadDirection.Y); if(!IsCurrent(identity)) return; }
        if(previous is null || composition.EyeDirection!=previous.EyeDirection || composition.EyesFacingCamera!=previous.EyesFacingCamera)
        { native.SetEyeDirection(view,composition.EyeDirection.X,composition.EyeDirection.Y); if(!IsCurrent(identity)) return; }
        // Native direction setters explicitly disable their respective camera-follow bits.
        // Restore the typed flags after both setters, preserving every unrelated mode bit.
        ref var follow=ref ((Character*)identity.Character)->LookAt.BannerCameraFollowFlag;
        const LookAtContainer.BannerCameraFollowFlags followMask=LookAtContainer.BannerCameraFollowFlags.Head|LookAtContainer.BannerCameraFollowFlags.Eyes;
        follow=(follow&~followMask)
            |(composition.HeadFacingCamera?LookAtContainer.BannerCameraFollowFlags.Head:0)
            |(composition.EyesFacingCamera?LookAtContainer.BannerCameraFollowFlags.Eyes:0);
        if(previous is null || composition.DirectionalRed!=previous.DirectionalRed || composition.DirectionalGreen!=previous.DirectionalGreen || composition.DirectionalBlue!=previous.DirectionalBlue)
        { native.SetDirectionalColor(view,composition.DirectionalRed,composition.DirectionalGreen,composition.DirectionalBlue); if(!IsCurrent(identity)) return; }
        if(previous is null || composition.DirectionalBrightness!=previous.DirectionalBrightness)
        { native.SetDirectionalBrightness(view,composition.DirectionalBrightness); if(!IsCurrent(identity)) return; }
        if(previous is null || composition.DirectionalVerticalAngle!=previous.DirectionalVerticalAngle || composition.DirectionalHorizontalAngle!=previous.DirectionalHorizontalAngle)
        { native.SetDirectionalAngle(view,composition.DirectionalVerticalAngle,composition.DirectionalHorizontalAngle); if(!IsCurrent(identity)) return; }
        if(previous is null || composition.AmbientRed!=previous.AmbientRed || composition.AmbientGreen!=previous.AmbientGreen || composition.AmbientBlue!=previous.AmbientBlue)
        { native.SetAmbientColor(view,composition.AmbientRed,composition.AmbientGreen,composition.AmbientBlue); if(!IsCurrent(identity)) return; }
        if(previous is null || composition.AmbientBrightness!=previous.AmbientBrightness)
        { native.SetAmbientBrightness(view,composition.AmbientBrightness); if(!IsCurrent(identity)) return; }
        if(previous is null || composition.Background!=previous.Background)
        { native.SetBackground(view,composition.Background); if(!IsCurrent(identity)) return; }
        if(previous is null || composition.CameraZoom!=previous.CameraZoom)
        { native.SetCameraZoom(view,composition.CameraZoom); if(!IsCurrent(identity)) return; }
        // Preserve full precision while using the native camera update.
        view->CameraPosition=new(composition.CameraPosition.X,composition.CameraPosition.Y,composition.CameraPosition.Z,composition.CameraPosition.W);
        view->CameraTarget=new(composition.CameraTarget.X,composition.CameraTarget.Y,composition.CameraTarget.Z,composition.CameraTarget.W);
        view->CameraYaw=composition.CameraYaw;view->CameraPitch=composition.CameraPitch;
        view->CameraDistance=composition.CameraDistance;view->CameraZoomNormalized=composition.CameraZoomNormalized;
        view->ImageRotation=composition.ImageRotation;
        native.ApplyCamera(view);
        if(!IsCurrent(identity)) return;
        if(previous is null || composition.Frame!=previous.Frame)
        { native.SetFrame(state,composition.Frame); if(!IsCurrent(identity)) return; }
        if(previous is null || composition.Decoration!=previous.Decoration)
        { native.SetAccent(state,composition.Decoration); if(!IsCurrent(identity)) return; }
        // BannerEntry is inline editor-local data, not the persistent BannerModule entry.
        ref var entry=ref state->BannerEntry;
        entry.CameraPosition=data.CameraPosition;entry.CameraTarget=data.CameraTarget;
        entry.HeadDirection=data.HeadDirection;entry.EyeDirection=data.EyeDirection;
        entry.ImageRotation=data.ImageRotation;entry.CameraZoom=data.CameraZoom;
        entry.BannerTimeline=data.BannerTimeline;entry.AnimationProgress=data.AnimationProgress;entry.Expression=data.Expression;
        entry.BannerBg=data.BannerBg;entry.BannerFrame=composition.Frame;entry.BannerDecoration=composition.Decoration;
        entry.DirectionalLightingColorRed=data.DirectionalLightingColorRed;entry.DirectionalLightingColorGreen=data.DirectionalLightingColorGreen;
        entry.DirectionalLightingColorBlue=data.DirectionalLightingColorBlue;entry.DirectionalLightingBrightness=data.DirectionalLightingBrightness;
        entry.DirectionalLightingVerticalAngle=data.DirectionalLightingVerticalAngle;entry.DirectionalLightingHorizontalAngle=data.DirectionalLightingHorizontalAngle;
        entry.AmbientLightingColorRed=data.AmbientLightingColorRed;entry.AmbientLightingColorGreen=data.AmbientLightingColorGreen;
        entry.AmbientLightingColorBlue=data.AmbientLightingColorBlue;entry.AmbientLightingBrightness=data.AmbientLightingBrightness;
        if(poseChanged || previous is null || composition.Paused!=previous.Paused) native.SetPaused(view,composition.Paused);
        if(IsCurrent(identity)) native.SetCharacterVisible(view, composition.CharacterVisible);
        if(IsCurrent(identity)) native.SyncControls(state,composition);
    }

    private static PortraitComposition ReadComposition(CharaViewPortrait* view, AgentBannerEditorState* state, ExportedPortraitData* d,bool paused)=>new() {
        CameraPosition=new(view->CameraPosition.X,view->CameraPosition.Y,view->CameraPosition.Z,view->CameraPosition.W),
        CameraTarget=new(view->CameraTarget.X,view->CameraTarget.Y,view->CameraTarget.Z,view->CameraTarget.W),
        CameraYaw=view->CameraYaw,CameraPitch=view->CameraPitch,CameraDistance=view->CameraDistance,CameraZoomNormalized=view->CameraZoomNormalized,
        ImageRotation=d->ImageRotation,CameraZoom=d->CameraZoom,BannerTimeline=d->BannerTimeline,AnimationProgress=d->AnimationProgress,Expression=d->Expression,
        HeadDirection=new((float)d->HeadDirection.X,(float)d->HeadDirection.Y),EyeDirection=new((float)d->EyeDirection.X,(float)d->EyeDirection.Y),
        DirectionalRed=d->DirectionalLightingColorRed,DirectionalGreen=d->DirectionalLightingColorGreen,DirectionalBlue=d->DirectionalLightingColorBlue,
        DirectionalBrightness=d->DirectionalLightingBrightness,DirectionalVerticalAngle=d->DirectionalLightingVerticalAngle,DirectionalHorizontalAngle=d->DirectionalLightingHorizontalAngle,
        AmbientRed=d->AmbientLightingColorRed,AmbientGreen=d->AmbientLightingColorGreen,AmbientBlue=d->AmbientLightingColorBlue,AmbientBrightness=d->AmbientLightingBrightness,
        Background=d->BannerBg,Frame=state->BannerEntry.BannerFrame,Decoration=state->BannerEntry.BannerDecoration,PoseClassJob=view->PoseClassJob,
        Paused=paused,CharacterVisible=view->CharacterVisible
    };
    private static ExportedPortraitData ToNative(PortraitComposition c)=>new() {
        CameraPosition=new((Half)c.CameraPosition.X,(Half)c.CameraPosition.Y,(Half)c.CameraPosition.Z,(Half)c.CameraPosition.W),
        CameraTarget=new((Half)c.CameraTarget.X,(Half)c.CameraTarget.Y,(Half)c.CameraTarget.Z,(Half)c.CameraTarget.W),
        ImageRotation=c.ImageRotation,CameraZoom=c.CameraZoom,BannerTimeline=c.BannerTimeline,AnimationProgress=c.AnimationProgress,Expression=c.Expression,
        HeadDirection=new((Half)c.HeadDirection.X,(Half)c.HeadDirection.Y),EyeDirection=new((Half)c.EyeDirection.X,(Half)c.EyeDirection.Y),
        DirectionalLightingColorRed=c.DirectionalRed,DirectionalLightingColorGreen=c.DirectionalGreen,DirectionalLightingColorBlue=c.DirectionalBlue,
        DirectionalLightingBrightness=c.DirectionalBrightness,DirectionalLightingVerticalAngle=c.DirectionalVerticalAngle,DirectionalLightingHorizontalAngle=c.DirectionalHorizontalAngle,
        AmbientLightingColorRed=c.AmbientRed,AmbientLightingColorGreen=c.AmbientGreen,AmbientLightingColorBlue=c.AmbientBlue,AmbientLightingBrightness=c.AmbientBrightness,BannerBg=c.Background
    };

    private sealed partial class LiveApi : IPortraitEditorNativeApi
    {
        public void Export(CharaViewPortrait* view,ExportedPortraitData* data)=>view->ExportPortraitData(data);
        public void SetPoseTimed(CharaViewPortrait* view,ushort timeline,float time)=>view->SetPoseTimed(timeline,time);
        public void SetExpression(CharaViewPortrait* view,byte expression)=>view->SetExpression(expression);
        public void SetHeadDirection(CharaViewPortrait* view,float x,float y)=>view->SetHeadDirection(x,y);
        public void SetEyeDirection(CharaViewPortrait* view,float x,float y)=>view->SetEyeDirection(x,y);
        public void SetCameraZoom(CharaViewPortrait* view,byte zoom)=>view->SetCameraZoom(zoom);
        public void SetBackground(CharaViewPortrait* view,ushort background)=>view->SetBackground(background);
        public void SetDirectionalColor(CharaViewPortrait* view,byte red,byte green,byte blue)=>view->SetDirectionalLightingColor(red,green,blue);
        public void SetDirectionalBrightness(CharaViewPortrait* view,byte brightness)=>view->SetDirectionalLightingBrightness(brightness);
        public void SetDirectionalAngle(CharaViewPortrait* view,short vertical,short horizontal)=>view->SetDirectionalLightingAngle(vertical,horizontal);
        public void SetAmbientColor(CharaViewPortrait* view,byte red,byte green,byte blue)=>view->SetAmbientLightingColor(red,green,blue);
        public void SetAmbientBrightness(CharaViewPortrait* view,byte brightness)=>view->SetAmbientLightingBrightness(brightness);
        public bool IsPaused(CharaViewPortrait* view)=>view->IsAnimationPaused();
        public void SetPaused(CharaViewPortrait* view,bool paused)=>view->ToggleAnimationPlayback(paused);
        public void SetCharacterVisible(CharaViewPortrait* view,bool visible)=>view->ToggleCharacterVisibility(visible);
        public void ApplyCamera(CharaViewPortrait* view)=>view->ApplyCameraPositions();
        public void SetFrame(AgentBannerEditorState* state,ushort frame)=>state->SetFrame(frame);
        public void SetAccent(AgentBannerEditorState* state,ushort accent)=>state->SetAccent(accent);
        public void SetEars(Character* character, bool hideVieraEars) => character->DrawData.HideVieraEars(hideVieraEars);
        public bool UpdateCustomization(CharaViewPortrait* view,Character* character,byte[] customize)
        {
            var generation=Generation(character);
            var draw=(CharacterBase*)character->DrawObject;
            if(draw==null || draw->GetModelType()!=CharacterBase.ModelType.Human) return false;
            if (view->GetCharacter()!=character || Generation(character)!=generation
                || character->DrawObject!=(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject*)draw) return false;
            var human=(Human*)draw;
            Human.DrawData data=default;
            customize.AsSpan().CopyTo(data.CustomizeData.Data);
            data.AnimationVariant=human->AnimationVariant;
            // skipEquipmentArrays keeps every existing equipment/facewear model and both dye channels.
            if(!human->UpdateDrawData(&data,true)) return false;
            return true;
        }
    }
}
