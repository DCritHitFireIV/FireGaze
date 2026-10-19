#if HAS_FEATURE_BACKEND
using System.Security.Cryptography;
using System.Text;

namespace FireGaze
{
 internal static class Plugin
 {
  public static FrameworkBoundary Framework{get;}=new();
  public static object TextureReadback{get;}=new();
 }
 internal sealed class FrameworkBoundary{public bool IsInFrameworkUpdateThread{get;set;}=true;}
}

namespace FireGaze.PortraitPreview
{
 internal readonly record struct PortraitCaptureViewport(int ID);
 internal sealed record PortraitCaptureSample(PortraitIdentity Editor,string StateKey,PortraitCaptureOwner Owner);
 internal readonly record struct PortraitCaptureOwner(ulong ContentID);
 internal sealed record PortraitCaptureSurface(string GenerationKey);
 internal sealed record PortraitLightingFitCaptureFrame(PortraitCaptureSample Sample,PortraitCaptureSurface Surface,int Width=8,int Height=8);
 internal sealed class PortraitLightingFitTextureReadback(object readback){public object Readback{get;}=readback;}

 // GPU boundary only. The production Feature/backend/adapter/session/binding/history are source-linked.
 internal sealed class PortraitLightingFitCaptureBridge(object api,PortraitLightingFitTextureReadback readback):IDisposable
 {
  readonly TargetBoundary target=(TargetBoundary)api;
  TaskCompletionSource<IReadOnlyList<PortraitLightRgba>>? pending;
  int[]? parameters;
  public int Captures;
  public bool IsBusy=>pending is {Task.IsCompleted:false};
  public bool TryReadFrame(PortraitCaptureViewport viewport,out PortraitLightingFitCaptureFrame? frame)
  {
   _=readback.Readback;frame=null;
   if(!target.Readable||!target.Snapshot.Composition.Paused)return false;
   var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.Snapshot.ToJSON())));
   frame=new(new(target.Identity,"1:"+hash,new(target.Key.ContentID)),new(target.Surface));return true;
  }
  public Task<IReadOnlyList<PortraitLightRgba>> CaptureAsync(PortraitLightingFitCaptureFrame frame)
  {
   if(IsBusy)throw new InvalidOperationException("GPU overlap");
   Captures++;parameters=PortraitNativeLightColors.From(target.Snapshot.Composition).Parameters;
   pending=new(TaskCreationOptions.RunContinuationsAsynchronously);return pending.Task;
  }
  public void Complete()
  {
   if(pending is not{Task.IsCompleted:false} complete||parameters is null)return;
   var p=parameters;
   var pixels=Enumerable.Range(0,64).Select(i=>i<32?new PortraitLightRgba(p[0]-50,p[1]-50,p[2]-50,255):new PortraitLightRgba(p[3],p[4],p[5],255)).ToArray();
   complete.SetResult(pixels);
  }
  public void Update(){}
  public void Draw(){}
  public void Close(){}
  public void Dispose(){pending?.TrySetException(new InvalidOperationException("disposed GPU publication"));}
 }

 internal readonly record struct PortraitNativeLightControl(int Minimum,int Maximum,int Value,int Steps);
 internal readonly record struct PortraitNativeLightColors(byte DirectionalRed,byte DirectionalGreen,byte DirectionalBlue,byte AmbientRed,byte AmbientGreen,byte AmbientBlue)
 {
  public int[] Parameters=>[DirectionalRed,DirectionalGreen,DirectionalBlue,AmbientRed,AmbientGreen,AmbientBlue];
  public static PortraitNativeLightColors From(PortraitComposition c)=>new(c.DirectionalRed,c.DirectionalGreen,c.DirectionalBlue,c.AmbientRed,c.AmbientGreen,c.AmbientBlue);
  public static bool TryCreate(IReadOnlyList<int> p,out PortraitNativeLightColors result)
  {result=default;if(p.Count!=6||p.Any(x=>x is<0 or>255))return false;result=new((byte)p[0],(byte)p[1],(byte)p[2],(byte)p[3],(byte)p[4],(byte)p[5]);return true;}
  public PortraitComposition Apply(PortraitComposition c)=>c with{DirectionalRed=DirectionalRed,DirectionalGreen=DirectionalGreen,DirectionalBlue=DirectionalBlue,AmbientRed=AmbientRed,AmbientGreen=AmbientGreen,AmbientBlue=AmbientBlue};
 }
 internal sealed record PortraitNativeLightWriteResult(bool Applied,PortraitNativeLightColors? Owned);
 internal sealed class TargetBoundary:IPortraitPreviewTarget
 {
  public PortraitIdentity Identity=new((nint)0x110,(nint)0x220,(nint)0x330,7,0x440);
  public PortraitHistoryKey Key=new(0x550,PortraitHistoryEntryPoint.GearsetPortrait,3,"cn-7.57:race1");
  public string Surface="srv/crop/uv:1";
  public bool Readable=true,PartialNextWrite,RejectCleanup,CanBlockSave=true;
  public int NativeWrites;
  public int[] SliderValues=[100,100,100,100,100,100];
  public PortraitEditorSnapshot Snapshot=new(){GameBuild="cn-7.57",EntryPoint=1,Look=new(Enumerable.Repeat(new PortraitPiece(100,2,3),13).ToArray(),true,true,false),Customize=Enumerable.Repeat((byte)1,26).ToArray(),Composition=new(){DirectionalRed=100,DirectionalGreen=100,DirectionalBlue=100,AmbientRed=100,AmbientGreen=100,AmbientBlue=100,Paused=true,CharacterVisible=true}};
  public bool TryResolve(out PortraitIdentity identity,out int bounds,out byte openType){identity=Identity;bounds=0;openType=1;return Readable;}
  public PortraitEditorSnapshot? CaptureEditorState(PortraitIdentity id)=>Readable&&id==Identity?Snapshot.Copy():null;
  public PortraitNativeLightControl[]? ReadLightControls(PortraitIdentity id)=>Readable&&id==Identity?SliderValues.Select(v=>new PortraitNativeLightControl(0,255,v,255)).ToArray():null;
  public PortraitNativeLightWriteResult WriteLightColors(PortraitIdentity id,PortraitEditorSnapshot expected,PortraitNativeLightColors desired)
  {
   NativeWrites++;
   if(!Readable||id!=Identity||expected.ToJSON()!=Snapshot.ToJSON())return new(false,null);
   if(PartialNextWrite)
   {
    PartialNextWrite=false;var owned=desired with{AmbientRed=Snapshot.Composition.AmbientRed,AmbientGreen=Snapshot.Composition.AmbientGreen,AmbientBlue=Snapshot.Composition.AmbientBlue};
    Snapshot=Snapshot with{Composition=owned.Apply(Snapshot.Composition)};return new(false,owned);
   }
   if(RejectCleanup)return new(false,null);
   Snapshot=Snapshot with{Composition=desired.Apply(Snapshot.Composition)};SliderValues=desired.Parameters;return new(true,desired);
  }
  public bool IsEditorOwnerDestroyed(PortraitIdentity id)=>false;
  public bool UpdatePlateMirror(PortraitIdentity id,PortraitEditorSnapshot snapshot,bool enabled)=>true;
  public bool IsCurrent(PortraitIdentity id)=>Readable&&id==Identity;
  public PortraitLook? Capture(PortraitIdentity id)=>Snapshot.Look.Copy();
  public bool Apply(PortraitIdentity id,PortraitLook look){Snapshot=Snapshot with{Look=look};return true;}
 }

 internal sealed unsafe partial class PortraitPreviewFeature:IDisposable
 {
  readonly string directory=Path.Combine(Path.GetTempPath(),"FireGazeLightingLifecycle",Guid.NewGuid().ToString("N"));
  readonly TargetBoundary target=new();
  readonly PortraitPreviewSession session;
  readonly PortraitHistoryStore history;
  readonly PortraitHistoryBatch historyBatch;
  readonly object liveCaptureAPI;
  readonly PortraitCaptureViewport? requestedCaptureViewport=new(1);
  PortraitEditorSnapshot? observedEditor,historyObservedEditor;
  PortraitEditorSnapshot? historyLabelBaseline;
  PortraitIdentity observedIdentity;
  PortraitHistoryKey? observedKey;
  bool deferredBaselineSync,syncPlate;
  public readonly List<string> Errors=[];
  public PortraitPreviewFeature()
  {
   Directory.CreateDirectory(directory);history=new(Path.Combine(directory,"history.json"));historyBatch=new(history);
   session=new(target);session.Open(target.Identity);liveCaptureAPI=target;
   observedIdentity=target.Identity;observedKey=target.Key;observedEditor=historyObservedEditor=target.Snapshot.Copy();
   history.Record(target.Key,target.Snapshot.ToJSON(),"打开肖像");InitializeLightingFit();
   // Read private observer fields so the fixture also asserts the real commit baseline publication.
   historyLabelBaseline=null;deferredBaselineSync=false;syncPlate=false;
  }
  public void Tick(){session.Validate();PollLightingFit();}
  public void CompleteCapture()=>lightingFitCapture?.Complete();
  public int CaptureCount=>lightingFitCapture?.Captures??0;
  public int NativeWrites=>target.NativeWrites;
  public int HistoryCount=>history.Count;
  public string[] HistoryLabels=>history.GetRecords(target.Key).Select(r=>r.Label).ToArray();
  public int[] NativeColors=>PortraitNativeLightColors.From(target.Snapshot.Composition).Parameters;
  public int[] RawSliderValues=>target.SliderValues;
  public bool IsHistorySuppressed=>LightingFitHistorySuppressed;
  public bool Blocked=>LightingFitMutationBlocked;
  public bool PreviewActive=>session.IsActive;
  public bool SaveBlocked=>session.BlockSave(target.Identity);
  public bool HistoryBaselineMatches=>observedEditor?.ToJSON()==target.Snapshot.ToJSON()&&historyObservedEditor?.ToJSON()==target.Snapshot.ToJSON()&&observedIdentity==target.Identity&&observedKey==target.Key&&historyLabelBaseline is null&&!deferredBaselineSync;
  public bool CancelForEdit()=>CancelLightingFitForEdit();
  public bool PartialNextWrite{set=>target.PartialNextWrite=value;}
  public bool RejectCleanup{set=>target.RejectCleanup=value;}
  public bool Readable{set=>target.Readable=value;}
  public bool Paused{get=>target.Snapshot.Composition.Paused;set=>target.Snapshot=target.Snapshot with{Composition=target.Snapshot.Composition with{Paused=value}};}
  public float CameraPitch=>target.Snapshot.Composition.CameraPitch;
  public void ChangeCamera()=>target.Snapshot=target.Snapshot with{Composition=target.Snapshot.Composition with{CameraPitch=9}};
  public void PlayAndMoveCamera()=>target.Snapshot=target.Snapshot with{Composition=target.Snapshot.Composition with{Paused=false,AnimationProgress=8,CameraPitch=9}};
  PortraitHistoryKey? GetHistoryKey(PortraitIdentity identity,PortraitEditorSnapshot snapshot)=>target.Key;
  static void Mutate(Action action)=>action();
  void Error(string text)=>Errors.Add(text);
  static void CancelLightDrag(){}
  static void HandleHistoryBatchResult(PortraitHistoryWriteResult? result){}
  static void HandleHistoryResult(PortraitHistoryWriteResult? result){}
  void RecordDirectChange(string label)=>history.Record(target.Key,target.Snapshot.ToJSON(),label);
  static void RefreshHistoryRows(){}
  public void Dispose(){DisposeLightingFit();history.Dispose();Directory.Delete(directory,true);}
 }
}
#endif
