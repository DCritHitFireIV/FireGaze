using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Runtime.InteropServices;

unsafe class Program
{
    private static int passed;
    private static void Check(bool value,string name) {if(!value)throw new InvalidOperationException(name);passed++;Console.WriteLine("PASS "+name);}
    private static bool ReportsHistorySpaceFailure(PortraitPreviewFeature feature) => feature.HistoryStatus.Contains("空间不足")
        && feature.HistoryStatus.Contains("本次修改尚未记录") && feature.HistoryStatus.Contains("已有记录已保留");
    public static int Main()
    {
        var root=Path.Combine(Path.GetTempPath(),"firegaze-feature-integration-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RaptureGearsetModule.Value=(RaptureGearsetModule*)NativeMemory.AllocZeroed((nuint)sizeof(RaptureGearsetModule));
        try
        {
            using(var feature=new PortraitPreviewFeature(Path.Combine(root,"session-history.json")))
            {
                feature.Sample();
                feature.EditComposition(c=>c with {CameraYaw=.25f});feature.RecordDirect();
                var ids=feature.History.Select(row=>row.ID).ToArray();
                feature.Owner=new(999,20);feature.Sample();
                Check(ids.All(id=>feature.History.Any(row=>row.ID==id)),
                    "new runtime account alias for the same character retains persistent portrait history");
                Check(feature.History.Count==ids.Length,
                    "session identity change does not create a duplicate unchanged portrait baseline");
                feature.Owner=new(999,21);feature.Sample();
                Check(feature.History.Count==1 && !feature.History.Any(row=>ids.Contains(row.ID)),
                    "stable history still isolates a different character content identity");
            }
            using(var feature=new PortraitPreviewFeature(Path.Combine(root,"mirror-close-retry.json")))
            {
                feature.Sample();feature.OpenPreview();feature.SetSyncPlate(true);
                feature.Native.RejectMirrorClose=true;feature.SetSyncPlate(false);
                Check(!feature.SyncPlate && feature.Native.IsPlateMirrorActive && feature.Status.Contains("尚未还原"),
                    "failed mirror close separates disabled intent from retained lease and reports incomplete restoration");
                var updates=feature.MirrorUpdates;var closes=feature.Native.MirrorCloseCalls;
                feature.OverwriteStatus("临时外观已还原，现在可以保存肖像。");
                Check(feature.Status.Contains("尚未还原") && !feature.Status.Contains("现在可以保存"),
                    "pending mirror restoration takes precedence over a later generic preview-close success status");
                feature.PollThrottled();
                Check(feature.Native.MirrorCloseCalls==closes+1 && feature.MirrorUpdates==updates && feature.Native.IsPlateMirrorActive,
                    "pending mirror restoration retries before snapshot throttling without resuming synchronization");
                feature.Native.Ready=false;feature.PollThrottled();
                Check(feature.Native.MirrorCloseCalls==closes+2 && feature.MirrorUpdates==updates && feature.Status.Contains("尚未还原"),
                    "hidden editor still retries its retained mirror close before the loading early return");
                feature.SetSyncPlate(true);
                Check(!feature.SyncPlate && feature.MirrorUpdates==updates && feature.Native.IsPlateMirrorActive,
                    "re-enabling cannot reacquire synchronization while an older mirror lease still cannot close");
                feature.Native.RejectMirrorClose=false;feature.PollThrottled();
                Check(!feature.Native.IsPlateMirrorActive && !feature.SyncPlate && feature.MirrorUpdates==updates && !feature.Status.Contains("尚未还原"),
                    "successful later close clears the pending error without silently re-enabling synchronization");
                closes=feature.Native.MirrorCloseCalls;feature.PollThrottled();
                Check(feature.Native.MirrorCloseCalls==closes,
                    "restored mirror stops retrying after its close succeeds");
                feature.Native.Ready=true;feature.SetSyncPlate(true);
                Check(feature.SyncPlate && feature.Native.IsPlateMirrorActive && !feature.Status.Contains("已结束"),
                    "explicit re-enable after completed restoration cannot retain the previous ended status");
            }
            foreach(var reason in new[]{"owner","model","flush"})
            using(var feature=new PortraitPreviewFeature(Path.Combine(root,$"mirror-close-{reason}.json")))
            {
                feature.Sample();feature.OpenPreview();feature.SetSyncPlate(true);
                var updates=feature.MirrorUpdates;
                feature.Native.RejectMirrorClose=true;
                if(reason=="owner") feature.Owner=new(100,200);
                else if(reason=="model") feature.Native.Current=false;
                else feature.FlushOnly();
                feature.Sample();
                Check(!feature.SyncPlate && feature.Native.IsPlateMirrorActive && feature.Status.Contains("尚未还原"),
                    $"{reason} close failure keeps restoration pending while disabling mirror intent");
                var closes=feature.Native.MirrorCloseCalls;
                feature.Sample();feature.RecordDirect();
                Check(feature.Native.MirrorCloseCalls>closes && feature.MirrorUpdates==updates,
                    $"{reason} cleanup retries on subsequent frames without mirroring current or replacement editor state");
                feature.Native.RejectMirrorClose=false;feature.Sample();
                Check(!feature.Native.IsPlateMirrorActive && !feature.SyncPlate && feature.MirrorUpdates==updates,
                    $"{reason} cleanup releases the pending lease without resuming old synchronization");
            }
            using(var feature=new PortraitPreviewFeature(Path.Combine(root,"mirror-replaced-model.json")))
            {
                feature.Sample();feature.OpenPreview();feature.SetSyncPlate(true);
                feature.Native.RejectMirrorClose=true;
                feature.Native.Identity=feature.Native.Identity with {Model=77,Generation=78};
                feature.Sample();
                Check(!feature.SyncPlate && feature.Native.IsPlateMirrorActive && feature.Status.Contains("尚未还原"),
                    "a newly resolved replacement model stops old mirror intent before its first editor sample");
                var updates=feature.MirrorUpdates;var look=feature.Native.Snapshot.Look.Copy();
                look.Pieces[(int)PortraitSlot.Body]=new(987,7,8);
                feature.Native.Snapshot=feature.Native.Snapshot with {Look=look};
                feature.Sample();feature.RecordDirect();
                Check(feature.MirrorUpdates==updates && feature.Native.Snapshot.Look.Pieces[(int)PortraitSlot.Body]==new PortraitPiece(987,7,8),
                    "pending old-model cleanup does not re-sync or overwrite replacement editor appearance");
                feature.Native.RejectMirrorClose=false;feature.Sample();
                Check(!feature.Native.IsPlateMirrorActive && !feature.SyncPlate && feature.MirrorUpdates==updates,
                    "replacement model cleanup completes without carrying synchronization into the new model");
            }
            using(var feature=new PortraitPreviewFeature(Path.Combine(root,"close.json")))
            {
                feature.Sample();
                Check(feature.Store.Count==1 && feature.History.Count==1,"real feature records and publishes initial editor baseline");
                feature.OpenPreview();feature.ChangeAppearance("hair",2);
                Check(feature.Store.Count==2 && feature.Native.Snapshot.Customize[6]==2,"beauty action applies then records a detached history state");
                feature.EditComposition(c=>c with {CameraYaw=1});feature.RecordDirect();
                var latest=feature.Store.CurrentRecordID(feature.Key!.Value);
                feature.Undo();
                Check(feature.Native.Snapshot.Customize[6]==2 && feature.Native.Snapshot.Composition.CameraYaw==0 && feature.CanRedo,
                    "real undo keeps the edited appearance while publishing the later composition as redo");
                feature.Native.Ready=false;
                feature.RollbackAndSync();
                Check(feature.DeferredBaseline && !feature.IsActive,"hidden native rollback schedules deferred baseline without a lease");
                Check(feature.Native.Snapshot.Customize[6]==1,"hidden close restores the original appearance across an actual visual change");
                var count=feature.Store.Count;
                feature.Sample();feature.Native.Ready=true;feature.Sample();feature.Sample(advanceMilliseconds:600);
                Check(feature.Store.Count==count && feature.CanRedo && feature.Store.TryGetRedo(feature.Key.Value,out var redo) && redo.Record.ID==latest,
                    "first ready sample after hidden close cannot record rollback or erase redo");
                feature.Sample(pointer:true);feature.Sample(pointer:false);feature.Sample(advanceMilliseconds:600);
                Check(feature.Store.Count==count,"passive mouse edges after rollback cannot resurrect an edit");
            }
            using(var feature=new PortraitPreviewFeature(Path.Combine(root,"mouse.json")))
            {
                feature.Sample();
                feature.EditComposition(c=>c with {CameraYaw=1});feature.Sample(pointer:true);
                feature.EditComposition(c=>c with {CameraYaw=2});feature.Sample(pointer:true,advanceMilliseconds:3000);
                Check(feature.Store.Count==1,"continuous mouse hold stays one pending edit despite long duration");
                feature.Sample(pointer:false);
                Check(feature.Store.Count==2 && feature.History.Count==2,"unchanged mouse release commits and immediately refreshes real history UI model");
                var stored=PortraitEditorSnapshot.FromJSON(feature.Store.GetRecords(feature.Key!.Value)[0].SnapshotJSON)!;
                Check(stored.Composition.CameraYaw==2,"mouse history retains the final native value");
                feature.Sample(advanceMilliseconds:600);Check(feature.Store.Count==2,"passive idle does not duplicate the released edit");
                feature.EditComposition(c=>c with {CameraPitch=1});feature.Sample(keyboard:true);
                feature.EditComposition(c=>c with {CameraPitch=2});feature.Sample(keyboard:true,advanceMilliseconds:3000);
                Check(feature.Store.Count==2,"keyboard hold keeps the current native edit pending");
                feature.Sample(keyboard:false);feature.Sample(advanceMilliseconds:499);
                Check(feature.Store.Count==2,"keyboard release obeys the full quiet interval");
                feature.Sample(advanceMilliseconds:1);
                Check(feature.Store.Count==3,"keyboard edit commits once after release and 500 ms quiet");
                feature.EditComposition(c=>c with {AnimationProgress=.9f});feature.Sample();feature.Sample(advanceMilliseconds:600);
                Check(feature.Store.Count==3,"ordinary playback progress remains outside feature history");
                feature.EditComposition(c=>c with {Paused=true,AnimationProgress=.4f});feature.Sample();feature.Sample(advanceMilliseconds:600);
                Check(feature.Store.Count==4,"paused native timeline change is recorded");
                feature.EditComposition(c=>c with {CameraYaw=3});feature.Sample(pointer:true,keyboard:true);
                feature.Sample(pointer:false,keyboard:true,advanceMilliseconds:3000);
                Check(feature.Store.Count==4,"releasing a pointer while a key remains held does not split the real feature transaction");
                feature.Sample(keyboard:false);feature.Sample(advanceMilliseconds:500);
                Check(feature.Store.Count==5,"mixed pointer and keyboard transaction commits once after both inputs release");
            }
            using(var feature=new PortraitPreviewFeature(Path.Combine(root,"sources.json")))
            {
                RaptureGearsetModule.Value->EnabledGearsetIndex2EntryIndex[7]=40;
                RaptureGearsetModule.Value->EnabledGearsetIndex2EntryIndex[8]=41;
                feature.Native.Snapshot=feature.Native.Snapshot with {EntryPoint=1};feature.Native.State->OpenerEnabledGearsetIndex=7;feature.Sample();
                Check(feature.Key!.Value.SourceID==40,"real feature uses opener enabled index mapping for source identity");
                feature.EditComposition(c=>c with {CameraYaw=1});feature.Sample(pointer:true);
                feature.Native.State->OpenerEnabledGearsetIndex=8;feature.Sample();
                Check(feature.Key!.Value.SourceID==41 && feature.History.Count==1,"switching sources publishes only the current source history");
                Check(feature.Store.GetRecords(feature.Key.Value with {SourceID=40}).Count==2,"switching sources flushes prior detached edit under its original source");
                feature.Owner=new(100,200);feature.Sample();
                Check(feature.History.Count==1 && feature.Key!.Value.ContentID==200,"character change replaces published history with the new owner records");
                feature.Owner=null;feature.Native.Current=false;feature.Sample();
                Check(feature.History.Count==0 && feature.AppearanceGroups.Count==0 && !feature.CanUndo && !feature.CanRedo,"logout clears owner-visible history and controls");
            }
            using(var feature=new PortraitPreviewFeature(Path.Combine(root,"quota.json"),byteLimit:512))
            {
                feature.Sample();
                Check(feature.Store.Count==0 && ReportsHistorySpaceFailure(feature),"initial oversized record reports capacity rejection and preserves existing history");
                feature.RefreshRows();
                Check(ReportsHistorySpaceFailure(feature),"ordinary history list refresh preserves capacity reason and recovery facts");
                feature.RecordDirect();feature.RefreshRows();
                Check(feature.Store.Count==0 && ReportsHistorySpaceFailure(feature),"direct action failure survives its unconditional list refresh with its capacity reason");
            }
            var blockedFile=Path.Combine(root,"blocked-history.json");Directory.CreateDirectory(blockedFile);
            using(var feature=new PortraitPreviewFeature(blockedFile))
            {
                feature.Sample();
                Check(!feature.Store.FlushAsync().GetAwaiter().GetResult() && !string.IsNullOrWhiteSpace(feature.Store.PersistenceError),
                    "real background writer reports an unavailable destination without losing the accepted memory record");
                feature.Sample(advanceMilliseconds:600);
                Check(feature.HistoryStatus.Contains(feature.Store.PersistenceError!,StringComparison.Ordinal),
                    "passive native polling publishes asynchronous history persistence failure without requiring another edit");
                var priorError=feature.Store.PersistenceError!;feature.RefreshRows();
                Directory.Delete(blockedFile);
                var recovered=feature.Native.Snapshot with {Composition=feature.Native.Snapshot.Composition with {CameraYaw=1}};
                feature.Store.Record(feature.Key!.Value,recovered.ToJSON());
                Check(feature.Store.FlushAsync().GetAwaiter().GetResult() && feature.Store.PersistenceError is null,
                    "real history writer can recover after its destination becomes writable");
                Check(!feature.HistoryStatus.Contains(priorError,StringComparison.Ordinal),
                    "completed writer recovery cannot reveal a persistence error cached by an earlier list refresh");
            }
            using(var feature=new PortraitPreviewFeature(Path.Combine(root,"validation.json")))
            {
                feature.Sample();var key=feature.Key!.Value;
                var bad=feature.Native.Snapshot with {Composition=feature.Native.Snapshot.Composition with {BannerTimeline=ushort.MaxValue}};
                var record=feature.Store.Record(key,bad.ToJSON()).Record!;
                var calls=feature.Native.ApplyCalls;var captures=feature.Native.CaptureCalls;var cursor=feature.Store.CurrentRecordID(key);
                feature.RestoreHistory(record.ID);
                Check(feature.Native.ApplyCalls==calls && feature.Native.CaptureCalls==captures && !feature.IsActive && feature.Store.CurrentRecordID(key)==cursor && feature.LastError?.Contains("不适用")==true,
                    "malicious history sheet token is rejected before native apply or acquiring a preview lease");
                var valid=feature.Store.Record(key,feature.Native.Snapshot.ToJSON()).Record!;
                feature.Native.CanBlockSave=false;feature.RestoreHistory(record.ID);
                Check(!feature.IsActive && feature.Native.ApplyCalls==calls,"invalid restoration cannot bypass validation when save guard is unavailable");
                feature.RestoreHistory(valid.ID);
                Check(!feature.IsActive && feature.Native.ApplyCalls==calls && feature.Native.CaptureCalls==captures && feature.LastError?.Contains("无法阻止肖像保存")==true
                    && feature.LastError.Contains("本次未恢复记录"),
                    "valid history restoration checks save guard before opening a lease");
            }
            using(var feature=new PortraitPreviewFeature(Path.Combine(root,"apply-failure.json")))
            {
                feature.Sample();
                feature.EditComposition(c=>c with {CameraYaw=1});feature.RecordDirect();
                var cursor=feature.Store.CurrentRecordID(feature.Key!.Value);var count=feature.Store.Count;
                feature.Native.LastError="fixture native apply refused";feature.Native.RejectApply=true;feature.Undo();
                Check(feature.Store.CurrentRecordID(feature.Key.Value)==cursor && feature.Store.Count==count && !feature.CanRedo,
                    "native history apply refusal cannot move or append the real store cursor");
                Check(feature.Native.Snapshot.Composition.CameraYaw==1 && feature.LastError=="fixture native apply refused",
                    "failed native history restoration preserves editor content and surfaces the native error");
            }
            using(var feature=new PortraitPreviewFeature(Path.Combine(root,"latest-look.json")))
            {
                feature.Sample();feature.OpenPreview();
                var edited=feature.Native.Snapshot.Look.Copy();edited.Pieces[(int)PortraitSlot.Body]=new(400,5,6);
                feature.Native.Snapshot=feature.Native.Snapshot with {Look=edited};feature.Sample();
                Check(feature.SetNativeVisibility(false,true,false) && feature.Native.Snapshot.Look.Pieces[(int)PortraitSlot.Body]==new PortraitPiece(400,5,6),
                    "passive editor samples synchronize session equipment before a later visibility action");
                edited=feature.Native.Snapshot.Look.Copy();edited.Pieces[(int)PortraitSlot.Body]=new(500,7,8);
                feature.Native.Snapshot=feature.Native.Snapshot with {Look=edited};
                Check(feature.SyncLatestLook() && feature.SetNativeVisibility(true,false,false)
                    && feature.Native.Snapshot.Look.Pieces[(int)PortraitSlot.Body]==new PortraitPiece(500,7,8),
                    "immediate look synchronization prevents the next session action from restoring stale equipment");
                feature.SetSyncPlate(true);var mirrors=feature.MirrorUpdates;var key=feature.Key;
                feature.Native.Ready=false;feature.Sample();
                Check(feature.SyncPlate && feature.Key==key && feature.History.Count>0 && feature.MirrorUpdates==mirrors,
                    "temporarily hidden editor retains source and mirror preference without writing to a loading allocation");
            }
            Console.WriteLine($"PortraitFeatureIntegrationTests: {passed} passed, 0 failed");
            return 0;
        }
        catch(Exception exception){Console.Error.WriteLine(exception);return 1;}
        finally{NativeMemory.Free(RaptureGearsetModule.Value);RaptureGearsetModule.Value=null;Directory.Delete(root,true);}
    }
}
