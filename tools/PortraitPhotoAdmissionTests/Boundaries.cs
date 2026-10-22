namespace FireGaze {
 internal static class Plugin {internal static readonly Framework Framework=new();internal static readonly Objects Objects=new();internal static object GameGUI=>new();internal static object Textures=>new();internal static object TextureReadback=>new();internal static object AddonLifecycle=>new();internal static readonly Logger Log=new();}
 internal sealed class Framework {internal bool IsInFrameworkUpdateThread=true;}
 internal sealed class Objects {internal Player? LocalPlayer;}
 internal sealed class Player {internal nint Address;}
 internal sealed class Logger {internal void Information(string format,string message){}}
}
namespace FFXIVClientStructs.FFXIV.Client.Game.Character {internal struct Character {internal ulong AccountId,ContentId;}}
namespace FireGaze.PortraitPreview {
 internal sealed unsafe partial class PortraitPreviewFeature:IDisposable {
  private readonly Target target=new();private readonly PortraitPreviewSession session;private readonly PortraitCaptureIsolation captureIsolation=new();
  private readonly History history=new();private readonly Favorites favoritesStore=new(),favoritesTrashStore=new();private readonly object storageBudget=new();
  private bool LightingFitHistorySuppressed,SourceCensusBlocksOperations,OwnedViewportBlocksOperations,pointerHeld,sampledKeyboard;
  private bool LightingFitMutationBlocked {get;set;}
  private readonly Drag lightDrag=new();private readonly Work liveImageService=new(),lightingFitCapture=new();
  private PortraitEditorSnapshot? observedEditor;private Key? observedKey=new(10);private PortraitIdentity observedIdentity;
  private string? historyPhotoStatus;private readonly nint player;
  internal PortraitPreviewFeature(){session=new(target);session.Open(target.Identity);observedIdentity=target.Identity;observedEditor=target.Snapshot.Copy();photos=new();
   player=(nint)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(FFXIVClientStructs.FFXIV.Client.Game.Character.Character));
   *(FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player=new(){AccountId=1,ContentId=10};Plugin.Objects.LocalPlayer=new(){Address=player};}
  internal void Poll()=>PollPhotos();internal bool Ready=>photos.Ready;internal int FreshReads=>target.FreshReads;
  internal PortraitNativeLiveContext NativeContext()=>ReadNativeLiveContext();internal void SetPhotoPhysical(bool value)=>photos.IsPhysicallyIdle=value;
  internal void SetPhotoWanted(bool value)=>photos.Wanted=value;
  internal void ChangeFresh()=>target.Snapshot=target.Snapshot with {Composition=target.Snapshot.Composition with {CameraYaw=1}};
  internal void Block(string reason){switch(reason){case "closed":session.Close();break;case "save":target.CanBlockSave=false;break;case "foreign-session":target.Identity=target.Identity with {Editor=99};observedIdentity=target.Identity;break;case "pointer":pointerHeld=true;break;case "keyboard":sampledKeyboard=true;break;case "lightdrag":lightDrag.IsActive=true;break;case "lighting":LightingFitHistorySuppressed=true;break;case "census":SourceCensusBlocksOperations=true;break;case "owned":OwnedViewportBlocksOperations=true;break;case "live":liveImageService.IsBusy=true;break;case "fit":lightingFitCapture.IsBusy=true;break;case "live-physical":liveImageService.IsPhysicallyIdle=false;break;case "fit-physical":lightingFitCapture.IsPhysicallyIdle=false;break;case "native-physical":nativeLiveController=new(null,null){IsPhysicallyIdle=false};break;case "diagnostic-live":captureIsolation.TryConfigure("live",false,false);break;case "wrong-owner":observedKey=new(20);break;case "wrong-generation":target.Identity=target.Identity with {Generation=2};break;}}
  private Key? CurrentCharacter()=>new(10);private IEnumerable<(ulong,PortraitEditorSnapshot)> FavoriteSnapshotsForPhotos()=>[];
  private void RefreshHistoryRows(){}private void Mutate(Action action)=>action();public void Dispose(){System.Runtime.InteropServices.NativeMemory.Free((void*)player);Plugin.Objects.LocalPlayer=null;}
 }
 internal readonly record struct Key(ulong ContentID);
 internal sealed class Target:IPortraitPreviewTarget {internal PortraitIdentity Identity=new(1,2,3,1,5);internal bool CanBlockSave=true;internal int FreshReads;internal PortraitEditorSnapshot Snapshot=new(){GameBuild="photo",Composition=new(){Paused=true}};
  public bool IsCurrent(PortraitIdentity identity)=>identity==Identity;public PortraitLook? Capture(PortraitIdentity identity)=>Snapshot.Look.Copy();public bool Apply(PortraitIdentity identity,PortraitLook look)=>true;
  internal bool TryResolve(out PortraitIdentity identity,out int bounds,out int type){identity=Identity;bounds=type=0;return true;}
  internal PortraitEditorSnapshot? CaptureEditorState(PortraitIdentity identity){FreshReads++;return identity==Identity?Snapshot.Copy():null;}
 }
 internal sealed class History {internal long NavigationRevision=>1;internal int Bytes=>0;internal object GetAllRecords()=>new();internal long? CurrentRecordID(Key key)=>1;internal bool SynchronizePhotographedRecords(object ids)=>false;}
 internal sealed class Favorites {internal bool IsReadOnly=>false;}
 internal sealed class Work {internal bool IsBusy;internal bool IsPhysicallyIdle=true;}
 internal sealed class Drag {internal bool IsActive;}
 internal sealed class PortraitHistoryPhotos {internal bool Ready,Wanted=true;internal bool IsBusy {get;set;}internal bool IsPhysicallyIdle=true;internal bool NeedsStateValidation=>false;internal long ImageRevision=>0;internal string? HistoryPublicationStatus=>null;
  internal PortraitHistoryPhotos(){}internal PortraitHistoryPhotos(string path,object textures,object readback,PortraitThumbnailSource source,int bytes,Action<string> log,string favoritePath,object? snapshots,object storage){}
  internal void SetRecords(object records,int bytes){}internal void Poll(string owner,PortraitIdentity identity,long? id,PortraitEditorSnapshot? snapshot,bool ready,ulong contentID=0){Ready=ready;}
  internal bool NeedsCurrentCapture(long? id,PortraitEditorSnapshot? snapshot,ulong contentID)=>Wanted;internal object GetPhotographedRecordIDs()=>new();
 }
 internal sealed class PortraitThumbnailSource {internal PortraitThumbnailSource(object framework,object gui,object textures,Func<PortraitIdentity,bool> current){}}
 internal readonly record struct PortraitCaptureOwner(ulong AccountID,ulong ContentID);
 internal readonly record struct PortraitNativeLiveContext(PortraitIdentity Editor,PortraitCaptureOwner Owner,bool SessionOwnsEditor,bool CanBlockSave,string CaptureMode,bool CapturesDrained,bool LightingFitBusy);
 internal enum PortraitNativeLiveStopReason {Disabled,UserClosed}
 internal sealed class PortraitNativeLiveBorrow {}
 internal sealed class PortraitNativeLiveNodeSource:IDisposable {internal PortraitNativeLiveNodeSource(object framework,object gui,object objects,object lifecycle,Func<PortraitIdentity?> current){}public void Dispose(){}}
 internal sealed class PortraitNativeLiveController:IDisposable {internal bool IsEnabled {get;set;}internal bool IsPhysicallyIdle=true;internal PortraitNativeLiveController(PortraitNativeLiveNodeSource? source,Func<PortraitNativeLiveContext>? read){}internal bool TryEnable(PortraitIdentity identity)=>false;internal bool TryStartSessionOnce(long rev,PortraitIdentity identity)=>false;internal bool TryBorrow(out PortraitNativeLiveBorrow? borrow){borrow=null;return false;}internal void Update(){}internal void Stop(PortraitNativeLiveStopReason reason){}public void Dispose(){}internal Task DrainPhysicalAsync()=>Task.CompletedTask;}
}
