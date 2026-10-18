using FireGaze.PortraitPreview;

static class DirectionalLightCases
{
    public static void Run(string root, Action<bool,string> check)
    {
#if LIGHT63
        // Removing same-field ownership checks would overwrite another plugin's latest edit.
        using (var f = Open(root, "conflict"))
        {
            f.BeginLightDrag(1); f.MoveLightDrag(1, 20, 40);
            f.EditComposition(c => c with { DirectionalVerticalAngle=11, DirectionalHorizontalAngle=12 });
            f.MoveLightDrag(1, 30, 50); f.EndLightDrag(1, 40, 60);
            check(f.Native.Snapshot.Composition.DirectionalVerticalAngle==11 && f.Native.Snapshot.Composition.DirectionalHorizontalAngle==12,
                "external same-field edit terminates ownership before a later move and release");
        }
        using (var f = Open(root, "other-fields"))
        {
            f.BeginLightDrag(2); f.MoveLightDrag(2, 20, 40); f.Sample(pointer:true);
            f.EditComposition(c => c with { CameraYaw=.75f,DirectionalRed=73,AmbientBlue=82,Frame=1,Expression=1 });
            f.MoveLightDrag(2, 30, 50); f.EndLightDrag(2, 40, 60);
            var c=f.Native.Snapshot.Composition;
            check(c.DirectionalVerticalAngle==40 && c.DirectionalHorizontalAngle==60 && c.CameraYaw==.75f
                && c.DirectionalRed==73 && c.AmbientBlue==82 && c.Frame==1 && c.Expression==1,
                "direction drag preserves independent live camera color frame and expression edits");
            check(f.Store.Count==2,"one continuous direction gesture persists exactly one release history entry");
            var history=PortraitEditorSnapshot.FromJSON(f.Store.GetRecords(f.Key!.Value).First().SnapshotJSON)!;
            check(history.Composition.CameraYaw==.75f && history.Composition.DirectionalHorizontalAngle==60,
                "release history captures latest actual editor including independent edits");
        }
        using (var f=Open(root,"release-latest"))
        {
            f.BeginLightDrag(3); f.MoveLightDrag(3, 10, 20); f.Sample(pointer:true);
            f.EndLightDrag(3, 44.6f, -179.6f);
            check(f.Native.Snapshot.Composition.DirectionalVerticalAngle==45 && f.Native.Snapshot.Composition.DirectionalHorizontalAngle==-180,
                "release applies its final quantized point before flushing history");
            check(f.Store.Count==2,"release after an earlier sample does not split direction history");
            f.MoveLightDrag(3,0,0);f.EndLightDrag(3,0,0);
            check(f.Native.Snapshot.Composition.DirectionalVerticalAngle==45 && f.Store.Count==2,
                "late move and duplicate release cannot write after gesture completion");
        }
        using (var f=Open(root,"no-op"))
        {
            f.BeginLightDrag(4);f.MoveLightDrag(4,.1f,.1f);f.EndLightDrag(4,0,0);
            check(f.Store.Count==1 && f.Native.LightWrites==0,"quantized no-op gesture produces no write or history entry");
            f.BeginLightDrag(4);f.EndLightDrag(4,20,20);
            check(f.Native.LightWrites==0,"reused gesture token cannot restart a completed gesture");
            f.EditComposition(c=>c with {CameraYaw=.5f});f.Sample(pointer:true);
            f.BeginLightDrag(4);
            check(f.Store.Count==1,"stale begin cannot flush an unrelated native drag that is still held");
        }
        using(var f=Open(root,"playing"))
        {
            f.EditComposition(c=>c with {Paused=true,BannerTimeline=1,AnimationProgress=.25f});f.RecordDirect();
            f.EditComposition(c=>c with {Paused=false,BannerTimeline=0,AnimationProgress=8});f.Sample();
            f.BeginLightDrag(5);f.MoveLightDrag(5,10,20);f.EndLightDrag(5,10,20);
            var history=PortraitEditorSnapshot.FromJSON(f.Store.GetRecords(f.Key!.Value).First().SnapshotJSON)!;
            check(history.Composition.Paused && history.Composition.BannerTimeline==1 && history.Composition.AnimationProgress==.25f,
                "light direction history preserves last paused pose while live animation plays");
        }
        foreach(var reason in new[]{"owner","source","identity","saveguard","closed"})
        using(var f=Open(root,"invalid-"+reason))
        {
            f.BeginLightDrag(6);f.MoveLightDrag(6,10,20);
            if(reason=="owner")f.Owner=new(10,99);
            if(reason=="source") f.Native.Snapshot=f.Native.Snapshot with {EntryPoint=1};
            if(reason=="identity") f.Native.Identity=f.Native.Identity with {Generation=99};
            if(reason=="saveguard") f.Native.CanBlockSave=false;
            if(reason=="closed") f.RollbackAndSync();
            var before=f.Native.Snapshot.Composition;
            f.MoveLightDrag(6,40,50);f.EndLightDrag(6,40,50);
            check(f.Native.Snapshot.Composition==before,$"{reason} invalidation blocks queued direction writes");
        }
        using(var f=Open(root,"invalid-point"))
        {
            f.BeginLightDrag(7);f.MoveLightDrag(7,float.NaN,20);f.EndLightDrag(7,float.NaN,20);
            check(f.Native.LightWrites==0 && f.Store.Count==1,"nonfinite light input never writes or creates history");
        }
        using(var f=Open(root,"poll-conflict"))
        {
            f.BeginLightDrag(8);f.MoveLightDrag(8,10,20);
            f.EditComposition(c=>c with {DirectionalHorizontalAngle=27});f.Sample(pointer:true);
            f.EditComposition(c=>c with {DirectionalHorizontalAngle=20});f.MoveLightDrag(8,30,40);
            check(f.Native.Snapshot.Composition.DirectionalHorizontalAngle==20,
                "framework observation permanently releases token even if external angles later return");
        }
        using(var f=Open(root,"reject"))
        {
            f.BeginLightDrag(9);f.Native.RejectLight=true;f.MoveLightDrag(9,10,20);f.Native.RejectLight=false;f.EndLightDrag(9,30,40);
            check(f.Native.LightWrites==0 && f.Store.Count==1,"native failure cancels later writes without adding history");
        }
        using(var f=Open(root,"sync"))
        {
            f.SetSyncPlate(true);f.BeginLightDrag(10);f.EndLightDrag(10,13,29);
            check(f.MirroredSnapshot?.Composition.DirectionalVerticalAngle==13 && f.MirroredSnapshot.Composition.DirectionalHorizontalAngle==29,
                "release publishes the actual direction to opt-in local plate mirror");
        }
#else
        check(false,"light direction gesture implementation is absent; no guarded two-angle editing is available");
#endif
    }
#if LIGHT63
    private static PortraitPreviewFeature Open(string root,string name)
    {
        var feature=new PortraitPreviewFeature(Path.Combine(root,"light-"+name,"history.json"));
        feature.Sample();feature.OpenPreview();return feature;
    }
#endif
}
