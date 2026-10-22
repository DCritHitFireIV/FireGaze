using System.Reflection;
using System.Text.Json;
using FireGaze.PortraitPreview;

internal static class PhotoBackedHistoryCases
{
    internal static async Task<int> Run(string directory, PortraitHistoryKey key)
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks++; Console.WriteLine("PASS " + message);
        }
        var enable = typeof(PortraitHistoryStore).GetMethod("EnablePhotoBackedPublication");
        var publish = typeof(PortraitHistoryStore).GetMethod("SynchronizePhotographedRecords");
        var visible = typeof(PortraitHistoryStore).GetMethod("GetPublishedRecords");
        Check(enable is not null && publish is not null && visible is not null,
            "photo availability is tracked independently of the complete durable undo chain");
        IReadOnlyList<PortraitHistoryRecord> Visible(PortraitHistoryStore store)
            => (IReadOnlyList<PortraitHistoryRecord>)visible!.Invoke(store, [key])!;
        void Publish(PortraitHistoryStore store, params long[] ids)
            => publish!.Invoke(store, [ids.ToHashSet()]);
        var path = Path.Combine(directory, "photo-backed-history.json");
        using (var store = new PortraitHistoryStore(path))
        {
            enable!.Invoke(store, null);
            var first = store.Record(key, "{\"look\":1}").Record!;
            var middle = store.Record(key, "{\"look\":2}").Record!;
            var last = store.Record(key, "{\"look\":3}").Record!;
            Check(store.Count == 3 && Visible(store).Count == 0,
                "rapid edits remain navigable while the photographed-only query stays empty");
            Check(store.TryGetUndo(key, out var undo) && undo.Record.ID == middle.ID,
                "unphotographed intermediate edits remain available to undo");
            Check(await store.FlushAsync(), "pending photographic history flush is bounded");
            using (var pending = new PortraitHistoryStore(path))
                Check(pending.Count == 3 && !pending.IsReadOnly
                    && pending.TryGetUndo(key, out var pendingUndo) && pendingUndo.Record.ID == middle.ID,
                    "capture failure cannot erase durable editing records or their undo navigation");
            var completeBytes = File.ReadAllBytes(path);
            var completeLength = store.Bytes;
            Publish(store, last.ID);
            Check(Visible(store).Select(row => row.ID).SequenceEqual([last.ID]),
                "only a completed state image publishes its matching photo card");
            Check(await store.FlushAsync(), "photographed record reaches the durable history writer");
            using (var restart = new PortraitHistoryStore(path))
                Check(!restart.IsReadOnly && restart.Count == 3 && restart.GetRecords(key)[0].ID == last.ID
                    && restart.GetRecords(key)[0].ParentID == middle.ID,
                    "photograph publication preserves every durable edit and original parent link");
            Check(store.Bytes == completeLength && File.ReadAllBytes(path).SequenceEqual(completeBytes),
                "photo availability cannot rewrite the complete history document or its byte accounting");
            Check(store.CommitNavigation(undo).Status == PortraitHistoryWriteStatus.Added,
                "publishing a photo does not invalidate an already prepared undo");
            Check(store.TryGetRedo(key, out var redo) && redo.Record.ID == last.ID,
                "publishing a photo does not invalidate an already prepared undo or lose redo");
            Check(store.CommitNavigation(redo).Status == PortraitHistoryWriteStatus.Added,
                "photo publication keeps full redo semantics");
            Publish(store, first.ID, last.ID);
            Check(await store.FlushAsync(), "late independent image completion republishes a valid document");
            using (var restart = new PortraitHistoryStore(path))
                Check(!restart.IsReadOnly && restart.Count == 5
                    && restart.GetRecords(key).Single(row => row.ID == last.ID).ParentID == middle.ID,
                    "out of order image completion preserves complete history and unphotographed ancestors");
            Publish(store, first.ID);
            Check(await store.FlushAsync(), "image eviction updates the durable history references");
            using (var restart = new PortraitHistoryStore(path))
                Check(!restart.IsReadOnly && restart.Count == 5 && restart.CurrentRecordID(key) == last.ID,
                    "cache eviction cannot delete durable editing records or move the saved navigation cursor");
            Check(store.Count == 5 && store.CurrentRecordID(key) == last.ID,
                "image eviction never deletes the current session navigation chain");
            var foreign = key with { ContentID = key.ContentID + 1 };
            var foreignRow = store.Record(foreign, "{\"look\":99}").Record!;
            Publish(store, first.ID, foreignRow.ID);
            Check(Visible(store).All(row => row.Key == key),
                "published photo cards preserve character and editor ownership isolation");
        }
        using (var restart = new PortraitHistoryStore(path))
            Check(!restart.IsReadOnly && restart.Count == 6,
                "closing drains all admitted edits even when some states have never been photographed");
        return checks;
    }
}
