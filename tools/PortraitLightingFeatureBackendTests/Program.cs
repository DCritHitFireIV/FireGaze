using FireGaze.PortraitPreview;

var checks=0;var failures=new List<string>();
void Check(bool condition,string label){checks++;if(!condition)failures.Add(label);}
#if HAS_FEATURE_BACKEND
using var host=new Host();var backend=new PortraitLightingFitFeatureBackend(host);
Check(backend.TryRead(out var initial),"backend reads detached native binding");
var requested=new[]{31,32,33,41,42,43};
var result=backend.Write(initial,requested);
Check(result.Applied && result.OwnedState!.Parameters.SequenceEqual(requested),"native owned parameters are explicitly published");
Check(host.Snapshot.Composition.DirectionalRed==31 && host.Snapshot.Composition.Background==25,"only RGB fields are written");
Check(!backend.Write(initial,[1,2,3,4,5,6]).Applied && host.Writes==1,"stale RGB cannot reach native write");
backend.TryRead(out var beforeCamera);host.Snapshot=host.Snapshot with{Composition=host.Snapshot.Composition with{CameraPitch=9}};
Check(!backend.Write(beforeCamera,requested).Applied && host.Writes==1,"stale camera cannot reach native write");
backend.TryRead(out var beforeSource);host.Key=host.Key with{SourceID=4};
Check(!backend.Write(beforeSource,requested).Applied && host.Writes==1,"source replacement cannot reach native write");
backend.TryRead(out var beforeSurface);host.Surface="new SRV/crop";
Check(!backend.Write(beforeSurface,requested).Applied && host.Writes==1,"surface replacement cannot reach native write");
backend.TryRead(out var beforeRange);host.Ranges[0]=new(1,255,1);
Check(!backend.Write(beforeRange,requested).Applied && host.Writes==1,"range replacement cannot reach native write");
host.Ranges[0]=new(0,255,1);backend.TryRead(out var valid);
Check(!backend.Write(valid,[256,0,0,0,0,0]).Applied && !backend.Write(valid,[1,2]).Applied && host.Writes==1,"out of range/incomplete writes never reach native");
host.Partial=true;var partial=backend.Write(valid,[61,62,63,71,72,73]);
Check(!partial.Applied && partial.OwnedState!.Parameters.SequenceEqual(new[]{61,62,63,41,42,43}),"partial write maps Owned values independently from a misleading Actual snapshot");
Check(host.MisleadingActual!.Composition.DirectionalRed==199 && partial.OwnedState!.Parameters[0]==61,"Actual is never substituted for ownership");
host.Readable=false;
Check(!backend.TryRead(out _) && !backend.IsEditorOwnerDestroyed(valid.EditorIdentity),"temporary unreadability is not destruction");
Check(!backend.IsEditorOwnerDestroyed("unknown owner"),"unknown ownership is not destruction");
host.Destroyed=true;
Check(backend.IsEditorOwnerDestroyed(valid.EditorIdentity),"explicit lifecycle destruction releases known owner");
host.Readable=true;host.Destroyed=false;
backend.SetHistorySuppressed(true);backend.TryRead(out var current);
var capture=backend.CaptureAsync(current);
Check(!capture.IsCompleted && host.Captures==1,"capture begins only for freshly verified binding");
host.CompleteCapture();Check(capture.Result.Count==4,"capture returns detached CPU pixels");
host.Snapshot=host.Snapshot with{Composition=host.Snapshot.Composition with{Background=26}};
Check(backend.CaptureAsync(current).IsFaulted && host.Captures==1,"stale capture cannot reach GPU host");
backend.TryRead(out current);var baseline=current.Parameters.ToArray();baseline[0]--;
Check(backend.CommitHistory(current,baseline) && host.History.Count==1 && host.Suppressed,"one final state is committed while history sampling is suppressed");
Check(host.History.GetRecords(host.Key).Single().Label=="调整灯光色彩","final history has a user facing label");
Check(backend.CommitHistory(current,baseline) && host.History.Count==1,"repeated identical commit cannot add duplicate records");
host.Key=host.Key with{ContentID=0x999};
Check(!backend.CommitHistory(current,baseline) && host.History.Count==1,"stale source never appends history");
backend.TryRead(out current);backend.SetHistorySuppressed(false);
Check(!backend.CommitHistory(current,baseline) && host.History.Count==1,"history commit requires the sampling transaction");
host.Framework=false;
Check(!backend.TryRead(out _) && !backend.Write(current,requested).Applied && !backend.CommitHistory(current,baseline),"read, native write and commit refuse worker thread calls");
Check(backend.CaptureAsync(current).IsFaulted && !backend.IsEditorOwnerDestroyed(current.EditorIdentity),"capture and lifecycle query refuse worker thread calls");
var suppressionThrew=false;try{backend.SetHistorySuppressed(true);}catch(InvalidOperationException){suppressionThrew=true;}
Check(suppressionThrew && !host.Suppressed,"suppression mutation also requires Framework");

using var cleanupHost=new Host();var cleanupBackend=new PortraitLightingFitFeatureBackend(cleanupHost);var adapter=new PortraitLightingFitAdapter(cleanupBackend);
adapter.TryRead(out var start);cleanupHost.Partial=true;cleanupHost.FailCleanup=true;
Check(!adapter.TrySet(start,[51,52,53,61,62,63]) && adapter.HasPendingCleanup,"production backend keeps partial write cleanup through the adapter");
cleanupHost.Readable=false;adapter.Poll();
Check(adapter.HasPendingCleanup && !adapter.TryRead(out _),"transient unreadability keeps cleanup pending");
cleanupHost.Readable=true;cleanupHost.FailCleanup=false;cleanupHost.Snapshot=cleanupHost.Snapshot with{Composition=cleanupHost.Snapshot.Composition with{Paused=false,AnimationProgress=8,CameraPitch=3,DirectionalRed=177}};
adapter.Poll();adapter.TryRead(out var cleaned);
Check(!adapter.HasPendingCleanup && cleaned.Parameters.SequenceEqual(new[]{177,12,13,21,22,23}),"cleanup while playing restores owned channels and preserves foreign RGB");
Check(!cleanupHost.Snapshot.Composition.Paused && cleanupHost.Snapshot.Composition.CameraPitch==3 && cleanupHost.Snapshot.Composition.AnimationProgress==8,"cleanup preserves current pose, time and camera");
Check(cleanupHost.History.Count==0,"probe and cleanup create no ordinary history");

using var sessionHost=new Host();var sessionBackend=new PortraitLightingFitFeatureBackend(sessionHost);var sessionAdapter=new PortraitLightingFitAdapter(sessionBackend);var session=new PortraitLightingFitSession(sessionAdapter);
PortraitColorToneTarget.TryCreate([70,134,198],[70,134,198],[70,134,198],out var target);
Check(session.TryStart(target!,[true,true,true,true]) && sessionHost.Suppressed,"production backend and real adapter start a session with suppressed history");
sessionHost.CompleteCapture();sessionAdapter.Poll();session.Poll();
Check(sessionHost.Captures==2 && sessionHost.History.Count==0,"first real probe does not enter history");
session.Cancel();sessionAdapter.CancelCapturePublication();
Check(session.RollbackComplete && sessionHost.Snapshot.Composition.DirectionalRed==11 && sessionHost.History.Count==0,"cancelling a probe restores RGB without any edit record");
Check(session.HasPendingCapture && sessionAdapter.HasPendingCapture,"cancel preserves physical capture barrier");
sessionHost.CompleteCapture();sessionAdapter.Poll();session.Poll();
Check(!session.HasPendingCapture && !sessionAdapter.HasPendingCapture && session.HistorySuppressionReleased && !sessionHost.Suppressed,"late capture drains before another transaction");
LifecycleCases.Run(Check);
#else
Check(false,"production lighting feature backend is absent");
#endif
foreach(var failure in failures)Console.WriteLine("FAIL "+failure);
Console.WriteLine($"PortraitLightingFeatureBackendTests: {checks-failures.Count} passed, {failures.Count} failed");
return failures.Count==0?0:1;

#if HAS_FEATURE_BACKEND
sealed class Host:IPortraitLightingFitFeatureHost,IDisposable
{
 readonly string directory=Path.Combine(Path.GetTempPath(),"FireGazeLightingBackendTest",Guid.NewGuid().ToString("N"));
 public Host(){Directory.CreateDirectory(directory);History=new(Path.Combine(directory,"history.json"));}
 public PortraitIdentity Identity=new((nint)0x110,(nint)0x220,(nint)0x330,7,0x440);
 public PortraitHistoryKey Key=new(0x550,PortraitHistoryEntryPoint.GearsetPortrait,3,"cn-7.57:race1");
 public string Surface="srv/crop/uv:1";
 public PortraitNativeLightRange[] Ranges=Enumerable.Repeat(new PortraitNativeLightRange(0,255,1),6).ToArray();
 public PortraitEditorSnapshot Snapshot=new(){GameBuild="cn-7.57",EntryPoint=1,Look=new(Enumerable.Repeat(new PortraitPiece(100,2,3),13).ToArray(),true,true,false),Customize=Enumerable.Repeat((byte)1,26).ToArray(),Composition=new(){DirectionalRed=11,DirectionalGreen=12,DirectionalBlue=13,AmbientRed=21,AmbientGreen=22,AmbientBlue=23,Background=25,Paused=true,CharacterVisible=true}};
 public PortraitEditorSnapshot? MisleadingActual;
 public bool Framework=true,Readable=true,Destroyed,Suppressed,Partial,FailCleanup;
 public int Writes,Captures;public PortraitHistoryStore History;
 TaskCompletionSource<IReadOnlyList<PortraitLightRgba>>? pending;
 public bool IsFrameworkThread=>Framework;
 public bool TryReadBinding(out PortraitLightingFitBinding binding){binding=null!;return Readable&&PortraitLightingFitBinding.TryCreate(Identity,Key,Snapshot,Surface,2,2,Ranges,out binding!);}
 public bool IsEditorOwnerDestroyed(PortraitIdentity identity)=>Destroyed&&identity==Identity;
 public PortraitLightingFitNativeWrite WriteColors(PortraitLightingFitBinding expected,IReadOnlyList<int> desired)
 {
  Writes++;if(FailCleanup&&!Partial)return new(false,null);
  var values=desired.ToArray();
  if(Partial){Partial=false;values[3]=Snapshot.Composition.AmbientRed;values[4]=Snapshot.Composition.AmbientGreen;values[5]=Snapshot.Composition.AmbientBlue;}
  else if(FailCleanup)throw new InvalidOperationException("fixture must partial first");
  var old=Snapshot.Composition;var wasPartial=values[3]!=desired[3]||values[4]!=desired[4]||values[5]!=desired[5];
  Snapshot=Snapshot with{Composition=old with{DirectionalRed=(byte)values[0],DirectionalGreen=(byte)values[1],DirectionalBlue=(byte)values[2],AmbientRed=(byte)values[3],AmbientGreen=(byte)values[4],AmbientBlue=(byte)values[5]}};
  MisleadingActual=Snapshot with{Composition=Snapshot.Composition with{DirectionalRed=199}};
  return new(!wasPartial,values);
 }
 public void SetHistorySuppressed(bool value)=>Suppressed=value;
 public Task<IReadOnlyList<PortraitLightRgba>> CaptureAsync(PortraitLightingFitBinding expected){Captures++;pending=new(TaskCreationOptions.RunContinuationsAsynchronously);return pending.Task;}
 public void CompleteCapture()=>pending!.SetResult(Enumerable.Repeat(new PortraitLightRgba(64,96,128,255),4).ToArray());
 public bool CommitHistory(PortraitLightingFitBinding expected,IReadOnlyList<int> baseline)=>History.Record(Key,Snapshot.ToJSON(),"调整灯光色彩").Status is PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate;
 public void Dispose(){History.Dispose();Directory.Delete(directory,true);}
}
#endif
