using System.Buffers.Binary;
using FireGaze.PortraitPreview;

int checks = 0;
void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
var memory = new Memory();
var proof = new PortraitOwnedSceneCurrentTransportProof(memory);
var targets = memory.Targets;
Check(memory.Reads == 0 && memory.IdentityReads == 0, "Construction does not touch native memory or identity");
Check(!proof.VerifyEntry(targets[0]), "No unqualified per-entry verification");
Check(proof.TryValidate(targets, out var reason), reason);
Check(PortraitOwnedSceneCurrentTransportProof.Manifest.Count == 39, "39 exact target entries");
Check(PortraitOwnedSceneCurrentTransportProof.Manifest.Sum(t => t.Body.Sum(s => s.Hex.Length/2)) == 13180, "Full canonical bytes pinned");
Check(PortraitOwnedSceneCurrentTransportProof.Manifest.Sum(t => t.Body.Count) == 87, "All chained fragments pinned");
foreach (var target in targets) Check(proof.VerifyEntry(target), "All targets validate against original disk bytes");
var longTarget = PortraitOwnedSceneCurrentTransportProof.Manifest.First(t => t.Body[0].Hex.Length/2 > 200);
var altered = memory.Base+(nint)longTarget.Body[0].RVA+70;
memory.Flip(altered);
Check(!proof.VerifyEntry(new(longTarget.Kind, memory.Base+(nint)longTarget.RVA)), "Mutation beyond32-byte prefix rejected");
memory.Flip(altered);
var split = PortraitOwnedSceneCurrentTransportProof.Manifest.First(t => t.Body.Count > 1);
altered = memory.Base+(nint)split.Body[^1].RVA;
memory.Flip(altered);
Check(!proof.VerifyEntry(new(split.Kind,memory.Base+(nint)split.RVA)), "Final chained fragment mutation rejected");
memory.Flip(altered);
altered=memory.Base+(nint)split.Metadata[0].RVA;
memory.Flip(altered); Check(!proof.VerifyEntry(new(split.Kind,memory.Base+(nint)split.RVA)), "Raw unwind/pdata mutation rejected"); memory.Flip(altered);
altered=memory.Base+(nint)PortraitOwnedSceneCurrentTransportProof.VTables[0].RVA;
memory.Flip(altered); Check(!proof.VerifyEntry(targets[0]), "Live vtable mutation rejected"); memory.Flip(altered);
memory.Preserve=false; Check(!proof.VerifyEntry(targets[0]), "SDK/module identity drift rejected"); memory.Preserve=true;
memory.Executable=false; Check(!proof.VerifyEntry(targets[0]), "Nonexecutable target rejected"); memory.Executable=true;
memory.FailRead=true; Check(!proof.VerifyEntry(targets[0]), "Failed copies reject"); memory.FailRead=false;
memory.ShortRead=true; Check(!proof.VerifyEntry(targets[0]), "Short copies reject"); memory.ShortRead=false;
memory.ThrowRead=true; Check(!proof.VerifyEntry(targets[0]), "Copy exception fails closed"); memory.ThrowRead=false;
var wrong=targets.ToArray(); wrong[0]=wrong[0] with { Address=wrong[0].Address+1 };
Check(!proof.TryValidate(wrong,out _), "Interior entry rejected");
Check(!proof.VerifyEntry(targets[0]), "Failed qualification clears prior authorization");
Check(!proof.TryValidate(targets.Reverse().ToArray(),out _), "Wrong inventory order rejected");
Check(!proof.TryValidate(targets.Append(targets[0]).ToArray(),out _), "Duplicate inventory rejected");
Check(proof.TryValidate(targets,out reason),reason);
var tiny = PortraitOwnedSceneCurrentTransportProof.Manifest.Single(t => t.RVA==0x9DB610);
Check(tiny.Body.Single().Hex=="C20000CC", "3-byte body plus one boundary INT3, no invented14-byte function");
Check(tiny.Padding.Single().Hex.Length/2==13 && tiny.Near.Hex=="C20000CCCC" && tiny.Far.Hex=="C20000"+new string('C',22), "Independent padding permits exact5/14 candidates");
altered=memory.Base+0x9DB61F; memory.Flip(altered);
Check(!proof.VerifyEntry(new(tiny.Kind,memory.Base+(nint)tiny.RVA)),"Last padding byte checked"); memory.Flip(altered);
Action detour=()=>{};
foreach (var target in targets)
{
    memory.InstallDescriptor(target,1);
    Check(proof.VerifyCreatedHook(target,new FakeHook(target.Address),detour,out reason),"E9 returned descriptor: "+reason);
}
var nearTarget=targets[0]; memory.InstallDescriptor(nearTarget,1);
var good=memory.HookState;
void Bad(PortraitOwnedSceneCreatedHookState state,string name) { memory.HookState=state; Check(!proof.VerifyCreatedHook(nearTarget,new FakeHook(nearTarget.Address),detour,out _),name); memory.HookState=good; }
Bad(good with { Enabled=true },"Enabled at Create rejected");
Bad(good with { Disposed=true },"Disposed returned hook rejected");
Bad(good with { Target=good.Target+1 },"Wrong native target rejected");
Bad(good with { Destination=good.Destination+1 },"Wrong detour pointer rejected");
Bad(good with { Trampoline=good.Trampoline+1 },"Wrong original/trampoline rejected");
Bad(good with { OriginalEnd=good.OriginalEnd+1 },"Nonminimal actual overwritten span rejected");
Bad(good with { OriginalCapacity=good.OriginalEnd-1 },"Invalid vector bounds rejected");
Bad(good with { TrampolineSize=good.TrampolineSize+1 },"Unexpected trampoline length rejected");
Bad(good with { Kind=3 },"Unknown native hook kind rejected");
memory.Flip(good.OriginalBegin); Check(!proof.VerifyCreatedHook(nearTarget,new FakeHook(nearTarget.Address),detour,out _),"Saved original corruption rejected"); memory.Flip(good.OriginalBegin);
memory.Flip(good.Trampoline+(nint)good.TrampolineSize-1); Check(!proof.VerifyCreatedHook(nearTarget,new FakeHook(nearTarget.Address),detour,out _),"Trampoline destination corruption rejected"); memory.Flip(good.Trampoline+(nint)good.TrampolineSize-1);
Check(!proof.VerifyCreatedHook(nearTarget,new MissingInspection(nearTarget.Address),detour,out _),"No actual managed object inspection rejected");
Check(!proof.VerifyCreatedHook(nearTarget,new EmptyInspection(nearTarget.Address),detour,out _),"Inspection must actually expose the managed object");
Check(!proof.VerifyCreatedHook(nearTarget,new DoubleInspection(nearTarget.Address),detour,out _),"Inspection must expose exactly one managed object");
memory.Describe=false; Check(!proof.VerifyCreatedHook(nearTarget,new FakeHook(nearTarget.Address),detour,out _),"Unofficial managed descriptor rejected"); memory.Describe=true;
foreach(var target in targets)
{
    var entry=PortraitOwnedSceneCurrentTransportProof.Manifest.Single(t=>memory.Base+(nint)t.RVA==target.Address);
    memory.InstallDescriptor(target,2);
    Check(proof.VerifyCreatedHook(target,new FakeHook(target.Address),detour,out _)==entry.Far.Allowed,"FF permits only nonrelative relocation-free exact span");
}
Console.WriteLine($"Portrait owned entry proof: {checks} checks passed; disk-derived fixtures only, no native reads/hooks.");

sealed class Memory : IPortraitOwnedSceneProofMemory
{
    public nint Base=unchecked((nint)0x140000000L); public int Reads,IdentityReads; public bool Preserve=true,Executable=true,FailRead,ShortRead,ThrowRead,Describe=true;
    public Dictionary<nint,byte> Bytes=[]; public PortraitOwnedSceneCreatedHookState HookState;
    public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets=>PortraitOwnedSceneCurrentTransportProof.Manifest.Select(t=>new PortraitOwnedSceneNativeTarget(t.Kind,Base+(nint)t.RVA)).ToArray();
    public Memory()
    {
        foreach(var t in PortraitOwnedSceneCurrentTransportProof.Manifest) foreach(var s in t.Body.Concat(t.Metadata).Concat(t.Padding)) Put(Base+(nint)s.RVA,Convert.FromHexString(s.Hex));
        foreach(var table in PortraitOwnedSceneCurrentTransportProof.VTables)
        { var bytes=new byte[table.Targets.Count*8]; for(int i=0;i<table.Targets.Count;i++) BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i*8),Base+(nint)table.Targets[i]); Put(Base+(nint)table.RVA,bytes); }
    }
    public void Flip(nint at)=>Bytes[at]^=1;
    public void Put(nint at,byte[] bytes) {for(int i=0;i<bytes.Length;i++)Bytes[at+i]=bytes[i];}
    public bool TryQualify(out PortraitOwnedSceneProofIdentity identity) {IdentityReads++; identity=new(Base,PortraitOwnedSceneCurrentTransportProof.FrozenImageSize,"test"); return Preserve;}
    public bool PreserveIdentity(PortraitOwnedSceneProofIdentity identity) {IdentityReads++;return Preserve&&identity.ModuleBase==Base;}
    public bool TryRead(nint address,int size,out byte[] bytes)
    {
        Reads++; if(ThrowRead)throw new IOException(); bytes=[]; if(FailRead)return false;
        var result=new byte[size]; for(int i=0;i<size;i++)if(!Bytes.TryGetValue(address+i,out result[i]))return false;
        bytes=ShortRead?result[..^1]:result;return true;
    }
    public bool IsExecutable(nint address,int size)=>Executable;
    public bool TryDescribeOfficialHook(object hook,Delegate stableDetour,out PortraitOwnedSceneCreatedHookState state) {state=HookState;return Describe;}
    public void InstallDescriptor(PortraitOwnedSceneNativeTarget target,int kind)
    {
        var t=PortraitOwnedSceneCurrentTransportProof.Manifest.Single(t=>Base+(nint)t.RVA==target.Address);
        var c=kind==1?t.Near:t.Far; var original=Convert.FromHexString(c.Hex); nint trampoline=Base+0x4000000, saved=Base+0x5000000,dest=Base+0x6000000;
        var tail=kind==1?19:14; var bytes=new byte[original.Length+tail];original.CopyTo(bytes,0);
        foreach(var fix in c.Relocations) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(fix.Offset),checked((int)(Base+(nint)fix.TargetRVA-trampoline-fix.InstructionEnd)));
        var e=original.Length;
        if(kind==1){bytes[e]=0xE9;BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(e+1),checked((int)(target.Address+e-trampoline-e-5)));bytes[e+5]=0xFF;bytes[e+6]=0x25;BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(e+11),dest);}
        else {bytes[e]=0xFF;bytes[e+1]=0x25;BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(e+6),target.Address+e);}
        Put(saved,original);Put(trampoline,bytes);
        HookState=new(Base+0x7000000,target.Address,dest,trampoline,saved,saved+original.Length,saved+original.Length,(nuint)bytes.Length,kind,false,false,trampoline,dest);
    }
}
class MissingInspection(nint address) : IPortraitNativePhaseHostHook
{public nint Address=>address;public string BackendName=>"SafetyHook";public bool IsEnabled=>false;public bool IsDisposed=>false;public Delegate Original=>(Action)(()=>{});public void Enable(){} public void Disable(){} public void Dispose(){} }
sealed class FakeHook(nint address) : MissingInspection(address),IPortraitNativeHookInspection {public bool TryInspect(Func<object,bool> inspect)=>inspect(this);}
sealed class EmptyInspection(nint address) : MissingInspection(address),IPortraitNativeHookInspection {public bool TryInspect(Func<object,bool> inspect)=>true;}
sealed class DoubleInspection(nint address) : MissingInspection(address),IPortraitNativeHookInspection {public bool TryInspect(Func<object,bool> inspect){inspect(this);inspect(this);return true;}}
