using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Character=FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

unsafe class Program
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int nativePlayCalls;
    static Action? afterNativePlay;
    static int Main(string[] args)
    {
        var method=typeof(ActorPreviewRuntime).GetMethod("TryPlaySpecialAction",All);
        if(method is null) { Console.WriteLine("FAIL: runtime has no clone-only special action executor"); return 1; }
        using var game=new Lumina.GameData(args[0],new Lumina.LuminaOptions{DefaultExcelLanguage=Lumina.Data.Language.ChineseSimplified});
        var fixtureType=Assembly.Load("NativeDismountTests").GetType("Program+F")!;
        var checks=0;
        void Check(bool valid,string name) { checks++;if(!valid)throw new Exception("FAIL: "+name); }
        NativeCollectionExitCases.Run(Check);
        object Create(bool accessory=false)
        {
            var f=Activator.CreateInstance(fixtureType,All,null,new object[]{(ushort)0,(ushort)0,(ushort)0},null)!;
            var runtime=(ActorPreviewRuntime)fixtureType.GetField("Runtime",All)!.GetValue(f)!;
            typeof(ActorPreviewRuntime).GetField("data",All)!.SetValue(runtime,new GameDataFixture(game));
            if(typeof(ActorPreviewRuntime).GetField("flightPoseResources",All) is { } flightCache)
                flightCache.SetValue(runtime,Activator.CreateInstance(flightCache.FieldType));
            fixtureType.GetMethod("PrepareVisibility",All)!.Invoke(f,null);
            var clone=(Character*)Pointer.Unbox(fixtureType.GetField("Clone",All)!.GetValue(f)!);
            var source=(Character*)Pointer.Unbox(fixtureType.GetField("Source",All)!.GetValue(f)!);
            var effects=(Dictionary<PreviewSlot,PreviewEffect>)fixtureType.GetField("Effects",All)!.GetValue(f)!;
            effects.Clear();
            if(accessory){effects[PreviewSlot.Accessory]=new(PreviewSlot.Accessory,56);clone->OrnamentData.OwnerObject=clone;clone->OrnamentData.OrnamentId=56;clone->Mount.MountId=0;clone->Mode=CharacterModes.Normal;}
            else {effects[PreviewSlot.Mount]=new(PreviewSlot.Mount,223);clone->Mount.MountId=223;clone->Mount.MountObject=(Character*)Pointer.Unbox(fixtureType.GetMethod("VisibilityForeignCharacter",All)!.Invoke(f,null)!);}
            PrepareNativeDraw(source,clone);
            if (!accessory)
            {
                var child=clone->Mount.MountObject;
                child->Timeline.OwnerObject=child;child->Timeline.TimelineSequencer.Parent=child;
                child->DrawObject=(DrawObject*)Pointer.Unbox(fixtureType.GetMethod("VisibilityDraw",All)!.Invoke(f,null)!);
            }
            // Establish the real hierarchy through production Tick rather than an IsReady flag.
            var display=(NativePreviewActor)fixtureType.GetProperty("Display",All)!.GetValue(f)!;
            display.Tick(source);
            if(!display.Tick(source)) throw new Exception("owned special-action fixture never reached draw readiness");
            nativePlayCalls=0;afterNativePlay=null;
            return f;
        }
        ActorPreviewRuntime R(object f)=>(ActorPreviewRuntime)fixtureType.GetField("Runtime",All)!.GetValue(f)!;
        Character* C(object f)=>(Character*)Pointer.Unbox(fixtureType.GetField("Clone",All)!.GetValue(f)!);
        Character* S(object f)=>(Character*)Pointer.Unbox(fixtureType.GetField("Source",All)!.GetValue(f)!);
        bool Play(object f,uint id)=>(bool)method.Invoke(R(f),new object[]{id})!;
        bool Active(object f)=>(bool)typeof(ActorPreviewRuntime).GetProperty("SpecialActionActive",All)!.GetValue(R(f))!;
        var activePreviewEmote=typeof(ActorPreviewRuntime).GetMethod("GetActivePreviewEmote",All);
        Check(activePreviewEmote!=null,"runtime exposes an observed active preview emote instead of only a remembered slot");
        (ushort EmoteId,byte Pose)? ReadPreview(object f)
            => ((ushort EmoteId,byte Pose)?)activePreviewEmote!.Invoke(R(f),null);
        void StartEmote(object f,ushort id,byte pose=0)
        {
            var effects=(Dictionary<PreviewSlot,PreviewEffect>)fixtureType.GetField("Effects",All)!.GetValue(f)!;
            effects.Clear();var effect=new PreviewEffect(PreviewSlot.Emote,id,pose);
            effects[PreviewSlot.Emote]=effect;
            typeof(ActorPreviewRuntime).GetMethod("Apply",All)!.Invoke(R(f),[Pointer.Box(C(f),typeof(Character*)),effect]);
        }
        using(var f=(IDisposable)Create(true))
        {
            StartEmote(f,21,3);C(f)->EmoteController.CPoseState=5;
            var source=new ReadOnlySpan<byte>(S(f),sizeof(Character)).ToArray();
            var display=new ReadOnlySpan<byte>(C(f),sizeof(Character)).ToArray();
            int calls=nativePlayCalls;
            Check(ReadPreview(f)==((ushort)21,(byte)5),"active preview returns the actually playing emote and its current pose");
            Check(nativePlayCalls==calls&&new ReadOnlySpan<byte>(S(f),sizeof(Character)).SequenceEqual(source)
                &&new ReadOnlySpan<byte>(C(f),sizeof(Character)).SequenceEqual(display),
                "observing active preview never plays an animation or writes either actor");
        }
        foreach(ushort sitting in new ushort[]{50,52})
        {
            using var f=(IDisposable)Create(true);StartEmote(f,sitting,2);
            Check(ReadPreview(f)==(sitting,(byte)2),"active trial sitting "+sitting+" remains available for GPose entry");
            C(f)->EmoteController.EmoteId=0;
            Check(ReadPreview(f)==(sitting,(byte)2),"native-cleared sitting EmoteId still resolves "+sitting+" through its matching posture mode and real timeline");
            for(int slot=0;slot<14;slot++)C(f)->Timeline.TimelineSequencer.TimelineIds[slot]=0;
            C(f)->Timeline.TimelineSequencer.TimelineIds[0]=3;C(f)->Mode=CharacterModes.Normal;
            Check(ReadPreview(f)==null,"standing after trial sitting "+sitting+" rejects a residual slot and stale EmoteId");
        }
        bool lifetimePassed=true;
        void CheckLifetime(bool valid,string name)
        {
            checks++;Console.WriteLine((valid?"PASS ":"FAIL ")+name);
            if(!valid)lifetimePassed=false;
        }
        foreach(ushort sitting in new ushort[]{50,52})
        foreach(var state in new[]{"cleared id","normal mode","other sit mode parameter","another nonzero emote","ended timeline"})
        {
            using var f=(IDisposable)Create(true);StartEmote(f,sitting,2);
            C(f)->EmoteController.EmoteId=0;
            if(state=="normal mode")C(f)->Mode=CharacterModes.Normal;
            else if(state=="other sit mode parameter")C(f)->ModeParam=(byte)(sitting==50?1:2);
            else if(state=="another nonzero emote")C(f)->EmoteController.EmoteId=21;
            else if(state=="ended timeline")
            {
                for(int lane=0;lane<14;lane++)C(f)->Timeline.TimelineSequencer.TimelineIds[lane]=0;
                C(f)->Timeline.TimelineSequencer.TimelineIds[0]=3;
            }
            var spawn=(Dictionary<PreviewSlot,int>)fixtureType.GetField("Spawn",All)!.GetValue(f)!;
            spawn[PreviewSlot.Emote]=60;
            var update=typeof(ActorPreviewRuntime).GetMethod("Update",All)!;
            var framework=(IFramework)typeof(ActorPreviewRuntime).GetField("framework",All)!.GetValue(R(f))!;
            // Exercise the actual lifetime update across its full startup grace.
            // The sit loop may remain valid after its native controller clears id.
            for(int frame=0;frame<61;frame++)update.Invoke(R(f),[framework]);
            bool effectPresent=R(f).ActiveEffects.Any(effect=>effect.Slot==PreviewSlot.Emote && effect.Id==sitting);
            CheckLifetime(state=="cleared id" ? effectPresent && ReadPreview(f)==(sitting,(byte)2)
                : !effectPresent && ReadPreview(f)==null,
                $"trial sitting {sitting} with {state} {(state=="cleared id"?"retains its live posture and GPose getter":"expires instead of borrowing stale posture data")} after 61 updates");
        }
        if(!lifetimePassed)throw new Exception("FAIL: trial sitting lifetime after startup grace");
        foreach(ushort sitting in new ushort[]{50,52})
        foreach(var mismatch in new[]{"normal mode","other sit mode parameter","another nonzero emote"})
        {
            using var f=(IDisposable)Create(true);StartEmote(f,sitting,2);
            C(f)->EmoteController.EmoteId=0;
            if(mismatch=="normal mode")C(f)->Mode=CharacterModes.Normal;
            else if(mismatch=="other sit mode parameter")C(f)->ModeParam=(byte)(sitting==50?1:2);
            else C(f)->EmoteController.EmoteId=21;
            Check(ReadPreview(f)==null,"a cleared sitting id with "+mismatch+" cannot borrow residual "+sitting+" timelines");
        }
        foreach(ushort sitting in new ushort[]{50,52})
        {
            using var f=(IDisposable)Create(true);StartEmote(f,sitting,2);
            C(f)->Mode=CharacterModes.Normal;
            Check(ReadPreview(f)==null,"standing mode rejects stale sitting "+sitting+" even if both its controller id and loop remain temporarily present");
        }
        using(var f=(IDisposable)Create(true))
        {
            StartEmote(f,21);C(f)->EmoteController.EmoteId=0;
            Check(ReadPreview(f)==null,"ordinary emotes do not use the sitting-only cleared-id allowance");
        }
        using(var f=(IDisposable)Create(true))
        {
            StartEmote(f,21);
            for(int slot=0;slot<14;slot++)C(f)->Timeline.TimelineSequencer.TimelineIds[slot]=0;
            C(f)->Timeline.TimelineSequencer.TimelineIds[0]=166;
            C(f)->Timeline.TimelineSequencer.TimelineIds[4]=686;
            Check(ReadPreview(f)==((ushort)21,(byte)0),"a mounted overlay is active even while the rider base lane remains idle");
        }
        foreach(var state in new[]{"deferred","removed slot","wrong emote","hidden draw","unready display","territory","source generation","display generation","timeline owner","sequencer parent","worker"})
        {
            using var f=(IDisposable)Create(true);StartEmote(f,21);
            if(state=="deferred")typeof(ActorPreviewRuntime).GetField("deferredEmote",All)!.SetValue(R(f),true);
            else if(state=="removed slot")((Dictionary<PreviewSlot,PreviewEffect>)fixtureType.GetField("Effects",All)!.GetValue(f)!).Clear();
            else if(state=="wrong emote")C(f)->EmoteController.EmoteId=22;
            else if(state=="hidden draw")C(f)->DrawObject->IsVisible=false;
            else if(state=="unready display")typeof(NativePreviewActor).GetProperty("IsReady",All)!.SetValue(fixtureType.GetProperty("Display",All)!.GetValue(f),false);
            else if(state=="territory")fixtureType.GetField("Territory",All)!.SetValue(f,2u);
            else if(state=="source generation")*(uint*)((byte*)S(f)+0x74)=77;
            else if(state=="display generation")*(uint*)((byte*)C(f)+0x74)=77;
            else if(state=="timeline owner")C(f)->Timeline.OwnerObject=S(f);
            else if(state=="sequencer parent")C(f)->Timeline.TimelineSequencer.Parent=S(f);
            else fixtureType.GetField("FrameworkThread",All)!.SetValue(f,false);
            int calls=nativePlayCalls;
            Check(ReadPreview(f)==null&&nativePlayCalls==calls,"active preview query rejects "+state+" without native animation playback");
        }
        foreach(var stale in new[]{"revision","stopped timeline","removed slot"})
        {
            using var f=(IDisposable)Create(true);StartEmote(f,21);
            fixtureType.GetField("LookupBefore",All)!.SetValue(f,(Action)(()=>
            {
                if(stale=="revision")typeof(ActorPreviewRuntime).GetField("revision",All)!.SetValue(R(f),9L);
                else if(stale=="stopped timeline")C(f)->Timeline.TimelineSequencer.TimelineIds[0]=3;
                else ((Dictionary<PreviewSlot,PreviewEffect>)fixtureType.GetField("Effects",All)!.GetValue(f)!).Clear();
            }));
            Check(ReadPreview(f)==null,"a "+stale+" changed inside native actor lookup cannot leak an old preview into GPose");
        }
        using(var f=(IDisposable)Create())
        {
            S(f)->Timeline.BaseOverride=333;C(f)->Mount.MountObject=S(f);
            var before=new ReadOnlySpan<byte>(S(f),sizeof(Character)).ToArray();
            Check(!Play(f,20122)&&nativePlayCalls==0&&new ReadOnlySpan<byte>(S(f),sizeof(Character)).SequenceEqual(before),
                "a mount child aliasing the genuine player is rejected without any real-player byte write");
        }
        foreach(var kind in new[]{"child","mount","minion","ornament"})
        {
            using var f=(IDisposable)Create();var child=C(f)->Mount.MountObject;
            if(kind=="child")S(f)->ChildObject=child;
            else if(kind=="mount")S(f)->Mount.MountObject=child;
            else if(kind=="minion")S(f)->CompanionData.CompanionObject=(Companion*)child;
            else S(f)->OrnamentData.OrnamentObject=(Ornament*)child;
            var before=new ReadOnlySpan<byte>(child,sizeof(Character)).ToArray();
            Check(!Play(f,20122)&&nativePlayCalls==0&&new ReadOnlySpan<byte>(child,sizeof(Character)).SequenceEqual(before),
                "a genuine player's "+kind+" attachment cannot become a local special-action target");
        }
        foreach(var defect in new[]{"timeline owner","sequencer parent","hidden draw","absent draw","generation"})
        {
            using var f=(IDisposable)Create();var child=C(f)->Mount.MountObject;
            if(defect=="timeline owner")child->Timeline.OwnerObject=S(f);
            else if(defect=="sequencer parent")child->Timeline.TimelineSequencer.Parent=S(f);
            else if(defect=="hidden draw")child->DrawObject->IsVisible=false;
            else if(defect=="absent draw")child->DrawObject=null;
            else *(uint*)((byte*)child+0x74)=77;
            Check(!Play(f,20122)&&nativePlayCalls==0,"a mount child's changed "+defect+" revokes playback before any native call");
        }
        foreach(var defect in new[]{"timeline owner","sequencer parent"})
        {
            using var f=(IDisposable)Create(true);
            if(defect=="timeline owner")C(f)->Timeline.OwnerObject=S(f);
            else C(f)->Timeline.TimelineSequencer.Parent=S(f);
            Check(!Play(f,50731)&&nativePlayCalls==0,"an accessory's foreign "+defect+" cannot receive playback");
        }
        using(var f=(IDisposable)Create())
        {
            fixtureType.GetField("LookupBefore",All)!.SetValue(f,(Action)(()=>typeof(ActorPreviewRuntime).GetField("revision",All)!.SetValue(R(f),9L)));
            Check(!Play(f,20122)&&nativePlayCalls==0,"a revision changed by native ownership lookup cannot receive the old action");
        }
        using(var f=(IDisposable)Create())
        {
            afterNativePlay=()=>C(f)->Mount.MountObject->Timeline.TimelineSequencer.Parent=S(f);
            Check(!Play(f,20122)&&nativePlayCalls==1&&!Active(f),"a transplanted child sequencer during native playback invalidates the action lease");
        }
        using(var f=(IDisposable)Create(true))
        {
            afterNativePlay=()=>C(f)->DrawObject->IsVisible=false;
            Check(!Play(f,50731)&&nativePlayCalls==1&&!Active(f),"losing accessory draw readiness during native playback leaves no active action lease");
        }
        foreach(var root in new[]{false,true})
        {
            using var f=(IDisposable)Create();
            var newDraw=(DrawObject*)Pointer.Unbox(fixtureType.GetMethod("VisibilityDraw",All)!.Invoke(f,null)!);
            afterNativePlay=()=>{if(root)C(f)->DrawObject=newDraw;else C(f)->Mount.MountObject->DrawObject=newDraw;};
            Check(!Play(f,20122)&&nativePlayCalls==1&&!Active(f),
                "replacement "+(root?"rider":"mount")+" draw during native playback invalidates the old action lease");
        }
        using(var f=(IDisposable)Create())
        {
            fixtureType.GetField("FrameworkThread",All)!.SetValue(f,false);
            Check(!Play(f,20122)&&nativePlayCalls==0,"special-action input outside Framework never calls native animation playback");
        }
        var toggleFlight=typeof(ActorPreviewRuntime).GetMethod("TryToggleFlightPose",All);
        var flightActive=typeof(ActorPreviewRuntime).GetProperty("FlightPoseActive",All);
        Check(toggleFlight!=null&&flightActive!=null,"runtime exposes pure visual flight appearance toggle");
        bool ToggleFlight(object f)=>(bool)toggleFlight!.Invoke(R(f),null)!;
        bool IsFlight(object f)=>(bool)flightActive!.GetValue(R(f))!;
        using(var f=(IDisposable)Create())
        {
            fixtureType.GetField("FrameworkThread",All)!.SetValue(f,false);
            typeof(ActorPreviewRuntime).GetField("objects",All)!.SetValue(R(f),DispatchProxy.Create<IObjectTable,ForbiddenBindingProxy>());
            Check(!ToggleFlight(f)&&nativePlayCalls==0,"off-Framework flight toggle is rejected before any player binding lookup");
        }
        foreach(var (id,idle,walk,backward,run) in new (ushort,ushort,ushort,ushort,ushort)[]{
            (1,4040,4053,4046,4043),(6,4040,4053,4046,4043),(187,4040,4053,4046,4043),
            (48,3,13,16,22),(201,4040,4053,4053,4043),(215,3,4053,4053,4043),(300,3,4053,4046,4043)})
        {
            using var f=(IDisposable)Create();
            var effects=(Dictionary<PreviewSlot,PreviewEffect>)fixtureType.GetField("Effects",All)!.GetValue(f)!;
            effects[PreviewSlot.Mount]=new(PreviewSlot.Mount,id);C(f)->Mount.MountId=id;
            Check(ToggleFlight(f)&&IsFlight(f),"resource-verified flight appearance supports mount "+id);
            var display=(NativePreviewActor)fixtureType.GetProperty("Display",All)!.GetValue(f)!;
            foreach(var (sourceClip,expected) in new (ushort,ushort)[]{(3,idle),(13,walk),(16,backward),(22,run)})
            {
                S(f)->Timeline.TimelineSequencer.TimelineIds[0]=sourceClip;
                var before=new ReadOnlySpan<byte>(S(f),sizeof(Character)).ToArray();
                display.UpdateGroundedMotion(false,null,true);
                Check(C(f)->Mount.MountObject->Timeline.TimelineSequencer.TimelineIds[0]==expected,
                    "mount "+id+" maps source "+sourceClip+" only to a verified resident loop");
                Check(new ReadOnlySpan<byte>(S(f),sizeof(Character)).SequenceEqual(before),
                    "mount "+id+" appearance preserves genuine source bytes for clip "+sourceClip);
            }
        }
        var resolveFlight=typeof(ActorPreviewRuntime).GetMethod("TryGetFlightPoseClips",All);
        Check(resolveFlight!=null,"resource resolver exposes cached per-mount flight loop profiles");
        using(var f=(IDisposable)Create())
        {
            foreach(var mount in game.GetExcelSheet<Lumina.Excel.Sheets.Mount>()!.Where(m=>m.RowId!=0&&!string.IsNullOrWhiteSpace(m.Singular.ToString())))
            {
                object?[] resolveArgs={(ushort)mount.RowId,null};
                Check((bool)resolveFlight!.Invoke(R(f),resolveArgs)!,"installed mount "+mount.RowId+" has a resource-verified flight profile");
            }
        }
        using(var f=(IDisposable)Create()) {
            var effects=(Dictionary<PreviewSlot,PreviewEffect>)fixtureType.GetField("Effects",All)!.GetValue(f)!;
            effects[PreviewSlot.Mount]=new(PreviewSlot.Mount,327);C(f)->Mount.MountId=327;
            byte[] before=new ReadOnlySpan<byte>(S(f),sizeof(Character)).ToArray();
            Check(ToggleFlight(f)&&IsFlight(f),"Ark verified resident flight posture is enabled");
            Check(new ReadOnlySpan<byte>(S(f),sizeof(Character)).SequenceEqual(before),"flight appearance preserves all genuine source bytes");
            Check(ToggleFlight(f)&&!IsFlight(f),"second toggle returns to grounded appearance");
            Check(ToggleFlight(f)&&IsFlight(f),"flight appearance can be reenabled");
            effects[PreviewSlot.Mount]=new(PreviewSlot.Mount,73);C(f)->Mount.MountId=73;
            Check(!IsFlight(f),"a different trial mount never inherits the old flight appearance");
        }
        using(var f=(IDisposable)Create(true)) {Check(!ToggleFlight(f)&&!IsFlight(f),"accessory preview has no mount flight appearance");}
        using(var f=(IDisposable)Create()) {fixtureType.GetField("Territory",All)!.SetValue(f,2u);Check(!ToggleFlight(f),"stale territory cannot toggle flight appearance");}
        using(var f=(IDisposable)Create()) {C(f)->DrawObject->IsVisible=false;Check(!ToggleFlight(f),"hidden display cannot enable flight appearance");}
        using(var f=(IDisposable)Create()) {
            Check(Play(f,20122),"tank cannon accepted for owned ready clone");
            Check(C(f)->Mount.MountObject->Timeline.TimelineSequencer.TimelineIds[0]==7863,"cannon plays on clone mount");
            Check(C(f)->Timeline.TimelineSequencer.TimelineIds[0]!=7863,"cannon never plays on rider");
            Check(S(f)->Timeline.TimelineSequencer.TimelineIds[0]==0&&S(f)->Mount.MountId==0,"source gameplay state unchanged");
            Check(Active(f),"special action temporarily yields motion timeline");
            Check(!Play(f,50731),"other accessory actions cannot escape current mount catalog");
            C(f)->Mount.MountId=224;Check(!Active(f)&&!Play(f,20122),"replacement clone mount revokes special action lease");
        }
        using(var f=(IDisposable)Create(true)) {
            Check(Play(f,50731),"pitchfork action accepted for current trial");
            Check(C(f)->Timeline.TimelineSequencer.TimelineIds[0]==13384,"pitchfork plays on display player");
            Check(S(f)->Timeline.TimelineSequencer.TimelineIds[0]==0&&S(f)->OrnamentData.OrnamentId==0,"accessory trial never animates source");
            Check(Active(f),"accessory action owns motion yield");
            typeof(NativePreviewActor).GetProperty("IsReady",All)!.SetValue(fixtureType.GetProperty("Display",All)!.GetValue(f),false);
            Check(!Play(f,50731)&&!Active(f),"unready display cannot accept or hold action");
        }
        using(var f=(IDisposable)Create()) {fixtureType.GetField("Territory",All)!.SetValue(f,2u);Check(!Play(f,20122),"stale territory rejected before native playback");}
        using(var f=(IDisposable)Create()) {*(uint*)((byte*)C(f)+0x74)=77;Check(!Play(f,20122),"reused display generation rejected before native playback");}
        using(var f=(IDisposable)Create()) {*(uint*)((byte*)S(f)+0x74)=99;Check(!Play(f,20122),"reused source generation rejected before native playback");}
        using(var f=(IDisposable)Create()) {Check(Play(f,20122),"special action starts for mount reuse guard");*(uint*)((byte*)C(f)->Mount.MountObject+0x74)=66;Check(!Active(f),"reused mount child generation releases old animation lease");}
        using(var f=(IDisposable)Create(true)) {C(f)->OrnamentData.OwnerObject=S(f);Check(!Play(f,50731),"foreign accessory container cannot animate source");}
        using(var f=(IDisposable)Create()) {C(f)->DrawObject->IsVisible=false;Check(!Play(f,20122),"hidden or lost display cannot play a special action");}
        using(var f=(IDisposable)Create()) {Check(!Play(f,0)&&!Play(f,uint.MaxValue),"zero and out-of-catalog action ids are rejected");}
        Console.WriteLine($"TrialSpecialRuntimeTests: {checks}/{checks} passed.");return 0;
    }
    static void PrepareNativeDraw(Character* source,Character* clone)
    {
        source->Timeline.OwnerObject=source;source->Timeline.TimelineSequencer.Parent=source;
        clone->Timeline.OwnerObject=clone;clone->Timeline.TimelineSequencer.Parent=clone;
        source->EmoteController.OwnerObject=(BattleChara*)source;clone->EmoteController.OwnerObject=(BattleChara*)clone;
        var table=*(nint**)clone;
        table[12]=(nint)(delegate* unmanaged<GameObject*,void>)&EnableDraw;
        table[23]=(nint)(delegate* unmanaged<GameObject*,DrawObject*>)&GetDraw;
        GameObject.Addresses.IsReadyToDraw.Value=(nint)(delegate* unmanaged<GameObject*,byte>)&DrawReady;
        GameObject.Addresses.SetPosition.Value=(nint)(delegate* unmanaged<GameObject*,float,float,float,void>)&Position;
        GameObject.Addresses.SetRotation.Value=(nint)(delegate* unmanaged<GameObject*,float,void>)&Rotation;
        Character.Addresses.GetTargetId.Value=(nint)(delegate* unmanaged<Character*,ulong>)&Target;
        Character.Addresses.GetSoftTargetId.Value=(nint)(delegate* unmanaged<Character*,ulong>)&SoftTarget;
        ActionTimelineSequencer.Addresses.PlayTimeline.Value=(nint)(delegate* unmanaged<ActionTimelineSequencer*,ushort,void*,void>)&NativePlay;
    }
    [UnmanagedCallersOnly] static void EnableDraw(GameObject* actor){if(actor->DrawObject!=null)actor->DrawObject->IsVisible=true;}
    [UnmanagedCallersOnly] static DrawObject* GetDraw(GameObject* actor)=>actor->DrawObject;
    [UnmanagedCallersOnly] static byte DrawReady(GameObject* actor)=>1;
    [UnmanagedCallersOnly] static void Position(GameObject* actor,float x,float y,float z)=>actor->Position=new(x,y,z);
    [UnmanagedCallersOnly] static void Rotation(GameObject* actor,float rotation)=>actor->Rotation=rotation;
    [UnmanagedCallersOnly] static ulong Target(Character* actor)=>actor->TargetId.Id;
    [UnmanagedCallersOnly] static ulong SoftTarget(Character* actor)=>actor->SoftTargetId.Id;
    [UnmanagedCallersOnly] static void NativePlay(ActionTimelineSequencer* sequencer,ushort id,void* unknown)
    {nativePlayCalls++;sequencer->TimelineIds[0]=id;afterNativePlay?.Invoke();}
}
public class ForbiddenBindingProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method,object?[]? args)
        =>throw new InvalidOperationException("flight toggle performed an off-Framework player binding lookup");
}
sealed class GameDataFixture(Lumina.GameData game) : IDataManager
{
    public Dalamud.Game.ClientLanguage Language=>Dalamud.Game.ClientLanguage.ChineseSimplified;
    public Lumina.GameData GameData=>game;
    public Lumina.Excel.ExcelModule Excel=>game.Excel;
    public bool HasModifiedGameDataFiles=>false;
    public Lumina.Excel.ExcelSheet<T> GetExcelSheet<T>(Dalamud.Game.ClientLanguage? language=null,string? name=null) where T:struct,Lumina.Excel.IExcelRow<T> => game.GetExcelSheet<T>(name:name)!;
    public Lumina.Excel.SubrowExcelSheet<T> GetSubrowExcelSheet<T>(Dalamud.Game.ClientLanguage? language=null,string? name=null) where T:struct,Lumina.Excel.IExcelSubrow<T> => throw new NotSupportedException();
    public Lumina.Data.FileResource? GetFile(string path)=>throw new NotSupportedException();
    public T? GetFile<T>(string path) where T:Lumina.Data.FileResource=>throw new NotSupportedException();
    public Task<T> GetFileAsync<T>(string path,CancellationToken token) where T:Lumina.Data.FileResource=>throw new NotSupportedException();
    public bool FileExists(string path)=>throw new NotSupportedException();
}
