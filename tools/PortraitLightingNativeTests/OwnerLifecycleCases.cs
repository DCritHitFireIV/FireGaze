using FireGaze.PortraitPreview;

internal static unsafe class OwnerLifecycleCases
{
 static bool Destroyed(NativePortraitTarget target,PortraitIdentity identity)
  =>target.IsEditorOwnerDestroyed(identity);
 public static void Run(Action<bool,string> check)
 {
  using var f=new EditorFixture();
  using var target=new NativePortraitTarget(f,_=>false,_=>{});
  check(target.TryResolve(out var original,out _,out _),"owner lifecycle fixture resolves");
  check(!Destroyed(target,original),"current editor owner is not destroyed");
  f.Active=false;check(!target.TryResolve(out _,out _,out _)&&!Destroyed(target,original),"inactive editor readiness never proves owner destruction");f.Active=true;
  f.BoundsAvailable=false;check(!target.TryResolve(out _,out _,out _)&&!Destroyed(target,original),"missing addon bounds never prove owner destruction");f.BoundsAvailable=true;
  f.View->State=0;check(!Destroyed(target,original),"temporarily unready CharaView never proves owner destruction");f.View->State=6;
  f.View->CharaViewPortraitCharacterLoaded=false;check(!Destroyed(target,original),"temporarily unloaded portrait never proves owner destruction");f.View->CharaViewPortraitCharacterLoaded=true;
  f.ReturnNoEditor=true;check(!Destroyed(target,original),"absent editor lookup is uncertainty rather than destruction");f.ReturnNoEditor=false;
  f.ReturnNoCharacter=true;check(!Destroyed(target,original),"absent character lookup is uncertainty rather than destruction");f.ReturnNoCharacter=false;
  f.Framework=false;check(!Destroyed(target,original),"off-Framework calls cannot inspect or retire owner");f.Framework=true;
  *(uint*)((byte*)f.Actor+0x74)=18;
  check(Destroyed(target,original),"conclusively different native character generation proves previous owner replacement");
  check(target.TryResolve(out var next,out _,out _)&&!Destroyed(target,next),"newly resolved native owner remains live");
  f.View->State=0;check(Destroyed(target,original)&&!Destroyed(target,next),"known retirement persists while new owner is temporarily unready");f.View->State=6;
  var lookupCalls=0;f.Editor->EditorState=null;f.OnEditorLookup=()=>{if(++lookupCalls==2)f.Editor->EditorState=f.State;};
  check(!Destroyed(target,next),"reentrant owner lookup restoring the original state cannot retire a live owner");f.OnEditorLookup=null;
  f.Editor->EditorState=null;check(Destroyed(target,next),"same live editor with detached state proves exact previous owner is gone");
  f.Editor->EditorState=f.State;*(uint*)((byte*)f.Actor+0x74)=19;target.TryResolve(out var third,out _,out _);
  var replacement=(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditorState*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditorState));
  try{f.Editor->EditorState=replacement;check(Destroyed(target,third),"same live editor with different state allocation proves replacement without dereferencing old state");}
  finally{f.Editor->EditorState=f.State;System.Runtime.InteropServices.NativeMemory.Free(replacement);}
  f.ReturnNoEditor=true;check(Destroyed(target,original)&&Destroyed(target,next)&&Destroyed(target,third),"proven retired identities remain retired after lookup becomes unavailable");
  f.ReturnNoEditor=false;
  check(Destroyed(target,new((nint)f.Editor,(nint)1,(nint)2,3,4)),"detached proof never dereferences any old state/model/character pointer");
 }
}
