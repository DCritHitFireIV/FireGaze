using System.Reflection;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal static class PhotoGallerySelectionCases
{
    internal static async Task<int> RunAsync(string root, PortraitIdentity identity)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++; Console.WriteLine("PASS " + name);
        }
        var at = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        using var history = new PortraitHistoryStore(Path.Combine(root,"gallery-complete-history.json"),clock:()=>at);
        var key = new PortraitHistoryKey(10,PortraitHistoryEntryPoint.GearsetPortrait,0,"gallery");
        var first = new PortraitEditorSnapshot { GameBuild="gallery",EntryPoint=1 };
        var second = first with { Composition=first.Composition with { CameraYaw=.1f } };
        var last = first with { Composition=first.Composition with { CameraYaw=.2f } };
        var one=history.Record(key,first.ToJSON(),"打开肖像").Record!;
        at=at.AddDays(1);
        var two=history.Record(key,second.ToJSON(),"快速调整镜头").Record!;
        var three=history.Record(key,last.ToJSON(),"再次调整镜头").Record!;
        if(!await history.FlushAsync()) throw new InvalidOperationException("gallery history fixture must finish its physical writer before synchronous photo seeding");
        var textures=DispatchProxy.Create<ITextureProvider,NoGpuProxy>();
        var readback=DispatchProxy.Create<ITextureReadbackProvider,NoGpuProxy>();
        var cachePath=Path.Combine(root,"gallery-photo-index.bin");
        var source=new PortraitThumbnailSource();
        using(var empty=new PortraitHistoryPhotos(Path.Combine(root,"gallery-empty.bin"),textures,readback,source,0))
        {
            empty.SetRecords(history.GetAllRecords(),0);
            var all=Rows(history,key);
            var projection=PortraitHistoryPhotoPresentation.SelectPhotographed(all,empty.HasPhotographedRecord);
            Check(projection.Count==0 && all.Count==3 && history.TryGetUndo(key,out var undo) && undo.Record.ID==two.ID,
                "rapid unphotographed edits create no missing photo cards while their complete operations and undo remain visible");
            empty.Poll("gallery",identity,two.ID,second,true);
            Check(empty.GetHistoryPhotoStatus(two.ID)==PortraitHistoryPhotoStatus.Generating
                && PortraitHistoryPhotoPresentation.SelectPhotographed(all,empty.HasPhotographedRecord).Count==0,
                "a genuinely generating photo is not promoted to a completed gallery card");
            empty.Poll("gallery",identity,three.ID,last,true);
            Check(source.Calls==0 && empty.GetHistoryPhotoStatus(two.ID)==PortraitHistoryPhotoStatus.Missing
                && PortraitHistoryPhotoPresentation.SelectPhotographed(all,empty.HasPhotographedRecord).Count==0,
                "a new edit before stable capture leaves the old operation intact without a permanent missing photo card");
        }
        var cache=new PortraitThumbnailCache(cachePath,PortraitThumbnailCache.MaximumBytes);
        var png=new EncodingReadbackProxy().Png;
        Check(cache.TryPut(PortraitThumbnailCache.KeyForSnapshot(10,first),png)
            && cache.TryPut(PortraitThumbnailCache.KeyForSnapshot(10,last),png),
            "gallery fixture stores two genuine durable PNG entries in the real cache");
        using var photos=new PortraitHistoryPhotos(cachePath,textures,readback,new(),0);
        photos.SetRecords(history.GetAllRecords(),0);
        var rows=Rows(history,key);
        var browse=new PortraitHistoryBrowseSession(); browse.Begin(rows);
        var archived=browse.Archived(rows);
        var selected=PortraitHistoryPhotoPresentation.SelectPhotographed(archived,photos.HasPhotographedRecord);
        Check(selected.Select(row=>row.ID).SequenceEqual([three.ID,one.ID]) && archived.Count==3 && rows.Count==3,
            "only durable photographed archive nodes enter the photo tab and no operation rows are removed");
        var days=new PortraitHistoryPresentation().Get(selected,TimeZoneInfo.Utc);
        Check(days.Count==2 && days[0].Records.Count==1 && days[1].Records.Count==1,
            "photo day headers count only actual durable photos rather than unphotographed operation nodes");
        Check(history.TryGetRecord(key,two.ID,out var restore)
            && history.CommitRestore(restore).Status==PortraitHistoryWriteStatus.Added,
            "an unphotographed operation remains explicitly restorable without automatic history replay");
        var restoredRows=Rows(history,key);
        var temporary=browse.Temporary(restoredRows);
        Check(temporary.Count==1 && temporary[0].IsCurrent && browse.Archived(restoredRows).Count==3,
            "temporary history attempts remain visible independently of photo filtering");
        photos.Get(one.ID);
        for(var attempt=0;attempt<100 && photos.GetHistoryPhotoStatus(one.ID)!=PortraitHistoryPhotoStatus.ReadError;attempt++)
            await Task.Delay(5);
        Check(photos.GetHistoryPhotoStatus(one.ID)==PortraitHistoryPhotoStatus.ReadError
            && PortraitHistoryPhotoPresentation.SelectPhotographed(archived,photos.HasPhotographedRecord).Any(row=>row.ID==one.ID),
            "a real saved photo with a texture read failure remains visible with its explicit failure status");
        Check(await history.FlushAsync(),"complete operation history persists independently of gallery filtering");
        return checks;
    }

    private static IReadOnlyList<PortraitHistoryListItem> Rows(PortraitHistoryStore history,PortraitHistoryKey key)
    {
        var current=history.CurrentRecordID(key);
        return history.GetRecords(key).Select(record=>new PortraitHistoryListItem(record.ID,record.Timestamp,record.Label,record.ID==current)).ToArray();
    }
}
