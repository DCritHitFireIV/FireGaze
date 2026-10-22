using FireGaze.PortraitPreview;

static unsafe class LightingSpaceNativeCases
{
    public static void Run(Action<bool, string> check)
    {
        using var f = new EditorFixture();
        using var target = new NativePortraitTarget(f, _ => false, _ => { });
        check(target.TryResolve(out var identity, out _, out _), "space fixture resolves actual native editor owner");
        var before = target.CaptureEditorState(identity)!;
        var desired = before.Composition with { CameraYaw = .75f, DirectionalHorizontalAngle = 35 };
        f.Data.Expression = 21;
        var writes = f.Calls;
        var applied = target.SetLightingSpace(identity, before, desired);
        check(!applied && f.Calls == writes && f.Data.Expression == 21,
            "native space compare guard rejects unrelated fresh input before any native write");

        var invalidSource = target.CaptureEditorState(identity)!;
        var invalidCamera = invalidSource.Composition with
        {
            CameraPosition = new(0, 0, 3.5f, 0), CameraDistance = 3.5f,
        };
        writes = f.Calls;
        var oldNativeDistance = f.View->CameraDistance;
        check(!target.SetLightingSpace(identity, invalidSource, invalidCamera) && f.Calls == writes
            && f.View->CameraDistance == oldNativeDistance,
            "native space rejects an out-of-domain camera candidate before any native write");

        before=target.CaptureEditorState(identity)!;
        f.Data.DirectionalLightingColorRed=73;f.Data.AmbientLightingColorBlue=82;
        before=target.CaptureEditorState(identity)!;
        desired=before.Composition with {CameraPosition=new(.808f,-.179f,.868f,4),CameraYaw=.75f,CameraPitch=.15f,CameraDistance=1.2f,
            DirectionalVerticalAngle=30,DirectionalHorizontalAngle=35,Expression=55,AmbientRed=99,Frame=19,
            CameraTarget=new(0,0,0,0),Paused=true,AnimationProgress=9};
        f.State->BannerEntry.Expression=21;f.State->BannerEntry.AmbientLightingColorRed=19;
        writes=f.Calls;
        check(target.SetLightingSpace(identity,before,desired),"narrow native space transaction accepts a current complete source");
        check(f.Calls==writes+2 && MathF.Abs(f.View->CameraPosition.X-.808f)<.002f && f.View->CameraYaw==.75f
            && f.View->CameraPitch==.15f && f.View->CameraDistance==1.2f && f.Data.DirectionalLightingVerticalAngle==30
            && f.Data.DirectionalLightingHorizontalAngle==35,
            "space camera and main-light direction each use one narrow native update");
        check(f.PoseCalls==0 && f.PauseCalls==0 && f.FrameCalls==0 && f.AccentCalls==0 && f.VisibilityCalls==0
            && f.Data.Expression==21 && f.Data.DirectionalLightingColorRed==73 && f.Data.AmbientLightingColorBlue==82
            && f.View->CameraTarget.X==0 && f.State->BannerEntry.Expression==21 && f.State->BannerEntry.AmbientLightingColorRed==19,
            "space native write never imports pose appearance framing playback or unrelated working-entry values");
        before=target.CaptureEditorState(identity)!;writes=f.Calls;
        check(target.SetLightingSpace(identity,before,before.Composition) && f.Calls==writes,
            "native spatial no-op performs no setters");
        f.Framework=false;
        check(!target.SetLightingSpace(identity,before,desired with {CameraYaw=.4f}) && f.Calls==writes,
            "native spatial transaction rejects off-Framework execution before touching the editor");
        f.Framework=true;
        check(!target.SetLightingSpace(identity with {Generation=88},before,desired with {CameraYaw=.4f}) && f.Calls==writes,
            "native spatial transaction rejects an editor allocation generation mismatch");
        f.AfterDirectionalSet=()=>f.Data.Expression=93;
        check(!target.SetLightingSpace(identity,before,before.Composition with {DirectionalHorizontalAngle=40})
            && f.Data.Expression==93 && f.State->BannerEntry.DirectionalLightingHorizontalAngle==35,
            "foreign input inside native light callback stops commit without broad rollback or working-entry overwrite");
        f.AfterDirectionalSet=null;
        before=target.CaptureEditorState(identity)!;f.ThrowDirectionalSet=true;
        check(!target.SetLightingSpace(identity,before,before.Composition with {DirectionalHorizontalAngle=50})
            && f.Data.Expression==93 && f.PoseCalls==0 && f.PauseCalls==0,
            "native space setter exception stops locally without restoring a stale full snapshot");
    }
}
