using System.Numerics;
using System.Runtime.InteropServices;
using FireGaze.LocalPreview.Runtime;
using FireGaze.LocalPreview.Compatibility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

unsafe class Program
{
    static int Main()
    {
        var tests = new (string, Action)[] {
            ("real native layout accepted", Layout),
            ("owned root children and draws replace all preview presentation", OwnedGraph),
            ("logical transform live and body offset baseline", BodyMoves),
            ("companion relative transform follows live player", MinionMoves),
            ("frame freezes within nested scope and refreshes next capture", Nested),
            ("stale actor territory revision never falls back live", Stale),
            ("capture never writes original native buffers", OriginalUnchanged),
            ("ground sprint jump and turn remain real while upper slots are baseline", GroundLocomotion),
            ("swim and underwater locomotion remain real", SwimLocomotion),
            ("trial mount cannot leak fly or mounted states", TrialMount),
            ("genuine mounted baseline permits real mount child locomotion", GenuineMount),
            ("unknown unowned timeline fails closed", Unknown),
            ("preview emote and accessory idle do not become HMS emotes", TrialTimeline),
            ("missing and disposed scopes cannot expose active preview", ScopeLifetime),
            ("no preview scope passes through real pointer", NoPreview),
            ("another object cannot receive the local shadow", WrongObject),
            ("out of order and bridge disposal preserve active lease memory", DisposeDuringCapture),
        };
        int failed=0;
        foreach(var (name,test) in tests) try { test(); Console.WriteLine("PASS "+name); } catch(Exception e) { failed++; Console.WriteLine("FAIL "+name+": "+e.Message); }
        Console.WriteLine($"{tests.Length-failed}/{tests.Length} passed"); return failed==0?0:1;
    }
    static void Equal<T>(T value,T expected) { if(!EqualityComparer<T>.Default.Equals(value,expected)) throw new Exception($"expected {expected}; got {value}"); }
    static void Throws(Action action) { try { action(); } catch(InvalidOperationException) { return; } throw new Exception("expected refusal"); }
    static void Layout() => Equal(HmsNativeLayout.IsSupported,true);
    static void OwnedGraph() {
        using var f=new Fixture(true); f.Preview(); using var shadow=new HmsShadowActor(f); Equal(shadow.TryBeginCapture(out var scope),true); using(scope) {
            var p=(NativeCharacter*)shadow.ReadDetectorAddress(f.Address,42);
            Equal(p!=f.Actor,true); Equal(p->Mode,CharacterModes.Normal); Equal(p->EmoteController.EmoteId,(ushort)12); Equal(p->EmoteController.Stance,(byte)3);
            Equal(p->Timeline.TimelineSequencer.TimelineIds[1],(ushort)71); Equal(p->Timeline.TimelineSequencer.TimelineIds[3],(ushort)73); Equal(p->Timeline.BaseOverride,(ushort)19);
            Equal(p->Timeline.IsWeaponDrawn,false); Equal(p->Mount.MountId,(ushort)8); Equal(p->CompanionData.CompanionId,(ushort)5); Equal(p->OrnamentData.OrnamentId,(ushort)6);
            Equal(p->Mount.MountObject!=f.Mount,true); Equal(p->Mount.MountObject->Timeline.TimelineSequencer.TimelineIds[2],(ushort)93);
            Equal(p->CompanionData.CompanionObject!=f.Minion,true); Equal(p->CompanionData.CompanionObject->Behavior,(CompanionMove)2);
            Equal(p->OrnamentData.OrnamentObject!=f.Ornament,true); Equal(p->OrnamentData.OrnamentObject->CustomizeGroupId,4u);
            Equal(p->DrawObject!=f.Actor->DrawObject,true); Equal(p->Mount.MountObject->DrawObject!=f.Mount->DrawObject,true);
            Equal(p->Timeline.OwnerObject==p,true); Equal(p->Mount.OwnerObject==p,true);
        }
    }
    static void BodyMoves() {
        using var f=new Fixture(); f.Preview(); f.Actor->Position=new Vector3(40,50,60); f.Actor->Rotation=1.2f;
        using var shadow=new HmsShadowActor(f); Equal(shadow.TryBeginCapture(out var s),true); using(s) { var p=(NativeCharacter*)shadow.ReadBodyAddress(f.Address,42); Equal((Vector3)p->Position,new Vector3(40,50,60)); Equal(p->Rotation,1.2f); Equal((Vector3)p->DrawObject->Position,new Vector3(41,52,63)); }
    }
    static void MinionMoves() {
        using var f=new Fixture(true); f.Preview(); f.Actor->Position=new Vector3(90,100,110);
        using var shadow=new HmsShadowActor(f); Equal(shadow.TryBeginCapture(out var s),true); using(s) { var p=(NativeCharacter*)shadow.ReadDetectorAddress(f.Address,42); Equal((Vector3)p->CompanionData.CompanionObject->Position,new Vector3(94,105,116)); Equal(p->CompanionData.CompanionObject->Rotation,0.7f); }
    }
    static void Nested() {
        using var f=new Fixture(); f.Preview(); using var shadow=new HmsShadowActor(f); Equal(shadow.TryBeginCapture(out var a),true);
        var p=shadow.ReadDetectorAddress(f.Address,42); f.Actor->Position=new Vector3(90,100,110); Equal(shadow.TryBeginCapture(out var b),true); Equal(shadow.ReadBodyAddress(f.Address,42),p); b!.Dispose(); Equal(((NativeCharacter*)p)->Position.X,10f); a!.Dispose();
        Equal(shadow.TryBeginCapture(out var c),true); using(c) Equal(((NativeCharacter*)shadow.ReadBodyAddress(f.Address,42))->Position.X,90f);
    }
    static void Stale() {
        foreach(int kind in new[]{0,1,2}) { using var f=new Fixture(); f.Preview(); using var shadow=new HmsShadowActor(f); Equal(shadow.TryBeginCapture(out var s),true); using(s) { if(kind==0)f.ActorId++; else if(kind==1)f.Territory++; else f.Revision++; Throws(()=>shadow.ReadDetectorAddress(f.Address,42)); Equal(shadow.TryBeginCapture(out var nested),false); nested?.Dispose(); } }
    }
    static void OriginalUnchanged() {
        using var f=new Fixture(true); f.Preview(); byte[] original=new ReadOnlySpan<byte>(f.Actor,sizeof(NativeCharacter)).ToArray(); byte[] child=new ReadOnlySpan<byte>(f.Minion,sizeof(Companion)).ToArray();
        using var shadow=new HmsShadowActor(f); Equal(shadow.TryBeginCapture(out var s),true); using(s) { _=shadow.ReadDetectorAddress(f.Address,42); _=shadow.ReadBodyAddress(f.Address,42); }
        Equal(original.SequenceEqual(new ReadOnlySpan<byte>(f.Actor,sizeof(NativeCharacter)).ToArray()),true); Equal(child.SequenceEqual(new ReadOnlySpan<byte>(f.Minion,sizeof(Companion)).ToArray()),true);
    }
    static void GroundLocomotion() {
        foreach(ushort tl in new ushort[]{7,8,30,31,32,33}) { using var f=new Fixture(); f.Preview(); f.Actor->Timeline.TimelineSequencer.TimelineIds[0]=tl; using var shadow=new HmsShadowActor(f); Equal(shadow.TryBeginCapture(out var s),true); using(s) { var p=(NativeCharacter*)shadow.ReadDetectorAddress(f.Address,42); Equal(p->Timeline.TimelineSequencer.TimelineIds[0],tl); Equal(p->Timeline.TimelineSequencer.TimelineIds[1],(ushort)71); } }
        using var armed=new Fixture(); armed.Preview(); armed.Actor->Timeline.TimelineSequencer.TimelineIds[0]=59; using var normal=new HmsShadowActor(armed); Equal(normal.TryBeginCapture(out var scope),true); using(scope) Equal(((NativeCharacter*)normal.ReadDetectorAddress(armed.Address,42))->Timeline.TimelineSequencer.TimelineIds[0],(ushort)31);
    }
    static void SwimLocomotion() { foreach(ushort tl in new ushort[]{4948,4958,4959,4960,4961,4969,4979,4980}) { using var f=new Fixture(); f.Preview(); f.Actor->Timeline.TimelineSequencer.TimelineIds[0]=tl; using var shadow=new HmsShadowActor(f); Equal(shadow.TryBeginCapture(out var s),true); using(s) Equal(((NativeCharacter*)shadow.ReadDetectorAddress(f.Address,42))->Timeline.TimelineSequencer.TimelineIds[0],tl); } }
    static void TrialMount() {
        foreach(ushort tl in new ushort[]{4040,4041,4051,5003,165,190}) { using var f=new Fixture(); f.Preview(); f.OwnMount=true; f.Actor->Timeline.TimelineSequencer.TimelineIds[0]=tl; using var shadow=new HmsShadowActor(f); Equal(shadow.TryBeginCapture(out var s),false); s?.Dispose(); }
        using var idle=new Fixture(); idle.Preview(); idle.OwnMount=true; idle.Actor->Timeline.TimelineSequencer.TimelineIds[0]=166; using var sh=new HmsShadowActor(idle); Equal(sh.TryBeginCapture(out var scope),true); using(scope) { var p=(NativeCharacter*)sh.ReadDetectorAddress(idle.Address,42); Equal(p->Mount.MountId,(ushort)0); Equal(p->Mount.MountObject==null,true); Equal(p->Mode,CharacterModes.Normal); Equal(p->Timeline.TimelineSequencer.TimelineIds[0],(ushort)3); }
    }
    static void GenuineMount() { using var f=new Fixture(true,true); f.Preview(); f.Actor->Mount.MountId=8; f.Actor->Timeline.TimelineSequencer.TimelineIds[0]=22; f.Mount->Timeline.TimelineSequencer.TimelineIds[0]=4041; using var sh=new HmsShadowActor(f); Equal(sh.TryBeginCapture(out var s),true); using(s) { var p=(NativeCharacter*)sh.ReadDetectorAddress(f.Address,42); Equal(p->Mode,CharacterModes.Mounted); Equal(p->Mount.MountObject->Timeline.TimelineSequencer.TimelineIds[0],(ushort)4041); Equal(p->Mount.MountObject->Timeline.TimelineSequencer.TimelineIds[2],(ushort)93); } }
    static void Unknown() { using var f=new Fixture(); f.Preview(); f.Actor->Timeline.TimelineSequencer.TimelineIds[0]=60000; using var sh=new HmsShadowActor(f); Equal(sh.TryBeginCapture(out var s),false); s?.Dispose(); }
    static void TrialTimeline() { foreach(ushort tl in new ushort[]{9000,7367}) { using var f=new Fixture(); f.Preview(); f.OwnEmote=tl==9000; f.Actor->Timeline.TimelineSequencer.TimelineIds[0]=tl; using var sh=new HmsShadowActor(f); Equal(sh.TryBeginCapture(out var s),true); using(s) Equal(((NativeCharacter*)sh.ReadDetectorAddress(f.Address,42))->Timeline.TimelineSequencer.TimelineIds[0],(ushort)3); } }
    static void ScopeLifetime() { using var f=new Fixture(); f.Preview(); using var sh=new HmsShadowActor(f); Throws(()=>sh.ReadDetectorAddress(f.Address,42)); Equal(sh.TryBeginCapture(out var a),true); a!.Dispose(); Throws(()=>sh.ReadBodyAddress(f.Address,42)); sh.Dispose(); Equal(sh.TryBeginCapture(out var s),false); s?.Dispose(); }
    static void NoPreview() { using var f=new Fixture(); f.Project=false; using var sh=new HmsShadowActor(f); Equal(sh.TryBeginCapture(out var a),true); using(a) Equal(sh.ReadDetectorAddress(f.Address,42),f.Address); }
    static void WrongObject() { using var f=new Fixture(); f.Preview(); using var sh=new HmsShadowActor(f); Equal(sh.TryBeginCapture(out var s),true); using(s) { Throws(()=>sh.ReadDetectorAddress(f.Address+8,42)); Throws(()=>sh.ReadBodyAddress(f.Address,43)); } }
    static void DisposeDuringCapture() {
        using var f=new Fixture(); f.Preview(); using var sh=new HmsShadowActor(f); Equal(sh.TryBeginCapture(out var a),true); var p=(NativeCharacter*)sh.ReadDetectorAddress(f.Address,42);
        Equal(sh.TryBeginCapture(out var b),true); a!.Dispose(); Equal(sh.ReadBodyAddress(f.Address,42),(nint)p);
        sh.Dispose(); Equal(p->EmoteController.Stance,(byte)3); Throws(()=>sh.ReadDetectorAddress(f.Address,42)); b!.Dispose(); b.Dispose();
    }
    sealed unsafe class Fixture : IHmsShadowSource, IDisposable {
        public readonly NativeCharacter* Actor=Alloc<NativeCharacter>(); public readonly NativeCharacter* Mount=Alloc<NativeCharacter>(); public readonly Companion* Minion=Alloc<Companion>(); public readonly Ornament* Ornament=Alloc<Ornament>();
        readonly DrawObject* bodyDraw=Alloc<DrawObject>(); readonly DrawObject* mountDraw=Alloc<DrawObject>();
        public ulong ActorId=42; public uint Territory=7; public long Revision=1; public bool Project=true,OwnMount,OwnEmote; public nint Address=>(nint)Actor; readonly HmsPresentationBaseline baseline;
        public Fixture(bool children=false,bool mounted=false) {
            Actor->Mode=mounted?CharacterModes.Mounted:CharacterModes.Normal; Actor->Position=new Vector3(10,20,30); Actor->DrawObject=bodyDraw; bodyDraw->Position=new Vector3(11,22,33);
            Actor->Timeline.TimelineSequencer.TimelineIds[0]=3; Actor->Timeline.TimelineSequencer.TimelineIds[1]=71; Actor->Timeline.TimelineSequencer.TimelineIds[2]=72; Actor->Timeline.TimelineSequencer.TimelineIds[3]=73; Actor->Timeline.BaseOverride=19; Actor->EmoteController.EmoteId=12; Actor->EmoteController.Stance=3;
            if(children) { Actor->Mount.MountId=8; Actor->Mount.MountObject=Mount; Mount->DrawObject=mountDraw; mountDraw->Rotation=Quaternion.Identity; Mount->Timeline.TimelineSequencer.TimelineIds[0]=166; Mount->Timeline.TimelineSequencer.TimelineIds[2]=93; Actor->CompanionData.CompanionId=5; Actor->CompanionData.CompanionObject=Minion; Minion->BaseId=5; Minion->Behavior=(CompanionMove)2; Minion->Position=new Vector3(14,25,36); Minion->Rotation=0.7f; Actor->OrnamentData.OrnamentId=6; Actor->OrnamentData.OrnamentObject=Ornament; Ornament->CustomizeGroupId=4; }
            baseline=HmsPresentationBaseline.Read(Actor);
        }
        public void Preview() { Actor->Mode=CharacterModes.EmoteLoop; Actor->EmoteController.EmoteId=900; Actor->EmoteController.Stance=9; Actor->Timeline.BaseOverride=9000; Actor->Timeline.IsWeaponDrawn=true; Actor->Timeline.TimelineSequencer.TimelineIds[1]=9001; Actor->Timeline.TimelineSequencer.TimelineIds[2]=9002; Actor->Mount.MountId=999; Actor->OrnamentData.OrnamentId=998; bodyDraw->Position=new Vector3(10,200,30); Minion->BaseId=997; Minion->Position=new Vector3(900,901,902); }
        public bool TryGetHmsCaptureFrame(out HmsCaptureFrame frame) { frame=new(Address,ActorId,Territory,Revision,Project,baseline,OwnMount,OwnEmote); return true; }
        public bool IsHmsCaptureFrameCurrent(in HmsCaptureFrame frame)=>frame.Address==Address&&frame.ActorId==ActorId&&frame.Territory==Territory&&frame.Revision==Revision&&frame.Project==Project;
        static T* Alloc<T>()where T:unmanaged=>(T*)NativeMemory.AllocZeroed((nuint)sizeof(T));
        public void Dispose() { NativeMemory.Free(Actor); NativeMemory.Free(Mount); NativeMemory.Free(Minion); NativeMemory.Free(Ornament); NativeMemory.Free(bodyDraw); NativeMemory.Free(mountDraw); }
    }
}
