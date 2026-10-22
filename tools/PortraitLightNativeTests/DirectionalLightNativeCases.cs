using FireGaze.PortraitPreview;

static unsafe class DirectionalLightNativeCases
{
    public static void Run(Action<bool,string> check)
    {
#if LIGHTNATIVE63
        // A broad ApplyEditorState implementation would restart pose and rewrite these sentinel fields.
        using var f=new EditorFixture();
        using var t=new NativePortraitTarget(f,_=>false,_=>{});
        check(t.TryResolve(out var id,out _,out _),"light fixture resolves actual editor identity");
        f.Data.DirectionalLightingVerticalAngle=10;f.Data.DirectionalLightingHorizontalAngle=20;
        f.Data.DirectionalLightingColorRed=73;f.Data.AmbientLightingColorBlue=82;
        f.State->BannerEntry.DirectionalLightingVerticalAngle=10;f.State->BannerEntry.DirectionalLightingHorizontalAngle=20;
        var yaw=f.View->CameraYaw;var timeline=f.Data.BannerTimeline;var expression=f.Data.Expression;
        var beforeCalls=f.Calls;
        check(t.SetDirectionalLightAngles(id,new(10,20),new(30,-45)),"narrow setter accepts owned native angles");
        check(f.Calls==beforeCalls+1 && f.Data.DirectionalLightingVerticalAngle==30 && f.Data.DirectionalLightingHorizontalAngle==-45,
            "narrow setter emits one directional native call with exact desired angles");
        check(f.Data.DirectionalLightingColorRed==73 && f.Data.AmbientLightingColorBlue==82 && f.View->CameraYaw==yaw
            && f.Data.BannerTimeline==timeline && f.Data.Expression==expression && f.PoseCalls==0 && f.PauseCalls==0,
            "narrow setter preserves live colors camera timeline expression and playback");
        check(f.State->BannerEntry.DirectionalLightingVerticalAngle==30 && f.State->BannerEntry.DirectionalLightingHorizontalAngle==-45,
            "native working entry receives only the new directional angles");
        beforeCalls=f.Calls;
        check(t.SetDirectionalLightAngles(id,new(30,-45),new(30,-45)) && f.Calls==beforeCalls,
            "exact native no-op does not dirty or restart the editor");
        f.Data.DirectionalLightingHorizontalAngle=5;
        check(!t.SetDirectionalLightAngles(id,new(30,-45),new(60,60)) && f.Data.DirectionalLightingHorizontalAngle==5 && f.Calls==beforeCalls,
            "narrow setter compare guard preserves another plugin's same-field change");
        check(!t.SetDirectionalLightAngles(id,new(30,5),new(181,0)) && f.Calls==beforeCalls,
            "native angle outside documented signed range never reaches setter");
        f.Framework=false;
        check(!t.SetDirectionalLightAngles(id,new(30,5),new(60,60)) && f.Calls==beforeCalls,
            "narrow directional setter refuses native reads and writes off Framework thread");
        f.Framework=true;
        check(!t.SetDirectionalLightAngles(id with {Generation=999},new(30,5),new(60,60)) && f.Calls==beforeCalls,
            "replacement portrait allocation cannot receive a stale directional write");
        f.AfterDirectionalSet=()=>f.Data.DirectionalLightingHorizontalAngle=77;
        check(!t.SetDirectionalLightAngles(id,new(30,5),new(60,60)) && f.Data.DirectionalLightingHorizontalAngle==77,
            "post-set foreign angle is detected and never broadly rolled back");
        check(f.State->BannerEntry.DirectionalLightingHorizontalAngle==-45,
            "post-set conflict does not overwrite current working entry with rejected desired angles");
        f.AfterDirectionalSet=null;f.ThrowDirectionalSet=true;beforeCalls=f.Calls;
        check(!t.SetDirectionalLightAngles(id,new(60,77),new(80,80)) && f.Calls==beforeCalls+1 && f.Data.DirectionalLightingHorizontalAngle==77,
            "native setter exception relinquishes without replaying any earlier snapshot");
#else
        check(false,"narrow native directional setter is absent; guarded real-structure direction updates cannot run");
#endif
    }
}
