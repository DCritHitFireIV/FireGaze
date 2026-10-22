using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using System.Runtime.InteropServices;
using System.Reflection;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using AgentInterfacePtr = Dalamud.Game.NativeWrapper.AgentInterfacePtr;
using Dalamud.Plugin.Services;
using AgentId = Dalamud.Game.Agent.AgentId;

unsafe class Program
{
 static int passed, failed;
 static void Check(bool ok,string text) { if(ok)passed++;else failed++;Console.WriteLine((ok?"PASS ":"FAIL ")+text); }
 static bool EventIntercept(NativePortraitTarget target,nint editor,nint values,uint count,ulong kind)
  => target.InterceptSaveEvent(editor,values,count,kind);
 static AgentReceiveEventArgs EventArgs(nint editor,nint values,nint result,uint count=2,ulong kind=0)
 {
  var args=(AgentReceiveEventArgs)Activator.CreateInstance(typeof(AgentReceiveEventArgs),true)!;
  typeof(AgentArgs).GetProperty(nameof(AgentArgs.Agent))!.SetValue(args,new AgentInterfacePtr(editor));
  typeof(AgentArgs).GetProperty(nameof(AgentArgs.AgentId))!.SetValue(args,AgentId.BannerEditor);
  args.AtkValues=values;args.ReturnValue=result;args.ValueCount=count;args.EventKind=kind;return args;
 }
 static bool Prevented(AgentArgs args)=>(bool)typeof(AgentArgs).GetProperty("PreventOriginalRequested",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(args)!;
 static int Main()
 {
  using var f=new Fixture();bool block=false;int errors=0;using var t=new NativePortraitTarget(f, _=>block, _=>errors++);
  Check(t.TryResolve(out var id,out _,out var open),"valid isolated current portrait resolves");
  Check(open==AgentBannerEditorState.EditorOpenType.Gearset,"gearset editor is supported");
  f.Real->DrawData.GlassesIds[0]=71;
  Check(t.CaptureLocalFacewear()==71 && f.View->PortraitCharacterData.GlassesIds[0]==42 && f.Calls==0,
   "actual facewear baseline reads the real player's typed DrawData field without changing the portrait or invoking native setters");
  f.Framework=false;
  Check(t.CaptureLocalFacewear() is null,"actual facewear cannot be captured off the Framework thread");f.Framework=true;
  f.Local=null;
  Check(t.CaptureLocalFacewear() is null,"missing real player leaves the facewear baseline unknown rather than empty");f.Local=f.Real;
  f.View->Agent=null;f.View->CharacterLoaded=false;
  Check(t.TryResolve(out _,out _,out _) && t.IsCurrent(id),"sampled CN portrait with null optional agent and base-loaded false resolves using portrait-loaded flag");
  f.View->Agent=(AgentInterface*)f.Real;
  Check(!t.TryResolve(out _,out _,out _),"nonnull foreign optional view agent is rejected");
  f.View->Agent=(AgentInterface*)f.Editor;f.View->CharacterLoaded=true;
  var look=t.Capture(id)!;
  Check(look.Pieces[5].ItemID==106 && look.Pieces[11].ItemID==112,"capture maps native waist gap and reads records by SlotId");
  Check(look.Pieces[3].ItemID==903 && look.Pieces[3].Stain0==5 && look.Pieces[3].Stain1==6,"capture reflects glamour appearance and both actual glamour stains");
  Check(look.Pieces[12].ItemID==42,"facewear captures Glasses row rather than item row");
  block=true;
  Check(t.ShouldBlockSave((nint)f.State),"active preview owns save guard");
  AtkValue* saveEvent=stackalloc AtkValue[2];
  saveEvent[0]=new(){Type=AtkValueType.Int,Int=0};saveEvent[1]=new(){Type=AtkValueType.Int,Int=9};
  Check(EventIntercept(t,(nint)f.Editor,(nint)saveEvent,2,0),
   "exact native two-int save event is stopped before dirty state clearing and success message");
  Check(!EventIntercept(t,(nint)f.Editor+8,(nint)saveEvent,2,0)
   && !EventIntercept(t,(nint)f.Editor,(nint)saveEvent,2,1)
   && !EventIntercept(t,(nint)f.Editor,(nint)saveEvent,1,0)
   && !EventIntercept(t,(nint)f.Editor,0,2,0),"unrelated editor kind count or null values never intercept as a save event");
  saveEvent[1].Int=8;
  Check(!EventIntercept(t,(nint)f.Editor,(nint)saveEvent,2,0),"unknown event values pass through");
  saveEvent[1].Int=9;saveEvent[1].Type=AtkValueType.UInt;
  Check(!EventIntercept(t,(nint)f.Editor,(nint)saveEvent,2,0),"unknown event value types pass through");
  saveEvent[1].Type=AtkValueType.Int;
  f.Active=false;f.BoundsAvailable=false;
  Check(t.IsCurrent(id) && !t.TryResolve(out _,out _,out _) && t.ShouldBlockSave((nint)f.State),
   "temporarily inactive hidden editor keeps its allocation and save guard while denying new capture");
  f.Active=true;f.BoundsAvailable=true;
  f.View->CharacterLoaded=false;f.View->CharaViewPortraitCharacterLoaded=false;
  Check(t.IsCurrent(id) && !t.TryResolve(out _,out _,out _) && t.ShouldBlockSave((nint)f.State),"draw loading keeps current session and save guard while denying new capture");
  Check(!t.ShouldBlockSave((nint)f.State+8),"unrelated save state passes through");
  block=false;Check(!t.ShouldBlockSave((nint)f.State),"closed preview releases save guard");
  f.View->CharacterLoaded=true;f.View->CharaViewPortraitCharacterLoaded=true;
  f.Framework=false;
  Check(!t.IsCurrent(id) && t.Capture(id)==null && !t.Apply(id,look),"worker thread cannot read or mutate native target");f.Framework=true;
  f.Local=f.Actor;
  Check(!t.TryResolve(out _,out _,out _) && !t.Apply(id,look) && f.Calls==0,"world player is never a preview target");f.Local=f.Real;
  f.View->ModelData.CustomizeData.Race=1;
  var next=look.Copy();next.Pieces[3]=new(777,9,10);next.Pieces[2]=default;next.Pieces[12]=new(33,0,0);
  Check(t.Apply(id,next),"wardrobe with clear, double dye and glasses applies");
  Check(f.Calls==2 && f.Cleared==0,"zero item never reaches SetItemSlotData and only changed nonzero item plus facewear invoke native APIs");
  Check(!f.View->Items.ToArray().Any(x=>x.SlotId==2),"clear removes matching native record");
  Check(f.View->Items.ToArray().Any(x=>x.SlotId==3 && x.ItemId==777 && x.Stain0Id==9 && x.Stain1Id==10),"double dye reaches target item record");
  Check(f.View->PortraitCharacterData.GlassesIds[0]==33 && f.Actor->DrawData.GlassesIds[0]==33,"glasses local caches and isolated actor stay aligned");
  Check(f.View->CameraYaw==1.5f && f.View->CameraDistance==2.5f && f.View->AmbientLightingBrightness==65 && f.View->ModelData.CustomizeData.Race==1,"apply preserves camera light and customize state");
  Check(t.Apply(id,look),"captured look restores");
  Check(f.View->Items.ToArray().Any(x=>x.SlotId==3 && x.ItemId==103 && x.GlamourItemId==903 && x.GlamourStain1Id==6),"restore retains original physical and glamour metadata");
  Check(f.Actor->DrawData.GlassesIds[0]==42,"restore retains original glasses");
  var calls=f.Calls;f.Generation++;
  Check(!t.Apply(id,next) && f.Calls==calls,"same address new generation rejects stale write");f.Generation--;
  f.State->OpenType=AgentBannerEditorState.EditorOpenType.AdventurerPlate;
  Check(t.TryResolve(out var plate,out _,out _) && t.IsCurrent(plate),"adventurer plate editor shares guarded adapter");
  f.State->AgentBannerEditor=null;
  Check(!t.IsCurrent(id),"wrong editor state owner rejects writes");f.State->AgentBannerEditor=f.Editor;
  f.Actor->DrawData.OwnerObject=f.Real;
  Check(!t.IsCurrent(id),"wrong DrawData owner rejects writes");f.Actor->DrawData.OwnerObject=f.Actor;
  f.OnLookup=()=>f.Editor->EditorState=null;
  Check(!t.IsCurrent(id),"reentrant last character lookup replacing editor rejects stale target");f.OnLookup=null;f.Editor->EditorState=f.State;
  Check(!t.Apply(id,new(new PortraitPiece[12],true,true,false)),"malformed look rejected before mutation");
  var tooLarge=look.Copy();tooLarge.Pieces[12]=new(70000,0,0);
  Check(!t.Apply(id,tooLarge),"out of range glasses rejected before mutation");
  f.State->OpenType=AgentBannerEditorState.EditorOpenType.Gearset;
  t.Capture(id);f.ThrowItem=true;var errorsBefore=errors;
  Check(!t.Apply(id,next) && errors==errorsBefore+1,"managed native failure reports once and rolls back current target");
  Check(f.View->Items.ToArray().Any(x=>x.SlotId==3 && x.ItemId==103 && x.GlamourItemId==903),"rollback retains physical and glamour metadata");f.ThrowItem=false;
  f.ReplaceOnItem=true;var previousGlasses=f.Actor->DrawData.GlassesIds[0];
  Check(!t.Apply(id,next) && f.Actor->DrawData.GlassesIds[0]==previousGlasses,"reentrant model replacement stops later glasses write without restoring over new allocation");f.ReplaceOnItem=false;f.Editor->EditorState=f.State;
  using(var notifyFailure=new NativePortraitTarget(f,_=>true,_=>throw new InvalidOperationException("chat hook rejected")))
  {
   notifyFailure.Capture(id);
   Check(notifyFailure.InterceptSave((nint)f.State) && notifyFailure.BlockedSaves==1 && notifyFailure.LastError!=null,
    "throwing chat notification remains intercepted and cannot escape native save detour");
   Check(!notifyFailure.InterceptSave((nint)f.State+8),"notification failure does not intercept unrelated save");
   Check(EventIntercept(notifyFailure,(nint)f.Editor,(nint)saveEvent,2,0),
    "throwing notification remains intercepted for native save events");
  }
  using(var predicateFailure=new NativePortraitTarget(f,_=>throw new InvalidOperationException("predicate failed"),_=>{}))
  {
   predicateFailure.Capture(id);
   Check(predicateFailure.InterceptSave((nint)f.State),"throwing guard callback fails closed for captured editor owner");
   Check(!predicateFailure.InterceptSave((nint)f.State+8),"throwing guard does not claim unrelated editor state");
  }
  var lifecycle=new Lifecycle();var eventTarget=new NativePortraitTarget(f,_=>true,_=>throw new InvalidOperationException("chat rejected"));
  eventTarget.Capture(id);eventTarget.RegisterSaveEvents(lifecycle);eventTarget.RegisterSaveEvents(lifecycle);
  Check(lifecycle.Registrations==1 && lifecycle.Event==AgentEvent.PreReceiveEvent && lifecycle.Agent==AgentId.BannerEditor,
   "official pre-receive listener registers once for BannerEditor");
  Check(!eventTarget.CanBlockSave,"event listener alone cannot authorize preview without the native save hook");
  AtkValue returnValue=default;
  var saveArgs=EventArgs((nint)f.Editor,(nint)saveEvent,(nint)(&returnValue));lifecycle.Handler!(AgentEvent.PreReceiveEvent,saveArgs);
  Check(Prevented(saveArgs) && eventTarget.BlockedSaveEvents==1 && saveArgs.ReturnValue==(nint)(&returnValue),
   "official callback prevents exact save even when notification throws and preserves caller return buffer");
  var otherArgs=EventArgs((nint)f.Real,(nint)saveEvent,(nint)(&returnValue));lifecycle.Handler!(AgentEvent.PreReceiveEvent,otherArgs);
  Check(!Prevented(otherArgs) && eventTarget.BlockedSaveEvents==1,"official callback never prevents a foreign editor event");
  var noResult=EventArgs((nint)f.Editor,(nint)saveEvent,0);lifecycle.Handler!(AgentEvent.PreReceiveEvent,noResult);
  Check(!Prevented(noResult),"callback without a valid return buffer is not treated as a native save event");
  eventTarget.Dispose();eventTarget.Dispose();
  Check(lifecycle.Unregistrations==1 && lifecycle.Handler==null,"disposing native target unregisters the exact official event listener once");
  f.Framework=false;
  Check(!t.AbortCapturedEditor() && f.HideCalls==0,"emergency teardown cannot hide editor on a worker thread");f.Framework=true;
  f.Generation++;
  Check(t.AbortCapturedEditor() && f.HideCalls==0,"replaced allocation is never closed as an old preview teardown");f.Generation--;
  f.RefuseHide=true;
  Check(!t.AbortCapturedEditor() && f.HideCalls==1 && f.OriginalHideCalls==0,
   "blocked ordinary hide with unavailable original table remains a teardown failure without calling an unknown entry");f.RefuseHide=false;
  Check(t.AbortCapturedEditor() && f.HideCalls==2 && f.Editor->EditorState==null,"emergency teardown closes exact captured native editor after restore failure");
  f.Editor->EditorState=f.State;f.ReplaceOwnerOnHide=true;f.OriginalHideAvailable=true;
  Check(t.AbortCapturedEditor() && f.OriginalHideCalls==0,
   "ordinary hide replacing the captured state owner never forwards original hide to the replacement");
  f.ReplaceOwnerOnHide=false;f.State->AgentBannerEditor=f.Editor;
  f.Editor->EditorState=f.State;f.RefuseHide=true;f.OriginalHideAvailable=true;f.RefuseOriginalHide=true;
  Check(!t.AbortCapturedEditor() && f.OriginalHideCalls==1 && f.Editor->EditorState==f.State,
   "unsuccessful original native hide remains a teardown failure");
  f.RefuseOriginalHide=false;
  Check(t.AbortCapturedEditor() && f.OriginalHideCalls==2 && f.Editor->EditorState==null,
   "emergency teardown uses verified original native hide when a PreHide listener blocks ordinary hide");
  AbortUnreadyCases();
  PreviewSessionUnreadyCases();
  NativeFailureNoticeCases.Run(Check);
  using(var owner=new PlateOwnerFixture())
  {
   var binding=owner.Binding;
   owner.Framework=false;
   Check(!PortraitPlateOwnerClose.TryClose(owner,binding) && owner.OwnerReads==0 && owner.HideCalls==0,
    "plate unload cannot inspect or hide native owner from a worker thread");
   owner.Framework=true;owner.Current=false;
   Check(!PortraitPlateOwnerClose.TryClose(owner,binding) && owner.OwnerReads==0,
    "stale plate lease never inspects a saved card pointer");
   owner.Current=true;owner.Card->Data=owner.OtherStorage;
   Check(!PortraitPlateOwnerClose.TryClose(owner,binding) && owner.HideCalls==0,
    "plate unload rejects the same agent after its storage owner changes");
   owner.Card->Data=owner.Storage;owner.Storage->ContentId++;
   Check(!PortraitPlateOwnerClose.TryClose(owner,binding) && owner.HideCalls==0,
    "plate unload never closes another character's plate at the same native addresses");
   owner.Storage->ContentId--;owner.RefuseHide=true;
   Check(!PortraitPlateOwnerClose.TryClose(owner,binding) && owner.HideCalls==1 && owner.OriginalCalls==0,
    "blocked ordinary plate hide without an original table returns failure rather than guessing an entry point");
   owner.OriginalAvailable=true;owner.RefuseOriginal=true;
   Check(!PortraitPlateOwnerClose.TryClose(owner,binding) && owner.OriginalCalls==1,
    "a refused original plate hide remains a disposal restoration failure");
   owner.RefuseOriginal=false;
   Check(PortraitPlateOwnerClose.TryClose(owner,binding) && owner.OriginalCalls==2 && !owner.Current,
    "verified original native hide closes the captured local plate when ordinary hide is prevented");
   owner.Current=true;owner.Card->Data=owner.Storage;owner.ReplaceOnHide=true;
   Check(!PortraitPlateOwnerClose.TryClose(owner,binding) && owner.OriginalCalls==2,
    "ordinary hide changing storage ownership never forwards original hide to the new owner");
   owner.Card->Data=owner.Storage;owner.ReplaceOnHide=false;owner.RefuseHide=false;
   Check(PortraitPlateOwnerClose.TryClose(owner,binding) && owner.OriginalCalls==2,
    "successful ordinary plate hide never unnecessarily invokes the original table");
  }
  var disposedTarget=new NativePortraitTarget(f,_=>false,_=>errors++);
  f.Editor->EditorState=f.State;
  disposedTarget.Capture(id);
  f.Framework=false;disposedTarget.Dispose();f.Framework=true;
  Check(disposedTarget.IsCurrent(id),"off-thread native target dispose cannot drop a still owned preview lease");
  disposedTarget.Dispose();
  var mirrorAPI=new RefusalMirrorApi();var failedMirror=new PortraitPlateMirror(mirrorAPI);
  failedMirror.Update(new(1,2,3,4,5),new PortraitEditorSnapshot(),true);mirrorAPI.RefuseWrite=true;
  var notifications=0;
  var mirrorTarget=new NativePortraitTarget(f,_=>false,_=>notifications++);
  typeof(NativePortraitTarget).GetField("plateMirror",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(mirrorTarget,failedMirror);
  f.Framework=false;mirrorTarget.Dispose();f.Framework=true;
  Check(mirrorTarget.IsPlateMirrorActive && mirrorTarget.PlateMirrorDisposalRestored && mirrorAPI.Releases==0 && notifications==0,
   "off-thread target disposal preserves an active mirror lease and its pending error state for framework cleanup");
  mirrorTarget.Dispose();mirrorTarget.Dispose();
  Check(!mirrorTarget.PlateMirrorDisposalRestored && mirrorTarget.LastError is not null && notifications==1
   && !mirrorTarget.IsPlateMirrorActive && mirrorAPI.Releases==1,
   "native target reports failed mirror restoration even after detaching its lease during unload");
  mirrorAPI=new RefusalMirrorApi();failedMirror=new PortraitPlateMirror(mirrorAPI);notifications=0;
  mirrorTarget=new NativePortraitTarget(f,_=>false,_=>notifications++);
  typeof(NativePortraitTarget).GetField("plateMirror",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(mirrorTarget,failedMirror);
  mirrorAPI.AfterInstallImage=()=>{mirrorAPI.RefuseWrite=true;mirrorTarget.Dispose();};
  _=failedMirror.Update(new(1,2,3,4,5),new PortraitEditorSnapshot(),true);
  Check(!mirrorTarget.PlateMirrorDisposalRestored && mirrorTarget.LastError is not null && notifications==1
   && !failedMirror.DisposalRestored && mirrorAPI.Releases==1,
   "deferred mirror disposal reports its final failure to the target after reentrant native teardown returns");
  t.Dispose();Check(!t.IsCurrent(id) && !t.ShouldBlockSave((nint)f.State),"disposed adapter cannot touch native pointers or block saves");
  Console.WriteLine($"{passed}/{passed+failed} portrait native boundary cases passed");return failed==0?0:1;
 }
 static void PreviewSessionUnreadyCases()
 {
  foreach(var missing in new[]{"view state","local player","character"})
  {
   using var f=new Fixture();PortraitPreviewSession? session=null;
   using var t=new NativePortraitTarget(f,identity=>session?.BlockSave(identity)==true,_=>{});
   session=new(t);t.TryResolve(out var owner,out _,out _);session.Open(owner);
   if(missing=="view state")f.View->State=5;else if(missing=="local player")f.Local=null;else f.ReturnNoCharacter=true;
   session.Validate();
   Check(session.IsActive && session.BlockSave(owner) && t.ShouldBlockSave((nint)f.State),
    $"{missing}: actual session validation retains native Save detour protection during same-owner unreadiness");
   var calls=f.Calls;
   Check(!session.Close() && session.IsActive && f.Calls==calls && t.ShouldBlockSave((nint)f.State),
    $"{missing}: close waits for a ready owned portrait and does not silently release save protection");
   f.View->State=6;f.Local=f.Real;f.ReturnNoCharacter=false;
   session.Validate();
   Check(session.IsActive && session.Close() && !session.IsActive && !t.ShouldBlockSave((nint)f.State),
    $"{missing}: recovery restores the original wardrobe and only then permits native saving");
  }
  using(var f=new Fixture())
  {
   PortraitPreviewSession? session=null;using var t=new NativePortraitTarget(f,identity=>session?.BlockSave(identity)==true,_=>{});
   session=new(t);t.TryResolve(out var owner,out _,out _);session.Open(owner);
   f.Editor->EditorState=null;session.Validate();
   Check(!session.IsActive && !session.BlockSave(owner) && session.Close(),
    "actual native state detachment retires the lease without touching a successor");
  }
 }
 static void AbortUnreadyCases()
 {
  foreach(var missing in new[]{"view state","local player","character"})
  {
   using var f=new Fixture();using var t=new NativePortraitTarget(f,_=>true,_=>{});
   t.TryResolve(out var owner,out _,out _);t.Capture(owner);
   if(missing=="view state")f.View->State=5;else if(missing=="local player")f.Local=null;else f.ReturnNoCharacter=true;
   Check(!t.IsCurrent(owner)&&t.ShouldBlockSave((nint)f.State),$"{missing}: unreadiness retains exact editor-state save lease");
   Check(t.AbortCapturedEditor()&&f.HideCalls==1&&f.Editor->EditorState==null,
    $"{missing}: unload really closes the retained editor instead of claiming it was destroyed");
  }
  using(var f=new Fixture())using(var t=new NativePortraitTarget(f,_=>true,_=>{}))
  {
   t.TryResolve(out var owner,out _,out _);t.Capture(owner);f.View->State=5;f.RefuseHide=true;f.OriginalHideAvailable=true;
   Check(t.AbortCapturedEditor()&&f.HideCalls==1&&f.OriginalHideCalls==1&&f.Editor->EditorState==null,
    "unready editor falls back to verified original hide on its unchanged state lease");
  }
  using(var f=new Fixture())using(var t=new NativePortraitTarget(f,_=>true,_=>{}))
  {
   t.TryResolve(out var owner,out _,out _);t.Capture(owner);f.ReturnNoEditor=true;
   Check(!t.AbortCapturedEditor()&&f.HideCalls==0,"missing editor lookup is uncertainty and cannot report successful unload cleanup");
   f.ReturnNoEditor=false;
   Check(t.ShouldBlockSave((nint)f.State)&&t.AbortCapturedEditor()&&f.HideCalls==1,
    "temporarily unreadable editor is still blocked and can be retried when lookup returns");
  }
  using(var f=new Fixture())using(var t=new NativePortraitTarget(f,_=>true,_=>{}))
  {
   t.TryResolve(out var owner,out _,out _);t.Capture(owner);f.OnHide=()=>f.Framework=false;
   Check(!t.AbortCapturedEditor()&&f.HideCalls==1,"losing Framework during native hide cannot turn an unreadable lease into success");
   f.Framework=true;
  }
  using(var f=new Fixture())
  {
   var t=new NativePortraitTarget(f,_=>true,_=>{});t.TryResolve(out var owner,out _,out _);t.Capture(owner);t.Dispose();
   Check(!t.AbortCapturedEditor()&&f.HideCalls==0,"disposed target cannot prove cleanup or hide its former editor");
  }
 }
}

unsafe sealed class RefusalMirrorApi : IPortraitPlateMirrorApi
{
 public bool RefuseWrite,ThrowClose;public int Releases;public Action? AfterInstallImage;
 private AtkTexture texture=Make(10);
 private static AtkTexture Make(nint value)=>new(){KernelTexture=(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture*)value,TextureType=TextureType.KernelTexture};
 public bool IsFrameworkThread=>true;
 public bool IsEditorCurrent(PortraitIdentity identity)=>true;
 public nint GetSourceTexture(PortraitIdentity identity)=>100;
 public PortraitPlateImageState GetSourceImage(PortraitIdentity identity)=>new(0,0,256,420,true);
 public bool TryResolve(PortraitIdentity identity,out PortraitPlateBinding binding){binding=new(1,2,3,4,5,6,7,8,9);return true;}
 public bool IsCurrent(PortraitPlateBinding binding)=>true;
 public AtkTexture ReadTexture(PortraitPlateBinding binding)=>texture;
 public PortraitPlateImageState ReadImage(PortraitPlateBinding binding)=>new(0,0,256,420,true);
 public bool TryWriteImage(PortraitPlateBinding binding,PortraitPlateImageState expected,PortraitPlateImageState replacement)
 {var callback=AfterInstallImage;AfterInstallImage=null;callback?.Invoke();return true;}
 public AtkTexture CreateRetainedTexture(PortraitIdentity identity,nint source)=>Make(source);
 public bool TryWriteTexture(PortraitPlateBinding binding,AtkTexture expected,AtkTexture replacement){if(RefuseWrite)return false;texture=replacement;return true;}
 public void ReleaseTexture(ref AtkTexture value){Releases++;value=default;}
 public bool TryCloseOwner(PortraitPlateBinding binding){if(ThrowClose)throw new InvalidOperationException("mirror close diagnostic sentinel");return false;}
}

unsafe sealed class PlateOwnerFixture : IPortraitPlateOwnerApi,IDisposable
{
 public AgentCharaCard* Card=(AgentCharaCard*)NativeMemory.AllocZeroed((nuint)sizeof(AgentCharaCard));
 public AgentCharaCard.Storage* Storage=(AgentCharaCard.Storage*)NativeMemory.AllocZeroed((nuint)sizeof(AgentCharaCard.Storage));
 public AgentCharaCard.Storage* OtherStorage=(AgentCharaCard.Storage*)NativeMemory.AllocZeroed((nuint)sizeof(AgentCharaCard.Storage));
 public bool Framework=true,Current=true,RefuseHide,OriginalAvailable,RefuseOriginal,ReplaceOnHide;
 public int OwnerReads,HideCalls,OriginalCalls;
 public PortraitPlateBinding Binding;
 public PlateOwnerFixture()
 {
  Card->Data=Storage;Card->AddonId=12;Storage->AccountId=17;Storage->ContentId=18;
  Binding=new(11,12,13,14,15,(nint)Storage,17,18,19);
 }
 public bool IsFrameworkThread=>Framework;
 public bool IsCurrent(PortraitPlateBinding binding)=>Current;
 public AgentCharaCard* GetOwner(){OwnerReads++;return Card;}
 public void HideOwner(AgentCharaCard* owner){HideCalls++;if(ReplaceOnHide){owner->Data=OtherStorage;return;}if(!RefuseHide)Current=false;}
 public bool TryHideOriginalOwner(AgentCharaCard* owner,PortraitPlateBinding binding)
 {if(!OriginalAvailable)return false;OriginalCalls++;if(!RefuseOriginal)Current=false;return true;}
 public void Dispose(){NativeMemory.Free(Card);NativeMemory.Free(Storage);NativeMemory.Free(OtherStorage);}
}

sealed class Lifecycle : IAgentLifecycle
{
 public int Registrations,Unregistrations;public AgentEvent Event;public AgentId Agent;public IAgentLifecycle.AgentEventDelegate? Handler;
 public void RegisterListener(AgentEvent type,AgentId id,IAgentLifecycle.AgentEventDelegate handler) {Registrations++;Event=type;Agent=id;Handler=handler;}
 public void RegisterListener(AgentEvent type,IEnumerable<AgentId> ids,IAgentLifecycle.AgentEventDelegate handler)=>throw new NotSupportedException();
 public void RegisterListener(AgentEvent type,IAgentLifecycle.AgentEventDelegate handler)=>throw new NotSupportedException();
 public void UnregisterListener(AgentEvent type,AgentId id,IAgentLifecycle.AgentEventDelegate handler) {Unregistrations++;if(type==Event&&id==Agent&&handler==Handler)Handler=null;}
 public void UnregisterListener(AgentEvent type,IEnumerable<AgentId> ids,IAgentLifecycle.AgentEventDelegate handler)=>throw new NotSupportedException();
 public void UnregisterListener(AgentEvent type,IAgentLifecycle.AgentEventDelegate handler)=>throw new NotSupportedException();
 public void UnregisterListener(params IAgentLifecycle.AgentEventDelegate[] handlers)=>throw new NotSupportedException();
 public nint GetOriginalVirtualTable(nint address)=>0;
}

unsafe sealed class Fixture : IPortraitNativeApi,IDisposable
{
 public AgentBannerEditor* Editor=(AgentBannerEditor*)NativeMemory.AllocZeroed((nuint)sizeof(AgentBannerEditor));
 public AgentBannerEditorState* State=(AgentBannerEditorState*)NativeMemory.AllocZeroed((nuint)sizeof(AgentBannerEditorState));
 public CharaViewPortrait* View=(CharaViewPortrait*)NativeMemory.AllocZeroed((nuint)sizeof(CharaViewPortrait));
 public Character* Actor=(Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
 public Character* Real=(Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
 public Character* Local;public bool Framework=true,Active=true,BoundsAvailable=true;public int Calls,Cleared;public Action? OnLookup;public bool ThrowItem,ReplaceOnItem,RefuseHide;public int HideCalls;
 public bool OriginalHideAvailable,RefuseOriginalHide,ReplaceOwnerOnHide;public int OriginalHideCalls;
 public bool ReturnNoEditor,ReturnNoCharacter,ThrowGlasses;public Action? OnHide;
 public uint Generation {get=>*(uint*)((byte*)Actor+0x74);set=>*(uint*)((byte*)Actor+0x74)=value;}
 public Fixture()
 {
  Local=Real;Editor->EditorState=State;State->AgentBannerEditor=Editor;State->CharaView=View;State->OpenType=AgentBannerEditorState.EditorOpenType.Gearset;
  View->Agent=(AgentInterface*)Editor;View->CharacterLoaded=true;View->CharaViewPortraitCharacterLoaded=true;View->State=6;
  View->CameraYaw=1.5f;View->CameraDistance=2.5f;View->AmbientLightingBrightness=65;
  Actor->DrawData.OwnerObject=Actor;Generation=100;
  for(int i=0;i<14;i++)View->Items[i]=new(){SlotId=14};
  for(int s=0;s<13;s++) {View->Items[12-s]=new(){SlotId=(byte)s,ItemId=(uint)(100+s),Stain0Id=1,Stain1Id=2,ApplyCompanyCrest=true};View->PortraitCharacterData.ItemIds[s]=(uint)(100+s);}
  for(int i=0;i<14;i++) if(View->Items[i].SlotId==3) {View->Items[i].GlamourItemId=903;View->Items[i].GlamourStain0Id=5;View->Items[i].GlamourStain1Id=6;}
  View->PortraitCharacterData.GlassesIds[0]=42;View->ModelData.GlassesIds[0]=42;Actor->DrawData.GlassesIds[0]=42;
 }
 public bool IsFrameworkThread=>Framework;
 public AgentBannerEditor* GetEditor()=>ReturnNoEditor?null:Editor;
 public bool IsEditorActive(AgentBannerEditor* e)=>Active;
 public bool TryGetBounds(AgentBannerEditor* e,out PortraitEditorBounds bounds) {bounds=new(10,20,800,650);return BoundsAvailable;}
 public Character* GetLocalPlayer()=>Local;
 public Character* GetCharacter(CharaViewPortrait* v) {OnLookup?.Invoke();return ReturnNoCharacter?null:Actor;}
 public void SetItem(CharaViewPortrait* v,byte slot,PortraitPiece p,bool crest) {Calls++;if(ThrowItem)throw new InvalidOperationException("native test failure");if(ReplaceOnItem){Editor->EditorState=null;return;}if(p.ItemID==0)Cleared++;for(int i=0;i<14;i++)if(v->Items[i].SlotId==slot||v->Items[i].SlotId==14) {v->Items[i]=new(){SlotId=slot,ItemId=p.ItemID,Stain0Id=p.Stain0,Stain1Id=p.Stain1};break;}}
 public void SetGlasses(Character* c,ushort glasses) {Calls++;if(ThrowGlasses)throw new InvalidOperationException("rollback glasses diagnostic sentinel");c->DrawData.GlassesIds[0]=glasses;}
 public void SetVisibility(CharaViewPortrait* v,bool head,bool weapon,bool visor) {v->PortraitCharacterData.HeadgearHidden=!head;v->PortraitCharacterData.WeaponHidden=!weapon;v->PortraitCharacterData.VisorClosed=visor;}
 public void HideEditor(AgentBannerEditor* e) {HideCalls++;OnHide?.Invoke();if(ReplaceOwnerOnHide){State->AgentBannerEditor=null;return;}if(!RefuseHide)e->EditorState=null;}
 public bool TryHideOriginalEditor(AgentBannerEditor* e,AgentBannerEditorState* state)
 {
  if(!OriginalHideAvailable || Editor!=e || e->EditorState!=state || state->AgentBannerEditor!=e)return false;
  OriginalHideCalls++;if(!RefuseOriginalHide)e->EditorState=null;return true;
 }
 public void Dispose(){NativeMemory.Free(Editor);NativeMemory.Free(State);NativeMemory.Free(View);NativeMemory.Free(Actor);NativeMemory.Free(Real);}
}
