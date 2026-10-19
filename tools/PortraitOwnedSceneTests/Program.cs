using System.Runtime.InteropServices;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

#if !OWNED_SCENE
Console.Error.WriteLine("FAIL: no native owned-character consumer exists; complete source data cannot reach a second portrait scene.");
return 1;
#else
return Checks.Run();

unsafe static class Checks
{
    static int count;
    static void Check(bool value, string name) { count++; if (!value) throw new Exception(name); }
    internal static int Run()
    {
        try
        {
            SnapshotPreservesCompleteAppearance();
            NoPermitDoesNotTouchNative();
            NativeLifecycleAndComposition();
            FailedAllocationDoesNotConstruct();
            NativeFailureRetiresInOrder();
            OwnershipLossNeverDeletesNewOwner();
            LateSetterStopsRemainingWrites();
            DuplicateFrameDoesNotRenderTwice();
            CharacterReplacementStopsComposition();
            PendingPauseCannotPublishReady();
            ReentrantRetireWaitsForNativeReturn();
            CharacterReplacementInsideSetterStopsImmediately();
            ReleaseFailureDoesNotCallDestructor();
            SourceCaptureOwnsAllValues();
            ReadyViewIsBorrowedOnlyInsideCurrentPhase();
            Console.WriteLine($"PASS: {count} owned-scene checks; real SDK structs, injected native boundary; no live game calls.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
    }
    static PortraitIdentity Source => new(101, 102, 103, 9, 104);
    static PortraitComposition Composition => new()
    {
        CameraPosition = new(1, 2, 3, 0), CameraTarget = new(.1f, .2f, .3f, 0), CameraYaw=.3f,
        CameraPitch=.2f, CameraDistance=3.1f, CameraZoomNormalized=.35f, CameraZoom=37,
        ImageRotation=12, BannerTimeline=42, AnimationProgress=.25f, Expression=7, PoseClassJob=22,
        HeadDirection=new(.15f,.25f), EyeDirection=new(.35f,.45f), HeadFacingCamera=true,
        DirectionalRed=11,DirectionalGreen=22,DirectionalBlue=33,DirectionalBrightness=44,
        DirectionalVerticalAngle=-35,DirectionalHorizontalAngle=123,
        AmbientRed=55,AmbientGreen=66,AmbientBlue=77,AmbientBrightness=88,
        Background=91,Frame=92,Decoration=93,Paused=true,CharacterVisible=true
    };
    static PortraitOwnedCharaViewSnapshot Snapshot()
    {
        CharaViewCharacterData character=default;
        CharaViewModelData model=default;
        for(int i=0;i<26;i++) character.CustomizeData.Data[i]=model.CustomizeData.Data[i]=(byte)(i+1);
        character.ClassJobId=22; character.HeadgearHidden=true; character.WeaponHidden=true;
        character.VisorClosed=true; character.HideVieraEars=true;
        model.HeadgearHidden=true;model.WeaponHidden=true;model.VisorClosed=true;model.VieraEarsHidden=true;
        character.GlassesIds[0]=model.GlassesIds[0]=101;character.GlassesIds[1]=model.GlassesIds[1]=102;
        var items=new CharaViewItem[14];
        for (int i=0;i<14;i++)
        {
            character.ItemIds[i]=(uint)(1000+i);character.ItemStain0Ids[i]=(byte)(i+1);character.ItemStain1Ids[i]=(byte)(i+21);
            items[i]=new(){SlotId=(byte)i,ItemId=(uint)(1000+i),Stain0Id=(byte)(i+1),Stain1Id=(byte)(i+21),
                GlamourItemId=(uint)(2000+i),GlamourStain0Id=(byte)(i+41),GlamourStain1Id=(byte)(i+61),
                ApplyCompanyCrest=i%2==0,ModelMain=(ulong)(3000+i),ModelSub=(ulong)(4000+i)};
        }
        // Native ModelData includes the complete eight-byte crest value.
        *(ulong*)&model.FreeCompanyCrestData=0x0102030405060708;
        return new(Source,character,model,items,0x15,Composition);
    }
    static void SnapshotPreservesCompleteAppearance()
    {
        var snapshot=Snapshot(); var data=snapshot.CreateSetupData();
        Check(sizeof(CharaViewCharacterData)==124 && sizeof(CharaViewPortrait)==1072,"verified native layouts");
        for(int i=0;i<14;i++)
        {
            Check(data.ItemIds[i]==2000+i,"effective glamour reaches Setup slot "+i);
            Check(data.ItemStain0Ids[i]==i+41 && data.ItemStain1Ids[i]==i+61,"both glamour dye channels slot "+i);
        }
        Check(data.GlassesIds[0]==101 && data.GlassesIds[1]==102 && data.ClassJobId==22,"two facewear slots and job survive");
        Check(data.HeadgearHidden&&data.WeaponHidden&&data.VisorClosed&&data.HideVieraEars,"all visibility flags survive");
        var model=snapshot.ModelData; Check(*(ulong*)&model.FreeCompanyCrestData==0x0102030405060708 && snapshot.CrestBits==0x15,"crest payload and separate visibility bits survive");
        var copy=snapshot.CopyItems();copy[0].GlamourItemId=9999;
        Check(snapshot.CreateSetupData().ItemIds[0]==2000,"caller item mutation cannot change retained snapshot");
        var raw=snapshot.CharacterData;raw.ItemIds[0]=9999;
        Check(snapshot.CharacterData.ItemIds[0]==1000,"raw character struct returned by value");
    }
    static void NoPermitDoesNotTouchNative()
    {
        using var api=new Fixture(); var scene=new PortraitOwnedCharaView(api);
        Check(api.Events.Count==0,"default construction has no native side effects");
        Check(!scene.TryCreate(Snapshot(),null)&&api.Events.Count==0,"missing permit cannot allocate");
        foreach(var lease in new[]{new Lease{IsCurrent=false},new Lease{Slot=8},new Lease{Source=new(1,2,3,4,5)},new Lease{CanCreate=false},new Lease{Epoch=0}})
            Check(!scene.TryCreate(Snapshot(),lease)&&api.Events.Count==0,"invalid native permit cannot allocate");
    }
    static void NativeLifecycleAndComposition()
    {
        using var api=new Fixture();var lease=new Lease();var scene=new PortraitOwnedCharaView(api);
        Check(scene.TryCreate(Snapshot(),lease),"native scene can be created only with reserved permit");
        Check(string.Join(',',api.Events.Take(3))=="allocate,ctor,setup:3","UI allocation, construction, then exact reserved slot setup");
        Check(api.SetupData.ItemIds[13]==2013&&api.SetupData.GlassesIds[1]==102,"complete typed value payload reaches native Setup");
        Check(api.View->Items[13].GlamourItemId==2013&&api.View->FreeCompanyCrestBitfield==0x15,"full item and crest records replay before Render");
        Check(api.View->ModelData.CustomizeData.Data[25]==26&&api.View->PortraitCharacterData.ItemStain1Ids[13]==74,"complete customization and both dyes reach owned scene");
        api.Ready=true;
        Check(scene.Advance(lease,10)&&scene.IsReady,"native readiness plus applied composition publishes ready");
        Check(Math.Abs(api.PoseTime-.251f)<.00001f&&api.Timeline==42,"positive exported timeline gets native seek bias correction");
        Check(api.View->CameraPosition.X==1&&api.View->CameraTarget.Z==.3f&&api.View->ImageRotation==12,"native camera matches source POV");
        Check(api.MainColor==(11,22,33)&&api.AmbientColor==(55,66,77)&&api.MainAngle==(-35,123),"both native lights match source values");
        Check((api.Actor->LookAt.BannerCameraFollowFlag&LookAtContainer.BannerCameraFollowFlags.Head)!=0,"follow restored after head setter clears it");
        Check((api.Actor->LookAt.BannerCameraFollowFlag&LookAtContainer.BannerCameraFollowFlags.Eyes)==0,"eye follow matches source");
        Check(api.Events.Count(x=>x.StartsWith("render:"))==1&&!api.Events.Contains("update"),"Render advances native state exactly once and owns Update internally");
        Check(scene.TryRetire(lease),"owned native scene retires");
        Check(api.Events[^2]=="release"&&api.Events[^1]=="dtor:1"&&lease.Released,"Release precedes freeing Dtor, then ownership is marked released");
        int total=api.Events.Count;Check(scene.TryRetire(lease)&&api.Events.Count==total,"retirement is idempotent");
    }
    static void FailedAllocationDoesNotConstruct()
    {
        using var api=new Fixture{FailAllocation=true};var scene=new PortraitOwnedCharaView(api);
        Check(!scene.TryCreate(Snapshot(),new Lease())&&api.Events.SequenceEqual(new[]{"allocate"}),"allocation failure never enters ctor/setup");
    }
    static void NativeFailureRetiresInOrder()
    {
        foreach(string fail in new[]{"setup","render","expression"})
        {
            using var api=new Fixture{FailAt=fail};var lease=new Lease();var scene=new PortraitOwnedCharaView(api);
            bool opened=scene.TryCreate(Snapshot(),lease);
            if(opened){api.Ready=true;Check(!scene.Advance(lease,10),"native failure returns false: "+fail);}
            Check(api.Events.Contains("release")&&api.Events[^1]=="dtor:1","failed native operation retires exactly owned resource: "+fail);
            Check(scene.IsRetired,"failed native operation cannot remain active: "+fail);
        }
    }
    static void OwnershipLossNeverDeletesNewOwner()
    {
        using var api=new Fixture();var lease=new Lease();var scene=new PortraitOwnedCharaView(api);
        Check(scene.TryCreate(Snapshot(),lease),"ownership fixture starts");lease.Owns=false;
        int calls=api.Events.Count;
        Check(!scene.Advance(lease,10)&&!scene.TryRetire(lease),"foreign replacement is not a valid retirement permit");
        Check(api.Events.Count==calls&&scene.IsQuarantined,"foreign slot takeover never calls late Render/Release/Dtor");
    }
    static void LateSetterStopsRemainingWrites()
    {
        using var api=new Fixture();var lease=new Lease();var scene=new PortraitOwnedCharaView(api);
        Check(scene.TryCreate(Snapshot(),lease),"late setter fixture starts");api.Ready=true;
        api.AfterEvent=x=>{if(x=="expression")lease.Owns=false;};
        Check(!scene.Advance(lease,10)&&scene.IsQuarantined,"native reentrant slot loss stops scene");
        Check(!api.Events.Contains("head")&&!api.Events.Contains("release")&&!api.Events.Contains("dtor:1"),"no setter or stale cleanup after reentrant ownership loss");
    }
    static void DuplicateFrameDoesNotRenderTwice()
    {
        using var api=new Fixture();var lease=new Lease();var scene=new PortraitOwnedCharaView(api);scene.TryCreate(Snapshot(),lease);
        Check(scene.Advance(lease,10),"loading render accepted");int n=api.Events.Count;
        Check(scene.Advance(lease,10)&&api.Events.Count==n,"same native frame never renders twice");
        Check(!scene.Advance(lease,11)&&api.Events.Count==n,"ImGui or arbitrary frame counter cannot advance native scene");
        lease.NativeFrame=11;Check(scene.Advance(lease,11)&&api.Events.Count(x=>x.StartsWith("render:"))==2,"new native phase advances one frame");
        scene.TryRetire(lease);
    }
    static void CharacterReplacementStopsComposition()
    {
        using var api=new Fixture();var lease=new Lease();var scene=new PortraitOwnedCharaView(api);scene.TryCreate(Snapshot(),lease);api.Ready=true;
        scene.Advance(lease,10);api.ChangeGeneration();lease.NativeFrame=11;
        Check(!scene.Advance(lease,11),"owned character generation replacement invalidates ready scene");
        Check(scene.IsRetired,"still-owned view can retire after character replacement");
    }
    static void PendingPauseCannotPublishReady()
    {
        using var api=new Fixture{DeferPause=true};var lease=new Lease();var scene=new PortraitOwnedCharaView(api);scene.TryCreate(Snapshot(),lease);api.Ready=true;
        Check(scene.Advance(lease,10)&&!scene.IsReady,"asynchronous pause is not a ready same-POV scene");
        api.DeferPause=false;lease.NativeFrame=11;
        Check(scene.Advance(lease,11)&&scene.IsReady,"next native Render can settle queued pose and pause");
        Check(api.Events.Count(x=>x=="pose")==1,"pending pause never restarts pose each frame");scene.TryRetire(lease);
    }
    static void ReentrantRetireWaitsForNativeReturn()
    {
        using var api=new Fixture();var lease=new Lease();var scene=new PortraitOwnedCharaView(api);scene.TryCreate(Snapshot(),lease);api.Ready=true;
        bool refused=false,cleanupWhileNative=false;
        api.AfterEvent=x=>{if(x=="render:10"){refused=!scene.TryRetire(lease);cleanupWhileNative=api.Events.Contains("release");}};
        Check(!scene.Advance(lease,10),"retire requested inside Render stops advancing");
        Check(refused&&!cleanupWhileNative,"native reentrancy cannot free a view while its Render is on stack");
        Check(scene.IsRetired&&!api.Events.Contains("pose"),"retirement runs after native return before composition");
    }
    static void CharacterReplacementInsideSetterStopsImmediately()
    {
        using var api=new Fixture();var lease=new Lease();var scene=new PortraitOwnedCharaView(api);scene.TryCreate(Snapshot(),lease);api.Ready=true;
        api.AfterEvent=x=>{if(x=="expression")api.ChangeGeneration();};
        Check(!scene.Advance(lease,10),"character generation replacement inside setter fails the native handoff");
        Check(!api.Events.Contains("head")&&scene.IsRetired,"no later gaze or light writes target a replacement actor");
    }
    static void ReleaseFailureDoesNotCallDestructor()
    {
        using var api=new Fixture{FailAt="release"};var lease=new Lease();var scene=new PortraitOwnedCharaView(api);scene.TryCreate(Snapshot(),lease);
        Check(!scene.TryRetire(lease)&&scene.IsQuarantined,"partial native Release is quarantined");
        Check(!api.Events.Contains("dtor:1")&&!lease.Released,"partial Release cannot free its view or return its slot");
        int n=api.Events.Count;Check(!scene.TryRetire(lease)&&api.Events.Count==n,"uncertain Release never runs twice");
    }
    static void SourceCaptureOwnsAllValues()
    {
#if OWNED_SOURCE
        using var fixture=new SourceFixture();using var target=new NativePortraitTarget(fixture,_=>false,_=>{});
        Check(target.TryResolve(out var identity,out _,out _),"source editor resolves before complete handoff");
        for(int i=0;i<14;i++)
        {
            fixture.View->PortraitCharacterData.ItemIds[i]=(uint)(5000+i);
            fixture.View->PortraitCharacterData.ItemStain0Ids[i]=(byte)(i+1);
            fixture.View->PortraitCharacterData.ItemStain1Ids[i]=(byte)(i+21);
            fixture.View->Items[i]=new(){SlotId=(byte)i,ItemId=(uint)(5000+i),GlamourItemId=(uint)(6000+i),
                GlamourStain0Id=(byte)(i+41),GlamourStain1Id=(byte)(i+61),ApplyCompanyCrest=i%2==0,ModelMain=(ulong)(7000+i),ModelSub=(ulong)(8000+i)};
        }
        fixture.View->ModelData.GlassesIds[0]=105;fixture.View->ModelData.GlassesIds[1]=106;
        fixture.View->PortraitCharacterData.ClassJobId=22;
        fixture.View->FreeCompanyCrestBitfield=5;
        *(ulong*)&fixture.View->ModelData.FreeCompanyCrestData=0x1122334455667788;
        var snapshot=target.CaptureOwnedSceneSource(identity);
        Check(snapshot is not null&&snapshot.Source==identity,"source handoff keeps the exact editor allocation identity");
        Check(snapshot!.CreateSetupData().ItemIds[13]==6013&&snapshot.CreateSetupData().GlassesIds[1]==106,"complete source carries all fourteen items and second facewear");
        var copiedModel=snapshot.ModelData;
        Check(*(ulong*)&copiedModel.FreeCompanyCrestData==0x1122334455667788&&snapshot.CrestBits==5,"source handoff retains both forms of crest data");
        fixture.View->Items[13].GlamourItemId=9999;fixture.View->PortraitCharacterData.ItemIds[13]=9999;
        fixture.View->ModelData.GlassesIds[1]=999;fixture.View->CameraPosition.X=999;
        Check(snapshot.CreateSetupData().ItemIds[13]==6013&&snapshot.ModelData.GlassesIds[1]==106&&snapshot.Composition.CameraPosition.X==0,"source edits cannot mutate a captured handoff");
        fixture.AfterExport=()=>fixture.Editor->EditorState=null;
        Check(target.CaptureOwnedSceneSource(identity) is null,"native export replacement discards source instead of reading an old editor allocation");
#else
        Check(false,"complete source capture does not yet exist; history Look omits native slots and crest data");
#endif
    }
    static void ReadyViewIsBorrowedOnlyInsideCurrentPhase()
    {
#if OWNED_FRAME_CONSUMER
        using var api=new Fixture();var lease=new Lease();var scene=new PortraitOwnedCharaView(api);scene.TryCreate(Snapshot(),lease);
        int reads=0;
        Check(!scene.UseReadyView(lease,_=>reads++)&&reads==0,"loading view cannot escape into texture or image capture");
        api.Ready=true;scene.Advance(lease,10);
        Check(scene.UseReadyView(lease,p=>{Check(p==(nint)api.View,"read consumer receives the owned view");reads++;})&&reads==1,"ready view is synchronously available inside its phase");
        lease.IsCurrent=false;
        Check(!scene.UseReadyView(lease,_=>reads++)&&reads==1,"expired native phase cannot hand out a saved view pointer");
#else
        Check(false,"ready scene has no phase-bounded consumer for texture capture");
#endif
    }
}

sealed class Lease : IPortraitOwnedSceneLease
{
    public uint Slot {get;set;}=3;public PortraitIdentity Source {get;set;}=new(101,102,103,9,104);
    public long Epoch {get;set;}=1;public uint NativeFrame {get;set;}=10;
    public bool IsCurrent {get;set;}=true;public bool CanCreate {get;set;}=true;public bool Owns=true,Released,RetirementBegun;
    nint pointer;
    public bool TryBindCreatedView(nint view){if(!CanCreate||!IsCurrent)return false;pointer=view;CanCreate=false;return true;}
    public bool CanAccess(nint view)=>IsCurrent&&Owns&&!Released&&pointer==view&&view!=0;
    public bool TryBeginRetirement(nint view){if(!CanAccess(view))return false;RetirementBegun=true;return true;}
    public bool CanReleaseView(nint view)=>CanAccess(view);
    public bool MarkReleased(nint view){if(!RetirementBegun||!CanReleaseView(view))return false;Released=true;return true;}
    public IDisposable? EnterOwnedCall(nint view)=>(view==0?IsCurrent&&CanCreate:CanAccess(view))?new Scope():null;
    sealed class Scope : IDisposable{public void Dispose(){}}
}

unsafe sealed class Fixture : IPortraitOwnedCharaViewNativeApi,IDisposable
{
    public readonly CharaViewPortrait* View=(CharaViewPortrait*)NativeMemory.AllocZeroed((nuint)sizeof(CharaViewPortrait));
    public readonly Character* Actor=(Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
    public readonly List<string> Events=[];
    public CharaViewCharacterData SetupData;public bool FailAllocation,Ready,DeferPause,Paused;
    public string? FailAt;public Action<string>? AfterEvent;public float PoseTime;public ushort Timeline;
    public (byte,byte,byte) MainColor,AmbientColor;public (short,short) MainAngle;
    public Fixture(){Actor->DrawData.OwnerObject=Actor;*(uint*)((byte*)Actor+0x74)=17;}
    void Event(string name){Events.Add(name);AfterEvent?.Invoke(name);if(FailAt==name)throw new InvalidOperationException(name+" failed");}
    public nint Allocate(){Event("allocate");return FailAllocation?0:(nint)View;}
    public void FreeUnconstructed(nint view){if(view!=(nint)View)throw new Exception("foreign allocation");Event("free-raw");}
    public nint Construct(nint view){if(view!=(nint)View)throw new Exception("foreign ctor");Event("ctor");return view;}
    public void Setup(nint view,uint slot,CharaViewCharacterData data){if(view!=(nint)View)throw new Exception("foreign setup");SetupData=data;View->PortraitCharacterData=data;View->ClientObjectIndex=slot;View->ClientObjectId=slot+0xF0;View->State=1;Event("setup:"+slot);if(FailAt=="setup")throw new InvalidOperationException("setup failed");}
    public void Render(nint view,uint frame){Event("render:"+frame);if(FailAt=="render")throw new InvalidOperationException("render failed");if(!Ready)return;View->State=6;View->CharaViewPortraitCharacterLoaded=true;View->Camera=(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera*)501;View->CameraManager=502;if(!DeferPause&&View->IsAnimationPauseStatePending){View->IsAnimationPauseStatePending=false;Paused=View->PendingAnimationPauseState;}}
    public nint GetCharacter(nint view)=>view==(nint)View?(nint)Actor:0;
    public nint WorldCharacter=>900;
    public void SetPoseTimed(nint view,ushort timeline,float time){Event("pose");Timeline=timeline;PoseTime=time;}
    public void SetExpression(nint view,byte expression)=>Event("expression");
    public void SetHeadDirection(nint view,float x,float y){Event("head");Actor->LookAt.BannerCameraFollowFlag&=~LookAtContainer.BannerCameraFollowFlags.Head;}
    public void SetEyeDirection(nint view,float x,float y){Event("eyes");Actor->LookAt.BannerCameraFollowFlag&=~LookAtContainer.BannerCameraFollowFlags.Eyes;}
    public void SetDirectionalColor(nint view,byte r,byte g,byte b){Event("main-color");MainColor=(r,g,b);}
    public void SetDirectionalBrightness(nint view,byte value)=>Event("main-brightness");
    public void SetDirectionalAngle(nint view,short vertical,short horizontal){Event("main-angle");MainAngle=(vertical,horizontal);}
    public void SetAmbientColor(nint view,byte r,byte g,byte b){Event("ambient-color");AmbientColor=(r,g,b);}
    public void SetAmbientBrightness(nint view,byte value)=>Event("ambient-brightness");
    public void SetBackground(nint view,ushort value)=>Event("background");
    public void SetCameraZoom(nint view,byte value){Event("zoom");View->CameraZoom=value;}
    public void ApplyCamera(nint view)=>Event("camera");
    public void SetPaused(nint view,bool value){Event("pause");if(DeferPause){View->IsAnimationPauseStatePending=true;View->PendingAnimationPauseState=value;}else Paused=value;}
    public bool IsPaused(nint view)=>Paused;
    public void SetCharacterVisible(nint view,bool value){Event("visible");View->CharacterVisible=value;}
    public void Release(nint view){if(view!=(nint)View)throw new Exception("foreign release");Event("release");View->State=0;}
    public void Destroy(nint view,byte freeFlags){if(view!=(nint)View)throw new Exception("foreign dtor");Event("dtor:"+freeFlags);}
    public void ChangeGeneration()=>*(uint*)((byte*)Actor+0x74)=18;
    public void Dispose(){NativeMemory.Free(View);NativeMemory.Free(Actor);}
}
#endif
