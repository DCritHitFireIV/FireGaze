using System.Reflection;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal static class FavoriteRetentionCases
{
    internal static async Task<int> RunAsync(string root, PortraitEditorSnapshot snapshot)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAIL " + name);
            Console.WriteLine("PASS " + name); checks++;
        }
        var path = Path.Combine(root, "retained-favorites.bin");
        var key = PortraitThumbnailCache.KeyForSnapshot("10|" + snapshot.ToJSON());
        var png = new byte[64];
        new byte[] { 137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,160,0,0,1,0,8,6 }.CopyTo(png, 0);
        var cache = new PortraitThumbnailCache(path, PortraitThumbnailCache.MaximumBytes);
        Check(cache.TryPut(key, png), "favorite retention begins with a previously completed history PNG");
        var textures = DispatchProxy.Create<ITextureProvider, FavoriteImageUpload>();
        var uploaded = (FavoriteImageUpload)(object)textures;
        var readback = DispatchProxy.Create<ITextureReadbackProvider, NoGpuProxy>();
        var source = new PortraitThumbnailSource();
        using (var service = new PortraitHistoryPhotos(path, textures, readback, source, 0))
        {
            var set = typeof(PortraitHistoryPhotos).GetMethod("SetFavoriteSnapshots");
            Check(set is not null, "photo service must retain favorite references rather than only read disposable cache");
            set!.Invoke(service, new object[] { new (ulong ContentID, PortraitEditorSnapshot Snapshot)[] { (10, snapshot), (10, snapshot) } });
        }
        cache.SetAvailableBytes(0);
        // A damaged/unreadable favorite snapshot library deliberately supplies no reference
        // set. Ordinary history completion must preserve its independently recoverable photos.
        var unrelated = snapshot with { Composition = snapshot.Composition with { Background = 77 } };
        var unrelatedSource = new PortraitThumbnailSource();
        using (var service = new PortraitHistoryPhotos(path, textures, readback, unrelatedSource, 0))
            await CompleteManagedPng(service, unrelatedSource, unrelated, png);
        cache = new PortraitThumbnailCache(path, PortraitThumbnailCache.MaximumBytes);
        cache.SetAvailableBytes(0);
        using (var service = new PortraitHistoryPhotos(path, textures, readback, source, 0))
        {
            var set = typeof(PortraitHistoryPhotos).GetMethod("SetFavoriteSnapshots")!;
            set.Invoke(service, new object[] { new (ulong ContentID, PortraitEditorSnapshot Snapshot)[] { (10, snapshot) } });
            var record = new PortraitHistoryRecord(9, new(10, PortraitHistoryEntryPoint.GearsetPortrait, 0, snapshot.GameBuild),
                DateTimeOffset.UtcNow, null, PortraitHistoryRecordKind.Edit, snapshot.ToJSON());
            service.SetRecords([record], 0);
            var identity = new PortraitIdentity(1, 2, 3, 4, 5);
            service.Poll("retained-favorite", identity, record.ID, snapshot, true);
            await Task.Delay(250);
            service.Poll("retained-favorite", identity, record.ID, snapshot, true);
            Check(source.Calls == 0, "a retained favorite photo also satisfies ordinary current-history eligibility without redundant capture");
            IDalamudTextureWrap? texture = null;
            for (var retry = 0; retry < 100 && texture is null; retry++)
            {
                texture = service.GetSnapshot(10, snapshot);
                if (texture is null) await Task.Delay(10);
            }
            Check(texture is not null && uploaded.LastImage?.SequenceEqual(png) == true,
                "favorite UI retains archived pixels when an ordinary photo completes before favorite references are known");
            Check(source.Calls == 0, "favorite photo retention and browsing never capture or replay a model");
            set.Invoke(service, new object[] { Array.Empty<(ulong ContentID, PortraitEditorSnapshot Snapshot)>() });
        }
        using (var service = new PortraitHistoryPhotos(path, textures, readback, source, 0))
        {
            var before = uploaded.Uploads;
            service.GetSnapshot(10, snapshot);
            await Task.Delay(100);
            Check(uploaded.Uploads == before, "removing the last favorite reference durably collects its image");
        }

        var latePath = Path.Combine(root, "late-favorite.bin");
        var lateSource = new PortraitThumbnailSource();
        using (var service = new PortraitHistoryPhotos(latePath, textures, readback, lateSource, 0))
        {
            var set = typeof(PortraitHistoryPhotos).GetMethod("SetFavoriteSnapshots")!;
            set.Invoke(service, new object[] { new (ulong ContentID, PortraitEditorSnapshot Snapshot)[] { (10, snapshot) } });
            await CompleteManagedPng(service, lateSource, snapshot, png);
        }
        new PortraitThumbnailCache(latePath, PortraitThumbnailCache.MaximumBytes).SetAvailableBytes(0);
        using (var service = new PortraitHistoryPhotos(latePath, textures, readback, source, 0))
        {
            var set = typeof(PortraitHistoryPhotos).GetMethod("SetFavoriteSnapshots")!;
            set.Invoke(service, new object[] { new (ulong ContentID, PortraitEditorSnapshot Snapshot)[] { (10, snapshot) } });
            IDalamudTextureWrap? image = null;
            for (var retry = 0; retry < 100 && image is null; retry++)
            {
                image = service.GetSnapshot(10, snapshot);
                if (image is null) await Task.Delay(10);
            }
            Check(image is not null, "a favorite made before PNG completion retains that image as soon as the existing writer completes");
        }

        var migrationPath = Path.Combine(root, "migration-favorite.bin");
        var migrationCache = new PortraitThumbnailCache(migrationPath, PortraitThumbnailCache.MaximumBytes);
        Check(migrationCache.TryPut(key, png), "first-upgrade migration starts with a completed photo that the new history budget cannot retain");
        var migrationConstructor = typeof(PortraitHistoryPhotos).GetConstructors().SingleOrDefault(constructor =>
            constructor.GetParameters().Any(parameter => parameter.Name == "favoriteSnapshots"));
        Check(migrationConstructor is not null, "startup must migrate known favorite photos before applying the smaller history cache budget");
        using (var service = (PortraitHistoryPhotos)migrationConstructor!.Invoke(new object?[]
            { migrationPath, textures, readback, source, PortraitThumbnailCache.MaximumBytes, null, null,
                new (ulong ContentID, PortraitEditorSnapshot Snapshot)[] { (10, snapshot) } }))
        {
            Check(!File.Exists(migrationPath), "migration still applies the actual zero-byte history image budget");
        }
        using (var service = new PortraitHistoryPhotos(migrationPath, textures, readback, source, 0))
        {
            typeof(PortraitHistoryPhotos).GetMethod("SetFavoriteSnapshots")!.Invoke(service,
                new object[] { new (ulong ContentID, PortraitEditorSnapshot Snapshot)[] { (10, snapshot) } });
            IDalamudTextureWrap? image = null;
            for (var retry = 0; retry < 100 && image is null; retry++)
            {
                image = service.GetSnapshot(10, snapshot);
                if (image is null) await Task.Delay(10);
            }
            Check(image is not null, "first-upgrade migration durably rescues a favorite PNG before ordinary budget eviction");
        }
        return checks;
    }

    private static async Task CompleteManagedPng(PortraitHistoryPhotos service, PortraitThumbnailSource source,
        PortraitEditorSnapshot snapshot, byte[] png)
    {
        var key = new PortraitHistoryKey(10, PortraitHistoryEntryPoint.GearsetPortrait, 0, snapshot.GameBuild);
        var identity = new PortraitIdentity(1, 2, 3, 4, 5);
        var record = new PortraitHistoryRecord(1, key, DateTimeOffset.UtcNow, null, PortraitHistoryRecordKind.Edit, snapshot.ToJSON());
        service.SetRecords([record], 0);
        service.Poll("favorite-photo-writer", identity, record.ID, snapshot, true);
        await Task.Delay(250);
        service.Poll("favorite-photo-writer", identity, record.ID, snapshot, true);
        var unused = new FakeTexture(); source.Complete(new(unused));
        var field = typeof(PortraitHistoryPhotos).GetField("capture", BindingFlags.NonPublic | BindingFlags.Instance)!;
        (await (Task<PortraitThumbnailLayers?>)field.GetValue(service)!)?.Dispose();
        field.SetValue(service, null);
        // The controlled boundary supplies only the already encoded managed PNG; all eligibility,
        // cache writing, completion, favorite retention and archive I/O remain production code.
        typeof(PortraitHistoryPhotos).GetField("encoding", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, Task.FromResult(png));
        for (var retry = 0; retry < 100; retry++)
        {
            service.Poll("favorite-photo-writer", identity, record.ID, snapshot, true);
            if (Convert.ToInt64(service.Diagnostics().GetType().GetProperty("saved")!.GetValue(service.Diagnostics())) > 0) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Managed PNG did not complete its real cache writer.");
    }
}

public class FavoriteImageUpload : DispatchProxy
{
    public byte[]? LastImage;
    public int Uploads;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != nameof(ITextureProvider.CreateFromImageAsync))
            throw new InvalidOperationException("Unexpected texture operation " + targetMethod?.Name);
        LastImage = ((ReadOnlyMemory<byte>)args![0]!).ToArray(); Uploads++;
        return Task.FromResult<IDalamudTextureWrap>(new FavoriteUploadedImage());
    }
}

internal sealed class FavoriteUploadedImage : IDalamudTextureWrap
{
    public Dalamud.Bindings.ImGui.ImTextureID Handle => default;
    public int Width => 160;
    public int Height => 256;
    public void Dispose() { }
    public IDalamudTextureWrap CreateWrapSharingLowLevelResource() => throw new InvalidOperationException("No shared resource expected");
}
