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
        ("native-hook-allocation-guard-rejects-foreign-reused-and-disposed", NativeAllocationGuard),
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
        ("weapon-draw-hidden-only-after-ready", WeaponVisibility),
        ("weapon-draw-restore-owned-bit-only", WeaponRestore),
        ("weapon-native-reassert-and-late-appearance", WeaponReassertLate),
        ("weapon-draw-replacement-no-stale-write", WeaponDrawSwap),
        ("weapon-source-pointer-replacement-untouched", WeaponSourcePointer),
        ("weapon-source-generation-reuse-untouched", WeaponSourceGeneration),
        ("weapon-child-identity-replacement-untouched", WeaponChildIdentity),
        ("weapon-clone-draw-loss-restores-source", WeaponDrawLoss),
        ("weapon-independent-render-pairs-and-extra-slot", WeaponRenderPairs),
        ("weapon-alias-slot-movement-keeps-restore-owner", WeaponAliasMovement),
        ("weapon-suppression-root-slots-read-only", SuppressionRoot),
        ("weapon-suppression-readiness-and-dispose", SuppressionLifecycle),
        ("weapon-suppression-foreign-and-invalid-slots", SuppressionForeign),
        ("weapon-suppression-container-owner-relation", SuppressionContainerOwner),
        ("weapon-suppression-current-child-containers", SuppressionChildren),
        ("weapon-suppression-child-pointer-and-generation", SuppressionChildIdentity),
        ("weapon-suppression-source-pointer-and-generation", SuppressionSourceIdentity),
        ("weapon-suppression-clone-slot-and-generation", SuppressionCloneIdentity),
        ("weapon-suppression-current-draw-loss", SuppressionDrawLoss),
        ("weapon-suppression-draw-owner-model-replacement", SuppressionWeaponReplacement),
        ("weapon-suppression-empty-and-late-weapon", SuppressionLateWeapon),
        ("ground-motion-separates-mounted-rider-and-mount", MotionOwnedRoot),
        ("ground-motion-does-not-restart-loop", MotionLoop),
        ("ground-motion-landing-once", MotionLanding),
        ("ground-motion-yields-to-emote", MotionEmote),
        ("ground-motion-refuses-unready-and-reused-actor", MotionOwnership),
        ("ground-motion-preserves-accessory-pose", MotionAccessory),
        ("ground-motion-normalizes-armed-and-style-mount-clips", MotionCanonical),
        ("ground-motion-cpose-idle-requires-native-idle-evidence", MotionPoseIdle),
        ("ground-motion-rejects-source-or-unready-mount-child", MotionForeignChild),
        ("ground-motion-rebinds-child-generation-with-fresh-edge", MotionChildGeneration),
        ("ground-motion-reentry-stops-before-second-lane", MotionReentry),
        ("ground-motion-request-reentry-does-not-consume-edges", MotionRequestGate),
        ("ground-motion-unknown-releases-only-owned-pins", MotionUnknown),
        ("flight-appearance-preserves-every-source-byte", FlightAppearanceSource),
        ("flight-appearance-rejects-foreign-child", FlightAppearanceForeign),
        ("flight-appearance-accepts-only-confirmed-native-ground-mounted-source", FlightAppearanceMountedSource),
        ("flight-appearance-normalizes-confirmed-native-flight-loops-read-only", FlightAppearanceNativeFlight),
        ("flight-appearance-refuses-invalid-native-flight-hierarchy", FlightAppearanceNativeFlightMetadata),
        ("flight-appearance-keeps-unverified-native-actions-unknown", FlightAppearanceNativeFlightUnknown),
        ("flight-appearance-source-mount-reentry-stops-second-lane", FlightAppearanceNativeFlightReentry),
    ];
    static void NativeAllocationGuard()
    {
        using var f=new F();var p=MustCreate(f);
        var method=ActorType.GetMethod("IsCurrentAllocation",All) ?? throw new Exception("native hook allocation guard missing");
        bool Test(Character* actor)=>(bool)method.Invoke(p,[Pointer.Box(actor,typeof(Character).MakePointerType())])!;
        Check(Test(f.Clone),"owned allocation rejected");Check(!Test(f.Source),"source accepted as display");
        Check(!Test(null),"null accepted");
        F.Generation(f.Clone,9876);Check(!Test(f.Clone),"reused generation accepted");
        Dispose(p);Check(!Test(f.Clone),"disposed allocation accepted");
    }
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
    static void WeaponVisibility()
    {
        using var f=new F();f.AttachWeapons();f.Ready=false;
        byte[] source=Bytes(f.Source);var p=MustCreate(f);
        for(int frame=0;frame<4;frame++)Tick(p,f.Source);
        foreach(nint draw in f.SourceWeapons)Eq(((DrawObject*)draw)->Flags,(byte)0x29,"source weapon hidden before clone ready");
        Settle(p,f);
        foreach(nint draw in f.SourceWeapons)Check(!((DrawObject*)draw)->IsVisible,"ready clone left original main/off/system weapon visible");
        foreach(nint draw in f.CloneWeapons)Eq(((DrawObject*)draw)->Flags,(byte)0x29,"clone weapon render flags changed");
        Same(source,f.Source,"hiding source weapon modified real Character or DrawData");
        Dispose(p);
        foreach(nint draw in f.SourceWeapons)Eq(((DrawObject*)draw)->Flags,(byte)0x29,"original weapon visibility not restored");
        foreach(nint draw in f.CloneWeapons)Eq(((DrawObject*)draw)->Flags,(byte)0x29,"cleanup modified clone weapon flags");
    }
    static void WeaponRestore()
    {
        using var f=new F();f.AttachWeapons();((DrawObject*)f.SourceWeapons[1])->Flags=0x28;
        var p=MustCreate(f);Settle(p,f);
        Check(!((DrawObject*)f.SourceWeapons[0])->IsVisible,"original weapon was never hidden");
        ((DrawObject*)f.SourceWeapons[0])->Flags|=0x40;
        Dispose(p);Dispose(p);
        Eq(((DrawObject*)f.SourceWeapons[0])->Flags,(byte)0x69,"weapon cleanup lost unrelated render flags");
        Eq(((DrawObject*)f.SourceWeapons[1])->Flags,(byte)0x28,"weapon cleanup showed a previously hidden offhand");
        Eq(((DrawObject*)f.SourceWeapons[2])->Flags,(byte)0x29,"system weapon visibility not restored");
    }
    static void WeaponReassertLate()
    {
        using var f=new F();f.AttachWeapons();f.Source->DrawData.WeaponData[2].Weapon=null;f.Source->DrawData.WeaponData[2].DrawData.DrawObject=null;
        var p=MustCreate(f);Settle(p,f);
        Check(!((DrawObject*)f.SourceWeapons[0])->IsVisible,"initial mainhand not hidden");
        ((DrawObject*)f.SourceWeapons[0])->Flags|=9;
        f.Source->DrawData.WeaponData[2].Weapon=(Weapon*)f.SourceWeapons[2];
        f.Source->DrawData.WeaponData[2].DrawData.DrawObject=(DrawObject*)f.SourceWeapons[2];
        Check(Tick(p,f.Source),"late weapon invalidated root binding");
        foreach(nint draw in f.SourceWeapons)Check(!((DrawObject*)draw)->IsVisible,"native refresh or late weapon escaped hiding");
        Dispose(p);foreach(nint draw in f.SourceWeapons)Eq(((DrawObject*)draw)->Flags,(byte)0x29,"native-refreshed or late weapon failed restore");
    }
    static void WeaponDrawSwap()
    {
        using var f=new F();f.AttachWeapons();var p=MustCreate(f);Settle(p,f);
        var old=(DrawObject*)f.SourceWeapons[0];Check(!old->IsVisible,"old weapon not hidden");byte oldFlags=old->Flags;
        var next=f.NewWeaponDraw();next->Flags=0x49;
        f.Source->DrawData.WeaponData[0].Weapon=(Weapon*)next;f.Source->DrawData.WeaponData[0].DrawData.DrawObject=next;
        Check(Tick(p,f.Source),"replacement weapon invalidated root");Check(!next->IsVisible,"new weapon draw not hidden");Eq(old->Flags,oldFlags,"replaced weapon draw touched");
        Dispose(p);Eq(next->Flags,(byte)0x49,"new weapon lease not restored");Eq(old->Flags,oldFlags,"stale weapon restored after replacement");
    }
    static void WeaponSourcePointer()
    {
        using var f=new F();f.AttachWeapons();var p=MustCreate(f);Settle(p,f);
        Check(!((DrawObject*)f.SourceWeapons[0])->IsVisible,"old source weapon not hidden");
        var replacement=f.AttachWeaponsTo((Character*)f.Replacement);byte[] old=f.SourceWeapons.Select(draw=>((DrawObject*)draw)->Flags).ToArray();
        *f.Local=f.Replacement;Check(!Tick(p,(Character*)f.Replacement),"replacement source accepted");Dispose(p);
        for(int i=0;i<3;i++){Eq(((DrawObject*)f.SourceWeapons[i])->Flags,old[i],"old source weapon touched after pointer replacement");Eq(((DrawObject*)replacement[i])->Flags,(byte)0x29,"new source weapon touched");}
    }
    static void WeaponSourceGeneration()
    {
        using var f=new F();f.AttachWeapons();var p=MustCreate(f);Settle(p,f);
        Check(!((DrawObject*)f.SourceWeapons[0])->IsVisible,"source weapon not hidden");
        F.Generation(f.Source,8765);foreach(nint draw in f.SourceWeapons)((DrawObject*)draw)->Flags|=1;
        Check(!Tick(p,f.Source),"reused source generation accepted");Dispose(p);
        foreach(nint draw in f.SourceWeapons)Eq(((DrawObject*)draw)->Flags,(byte)0x29,"reused source weapon modified");
    }
    static void WeaponChildIdentity()
    {
        using var f=new F();f.AttachChildren();var oldChild=(Character*)f.Children[0];var old=f.AttachWeaponsTo(oldChild);
        var p=MustCreate(f);Settle(p,f);
        foreach(nint draw in old)Check(!((DrawObject*)draw)->IsVisible,"source attachment weapon not hidden");
        var replacement=f.AttachWeaponsTo((Character*)f.Replacement);f.Source->Mount.MountObject=(Character*)f.Replacement;
        foreach(nint draw in old)((DrawObject*)draw)->Flags|=0x40;
        Check(Tick(p,f.Source),"child identity replacement invalidated root");Dispose(p);
        foreach(nint draw in old)Eq(((DrawObject*)draw)->Flags,(byte)0x68,"detached child weapon restored through stale identity");
        foreach(nint draw in replacement)Eq(((DrawObject*)draw)->Flags,(byte)0x29,"replacement child weapon touched without lease identity");
    }
    static void WeaponDrawLoss()
    {
        using var f=new F();f.AttachWeapons();var p=MustCreate(f);Settle(p,f);
        foreach(nint draw in f.SourceWeapons)Check(!((DrawObject*)draw)->IsVisible,"ready source weapon not hidden");
        f.Clone->DrawObject=null;f.Ready=false;Check(!Tick(p,f.Source),"missing clone draw still ready");
        foreach(nint draw in f.SourceWeapons)Eq(((DrawObject*)draw)->Flags,(byte)0x29,"draw loss left floating source weapon hidden");
        Settle(p,f);foreach(nint draw in f.SourceWeapons)Check(!((DrawObject*)draw)->IsVisible,"weapon did not rehide after clone recovered");Dispose(p);
    }
    static void WeaponRenderPairs()
    {
        using var f=new F();f.CloneWeapons.AddRange(f.AttachWeaponsTo(f.Clone));
        var pairs=new List<(nint Weapon,nint Draw)>();
        for(int i=0;i<4;i++)
        {
            var weapon=f.NewWeaponDraw();var draw=f.NewWeaponDraw();draw->Flags=0x49;
            ref var data=ref (i<3?ref f.Source->DrawData.WeaponData[i]:ref f.Source->DrawData.UnkWeaponData[0]);
            data.Weapon=(Weapon*)weapon;data.DrawData.DrawObject=draw;pairs.Add(((nint)weapon,(nint)draw));
        }
        byte[] source=Bytes(f.Source);var p=MustCreate(f);Settle(p,f);
        foreach(var pair in pairs)
        {
            Eq(((DrawObject*)pair.Weapon)->Flags,(byte)0x28,"independent weapon render remained visible");
            Eq(((DrawObject*)pair.Draw)->Flags,(byte)0x48,"independent draw render remained visible");
            ((DrawObject*)pair.Draw)->Flags|=0x80;
        }
        Check(!f.Source->DrawObject->IsVisible,"full render set left body visible");
        Same(source,f.Source,"independent render hiding modified real Character");
        Dispose(p);
        foreach(var pair in pairs)
        {
            Eq(((DrawObject*)pair.Weapon)->Flags,(byte)0x29,"independent weapon restore lost owned visibility");
            Eq(((DrawObject*)pair.Draw)->Flags,(byte)0xC9,"independent draw restore lost unrelated flags");
        }
        foreach(nint draw in f.CloneWeapons)Eq(((DrawObject*)draw)->Flags,(byte)0x29,"independent source render lease touched clone weapons");
    }
    static void WeaponAliasMovement()
    {
        using var f=new F();var shared=f.NewWeaponDraw();
        f.Source->DrawData.WeaponData[0].Weapon=(Weapon*)shared;f.Source->DrawData.WeaponData[0].DrawData.DrawObject=shared;
        f.Source->DrawData.WeaponData[1]=f.Source->DrawData.WeaponData[0];
        var p=MustCreate(f);Settle(p,f);Eq(shared->Flags,(byte)0x28,"shared source render not hidden");
        f.Source->DrawData.WeaponData[0]=default;
        Check(Tick(p,f.Source),"moving alias invalidated root");Eq(shared->Flags,(byte)0x28,"moving alias exposed source render");
        shared->Flags|=0x40;Dispose(p);Dispose(p);
        Eq(shared->Flags,(byte)0x69,"moving alias lost its original visibility owner");
    }
    static bool Suppress(F f,object actor,DrawDataContainer* container,int slot)
    {
        var method=ActorType.GetMethod("ShouldSuppressWeaponVisibility",All);
        Check(method!=null,"weapon visibility ownership predicate is missing");
        int nativeCalls=f.NativeCalls;
        bool result=(bool)method!.Invoke(method.IsStatic?null:actor,[Pointer.Box(container,typeof(DrawDataContainer*)),slot])!;
        Eq(f.NativeCalls,nativeCalls,"visibility predicate called native code from a visibility hook");
        return result;
    }
    static bool Suppress(F f,object actor,Character* owner,int slot=0)=>Suppress(f,actor,&owner->DrawData,slot);
    static void SuppressionRoot()
    {
        using var f=new F();f.AttachWeapons();f.AttachExtraWeaponTo(f.Source);var p=MustCreate(f);Settle(p,f);
        byte[] source=Bytes(f.Source),clone=Bytes(f.Clone);
        byte[] flags=f.WeaponDraws.Select(draw=>((DrawObject*)draw)->Flags).ToArray();
        byte sourceBody=f.Source->DrawObject->Flags,cloneBody=f.Clone->DrawObject->Flags;
        for(int i=0;i<4;i++)
        {
            Check(Suppress(f,p,f.Source,i),"current leased source weapon slot was not suppressed");
            Check(Suppress(f,p,f.Source,i),"query consumed the source weapon lease");
        }
        Same(source,f.Source,"visibility predicate wrote source Character");Same(clone,f.Clone,"visibility predicate wrote clone Character");
        for(int i=0;i<flags.Length;i++)Eq(((DrawObject*)f.WeaponDraws[i])->Flags,flags[i],"visibility predicate changed a weapon draw flag");
        Eq(f.Source->DrawObject->Flags,sourceBody,"visibility predicate changed source body flags");Eq(f.Clone->DrawObject->Flags,cloneBody,"visibility predicate changed clone body flags");Dispose(p);
    }
    static void SuppressionLifecycle()
    {
        using var f=new F();f.AttachWeapons();f.Ready=false;var p=MustCreate(f);
        Check(!Suppress(f,p,f.Source),"unready clone suppressed source weapon visibility");
        Tick(p,f.Source);Check(!Suppress(f,p,f.Source),"pending draw suppressed source weapon visibility");
        Settle(p,f);Check(Suppress(f,p,f.Source),"ready clone did not acquire source suppression");
        Dispose(p);Check(!Suppress(f,p,f.Source),"disposed clone retained suppression");
    }
    static void SuppressionForeign()
    {
        using var f=new F();f.AttachWeapons();f.AttachWeaponsTo((Character*)f.Replacement);var p=MustCreate(f);Settle(p,f);
        Check(!Suppress(f,p,(DrawDataContainer*)null,0),"null container suppressed");
        Check(!Suppress(f,p,f.Source,-1),"negative weapon slot suppressed");
        Check(!Suppress(f,p,f.Source,4),"out-of-range weapon slot suppressed");
        Check(!Suppress(f,p,f.Clone),"clone weapon suppressed");
        Check(!Suppress(f,p,(Character*)f.Replacement),"foreign character weapon suppressed");
        Check(Suppress(f,p,f.Source),"invalid queries changed the valid lease");Dispose(p);
    }
    static void SuppressionContainerOwner()
    {
        using var f=new F();f.AttachWeapons();var p=MustCreate(f);Settle(p,f);
        var copy=f.Source->DrawData;
        Check(!Suppress(f,p,&copy,0),"copied container impersonated the source DrawData address");
        f.Source->DrawData.OwnerObject=null;Check(!Suppress(f,p,f.Source),"container with no owner suppressed");
        f.Source->DrawData.OwnerObject=f.Clone;Check(!Suppress(f,p,f.Source),"container with another owner suppressed");
        f.Source->DrawData.OwnerObject=f.Source;Check(Suppress(f,p,f.Source),"valid owner/container relationship refused");Dispose(p);
    }
    static void SuppressionChildren()
    {
        using var f=new F();f.AttachChildren();f.AttachCloneChildren();
        foreach(nint child in f.Children){f.AttachWeaponsTo((Character*)child);f.AttachExtraWeaponTo((Character*)child);}
        foreach(nint child in f.PreviewChildren)f.AttachWeaponsTo((Character*)child);
        var p=MustCreate(f);Settle(p,f);
        foreach(nint child in f.Children)
        {
            var owner=(Character*)child;byte[] before=Bytes(owner);
            for(int slot=0;slot<4;slot++)Check(Suppress(f,p,owner,slot),"current source attachment weapon not suppressed");
            Same(before,owner,"attachment visibility query changed Character bytes");
        }
        foreach(nint child in f.PreviewChildren)Check(!Suppress(f,p,(Character*)child),"clone attachment weapon suppressed");Dispose(p);
    }
    static void SuppressionChildIdentity()
    {
        using var f=new F();f.AttachChildren();foreach(nint child in f.Children)f.AttachWeaponsTo((Character*)child);
        f.AttachWeaponsTo((Character*)f.Replacement);var p=MustCreate(f);Settle(p,f);
        var oldMount=(Character*)f.Children[0];Check(Suppress(f,p,oldMount),"initial mount child not leased");
        f.Source->Mount.MountObject=(Character*)f.Replacement;
        Check(!Suppress(f,p,oldMount),"detached child retained suppression before Tick");
        Check(!Suppress(f,p,(Character*)f.Replacement),"new child inherited stale visibility lease");
        var companion=(Character*)f.Children[1];F.Generation(companion,99991);
        Check(!Suppress(f,p,companion),"reused child generation retained suppression before Tick");
        Check(Suppress(f,p,(Character*)f.Children[2]),"unrelated valid attachment lost suppression");
        Tick(p,f.Source);Check(!Suppress(f,p,oldMount),"detached child reacquired stale suppression");
        Check(!Suppress(f,p,(Character*)f.Replacement),"replacement child acquired an original-child lease");Dispose(p);
    }
    static void SuppressionSourceIdentity()
    {
        using(var f=new F())
        {
            f.AttachWeapons();f.AttachWeaponsTo((Character*)f.Replacement);var p=MustCreate(f);Settle(p,f);*f.Local=f.Replacement;
            Check(!Suppress(f,p,f.Source),"old local-player pointer retained suppression before Tick");
            Check(!Suppress(f,p,(Character*)f.Replacement),"new local player inherited old suppression");Dispose(p);
        }
        using(var f=new F())
        {
            f.AttachWeapons();var p=MustCreate(f);Settle(p,f);F.Generation(f.Source,99992);
            Check(!Suppress(f,p,f.Source),"reused local-player generation retained suppression before Tick");Dispose(p);
        }
    }
    static void SuppressionCloneIdentity()
    {
        using(var f=new F())
        {
            f.AttachWeapons();var p=MustCreate(f);Settle(p,f);f.Manager->BattleCharas[F.Slot].BattleChara=f.Replacement;
            Check(!Suppress(f,p,f.Source),"replaced clone manager slot retained source suppression before Tick");Dispose(p);
        }
        using(var f=new F())
        {
            f.AttachWeapons();var p=MustCreate(f);Settle(p,f);F.Generation(f.Clone,99993);
            Check(!Suppress(f,p,f.Source),"reused clone generation retained source suppression before Tick");Dispose(p);
        }
    }
    static void SuppressionDrawLoss()
    {
        using var f=new F();f.AttachWeapons();var p=MustCreate(f);Settle(p,f);
        f.Clone->DrawObject->IsVisible=false;Check(!Suppress(f,p,f.Source),"invisible clone kept hiding source weapons before Tick");
        f.Clone->DrawObject->IsVisible=true;Check(Suppress(f,p,f.Source),"visible current clone lost suppression");
        f.Clone->DrawObject=null;f.Ready=false;Check(!Suppress(f,p,f.Source),"missing clone draw kept suppression before Tick");
        Tick(p,f.Source);Check(!Suppress(f,p,f.Source),"lost readiness retained source weapon suppression");
        Settle(p,f);Check(Suppress(f,p,f.Source),"recovered clone did not reacquire suppression");Dispose(p);
    }
    static void SuppressionWeaponReplacement()
    {
        using var f=new F();f.AttachWeapons();var p=MustCreate(f);Settle(p,f);
        var nextDraw=f.NewWeaponDraw();f.Source->DrawData.WeaponData[0].DrawData.DrawObject=nextDraw;
        Check(!Suppress(f,p,f.Source),"new draw inherited old render lease before Tick");
        Eq(nextDraw->Flags,(byte)0x29,"refused draw query changed new render flags");
        Tick(p,f.Source);Check(Suppress(f,p,f.Source),"freshly collected draw did not gain suppression");
        var nextOwner=f.NewWeaponDraw();f.Source->DrawData.WeaponData[0].Weapon=(Weapon*)nextOwner;
        Check(!Suppress(f,p,f.Source),"same draw inherited a lease from a different weapon owner");
        Tick(p,f.Source);Check(Suppress(f,p,f.Source),"freshly collected weapon owner did not gain suppression");
        f.Source->DrawData.WeaponData[0].ModelId.Value=0x1122334455667788UL;
        Check(!Suppress(f,p,f.Source),"same draw/owner inherited a lease from a different model");
        Tick(p,f.Source);Check(Suppress(f,p,f.Source),"freshly collected model did not gain suppression");Dispose(p);
    }
    static void SuppressionLateWeapon()
    {
        using var f=new F();var p=MustCreate(f);Settle(p,f);
        Check(!Suppress(f,p,f.Source),"empty weapon slot suppressed merely because source body was hidden");
        var next=f.NewWeaponDraw();f.Source->DrawData.WeaponData[0].Weapon=(Weapon*)next;f.Source->DrawData.WeaponData[0].DrawData.DrawObject=next;
        Check(!Suppress(f,p,f.Source),"late weapon suppressed before its first visibility lease");
        Tick(p,f.Source);Check(Suppress(f,p,f.Source),"late weapon not suppressed after collection");Dispose(p);
    }
    static void Motion(object actor,bool emote=false)
    {
        var method=ActorType.GetMethod("UpdateGroundedMotion",All);
        Check(method!=null,"owned actor has no grounded animation writer");
        var args=method!.GetParameters().Length==3?new object?[]{emote,null,false}:new object?[]{emote,null};
        method.Invoke(actor,args);
    }
    static void MotionOwnedRoot()
    {
        using var f=new F();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);
        f.Source->Timeline.TimelineSequencer.TimelineIds[0]=13;f.Clone->Mount.MountId=446;
        byte[] source=Bytes(f.Source);
        Motion(p);Eq(f.Clone->Timeline.BaseOverride,(ushort)6709,"rider mounted walk pose");
        Eq(((Character*)f.PreviewChildren[0])->Timeline.BaseOverride,(ushort)13,"mount receives its own ground walk");
        Eq(f.Played.Count(x=>x.Actor==(nint)f.Clone&&x.Clip==6709),1,"rider playback");
        Eq(f.Played.Count(x=>x.Actor==f.PreviewChildren[0]&&x.Clip==13),1,"mount playback");
        Same(source,f.Source,"motion touched real player");
        Eq(f.SourceWrites,0,"native source writes");Dispose(p);
    }
    static void MotionLoop()
    {
        using var f=new F();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=446;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=22;
        Motion(p);Motion(p);Eq(f.PlayCalls,2,"held rider and mount loops did not restart");
        f.Source->Timeline.TimelineSequencer.TimelineIds[0]=3;Motion(p);Eq(f.Clone->Timeline.BaseOverride,(ushort)166,"stop uses rider mounted idle");Eq(((Character*)f.PreviewChildren[0])->Timeline.BaseOverride,(ushort)3,"stop uses mount idle");Eq(f.Clone->Mount.MountId,(ushort)446,"mount state preserved");Dispose(p);
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
    static void MotionCanonical()
    {
        foreach(var (sourceClip,mountClip,riderClip) in new (ushort,ushort,ushort)[]{(41,13,6709),(42,14,6709),(43,15,6709),(44,16,6709),(50,22,167),(51,23,167),(52,24,167),(12002,13,6709),(12009,23,167),(12012,31,6710),(12014,33,166),(25,22,167),(26,22,167),(27,22,167),(30,22,167),(58,22,167),(12011,22,167),(7,3,9306),(8,3,9307),(35,3,9306),(12007,3,9307)})
        {
            using var f=new F();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=73;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=sourceClip;
            Motion(p);Eq(f.Clone->Timeline.BaseOverride,riderClip,"rider pose for source "+sourceClip);
            Check(f.Played.Any(x=>x.Actor==f.PreviewChildren[0]&&x.Clip==mountClip),"missing canonical mount clip "+mountClip+" for source "+sourceClip);Dispose(p);
        }
    }
    static void MotionPoseIdle()
    {
        using var f=new F();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=73;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=7404;
        f.Source->Mode=CharacterModes.Normal;f.Source->EmoteController.EmoteId=0;f.Source->EmoteController.CurrentPoseType=0;f.Source->EmoteController.CPoseState=5;
        Motion(p);Eq(f.Clone->Timeline.BaseOverride,(ushort)166,"confirmed standing cpose is rider idle");Eq(((Character*)f.PreviewChildren[0])->Timeline.BaseOverride,(ushort)3,"confirmed standing cpose is mount idle");
        f.Played.Clear();f.Source->Timeline.TimelineSequencer.TimelineIds[0]=164;Motion(p);Eq(f.Played.Count,0,"teleport is never locomotion");
        Eq(f.Clone->Timeline.BaseOverride,(ushort)0,"unknown releases own rider pin");
        f.Source->Timeline.TimelineSequencer.TimelineIds[0]=7405;f.Source->EmoteController.EmoteId=71;Motion(p);Eq(f.Played.Count,0,"cpose-looking clip with active emote is unknown");
        f.Source->Timeline.TimelineSequencer.TimelineIds[0]=7404;f.Source->EmoteController.EmoteId=0;f.Source->Mode=(CharacterModes)3;Motion(p);Eq(f.Played.Count,0,"pose outside normal mode is unknown");
        f.Source->Mode=CharacterModes.Normal;f.Source->EmoteController.CurrentPoseType=(EmoteController.PoseType)1;Motion(p);Eq(f.Played.Count,0,"non-standing pose type is unknown");
        f.Source->EmoteController.CurrentPoseType=0;f.Source->EmoteController.CPoseState=0;Motion(p);Eq(f.Played.Count,0,"unset cpose is unknown");
        f.Source->EmoteController.CPoseState=5;Motion(p);Eq(f.Played.Count,2,"confirmed standing start resumes both idle lanes");Dispose(p);
    }
    static void MotionForeignChild()
    {
        using var f=new F();f.AttachChildren();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=73;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=13;
        var owned=f.Clone->Mount.MountObject;f.Clone->Mount.MountObject=f.Source->Mount.MountObject;byte[] source=Bytes(f.Source),foreign=Bytes(f.Source->Mount.MountObject),root=Bytes(f.Clone);
        Motion(p);Eq(f.PlayCalls,0,"foreign mount rejected before rider write");Same(source,f.Source,"source modified");Same(foreign,f.Source->Mount.MountObject,"foreign mount modified");Same(root,f.Clone,"rider written without own mount");
        f.Clone->Mount.MountObject=owned;owned->DrawObject->IsVisible=false;Motion(p);Eq(f.PlayCalls,0,"unready mount rejected");Dispose(p);
    }
    static void MotionChildGeneration()
    {
        using var f=new F();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=73;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=22;
        Motion(p);Eq(f.PlayCalls,2,"initial two lanes");var child=f.Clone->Mount.MountObject;F.Generation(child,777);child->Timeline.BaseOverride=0;
        Motion(p);Eq(f.PlayCalls,2,"new generation not yet collected");Tick(p,f.Source);Motion(p);Eq(f.PlayCalls,4,"collected generation receives two fresh edges");Dispose(p);
    }
    static void MotionReentry()
    {
        using var f=new F();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=73;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=13;
        byte[] child=Bytes(f.Clone->Mount.MountObject);f.OnPlay=()=>f.Manager->BattleCharas[F.Slot].BattleChara=f.Replacement;
        Motion(p);Eq(f.PlayCalls,1,"slot reentry stops later playback");Same(child,f.Clone->Mount.MountObject,"mount written after allocation reentry");Dispose(p);
    }
    static void MotionRequestGate()
    {
        using var f=new F();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=73;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=13;
        var method=ActorType.GetMethod("UpdateGroundedMotion",All)!;method.Invoke(p,[false,(Func<bool>)(()=>false),false]);Eq(f.PlayCalls,0,"cancelled request writes nothing");
        Motion(p);Eq(f.PlayCalls,2,"rejected plan did not consume either edge");Dispose(p);
    }
    static void MotionUnknown()
    {
        using var f=new F();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=73;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=13;
        Motion(p);f.Source->Timeline.TimelineSequencer.TimelineIds[0]=164;Motion(p);Eq(f.Clone->Timeline.BaseOverride,(ushort)0,"unknown releases rider owned pin");Eq(f.Clone->Mount.MountObject->Timeline.BaseOverride,(ushort)0,"unknown releases mount owned pin");
        f.Source->Timeline.TimelineSequencer.TimelineIds[0]=13;Motion(p);f.Clone->Timeline.BaseOverride=8194;f.Clone->Mount.MountObject->Timeline.BaseOverride=999;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=164;Motion(p);
        Eq(f.Clone->Timeline.BaseOverride,(ushort)8194,"foreign rider pin kept");Eq(f.Clone->Mount.MountObject->Timeline.BaseOverride,(ushort)999,"foreign mount pin kept");Dispose(p);
    }
    static void FlightMotion(object actor,bool active) {var method=ActorType.GetMethod("UpdateGroundedMotion",All)!;Check(method.GetParameters().Length==3,"flight appearance writer missing");method.Invoke(actor,[false,null,active]);}
    static void FlightAppearanceSource() {
        using var f=new F();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=327;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=22;
        byte[] source=Bytes(f.Source);FlightMotion(p,true);Eq(f.Clone->Timeline.BaseOverride,(ushort)4048,"flight rider loop");Eq(f.Clone->Mount.MountObject->Timeline.BaseOverride,(ushort)4043,"flight child loop");Same(source,f.Source,"flight appearance changed source bytes");Eq(f.SourceWrites,0,"flight appearance native source writes");
        var oldPosition=f.Clone->Position;var oldMode=f.Clone->Mode;FlightMotion(p,false);Eq(f.Clone->Timeline.BaseOverride,(ushort)167,"ground rider fresh edge");Eq(f.Clone->Mount.MountObject->Timeline.BaseOverride,(ushort)22,"ground mount fresh edge");Eq(f.Clone->Position,oldPosition,"appearance preserves clone position");Eq(f.Clone->Mode,oldMode,"appearance preserves clone mode");Same(source,f.Source,"ground toggle changed source bytes");Dispose(p);
    }
    static void FlightAppearanceForeign() {
        using var f=new F();f.AttachChildren();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=327;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=22;var child=f.Clone->Mount.MountObject;f.Clone->Mount.MountObject=f.Source->Mount.MountObject;
        byte[] source=Bytes(f.Source),foreign=Bytes(f.Source->Mount.MountObject);FlightMotion(p,true);Eq(f.PlayCalls,0,"flight writer refuses foreign mount");Same(source,f.Source,"foreign flight source touched");Same(foreign,f.Source->Mount.MountObject,"foreign flight child touched");f.Clone->Mount.MountObject=child;Dispose(p);
    }
    static void FlightAppearanceMountedSource() {
        foreach(var (sourceClip,rider,child) in new (ushort,ushort,ushort)[]{(166,4047,4040),(6709,4048,4053),(167,4048,4043)}) {
            using var f=new F();f.AttachChildren();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=327;f.Source->Mode=CharacterModes.Mounted;f.Source->Mount.MountId=73;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=sourceClip;
            byte[] source=Bytes(f.Source),realMount=Bytes(f.Source->Mount.MountObject);FlightMotion(p,true);Eq(f.Clone->Timeline.BaseOverride,rider,"mounted source rider "+sourceClip);Eq(f.Clone->Mount.MountObject->Timeline.BaseOverride,child,"mounted source child "+sourceClip);Same(source,f.Source,"mounted source bytes unchanged");Same(realMount,f.Source->Mount.MountObject,"real source mount unchanged");
            f.Source->Timeline.TimelineSequencer.TimelineIds[0]=6702;f.Played.Clear();FlightMotion(p,true);Eq(f.Played.Count,0,"unrelated eureka action remains unknown");Dispose(p);
        }
        using(var f=new F()){f.AttachChildren();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);f.Clone->Mount.MountId=327;f.Source->Mode=CharacterModes.Mounted;f.Source->Mount.MountId=73;f.Source->Mount.OwnerObject=f.Clone;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=167;FlightMotion(p,true);Eq(f.PlayCalls,0,"invalid source mounted container rejected");Dispose(p);}
    }
    static void FlightAppearanceNativeFlight()
    {
        foreach(bool flightPose in new[]{false,true})
        foreach(var (sourceClip,sourceChild,groundRider,groundChild,flightRider,flightChild) in new (ushort,ushort,ushort,ushort,ushort,ushort)[]{
            (4047,4040,166,3,4047,4040),(4048,4053,6709,13,4048,4053),(4048,4046,6709,16,4048,4046),
            (4048,4043,167,22,4048,4043),(4048,999,167,22,4048,4043),(6703,4049,167,22,4048,4043)})
        {
            using var f=new F();f.AttachChildren();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);
            f.Clone->Mount.MountId=327;f.Source->Mode=CharacterModes.Mounted;f.Source->Mount.MountId=335;
            f.Source->Timeline.TimelineSequencer.TimelineIds[0]=sourceClip;f.Source->Mount.MountObject->Timeline.TimelineSequencer.TimelineIds[0]=sourceChild;
            byte[] source=Bytes(f.Source),realMount=Bytes(f.Source->Mount.MountObject);
            var position=f.Clone->Position;var mode=f.Clone->Mode;
            FlightMotion(p,flightPose);
            Eq(f.Clone->Timeline.BaseOverride,flightPose?flightRider:groundRider,"confirmed native flight rider "+sourceClip+"/"+sourceChild);
            Eq(f.Clone->Mount.MountObject->Timeline.BaseOverride,flightPose?flightChild:groundChild,"confirmed native flight child "+sourceClip+"/"+sourceChild);
            Eq(f.Clone->Position,position,"animation sampling cannot change clone position");Eq(f.Clone->Mode,mode,"animation sampling cannot change clone mode");
            Same(source,f.Source,"native flight sampling changed genuine character bytes");Same(realMount,f.Source->Mount.MountObject,"native flight sampling changed genuine mount bytes");Eq(f.SourceWrites,0,"native flight sampling issued genuine actor native writes");
            Eq(f.PlayCalls,2,"native flight starts separate display lanes");FlightMotion(p,flightPose);Eq(f.PlayCalls,2,"native flight loop is sustained without restart");Dispose(p);
        }
    }
    static void FlightAppearanceNativeFlightMetadata()
    {
        foreach(var defect in new[]{"mode","mount id","mount owner","mount pointer","source owner","source parent","child owner","child parent","clone alias","minion alias"})
        {
            using var f=new F();f.AttachChildren();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);
            f.Clone->Mount.MountId=327;f.Source->Mode=CharacterModes.Mounted;f.Source->Mount.MountId=335;
            f.Source->Timeline.TimelineSequencer.TimelineIds[0]=4048;var realMount=f.Source->Mount.MountObject;realMount->Timeline.TimelineSequencer.TimelineIds[0]=4053;
            if(defect=="mode")f.Source->Mode=CharacterModes.Normal;
            else if(defect=="mount id")f.Source->Mount.MountId=0;
            else if(defect=="mount owner")f.Source->Mount.OwnerObject=f.Clone;
            else if(defect=="mount pointer")f.Source->Mount.MountObject=null;
            else if(defect=="source owner")f.Source->Timeline.OwnerObject=f.Clone;
            else if(defect=="source parent")f.Source->Timeline.TimelineSequencer.Parent=f.Clone;
            else if(defect=="child owner")realMount->Timeline.OwnerObject=f.Clone;
            else if(defect=="child parent")realMount->Timeline.TimelineSequencer.Parent=f.Clone;
            else if(defect=="clone alias")f.Source->Mount.MountObject=f.Clone->Mount.MountObject;
            else if(defect=="minion alias")f.Source->Mount.MountObject=(Character*)f.Source->CompanionData.CompanionObject;
            byte[] source=Bytes(f.Source),mount=Bytes(realMount),display=Bytes(f.Clone);
            FlightMotion(p,true);Eq(f.PlayCalls,0,"unconfirmed native flight "+defect+" must remain unknown");
            Same(source,f.Source,"invalid source flight metadata modified genuine character");Same(mount,realMount,"invalid source flight metadata modified genuine child");Same(display,f.Clone,"invalid source flight metadata wrote display animation");Dispose(p);
        }
    }
    static void FlightAppearanceNativeFlightUnknown()
    {
        foreach(ushort sourceClip in new ushort[]{0,6702,4050,4051,4057,4947,8194,4040,4043,4049})
        {
            using var f=new F();f.AttachChildren();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);
            f.Clone->Mount.MountId=327;f.Source->Mode=CharacterModes.Mounted;f.Source->Mount.MountId=335;
            f.Source->Timeline.TimelineSequencer.TimelineIds[0]=sourceClip;f.Source->Mount.MountObject->Timeline.TimelineSequencer.TimelineIds[0]=4043;
            byte[] source=Bytes(f.Source),mount=Bytes(f.Source->Mount.MountObject);FlightMotion(p,true);
            Eq(f.PlayCalls,0,"non-rider or event timeline must not be inferred as native flight input "+sourceClip);Same(source,f.Source,"unknown input changed source");Same(mount,f.Source->Mount.MountObject,"unknown input changed real mount");Dispose(p);
        }
    }
    static void FlightAppearanceNativeFlightReentry()
    {
        foreach(var defect in new[]{"generation","owner","id","pointer"})
        {
            using var f=new F();f.AttachChildren();f.AttachCloneChildren();var p=MustCreate(f);Settle(p,f);
            f.Clone->Mount.MountId=327;f.Source->Mode=CharacterModes.Mounted;f.Source->Mount.MountId=335;f.Source->Timeline.TimelineSequencer.TimelineIds[0]=4048;
            var realMount=f.Source->Mount.MountObject;realMount->Timeline.TimelineSequencer.TimelineIds[0]=4053;byte[] child=Bytes(f.Clone->Mount.MountObject);
            f.OnPlay=()=>{if(defect=="generation")F.Generation(realMount,9999);else if(defect=="owner")realMount->Timeline.OwnerObject=f.Clone;else if(defect=="id")f.Source->Mount.MountId=336;else f.Source->Mount.MountObject=(Character*)f.Source->CompanionData.CompanionObject;};
            FlightMotion(p,true);Eq(f.PlayCalls,1,"native source mount "+defect+" reentry must stop the second display lane");Same(child,f.Clone->Mount.MountObject,"display child touched after source mount reentry");Dispose(p);
        }
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
        public readonly List<nint> Children=[], PreviewChildren=[], PreviewDraws=[], SourceDraws=[], SourceWeapons=[], CloneWeapons=[], WeaponDraws=[]; public readonly List<uint> Flags=[];
        public readonly List<(nint Actor,ushort Clip)> Played=[];
        public Action? OnPlay;
        public bool Ready=true,ChildReady=true,DrawVisibleOnEnable=true,FailAllocate,FailLookup,ReplaceSlotDuringCopy,ReplaceSlotDuringChildEnable; public int Allocates,Deletes,Enables,ChildEnables,SourceWrites,UnownedNativeWrites,PlayCalls,NativeCalls;public ushort LastTimeline; public byte Param; public ulong? ResolvedTarget,ResolvedSoftTarget;
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
        void Init(Character* p,ushort index,uint eid,uint gen) { *(nint**)p=vtable; p->ObjectIndex=index; p->EntityId=eid; p->ObjectKind=ObjectKind.Pc; p->TargetableStatus=(ObjectTargetableFlags)11; Generation(p,gen); p->Mount.OwnerObject=p; p->CompanionData.OwnerObject=p; p->OrnamentData.OwnerObject=p; p->DrawData.OwnerObject=p; p->CharacterSetup.OwnerObject=p; p->Timeline.OwnerObject=p; p->Timeline.TimelineSequencer.Parent=p; }
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
        public void AttachWeapons()
        {
            SourceWeapons.AddRange(AttachWeaponsTo(Source));CloneWeapons.AddRange(AttachWeaponsTo(Clone));
        }
        public nint[] AttachWeaponsTo(Character* actor)
        {
            var draws=new nint[3];
            for(int i=0;i<3;i++)
            {
                var draw=NewWeaponDraw();draws[i]=(nint)draw;
                actor->DrawData.WeaponData[i].Weapon=(Weapon*)draw;
                actor->DrawData.WeaponData[i].DrawData.DrawObject=draw;
            }
            return draws;
        }
        public DrawObject* NewWeaponDraw()
        {
            var weapon=Alloc<Weapon>();weapon->Flags=0x29;WeaponDraws.Add((nint)weapon);return (DrawObject*)weapon;
        }
        public DrawObject* AttachExtraWeaponTo(Character* actor)
        {
            var draw=NewWeaponDraw();actor->DrawData.UnkWeaponData[0].Weapon=(Weapon*)draw;actor->DrawData.UnkWeaponData[0].DrawData.DrawObject=draw;return draw;
        }
        public static void Generation(Character* p,uint value)=>*(uint*)((byte*)p+0x74)=value;
        static void Track(GameObject* p) { if(p==(GameObject*)current.Source||p==(GameObject*)current.Replacement)current.SourceWrites++; }
        [UnmanagedCallersOnly] static ulong Identity(GameObject* p) { current.NativeCalls++;return ((ulong)p->EntityId<<32)|p->ObjectIndex; }
        [UnmanagedCallersOnly] static uint Allocate(ClientObjectManager* m,uint index,byte param) { current.NativeCalls++;current.Allocates++; current.Param=param; if(current.FailAllocate)return uint.MaxValue; m->BattleCharas[Slot].BattleChara=current.clone; m->BattleCharas[Slot].ObjectKind=ObjectKind.Pc; return Slot; }
        [UnmanagedCallersOnly] static Character* Lookup(ClientObjectManager* m,ushort index) { current.NativeCalls++;return current.FailLookup?null:(Character*)m->BattleCharas[index].BattleChara; }
        [UnmanagedCallersOnly] static uint Index(ClientObjectManager* m,GameObject* p) { current.NativeCalls++;return (GameObject*)m->BattleCharas[Slot].BattleChara==p?Slot:uint.MaxValue; }
        [UnmanagedCallersOnly] static void Delete(ClientObjectManager* m,ushort index,byte param) { current.NativeCalls++;current.Deletes++; m->BattleCharas[index].BattleChara=null; }
        [UnmanagedCallersOnly] static ulong Copy(CharacterSetupContainer* c,Character* source,uint flags)
        {
            current.NativeCalls++;
            if(current.Manager->BattleCharas[Slot].BattleChara!=(BattleChara*)c->OwnerObject)current.UnownedNativeWrites++;
            current.Flags.Add(flags); Track((GameObject*)c->OwnerObject); c->OwnerObject->ObjectKind=source->ObjectKind;
            if(current.ReplaceSlotDuringCopy)current.Manager->BattleCharas[Slot].BattleChara=current.Replacement;
            return 1;
        }
        [UnmanagedCallersOnly] static byte NativeReady(GameObject* p) { current.NativeCalls++;return (current.PreviewChildren.Contains((nint)p)?current.ChildReady:current.Ready)?(byte)1:(byte)0; }
        [UnmanagedCallersOnly] static void Enable(GameObject* p)
        {
            current.NativeCalls++;
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
        [UnmanagedCallersOnly] static void Disable(GameObject* p) { current.NativeCalls++;Track(p); p->DrawObject=null; }
        [UnmanagedCallersOnly] static DrawObject* GetDraw(GameObject* p) { current.NativeCalls++;return p->DrawObject; }
        [UnmanagedCallersOnly] static void Position(GameObject* p,float x,float y,float z) { current.NativeCalls++;Track(p); p->Position=new Vector3(x,y,z); }
        [UnmanagedCallersOnly] static void Rotation(GameObject* p,float value) { current.NativeCalls++;Track(p); p->Rotation=value; }
        [UnmanagedCallersOnly] static ulong Target(Character* p) { current.NativeCalls++;return p==current.Source?current.ResolvedTarget??p->TargetId.Id:p->TargetId.Id; }
        [UnmanagedCallersOnly] static ulong SoftTarget(Character* p) { current.NativeCalls++;return p==current.Source?current.ResolvedSoftTarget??p->SoftTargetId.Id:p->SoftTargetId.Id; }
        [UnmanagedCallersOnly] static void SetTarget(Character* p,ulong id) { current.NativeCalls++;Track((GameObject*)p); p->TargetId=id; }
        [UnmanagedCallersOnly] static void SetSoftTarget(Character* p,ulong id) { current.NativeCalls++;Track((GameObject*)p); p->SoftTargetId=id; }
        [UnmanagedCallersOnly] static void Play(ActionTimelineSequencer* sequence,ushort id,void* unknown) {current.NativeCalls++;current.PlayCalls++;current.LastTimeline=id;current.Played.Add(((nint)sequence->Parent,id));sequence->TimelineIds[0]=id;Track((GameObject*)sequence->Parent);current.OnPlay?.Invoke();}
        static T* Alloc<T>()where T:unmanaged=>(T*)NativeMemory.AllocZeroed((nuint)sizeof(T));
        public void Dispose() { foreach(nint child in Children.Concat(PreviewChildren).Concat(PreviewDraws).Concat(SourceDraws).Concat(WeaponDraws))NativeMemory.Free((void*)child); NativeMemory.Free(Manager); NativeMemory.Free(root); NativeMemory.Free(clone); NativeMemory.Free(reservedChild); NativeMemory.Free(Replacement); NativeMemory.Free(Local); NativeMemory.Free(vtable); NativeMemory.Free(bodyDraw); NativeMemory.Free(cloneDraw); }
    }
}
