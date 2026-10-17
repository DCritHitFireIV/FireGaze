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
  t.Dispose();Check(!t.IsCurrent(id) && !t.ShouldBlockSave((nint)f.State),"disposed adapter cannot touch native pointers or block saves");
  Console.WriteLine($"{passed}/{passed+failed} portrait native boundary cases passed");return failed==0?0:1;
 }
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
 public AgentBannerEditor* GetEditor()=>Editor;
 public bool IsEditorActive(AgentBannerEditor* e)=>Active;
 public bool TryGetBounds(AgentBannerEditor* e,out PortraitEditorBounds bounds) {bounds=new(10,20,800,650);return BoundsAvailable;}
 public Character* GetLocalPlayer()=>Local;
 public Character* GetCharacter(CharaViewPortrait* v) {OnLookup?.Invoke();return Actor;}
 public void SetItem(CharaViewPortrait* v,byte slot,PortraitPiece p,bool crest) {Calls++;if(ThrowItem)throw new InvalidOperationException("native test failure");if(ReplaceOnItem){Editor->EditorState=null;return;}if(p.ItemID==0)Cleared++;for(int i=0;i<14;i++)if(v->Items[i].SlotId==slot||v->Items[i].SlotId==14) {v->Items[i]=new(){SlotId=slot,ItemId=p.ItemID,Stain0Id=p.Stain0,Stain1Id=p.Stain1};break;}}
 public void SetGlasses(Character* c,ushort glasses) {Calls++;c->DrawData.GlassesIds[0]=glasses;}
 public void SetVisibility(CharaViewPortrait* v,bool head,bool weapon,bool visor) {v->PortraitCharacterData.HeadgearHidden=!head;v->PortraitCharacterData.WeaponHidden=!weapon;v->PortraitCharacterData.VisorClosed=visor;}
 public void HideEditor(AgentBannerEditor* e) {HideCalls++;if(ReplaceOwnerOnHide){State->AgentBannerEditor=null;return;}if(!RefuseHide)e->EditorState=null;}
 public bool TryHideOriginalEditor(AgentBannerEditor* e,AgentBannerEditorState* state)
 {
  if(!OriginalHideAvailable || Editor!=e || e->EditorState!=state || state->AgentBannerEditor!=e)return false;
  OriginalHideCalls++;if(!RefuseOriginalHide)e->EditorState=null;return true;
 }
 public void Dispose(){NativeMemory.Free(Editor);NativeMemory.Free(State);NativeMemory.Free(View);NativeMemory.Free(Actor);NativeMemory.Free(Real);}
}
