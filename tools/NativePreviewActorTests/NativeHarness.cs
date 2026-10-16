using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

// Standalone native layout simulations. No game process access of any kind.
static unsafe class NativeHarness
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Type ActorType = Assembly.GetExecutingAssembly().GetType("FireGaze.LocalPreview.Runtime.NativePreviewActor")!;
    static readonly (string Name, Action Test)[] Cases = [
        ("root-state-unchanged", RootUnchanged), ("hide-only-after-ready", HideAfterReady),
        ("owned-visibility-restored", OwnedFlags), ("preexisting-hide-preserved", PreexistingFlags),
        ("slot-reused-pointer", SlotPointer), ("slot-reused-generation", SlotGeneration),
        ("source-replaced-pointer", SourcePointer), ("source-replaced-generation", SourceGeneration),
        ("allocator-failure", AllocationFailure), ("copy-failure-cleanup", CopyFailure),
        ("lookup-failure-cleanup", LookupFailure), ("idempotent-dispose", DisposeTwice),
        ("original-children-visibility", ChildVisibility), ("replaced-children-untouched", ReplacedChildren),
        ("native-local-target-resolution", LocalTargetResolution), ("draw-loss-restores-source", DrawLoss),
        ("wrong-source-refused", WrongSource), ("child-generation-reused", ChildGeneration),
        ("child-preexisting-hide", ChildPreexistingHide),
        ("slot-reentry-during-copy", CopyReentry),
        ("clone-children-draw-readiness", CloneChildrenReady),
        ("clone-child-draw-loss-restores-source", CloneChildDrawLoss),
        ("slot-reentry-during-child-enable", ChildEnableReentry),
        ("clone-hierarchy-not-targetable", CloneNotTargetable),
        ("pending-child-draw-enabled-once", PendingDraw),
        ("source-draw-swap-no-stale-restore", SourceDrawSwap),
        ("source-draw-lease-reasserts-owned-bit", ReassertVisibility),
        ("ground-motion-writes-owned-root-only", MotionOwnedRoot),
        ("ground-motion-does-not-restart-loop", MotionLoop),
        ("ground-motion-landing-once", MotionLanding),
        ("ground-motion-yields-to-emote", MotionEmote),
        ("ground-motion-refuses-unready-and-reused-actor", MotionOwnership),
        ("ground-motion-preserves-accessory-pose", MotionAccessory),
    ];
    public static int Run(string[] args)
    {
        if (args.Length == 2 && args[0] == "--case")
        {
            try { Cases.Single(c => c.Name == args[1]).Test(); Console.WriteLine("PASS " + args[1]); return 0; }
            catch (Exception e) { Console.WriteLine("FAIL " + args[1] + ": " + Unwrap(e)); return 1; }
        }
        int failed = 0;
        foreach (var c in Cases)
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--case " + c.Name)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000)) { process.Kill(); failed++; Console.WriteLine("FAIL " + c.Name + ": timeout"); continue; }
            Console.Write(output);
            if (process.ExitCode != 0) { failed++; if (output.Length == 0) Console.WriteLine($"FAIL {c.Name}: isolated exit {process.ExitCode}"); if (error.Length > 0) Console.Write(error); }
        }
        Console.WriteLine($"{Cases.Length-failed}/{Cases.Length} isolated native layout boundary cases passed"); return failed == 0 ? 0 : 1;
    }
    static Exception Unwrap(Exception e) => e is TargetInvocationException { InnerException: {} inner } ? Unwrap(inner) : e;
    static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    static void Eq<T>(T actual, T expected, string why) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"{why}: expected {expected}; got {actual}"); }
    static object Box(Character* actor) => Pointer.Box(actor, typeof(Character*));
    static object? Create(Character* source) => ActorType.GetMethod("Create", All)!.Invoke(null, [Box(source), null]);
    static object MustCreate(F f) { var p = Create(f.Source); Check(p != null, "Create refused valid source"); return p!; }
    static bool Tick(object p, Character* source) => (bool)ActorType.GetMethod("Tick", All)!.Invoke(p, [Box(source)])!;
    static Character* Actor(object p) => (Character*)Pointer.Unbox(ActorType.GetProperty("Actor", All)!.GetValue(p)!);
    static bool Ready(object p) => (bool)ActorType.GetProperty("IsReady", All)!.GetValue(p)!;
    static void Dispose(object p) => ActorType.GetMethod("Dispose", All)!.Invoke(p, null);
    static void Settle(object p, F f) { f.Ready = true; for (int i = 0; i < 8 && !Ready(p); i++) Tick(p, f.Source); Check(Ready(p), "clone never ready"); }
    static byte[] Bytes(Character* p) => new ReadOnlySpan<byte>(p, sizeof(Character)).ToArray();
    static void Same(byte[] before, Character* after, string why) => Check(before.SequenceEqual(Bytes(after)), why);
    static void OnlyFlags(byte[] before, Character* p)
    {
        Same(before, p, "presentation must preserve every real Character byte including RenderFlags");
    }
    static void RootUnchanged()
    {
        using var f = new F(); byte[] before = Bytes(f.Source); var p = MustCreate(f);
        Eq((nint)Actor(p), (nint)f.Clone, "separate actor pointer"); Same(before, f.Source, "Create touched source"); Settle(p, f); OnlyFlags(before, f.Source);
        Eq((ulong)f.Source->RenderFlags, 0x800UL, "real model lifecycle flags preserved"); Check(!f.Source->DrawObject->IsVisible,"real source draw still visible"); Eq(f.Allocates, 1, "allocate once"); Eq(f.Param, (byte)1, "reserve child slot");
        Check(f.Flags.Count > 0, "native copy absent"); foreach (uint flags in f.Flags) Eq(flags & (1u|2u|0x20u|0x400u|0x8000u|0x400000u), 0u, "copy excludes mode children targets timeline");
        f.Source->Position = new Vector3(40,50,60); f.Source->Rotation = 1.25f; f.Source->TargetId = 901UL; f.Source->SoftTargetId = 902UL;
        before = Bytes(f.Source); Check(Tick(p, f.Source), "follow refused");
        Eq((Vector3)f.Clone->Position, new Vector3(40,50,60), "follow logical position"); Eq(f.Clone->Rotation, 1.25f, "follow yaw");
        Eq(f.Clone->TargetId.Id, 901UL, "follow target"); Eq(f.Clone->SoftTargetId.Id, 902UL, "follow soft target"); Same(before, f.Source, "follow touched source"); Eq(f.SourceWrites, 0, "native writes source");
        Dispose(p); OnlyFlags(before, f.Source); Eq((ulong)f.Source->RenderFlags, 0x800UL, "source visible again"); Check(f.Source->DrawObject->IsVisible,"source visibility not restored"); Eq(f.Deletes, 1, "delete once");
    }
    static void HideAfterReady() { using var f = new F(); f.Ready = false; var p = MustCreate(f); for(int i=0;i<4;i++) { Tick(p,f.Source); Eq((ulong)f.Source->RenderFlags,0x800UL,"hidden before draw ready"); } Check(!Ready(p),"ready too early"); Eq(f.Enables,0,"enable before ready"); Settle(p,f); Check(f.Enables>0,"enable missing"); Dispose(p); }
    static void OwnedFlags() { using var f = new F(); var p = MustCreate(f); Settle(p,f); f.Source->RenderFlags |= (VisibilityFlags)0x400; f.Source->DrawObject->Flags |= 0x40; Dispose(p); Eq((ulong)f.Source->RenderFlags,0xC00UL,"unrelated flags lost"); Eq(f.Source->DrawObject->Flags,(byte)0x69,"restore owns only visibility bit"); }
    static void PreexistingFlags() { using var f = new F(); f.Source->DrawObject->Flags=0x28; var p=MustCreate(f); Settle(p,f); Dispose(p); Eq(f.Source->DrawObject->Flags,(byte)0x28,"preexisting visibility preserved"); }
    static void SlotPointer() { using var f = new F(); var p=MustCreate(f); Settle(p,f); f.Manager->BattleCharas[F.Slot].BattleChara=f.Replacement; byte[] b=Bytes((Character*)f.Replacement); Check(!Tick(p,f.Source),"slot reuse accepted"); Dispose(p); Eq(f.Deletes,0,"reused slot deleted"); Same(b,(Character*)f.Replacement,"replacement touched"); }
    static void SlotGeneration() { using var f = new F(); var p=MustCreate(f); Settle(p,f); F.Generation(f.Clone,9876); byte[] b=Bytes(f.Clone); Check(!Tick(p,f.Source),"same pointer generation accepted"); Dispose(p); Eq(f.Deletes,0,"new generation deleted"); Same(b,f.Clone,"new generation touched"); }
    static void SourcePointer() { using var f = new F(); var p=MustCreate(f); Settle(p,f); *f.Local=f.Replacement; byte[] old=Bytes(f.Source), next=Bytes((Character*)f.Replacement); Check(!Tick(p,(Character*)f.Replacement),"new source accepted"); Dispose(p); Same(old,f.Source,"old source touched"); Same(next,(Character*)f.Replacement,"new source touched"); Eq(f.Deletes,1,"clone leaked"); }
    static void SourceGeneration() { using var f = new F(); var p=MustCreate(f); Settle(p,f); F.Generation(f.Source,22222); byte[] b=Bytes(f.Source); Check(!Tick(p,f.Source),"new source generation accepted"); Dispose(p); Same(b,f.Source,"reused source touched"); Eq(f.Deletes,1,"clone leaked"); }
    static void AllocationFailure() { using var f=new F(); f.FailAllocate=true; byte[] b=Bytes(f.Source); Check(Create(f.Source)==null,"allocator failure accepted"); Same(b,f.Source,"failed alloc touched source"); Eq(f.Deletes,0,"unowned delete"); }
    static void CopyFailure() { using var f=new F(); CharacterSetupContainer.Addresses.CopyFromCharacter.Value=0; byte[] b=Bytes(f.Source); Check(Create(f.Source)==null,"copy failure accepted"); Same(b,f.Source,"copy fail touched source"); Eq(f.Deletes,1,"copy fail leaked owned slot"); }
    static void LookupFailure() { using var f=new F(); f.FailLookup=true; byte[] b=Bytes(f.Source); Check(Create(f.Source)==null,"lookup failure accepted"); Same(b,f.Source,"lookup fail touched source"); Eq(f.Deletes,1,"lookup fail leaked owned slot"); }
    static void DisposeTwice() { using var f=new F(); var p=MustCreate(f); Settle(p,f); Dispose(p); Dispose(p); Eq(f.Deletes,1,"double delete"); Check(!Tick(p,f.Source),"disposed tick accepted"); Eq((ulong)f.Source->RenderFlags,0x800UL,"disposed tick hid source"); }
    static void ChildVisibility()
    {
        using var f=new F(); f.AttachChildren(); var p=MustCreate(f); Settle(p,f);
        foreach(nint a in f.Children) Check(!((Character*)a)->DrawObject->IsVisible,"original child not hidden");
        foreach(nint a in f.Children) ((Character*)a)->RenderFlags |= (VisibilityFlags)0x400;
        Dispose(p); foreach(nint a in f.Children) Eq((ulong)((Character*)a)->RenderFlags,0xC00UL,"original child owned flag restore");
    }
    static void ReplacedChildren()
    {
        using var f=new F(); f.AttachChildren(); var p=MustCreate(f); Settle(p,f);
        var next=(Character*)f.Replacement; next->RenderFlags=(VisibilityFlags)0x420;
        f.Source->Mount.MountObject=next; f.Source->CompanionData.CompanionObject=(Companion*)next; f.Source->OrnamentData.OrnamentObject=(Ornament*)next;
        byte[] b=Bytes(next); Check(Tick(p,f.Source),"legitimate child replacement killed root binding"); Dispose(p); Same(b,next,"new native child touched");
    }
    static void LocalTargetResolution()
    {
        using var f=new F(); var p=MustCreate(f); f.ResolvedTarget=1901; f.ResolvedSoftTarget=1902; Settle(p,f);
        Eq(f.Clone->TargetId.Id,1901UL,"local target must resolve through native getter"); Eq(f.Clone->SoftTargetId.Id,1902UL,"local soft target must resolve through native getter"); Dispose(p);
    }
    static void DrawLoss()
    {
        using var f=new F(); var p=MustCreate(f); Settle(p,f); f.Clone->DrawObject=null; Tick(p,f.Source);
        Eq((ulong)f.Source->RenderFlags,0x800UL,"draw loss left real source hidden"); Dispose(p); Eq(f.Deletes,1,"draw-loss clone leaked");
    }
    static void WrongSource()
    {
        using var f=new F(); byte[] source=Bytes(f.Source), replacement=Bytes((Character*)f.Replacement);
        Check(Create((Character*)f.Replacement)==null,"Create accepted another actor"); Eq(f.Allocates,0,"wrong source allocated clone"); Same(source,f.Source,"wrong source touched local"); Same(replacement,(Character*)f.Replacement,"wrong source touched argument");
    }
    static void ChildGeneration()
    {
        using var f=new F(); f.AttachChildren(); var p=MustCreate(f); Settle(p,f);
        foreach(nint child in f.Children) F.Generation((Character*)child,8765);
        byte[][] before=f.Children.Select(c=>Bytes((Character*)c)).ToArray(); Tick(p,f.Source); Dispose(p);
        for(int i=0;i<before.Length;i++) Same(before[i],(Character*)f.Children[i],"reused child generation touched");
    }
    static void ChildPreexistingHide()
    {
        using var f=new F(); f.AttachChildren(); foreach(nint child in f.Children) ((Character*)child)->DrawObject->Flags=0x28;
        var p=MustCreate(f); Settle(p,f); Dispose(p); foreach(nint child in f.Children) Eq(((Character*)child)->DrawObject->Flags,(byte)0x28,"preexisting child hide cleared");
    }
    static void CopyReentry()
    {
        using var f=new F(); f.ReplaceSlotDuringCopy=true; byte[] source=Bytes(f.Source), replacement=Bytes((Character*)f.Replacement);
        Check(Create(f.Source)==null,"Create escaped after copy reentered and replaced owned slot");
        Eq(f.UnownedNativeWrites,0,"second native setup called after allocation ownership was lost"); Eq(f.Deletes,0,"reentrant replacement was deleted");
        Same(source,f.Source,"copy reentry touched source"); Same(replacement,(Character*)f.Replacement,"copy reentry touched replacement");
    }
    static void CloneChildrenReady()
    {
        using var f=new F(); f.AttachChildren(); f.AttachCloneChildren(); f.ChildReady=false; var p=MustCreate(f);
        for(int i=0;i<4;i++) { Tick(p,f.Source); Eq((ulong)f.Source->RenderFlags,0x800UL,"source hidden before clone children ready"); }
        Eq(f.ChildEnables,0,"clone child enabled before native readiness");
        foreach(nint child in f.Children) Eq((ulong)((Character*)child)->RenderFlags,0x800UL,"source child hidden before clone children ready");
        f.ChildReady=true; Settle(p,f); Eq(f.ChildEnables,3,"all clone children need drawing enabled");
        foreach(nint child in f.PreviewChildren) Check(((Character*)child)->DrawObject!=null&&((Character*)child)->DrawObject->IsVisible,"clone child draw absent");
        foreach(nint child in f.Children) Check(!((Character*)child)->DrawObject->IsVisible,"source child not hidden atomically"); Dispose(p);
    }
    static void CloneChildDrawLoss()
    {
        using var f=new F(); f.AttachChildren(); f.AttachCloneChildren(); var p=MustCreate(f); Settle(p,f);
        ((Character*)f.PreviewChildren[0])->DrawObject=null; f.ChildReady=false; Tick(p,f.Source);
        Eq((ulong)f.Source->RenderFlags,0x800UL,"child draw loss left source hidden");
        foreach(nint child in f.Children) Eq((ulong)((Character*)child)->RenderFlags,0x800UL,"child draw loss left source child hidden");
        f.ChildReady=true; Settle(p,f); Dispose(p);
    }
    static void ChildEnableReentry()
    {
        using var f=new F(); f.AttachCloneChildren(); f.ReplaceSlotDuringChildEnable=true; var p=MustCreate(f);
        byte[] before=Bytes(f.Source), replacement=Bytes((Character*)f.Replacement); Tick(p,f.Source); Tick(p,f.Source);
        Check(!Ready(p),"child draw reentry left preview ready"); Eq(f.UnownedNativeWrites,0,"native child drawing continued after allocation ownership was lost");
        Eq(f.Deletes,0,"child draw reentry deleted replacement"); Same(before,f.Source,"child draw reentry touched source"); Same(replacement,(Character*)f.Replacement,"child draw reentry touched replacement"); Dispose(p);
    }
    static void CloneNotTargetable()
    {
        using var f=new F(); f.AttachCloneChildren(); byte[] before=Bytes(f.Source); var p=MustCreate(f); Settle(p,f);
        Eq((uint)f.Clone->TargetableStatus&2u,0u,"clone remains targetable"); Eq((uint)f.Clone->TargetableStatus&9u,9u,"unrelated clone flags changed");
        foreach(nint child in f.PreviewChildren) { Eq((uint)((Character*)child)->TargetableStatus&2u,0u,"clone child remains targetable"); Eq((uint)((Character*)child)->TargetableStatus&9u,9u,"unrelated child flags changed"); }
        OnlyFlags(before,f.Source); Dispose(p); Eq((uint)f.Source->TargetableStatus,11u,"source targetability changed");
    }
    static void PendingDraw()
    {
        using var f=new F(); f.AttachChildren(); f.AttachCloneChildren(); f.DrawVisibleOnEnable=false; var p=MustCreate(f);
        for(int i=0;i<6;i++) Tick(p,f.Source);
        Eq(f.Enables,4,"pending resource draw enabled repeatedly"); Eq((ulong)f.Source->RenderFlags,0x800UL,"pending resource draw hid source");
        f.Clone->DrawObject->IsVisible=true; foreach(nint child in f.PreviewChildren) ((Character*)child)->DrawObject->IsVisible=true;
        Settle(p,f); Dispose(p);
    }
    static void SourceDrawSwap()
    {
        using var f=new F();var p=MustCreate(f);Settle(p,f);var old=f.Source->DrawObject;byte oldFlags=old->Flags;
        var next=(DrawObject*)NativeMemory.AllocZeroed((nuint)sizeof(DrawObject));
        try { next->Flags=0x49;f.Source->DrawObject=next;Tick(p,f.Source);Check(!next->IsVisible,"replacement draw not hidden");Eq(old->Flags,oldFlags,"stale draw touched");Dispose(p);Eq(next->Flags,(byte)0x49,"replacement lease not restored");Eq(old->Flags,oldFlags,"stale draw restored"); }
        finally {NativeMemory.Free(next);}
    }
    static void ReassertVisibility()
    {
        using var f=new F();var p=MustCreate(f);Settle(p,f);f.Source->DrawObject->Flags|=9;Tick(p,f.Source);
        Check(!f.Source->DrawObject->IsVisible,"native refresh leaked real model");Dispose(p);Eq(f.Source->DrawObject->Flags,(byte)0x29,"owned visibility not restored");
    }
    static void Motion(object actor,bool emote=false)
    {
        var method=ActorType.GetMethod("UpdateGroundedMotion",All);
        Check(method!=null,"owned actor has no grounded animation writer");
        method!.Invoke(actor,[emote, null]);
    }
    static void MotionOwnedRoot()
    {
        using var f=new F();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);
        f.Source->Timeline.TimelineSequencer.TimelineIds[0]=13;f.Clone->Mount.MountId=446;
        byte[] source=Bytes(f.Source),child=Bytes((Character*)f.PreviewChildren[0]);
        Motion(p);Eq(f.PlayCalls,1,"one animation start");Eq(f.LastTimeline,(ushort)13,"sampled ground clip on root");
        Eq(f.Clone->Timeline.BaseOverride,(ushort)13,"root pin");Same(source,f.Source,"motion touched real player");Same(child,(Character*)f.PreviewChildren[0],"motion directly wrote mount child");
        Eq(f.SourceWrites,0,"native source writes");Dispose(p);
    }
    static void MotionLoop()
    {
        using var f=new F();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=446;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=22;
        Motion(p);Motion(p);Eq(f.PlayCalls,1,"held run did not restart");
        f.Source->Timeline.TimelineSequencer.TimelineIds[0]=3;Motion(p);Eq(f.LastTimeline,(ushort)166,"stop uses rider mounted idle");Eq(f.Clone->Mount.MountId,(ushort)446,"mount state preserved");Dispose(p);
    }
    static void MotionLanding()
    {
        using var f=new F();var p=MustCreate(f);Settle(p,f);
        foreach(ushort clip in new ushort[]{31,32,33}){f.Source->Timeline.TimelineSequencer.TimelineIds[0]=clip;Motion(p);}
        Eq(f.PlayCalls,3,"jump stages played once");Eq(f.Clone->Timeline.BaseOverride,(ushort)0,"landing not pinned");
        for(int i=0;i<10;i++)Motion(p);Eq(f.PlayCalls,3,"landing never restarted");Dispose(p);
    }
    static void MotionEmote()
    {
        using var f=new F();var p=MustCreate(f);Settle(p,f);f.Source->Timeline.TimelineSequencer.TimelineIds[0]=13;f.Clone->Timeline.BaseOverride=999;
        Motion(p,true);Eq(f.PlayCalls,0,"actual emote owns animation");Eq(f.Clone->Timeline.BaseOverride,(ushort)999,"emote pin preserved");Motion(p);Eq(f.PlayCalls,1,"motion resumes after action");Dispose(p);
    }
    static void MotionOwnership()
    {
        using var f=new F();var p=MustCreate(f);f.Source->Timeline.TimelineSequencer.TimelineIds[0]=13;Motion(p);Eq(f.PlayCalls,0,"unready actor untouched");
        Settle(p,f);f.Manager->BattleCharas[F.Slot].BattleChara=f.Replacement;byte[] old=Bytes(f.Clone),replacement=Bytes((Character*)f.Replacement);
        Motion(p);Eq(f.PlayCalls,0,"reused actor untouched");Same(old,f.Clone,"old actor written");Same(replacement,(Character*)f.Replacement,"replacement written");Dispose(p);
    }
    static void MotionAccessory()
    {
        using var f=new F();var p=MustCreate(f);Settle(p,f);f.Source->Timeline.TimelineSequencer.TimelineIds[0]=13;f.Clone->OrnamentData.OrnamentId=58;f.Clone->Timeline.BaseOverride=7367;
        Motion(p);Eq(f.PlayCalls,0,"onfoot sample did not overwrite accessory pose");Eq(f.Clone->Timeline.BaseOverride,(ushort)7367,"native accessory pose retained");Dispose(p);
    }

    sealed class F : IDisposable
    {
        public const ushort Slot=7; static F current=null!;
        public readonly ClientObjectManager* Manager=Alloc<ClientObjectManager>();
        readonly BattleChara* root=Alloc<BattleChara>(); readonly BattleChara* clone=Alloc<BattleChara>(); readonly BattleChara* reservedChild=Alloc<BattleChara>();
        public readonly BattleChara* Replacement=Alloc<BattleChara>();
        public Character* Source=>(Character*)root; public Character* Clone=>(Character*)clone;
        public readonly BattleChara** Local=(BattleChara**)NativeMemory.AllocZeroed((nuint)sizeof(nint));
        readonly nint* vtable=(nint*)NativeMemory.AllocZeroed(0x300); readonly DrawObject* bodyDraw=Alloc<DrawObject>(); readonly DrawObject* cloneDraw=Alloc<DrawObject>();
        public readonly List<nint> Children=[], PreviewChildren=[], PreviewDraws=[], SourceDraws=[]; public readonly List<uint> Flags=[];
        public bool Ready=true,ChildReady=true,DrawVisibleOnEnable=true,FailAllocate,FailLookup,ReplaceSlotDuringCopy,ReplaceSlotDuringChildEnable; public int Allocates,Deletes,Enables,ChildEnables,SourceWrites,UnownedNativeWrites,PlayCalls;public ushort LastTimeline; public byte Param; public ulong? ResolvedTarget,ResolvedSoftTarget;
        public F()
        {
            current=this; vtable[1]=(nint)(delegate* unmanaged<GameObject*,ulong>)&Identity; vtable[12]=(nint)(delegate* unmanaged<GameObject*,void>)&Enable; vtable[13]=(nint)(delegate* unmanaged<GameObject*,void>)&Disable; vtable[23]=(nint)(delegate* unmanaged<GameObject*,DrawObject*>)&GetDraw;
            Init(Source,0,500,111); Init(Clone,207,0xE0000000,222); Init((Character*)Replacement,207,0xE0000000,333); Clone->ChildObject=(Character*)reservedChild;
            Source->Mode=CharacterModes.Normal; Source->ModeParam=19; Source->EmoteController.EmoteId=71; Source->Timeline.BaseOverride=33; Source->Timeline.TimelineSequencer.TimelineIds[0]=31;
            Source->Position=new Vector3(10,20,30); Source->Rotation=.75f; Source->RenderFlags=(VisibilityFlags)0x800; Source->DrawObject=bodyDraw; bodyDraw->Flags=0x29; Source->TargetId=801UL; Source->SoftTargetId=802UL;
            *Local=root; Manager->BattleCharaMemory=clone; Manager->BattleCharaSize=(uint)sizeof(BattleChara);
            ClientObjectManager.Addresses.Instance.Value=(nint)Manager; ClientObjectManager.Addresses.CreateBattleCharacter.Value=(nint)(delegate* unmanaged<ClientObjectManager*,uint,byte,uint>)&Allocate;
            ClientObjectManager.Addresses.GetObjectByIndex.Value=(nint)(delegate* unmanaged<ClientObjectManager*,ushort,Character*>)&Lookup; ClientObjectManager.Addresses.GetIndexByObject.Value=(nint)(delegate* unmanaged<ClientObjectManager*,GameObject*,uint>)&Index;
            ClientObjectManager.Addresses.DeleteObjectByIndex.Value=(nint)(delegate* unmanaged<ClientObjectManager*,ushort,byte,void>)&Delete; Control.Addresses.GetLocalPlayer.Value=(nint)Local;
            CharacterSetupContainer.Addresses.CopyFromCharacter.Value=(nint)(delegate* unmanaged<CharacterSetupContainer*,Character*,uint,ulong>)&Copy;
            GameObject.Addresses.IsReadyToDraw.Value=(nint)(delegate* unmanaged<GameObject*,byte>)&NativeReady; GameObject.Addresses.SetPosition.Value=(nint)(delegate* unmanaged<GameObject*,float,float,float,void>)&Position; GameObject.Addresses.SetRotation.Value=(nint)(delegate* unmanaged<GameObject*,float,void>)&Rotation;
            Character.Addresses.GetTargetId.Value=(nint)(delegate* unmanaged<Character*,ulong>)&Target; Character.Addresses.GetSoftTargetId.Value=(nint)(delegate* unmanaged<Character*,ulong>)&SoftTarget;
            Character.Addresses.SetTargetId.Value=(nint)(delegate* unmanaged<Character*,ulong,void>)&SetTarget; Character.Addresses.SetSoftTargetId.Value=(nint)(delegate* unmanaged<Character*,ulong,void>)&SetSoftTarget;
            ActionTimelineSequencer.Addresses.PlayTimeline.Value=(nint)(delegate* unmanaged<ActionTimelineSequencer*,ushort,void*,void>)&Play;
        }
        void Init(Character* p,ushort index,uint eid,uint gen) { *(nint**)p=vtable; p->ObjectIndex=index; p->EntityId=eid; p->ObjectKind=ObjectKind.Pc; p->TargetableStatus=(ObjectTargetableFlags)11; Generation(p,gen); p->Mount.OwnerObject=p; p->CompanionData.OwnerObject=p; p->OrnamentData.OwnerObject=p; p->DrawData.OwnerObject=p; p->CharacterSetup.OwnerObject=p; p->Timeline.OwnerObject=p; }
        public void AttachChildren()
        {
            for(int i=0;i<3;i++) { var child=(Character*)Alloc<BattleChara>(); Init(child,(ushort)(2+i),1000u+(uint)i,400u+(uint)i); child->RenderFlags=(VisibilityFlags)0x800; child->DrawObject=Alloc<DrawObject>(); child->DrawObject->Flags=0x29; SourceDraws.Add((nint)child->DrawObject); Children.Add((nint)child); }
            Source->Mount.MountObject=(Character*)Children[0]; Source->CompanionData.CompanionObject=(Companion*)Children[1]; Source->OrnamentData.OrnamentObject=(Ornament*)Children[2];
        }
        public void AttachCloneChildren()
        {
            for(int i=0;i<3;i++) { var child=(Character*)Alloc<BattleChara>(); Init(child,(ushort)(10+i),2000u+(uint)i,600u+(uint)i); PreviewChildren.Add((nint)child); PreviewDraws.Add((nint)Alloc<DrawObject>()); }
            Clone->Mount.MountObject=(Character*)PreviewChildren[0]; Clone->CompanionData.CompanionObject=(Companion*)PreviewChildren[1]; Clone->OrnamentData.OrnamentObject=(Ornament*)PreviewChildren[2];
        }
        public static void Generation(Character* p,uint value)=>*(uint*)((byte*)p+0x74)=value;
        static void Track(GameObject* p) { if(p==(GameObject*)current.Source||p==(GameObject*)current.Replacement)current.SourceWrites++; }
        [UnmanagedCallersOnly] static ulong Identity(GameObject* p)=>((ulong)p->EntityId<<32)|p->ObjectIndex;
        [UnmanagedCallersOnly] static uint Allocate(ClientObjectManager* m,uint index,byte param) { current.Allocates++; current.Param=param; if(current.FailAllocate)return uint.MaxValue; m->BattleCharas[Slot].BattleChara=current.clone; m->BattleCharas[Slot].ObjectKind=ObjectKind.Pc; return Slot; }
        [UnmanagedCallersOnly] static Character* Lookup(ClientObjectManager* m,ushort index)=>current.FailLookup?null:(Character*)m->BattleCharas[index].BattleChara;
        [UnmanagedCallersOnly] static uint Index(ClientObjectManager* m,GameObject* p)=>(GameObject*)m->BattleCharas[Slot].BattleChara==p?Slot:uint.MaxValue;
        [UnmanagedCallersOnly] static void Delete(ClientObjectManager* m,ushort index,byte param) { current.Deletes++; m->BattleCharas[index].BattleChara=null; }
        [UnmanagedCallersOnly] static ulong Copy(CharacterSetupContainer* c,Character* source,uint flags)
        {
            if(current.Manager->BattleCharas[Slot].BattleChara!=(BattleChara*)c->OwnerObject)current.UnownedNativeWrites++;
            current.Flags.Add(flags); Track((GameObject*)c->OwnerObject); c->OwnerObject->ObjectKind=source->ObjectKind;
            if(current.ReplaceSlotDuringCopy)current.Manager->BattleCharas[Slot].BattleChara=current.Replacement;
            return 1;
        }
        [UnmanagedCallersOnly] static byte NativeReady(GameObject* p)=>(current.PreviewChildren.Contains((nint)p)?current.ChildReady:current.Ready)?(byte)1:(byte)0;
        [UnmanagedCallersOnly] static void Enable(GameObject* p)
        {
            Track(p); current.Enables++;
            if(current.Manager->BattleCharas[Slot].BattleChara!=current.clone)current.UnownedNativeWrites++;
            int child=current.PreviewChildren.IndexOf((nint)p);
            if(child>=0)
            {
                current.ChildEnables++; p->DrawObject=(DrawObject*)current.PreviewDraws[child]; p->DrawObject->IsVisible=current.DrawVisibleOnEnable;
                if(current.ReplaceSlotDuringChildEnable)current.Manager->BattleCharas[Slot].BattleChara=current.Replacement;
            }
            else { p->DrawObject=current.cloneDraw; current.cloneDraw->IsVisible=current.DrawVisibleOnEnable; }
        }
        [UnmanagedCallersOnly] static void Disable(GameObject* p) { Track(p); p->DrawObject=null; }
        [UnmanagedCallersOnly] static DrawObject* GetDraw(GameObject* p)=>p->DrawObject;
        [UnmanagedCallersOnly] static void Position(GameObject* p,float x,float y,float z) { Track(p); p->Position=new Vector3(x,y,z); }
        [UnmanagedCallersOnly] static void Rotation(GameObject* p,float value) { Track(p); p->Rotation=value; }
        [UnmanagedCallersOnly] static ulong Target(Character* p)=>p==current.Source?current.ResolvedTarget??p->TargetId.Id:p->TargetId.Id;
        [UnmanagedCallersOnly] static ulong SoftTarget(Character* p)=>p==current.Source?current.ResolvedSoftTarget??p->SoftTargetId.Id:p->SoftTargetId.Id;
        [UnmanagedCallersOnly] static void SetTarget(Character* p,ulong id) { Track((GameObject*)p); p->TargetId=id; }
        [UnmanagedCallersOnly] static void SetSoftTarget(Character* p,ulong id) { Track((GameObject*)p); p->SoftTargetId=id; }
        [UnmanagedCallersOnly] static void Play(ActionTimelineSequencer* sequence,ushort id,void* unknown) {current.PlayCalls++;current.LastTimeline=id;Track((GameObject*)sequence->Parent);}
        static T* Alloc<T>()where T:unmanaged=>(T*)NativeMemory.AllocZeroed((nuint)sizeof(T));
        public void Dispose() { foreach(nint child in Children.Concat(PreviewChildren).Concat(PreviewDraws).Concat(SourceDraws))NativeMemory.Free((void*)child); NativeMemory.Free(Manager); NativeMemory.Free(root); NativeMemory.Free(clone); NativeMemory.Free(reservedChild); NativeMemory.Free(Replacement); NativeMemory.Free(Local); NativeMemory.Free(vtable); NativeMemory.Free(bodyDraw); NativeMemory.Free(cloneDraw); }
    }
}
