using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(sdk, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
return await Run();

static async Task<int> Run()
{
    var checks = 0;
    void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        checks++; Console.WriteLine("PASS " + name);
    }
    var root = Path.Combine(Path.GetTempPath(), "FireGazePhotoIntegration-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var identity = new PortraitIdentity(1, 2, 3, 4, 5);
    var key = new PortraitHistoryKey(10, PortraitHistoryEntryPoint.GearsetPortrait, 0, "fixture");
    var initial = new PortraitEditorSnapshot { GameBuild = "fixture", EntryPoint = 1 };
    var changed = initial with { Composition = initial.Composition with { CameraYaw = .5f } };
    ITextureProvider textures = DispatchProxy.Create<ITextureProvider, NoGpuProxy>();
    ITextureReadbackProvider readback = DispatchProxy.Create<ITextureReadbackProvider, NoGpuProxy>();
    PortraitHistoryPhotos Photos(string name, PortraitThumbnailSource source, int bytes = 0)
        => new(Path.Combine(root, name), textures, readback, source, bytes);
    async Task Settle(PortraitHistoryPhotos photos, long? id, PortraitEditorSnapshot? snapshot, bool ready = true, string owner = "owner-A")
    {
        photos.Poll(owner, identity, id, snapshot, ready);
        await Task.Delay(250);
        photos.Poll(owner, identity, id, snapshot, ready);
    }
    try
    {
        var historyPath = Path.Combine(root, "actual-history.json");
        using var history = new PortraitHistoryStore(historyPath);
        var one = history.Record(key, initial.ToJSON(), "打开肖像").Record!;
        var two = history.Record(key, changed.ToJSON(), "调整镜头").Record!;
        Check(await history.FlushAsync(), "real history finishes its own independent writer before photo checks");
        var historyBefore = File.ReadAllBytes(historyPath);
        var records = history.GetRecords(key);
        checks += await FavoriteRetentionCases.RunAsync(root, initial);
        checks += await PhotoEncodingBoundaryTests.RunAsync(root, history, historyPath, key, one, initial, identity);
        checks += await PhotoDrawCompositionTests.RunAsync(root, history, key, one, initial, identity);
        checks += await PhotoTraceServiceCases.RunAsync(root, history, key, one, initial, identity);
        checks += await PhotoManagedDrainCases.RunAsync(root, textures, readback, history.Bytes);
        checks += await ManagedDrainOutcomeCases.RunAsync(root, key, initial);
        checks += await PhotoPhysicalDrainCases.RunAsync(root, textures, readback);
        checks += await PhotoProgressReadCases.RunAsync(root, textures, readback);

        var source = new PortraitThumbnailSource();
        using (var photos = Photos("eligibility.bin", source, history.Bytes))
        {
            photos.SetRecords(records, history.Bytes);
            await Settle(photos, two.ID, initial);
            Check(source.Calls == 0, "current image must match the stored current record before capture");
            await Settle(photos, 999, changed);
            Check(source.Calls == 0, "an unknown current record cannot borrow another record's image");
            await Settle(photos, two.ID, changed, ready: false);
            Check(source.Calls == 0, "closed or held-input editor cannot start a capture");
            photos.Poll("owner-A", identity, two.ID, changed, true);
            Check(source.Calls == 0, "the first ready Framework sample cannot bypass the stable delay");
            await Settle(photos, two.ID, changed);
            Check(source.Calls == 1 && source.RequestedIdentity == identity, "one settled current record captures the exact validated editor identity");
            photos.Poll("owner-A", identity, two.ID, changed, true);
            Check(source.Calls == 1, "repeated polls cannot start a second copy while one is pending");
            photos.Poll("owner-A", identity, one.ID, initial, true);
            var staleTexture = new FakeTexture(); source.Complete(new(staleTexture));
            Check(photos.NeedsStateValidation, "completed asynchronous copy requests a fresh Framework state check");
            photos.Poll("owner-A", identity, one.ID, initial, true);
            Check(staleTexture.Disposes == 1, "a late copy for the old record is released before any GPU composition");
            Check(!File.Exists(Path.Combine(root, "eligibility.bin")), "stale pixels never create a cache file under the new record");
        }

        var lateSource = new PortraitThumbnailSource();
        var closingPhotos = Photos("closing.bin", lateSource, history.Bytes);
        closingPhotos.SetRecords(records, history.Bytes);
        await Settle(closingPhotos, two.ID, changed);
        Check(lateSource.Calls == 1, "unload case has a genuine pending service capture");
        closingPhotos.Dispose();
        Check(!lateSource.RequestedCancellation.CanBeCanceled, "unload invalidates publication while preserving physical provider completion");
        var lateTexture = new FakeTexture(); lateSource.Complete(new(lateTexture));
        for (var retry = 0; retry < 100 && lateTexture.Disposes == 0; retry++) await Task.Delay(10);
        Check(lateTexture.Disposes == 1, "a copy completing after unload releases its owned resources once");
        closingPhotos.Dispose(); closingPhotos.Poll("owner-B", identity, two.ID, changed, true);
        Check(lateTexture.Disposes == 1 && lateSource.Calls == 1 && closingPhotos.Get(two.ID) is null,
            "disposed photo service neither recaptures nor exposes a stale image");

        var otherKey = key with { ContentID = 20 };
        var otherSnapshot = initial with { Composition = initial.Composition with { Background = 2 } };
        var other = new PortraitHistoryRecord(30, otherKey, DateTimeOffset.UtcNow, null, PortraitHistoryRecordKind.Edit, otherSnapshot.ToJSON());
        var currentImageKey = PortraitThumbnailCache.KeyForSnapshot("10|" + initial.ToJSON());
        var otherImageKey = PortraitThumbnailCache.KeyForSnapshot("20|" + other.SnapshotJSON);
        var cachedPath = Path.Combine(root, "characters.bin");
        var seed = new PortraitThumbnailCache(cachedPath, PortraitThumbnailCache.MaximumBytes - history.Bytes);
        Check(seed.TryPut(currentImageKey, new byte[] { 1, 2 }) && seed.TryPut(otherImageKey, new byte[] { 3, 4 }), "seed distinct disposable photos for two characters");
        var cachedSource = new PortraitThumbnailSource();
        using (var photos = Photos("characters.bin", cachedSource, history.Bytes))
        {
            photos.SetRecords([one], history.Bytes);
            await Settle(photos, one.ID, initial);
            Check(cachedSource.Calls == 0, "an existing image is not regenerated by ordinary stable polls");
            photos.SetRecords([other], history.Bytes);
            await Settle(photos, other.ID, otherSnapshot, owner: "owner-B");
            Check(cachedSource.Calls == 0, "switching to another character uses that character's existing current-state image");
        }
        var afterSwitch = new PortraitThumbnailCache(cachedPath, PortraitThumbnailCache.MaximumBytes - history.Bytes);
        Check(afterSwitch.Count == 2 && afterSwitch.TryGet(currentImageKey, out _) && afterSwitch.TryGet(otherImageKey, out _),
            "showing one character's history does not trim another character's photos");

        var sameLookOther = other with { ID = 31, SnapshotJSON = initial.ToJSON() };
        var isolatedSource = new PortraitThumbnailSource();
        using (var photos = Photos("characters.bin", isolatedSource, history.Bytes))
        {
            photos.SetRecords([sameLookOther], history.Bytes);
            await Settle(photos, sameLookOther.ID, initial, owner: "owner-B");
            Check(isolatedSource.Calls == 1, "an identical snapshot for another character cannot reuse the first character's cached photo");
        }
        var isolatedTexture = new FakeTexture(); isolatedSource.Complete(new(isolatedTexture));
        for (var retry = 0; retry < 100 && isolatedTexture.Disposes == 0; retry++) await Task.Delay(10);
        Check(isolatedTexture.Disposes == 1, "the separate-character capture still follows unload resource ownership");

        var zeroSource = new PortraitThumbnailSource();
        using (var photos = Photos("full-history.bin", zeroSource, PortraitHistoryStore.MaximumBytes))
        {
            photos.SetRecords(records, PortraitHistoryStore.MaximumBytes);
            await Settle(photos, two.ID, changed);
            Check(zeroSource.Calls == 0 && !File.Exists(Path.Combine(root, "full-history.bin")),
                "history using the entire shared budget leaves no space or work for disposable photos");
        }

        var failurePath = Path.Combine(root, "locked.bin");
        var failureSeed = new PortraitThumbnailCache(failurePath, PortraitThumbnailCache.MaximumBytes);
        Check(failureSeed.TryPut(currentImageKey, new byte[] { 1, 2 }), "seed a real cache before a failed budget reduction");
        var failedSource = new PortraitThumbnailSource();
        using (var photos = Photos("locked.bin", failedSource, history.Bytes))
        using (var exclusive = new FileStream(failurePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            photos.SetRecords(records, PortraitHistoryStore.MaximumBytes);
            await Settle(photos, two.ID, changed);
            Check(failedSource.Calls == 0, "a cache deletion failure cannot spend the old budget on a new capture");
            var error = photos.Diagnostics().GetType().GetProperty("lastError")!.GetValue(photos.Diagnostics()) as string;
            Check(!string.IsNullOrWhiteSpace(error), "failed cache maintenance remains diagnosable without failing history");
        }
        Check(File.ReadAllBytes(historyPath).SequenceEqual(historyBefore) && history.Count == 2
            && history.CurrentRecordID(key) == two.ID && history.TryGetUndo(key, out _),
            "all photo eligibility lifecycle and I/O failures preserve real history bytes cursor and undo");

        // Closing a portrait feature on Framework must only seal admission there. The
        // immutable history writer is drained by the caller outside Framework, so the
        // close phase never blocks on file I/O.
        var asyncHistoryPath = Path.Combine(root, "async-close-history.json");
        var asyncHistory = new PortraitHistoryStore(asyncHistoryPath);
        asyncHistory.Record(key, initial.ToJSON(), "异步关闭测试");
        var asyncDrain = asyncHistory.BeginDispose();
        Check(asyncDrain is not null, "history exposes an explicit managed drain task");
        Check(await asyncDrain!, "history drain completes accepted writes outside the Framework phase");
        Check(asyncHistory.Record(key, changed.ToJSON()).Status == PortraitHistoryWriteStatus.Disposed,
            "history closes admission before its managed writer drain");
        asyncHistory.Dispose();

        var pendingPhotoMaintenance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drainingPhotos = Photos("async-close-photos.bin", new PortraitThumbnailSource(), history.Bytes);
        typeof(PortraitHistoryPhotos).GetField("maintenance", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(drainingPhotos, pendingPhotoMaintenance.Task);
        var photoServiceDrain = drainingPhotos.BeginDispose();
        Check(!photoServiceDrain.IsCompleted, "photo close seals immediately while managed maintenance remains observable");
        pendingPhotoMaintenance.SetResult();
        await photoServiceDrain;
        Check(photoServiceDrain.IsCompletedSuccessfully, "photo managed maintenance drains outside the Framework phase");
        drainingPhotos.Dispose();

        var photoDrainGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyDrainGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var combinedDrain = PortraitManagedDrain.Combine(photoDrainGate.Task, historyDrainGate.Task);
        Check(!combinedDrain.IsCompleted, "feature managed drain waits for every accepted close task");
        historyDrainGate.SetResult();
        Check(!combinedDrain.IsCompleted, "history completion cannot hide pending photo persistence");
        photoDrainGate.SetResult();
        await combinedDrain;
        Check(combinedDrain.IsCompletedSuccessfully, "managed drain completes after photo and history persistence");
        Console.WriteLine($"PortraitHistoryPhotoIntegrationTests: {checks} passed, 0 failed; no native rendering, game or GPU calls.");
        return 0;
    }
    finally { Directory.Delete(root, true); }
}
