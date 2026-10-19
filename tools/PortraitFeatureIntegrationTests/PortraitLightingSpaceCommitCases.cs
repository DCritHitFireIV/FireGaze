using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

static unsafe class PortraitLightingSpaceCommitCases
{
    public static void Run(string root, Action<bool, string> check)
    {
        RunStale(root, check);
        RunOwners(root, check);
        RunPlayback(root, check);
        RunCommit(root, check);
        RunDetachedBinding(check);
    }

    private static void RunStale(string root, Action<bool, string> check)
    {
        using var feature = new PortraitPreviewFeature(Path.Combine(root, "space-stale.json"));
        feature.Sample(); feature.OpenPreview();
        var binding = feature.CurrentLightingSpaceBinding!;
        var desired = binding.Composition with { CameraYaw = .8f };
        feature.EditComposition(c => c with { Expression = 17, AmbientRed = 127, Frame = 1 });
        var before = feature.Native.ApplyCalls;
        var count = feature.History.Count;
        feature.ApplyLightingSpace(binding, WithYaw(desired, .8f));
        check(feature.Native.ApplyCalls == before && feature.Native.Snapshot.Composition.Expression == 17
            && feature.Native.Snapshot.Composition.AmbientRed == 127 && feature.Native.Snapshot.Composition.Frame == 1,
            "queued space gesture rejects fresh expression light-color and frame changes without overwriting them");
        check(feature.History.Count == count, "stale space gesture creates no history record");
        foreach (var (label, edit) in new (string, Func<PortraitEditorSnapshot, PortraitEditorSnapshot>)[]
        {
            ("wardrobe", s => { var look=s.Look.Copy();look.Pieces[3]=new(998,4,7);return s with {Look=look}; }),
            ("beautician", s => {var customize=(byte[])s.Customize.Clone();customize[6]=2;return s with {Customize=customize};}),
            ("pose", s => s with {Composition=s.Composition with {BannerTimeline=43}}),
            ("ambient light", s => s with {Composition=s.Composition with {AmbientBrightness=73}}),
            ("image rotation", s => s with {Composition=s.Composition with {ImageRotation=10}}),
            ("head follow", s => s with {Composition=s.Composition with {HeadFacingCamera=true}}),
        })
        {
            feature.Sample(); binding=feature.CurrentLightingSpaceBinding!;
            feature.Native.Snapshot=edit(feature.Native.Snapshot);
            var actual=feature.Native.Snapshot.ToJSON();before=feature.Native.ApplyCalls;count=feature.History.Count;
            feature.ApplyLightingSpace(binding,WithYaw(binding.Composition,.2f));
            check(feature.Native.ApplyCalls==before && feature.Native.Snapshot.ToJSON()==actual && feature.History.Count==count,
                $"space full-state guard rejects a newer {label} input without writes or history");
        }
    }

    private static void RunOwners(string root, Action<bool, string> check)
    {
        foreach (var kind in new[] {"generation", "character", "source", "entry point", "closed preview", "save guard"})
        {
            using var feature=new PortraitPreviewFeature(Path.Combine(root,"space-owner-"+kind+".json"));
            if(kind=="source") feature.Native.Snapshot=feature.Native.Snapshot with {EntryPoint=1};
            feature.Sample();feature.OpenPreview();var binding=feature.CurrentLightingSpaceBinding!;
            var count=feature.History.Count;var calls=feature.Native.ApplyCalls;
            switch(kind)
            {
                case "generation": feature.Native.Identity=feature.Native.Identity with {Generation=99};break;
                case "character": feature.Owner=new(10,999);break;
                case "source": RaptureGearsetModule.Value->EnabledGearsetIndex2EntryIndex[1]=1;feature.Native.State->OpenerEnabledGearsetIndex=1;break;
                case "entry point": feature.Native.Snapshot=feature.Native.Snapshot with {EntryPoint=1};break;
                case "closed preview": feature.RollbackAndSync();calls=feature.Native.ApplyCalls;break;
                case "save guard": feature.Native.CanBlockSave=false;break;
            }
            var actual=feature.Native.Snapshot.ToJSON();
            feature.ApplyLightingSpace(binding,WithYaw(binding.Composition,.7f));
            check(feature.Native.ApplyCalls==calls && feature.Native.Snapshot.ToJSON()==actual && feature.History.Count==count,
                $"space commit rejects changed {kind} without writing a replacement owner or recording history");
            if(kind=="source") RaptureGearsetModule.Value->EnabledGearsetIndex2EntryIndex[1]=0;
        }
    }

    private static void RunPlayback(string root, Action<bool, string> check)
    {
        using var feature=new PortraitPreviewFeature(Path.Combine(root,"space-playback.json"));
        feature.EditComposition(c=>c with {Paused=false,BannerTimeline=22,AnimationProgress=.4f,
            CameraPosition=new(0,0,1.2f,1),CameraTarget=new(0,0,0,1),CameraDistance=1.2f});
        feature.Sample();feature.OpenPreview();var binding=feature.CurrentLightingSpaceBinding!;
        var calls=feature.Native.ApplyCalls;var count=feature.History.Count;
        feature.ApplyLightingSpace(binding,binding.Composition with {CameraYaw=.6f});
        check(feature.Native.ApplyCalls==calls && feature.History.Count==count,
            "inconsistent camera yaw and position are rejected before the native space write");
        feature.EditComposition(c=>c with {AnimationProgress=.9f});
        feature.ApplyLightingSpace(binding,WithYaw(binding.Composition,.6f));
        check(feature.Native.Snapshot.Composition.CameraYaw==.6f && feature.Native.Snapshot.Composition.AnimationProgress==.9f
            && !feature.Native.Snapshot.Composition.Paused && feature.History.Count==count+1,
            "running timeline advances during a gesture without rejection or rewind and produces one camera history entry");
        var recorded=PortraitEditorSnapshot.FromJSON(feature.Store.GetRecords(feature.Key!.Value)[0].SnapshotJSON)!;
        check(recorded.Composition.AnimationProgress==.4f && recorded.Composition.CameraYaw==.6f,
            "space camera history retains the prior playback baseline rather than recording automatic animation time");
        feature.EditComposition(c=>c with {Paused=true,AnimationProgress=2});feature.Sample();binding=feature.CurrentLightingSpaceBinding!;
        count=feature.History.Count;calls=feature.Native.ApplyCalls;feature.EditComposition(c=>c with {AnimationProgress=2.1f});
        feature.ApplyLightingSpace(binding,WithYaw(binding.Composition,.2f));
        check(feature.Native.ApplyCalls==calls && feature.Native.Snapshot.Composition.AnimationProgress==2.1f && feature.History.Count==count,
            "paused frame edits are substantive state changes and reject a stale spatial gesture");
    }

    private static PortraitComposition WithYaw(PortraitComposition composition, float yaw)
    {
        var distance = composition.CameraDistance;
        var target = composition.CameraTarget;
        var horizontalDistance = MathF.Cos(composition.CameraPitch) * distance;
        var position = new PortraitVector4(
            target.X + MathF.Sin(yaw) * horizontalDistance,
            target.Y - MathF.Sin(composition.CameraPitch) * distance,
            target.Z + MathF.Cos(yaw) * horizontalDistance,
            composition.CameraPosition.W);
        return composition with { CameraYaw = yaw, CameraPosition = position };
    }

    private static void RunCommit(string root, Action<bool, string> check)
    {
        using var feature=new PortraitPreviewFeature(Path.Combine(root,"space-commit.json"));
        feature.EditComposition(c=>c with {CameraPosition=new(0,0,1.2f,1),CameraTarget=new(0,0,0,1),CameraDistance=1.2f});
        feature.Sample();feature.OpenPreview();var binding=feature.CurrentLightingSpaceBinding!;
        var count=feature.History.Count;var calls=feature.Native.ApplyCalls;
        feature.ApplyLightingSpace(binding,binding.Composition);
        check(feature.Native.ApplyCalls==calls && feature.History.Count==count,"unchanged space gesture writes and records nothing");
        feature.ApplyLightingSpace(binding,WithYaw(binding.Composition,.9f) with {DirectionalHorizontalAngle=30,
            Expression=55,Background=9,AmbientRed=88,Paused=true,ImageRotation=80,CameraZoom=120,
            CameraTarget=new(2,2,2,2),CameraZoomNormalized=.9f});
        var actual=feature.Native.Snapshot.Composition;
        check(actual.CameraYaw==.9f && actual.DirectionalHorizontalAngle==30 && actual.Expression==0 && actual.Background==0
            && actual.AmbientRed==0 && !actual.Paused && actual.ImageRotation==0 && actual.CameraZoom==0
            && actual.CameraTarget==new PortraitVector4(0,0,0,1) && actual.CameraDistance==1.2f && actual.CameraZoomNormalized==0,
            "successful space commit masks caller input to owned orbit and light-direction fields only");
        check(feature.History.Count==count+1 && feature.Native.ApplyCalls==calls+1,
            "successful space gesture performs one narrow transaction and appends exactly one history record");
        count=feature.History.Count;calls=feature.Native.ApplyCalls;
        feature.ApplyLightingSpace(binding,WithYaw(binding.Composition,.3f));
        check(feature.History.Count==count && feature.Native.ApplyCalls==calls,"replaying an already-consumed space binding is rejected as stale");
        feature.Sample();binding=feature.CurrentLightingSpaceBinding!;feature.Native.AfterEditorApply=()=>feature.EditComposition(c=>c with {Expression=97});
        feature.ApplyLightingSpace(binding,WithYaw(binding.Composition,.4f));
        check(feature.History.Count==count && feature.Native.Snapshot.Composition.Expression==97,
            "post-write foreign input is preserved and cannot be published as a successful space history record");
    }

    private static void RunDetachedBinding(Action<bool, string> check)
    {
        var source=new PortraitEditorSnapshot {GameBuild="binding-fixture"};
        var identity=new PortraitIdentity(1,2,3,4,5);
        var key=new PortraitHistoryKey(6,PortraitHistoryEntryPoint.AdventurerPlate,0,"model");
        check(PortraitLightingSpaceBinding.TryCreate(identity,key,source,out var binding),"valid spatial binding captures full detached editor state");
        var originalKey=binding!.StateKey;
        source.Customize[6]=99;source.Look.Pieces[3]=new(772,3,4);
        var returned=binding.Snapshot;returned.Customize[7]=98;returned.Look.Pieces[4]=new(773,5,6);
        check(binding.StateKey==originalKey && binding.Snapshot.Customize[6]==0 && binding.Snapshot.Customize[7]==0
            && binding.Snapshot.Look.Pieces[3].ItemID==0 && binding.Snapshot.Look.Pieces[4].ItemID==0,
            "spatial binding owns appearance arrays independently of both producer and consumer mutation");
        check(!binding.Matches(identity with {Generation=8},key,binding.Snapshot)
            && !binding.Matches(identity,key with {ContentID=9},binding.Snapshot),
            "same composition cannot authorize a reopened editor or different history owner");
        check(!PortraitLightingSpaceBinding.TryCreate(identity with {Generation=0},key,new(),out _),
            "missing native generation cannot become a valid detached spatial binding");
    }
}
