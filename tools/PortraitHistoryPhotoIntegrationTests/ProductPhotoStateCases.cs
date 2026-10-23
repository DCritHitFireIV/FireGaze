using System.Reflection;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal static class ProductPhotoStateCases
{
    internal static async Task<int> RunAsync(string root, PortraitIdentity identity)
    {
        var snapshot = new PortraitEditorSnapshot { GameBuild = "product-photo", EntryPoint = 1,
            Composition = new() { BannerTimeline = 2, AnimationProgress = .25f } };
        var advanced = snapshot with { Composition = snapshot.Composition with { AnimationProgress = .9f } };
        var method = typeof(PortraitThumbnailCache).GetMethod("KeyForSnapshot", [typeof(ulong), typeof(PortraitEditorSnapshot)]);
        if (method is null) throw new InvalidOperationException("Playing history and favorites require one canonical snapshot photo identity.");
        string Key(ulong owner, PortraitEditorSnapshot value) => (string)method.Invoke(null, [owner, value])!;
        if (Key(10, snapshot) != Key(10, advanced)) throw new InvalidOperationException("Natural playback must share its actual representative photograph.");
        var paused = snapshot with { Composition = snapshot.Composition with { Paused = true } };
        if (Key(10, paused) == Key(10, snapshot) || Key(10, paused) == Key(10, paused with
            { Composition = paused.Composition with { AnimationProgress = .9f } }) || Key(20, snapshot) == Key(10, snapshot))
            throw new InvalidOperationException("Paused exact frames, playback and characters must never share photographs.");
        if (snapshot.Composition.AnimationProgress != .25f) throw new InvalidOperationException("Photo normalization must not rewrite the user's restoration progress.");

        var textures = DispatchProxy.Create<ITextureProvider, SafePhotoProvider>();
        var readback = DispatchProxy.Create<ITextureReadbackProvider, SafePhotoReadback>();
        var source = new PortraitThumbnailSource();
        using var photos = new PortraitHistoryPhotos(Path.Combine(root, "product-playing.bin"), textures, readback, source, 0);
        var recordKey = new PortraitHistoryKey(10, PortraitHistoryEntryPoint.GearsetPortrait, 0, "product");
        var record = new PortraitHistoryRecord(1, recordKey, DateTimeOffset.UtcNow, null, PortraitHistoryRecordKind.Edit, snapshot.ToJSON());
        photos.SetRecords([record], 0);
        photos.Poll("product", identity, 1, snapshot, true);
        if (photos.GetHistoryPhotoStatus(1).ToString() != "Generating")
            throw new InvalidOperationException("A real current history photo reports automatic generation during its stable-delay admission.");
        await Task.Delay(250);
        photos.Poll("product", identity, 1, advanced, true);
        if (source.Calls != 1) throw new InvalidOperationException("Playback progress cannot continuously reset the stable capture gate.");
        if (photos.GetHistoryPhotoStatus(1).ToString() != "Generating")
            throw new InvalidOperationException("An admitted history capture stays generating while the provider completes.");
        source.Complete(new(new FakeTexture()));
        photos.Poll("product", identity, 1, advanced, true); photos.Draw();
        for (var attempt = 0; attempt != 200 && !photos.HasPhotographedRecord(1); attempt++)
        { photos.Poll("product", identity, 1, advanced, true); await Task.Delay(5); }
        if (!photos.HasPhotographedRecord(1)) throw new InvalidOperationException("A genuine playing capture must become durable under its canonical identity.");
        if (photos.GetHistoryPhotoStatus(1) != PortraitHistoryPhotoStatus.Loading)
            throw new InvalidOperationException("A durable photo awaiting texture upload reports loading rather than generating or missing.");
        photos.SetFavoriteSnapshots([(10, advanced)]);
        if (photos.FavoritePhotoStatus(10, advanced) != PortraitFavoritePhotoStatus.Available)
            throw new InvalidOperationException("A later playing favorite must reuse the same real logical-state photograph.");

        var legacyPath = Path.Combine(root, "product-legacy.bin");
        var legacyKey = PortraitThumbnailCache.KeyForSnapshot("10|" + snapshot.ToJSON());
        var legacy = new PortraitThumbnailCache(legacyPath, PortraitThumbnailCache.MaximumBytes);
        if (!legacy.TryPut(legacyKey, new EncodingReadbackProxy().Png)) throw new InvalidOperationException("legacy image fixture failed");
        using var old = new PortraitHistoryPhotos(legacyPath, textures, readback, new(), 0);
        old.SetRecords([record], 0);
        if (!old.HasPhotographedRecord(1)) throw new InvalidOperationException("Upgrade must preserve photographs stored under the previous playback-progress key.");
        old.SetFavoriteSnapshots([(10, advanced)]);
        if (old.FavoritePhotoStatus(10, advanced) != PortraitFavoritePhotoStatus.Available || !legacy.Contains(legacyKey))
            throw new InvalidOperationException("Legacy photographs must migrate to favorite canonical references without deleting their original bytes.");
        var favoriteSource = new PortraitThumbnailSource();
        using var favorite = new PortraitHistoryPhotos(Path.Combine(root, "product-favorite-current.bin"), textures, readback, favoriteSource, 0);
        favorite.SetRecords([record], 0);
        var newAction = snapshot with { Composition = snapshot.Composition with { BannerTimeline = 5 } };
        favorite.SetFavoriteSnapshots([(10, newAction)]);
        if (favorite.FavoritePhotoStatus(10, newAction) != PortraitFavoritePhotoStatus.Missing)
            throw new InvalidOperationException("An old missing favorite must not claim a capture is running before its real state is current.");
        favorite.Poll("favorite", identity, 1, newAction, true, contentID: 20);
        if (favoriteSource.Calls != 0 || favorite.FavoritePhotoStatus(10, newAction) != PortraitFavoritePhotoStatus.Missing)
            throw new InvalidOperationException("A different player's editor cannot fulfill an intentional favorite photo.");
        favorite.Poll("favorite", identity, 1, newAction, true, contentID: 10);
        if (favorite.FavoritePhotoStatus(10, newAction) != PortraitFavoritePhotoStatus.Pending)
            throw new InvalidOperationException("A currently qualified favorite honestly reports its stable-delay capture as pending.");
        for (var attempt = 0; attempt != 200 && favoriteSource.Calls == 0; attempt++)
        { favorite.Poll("favorite", identity, 1, newAction, true, contentID: 10); await Task.Delay(5); }
        if (favoriteSource.Calls != 1) throw new InvalidOperationException("A real current favorite needs a photo even when the history cursor belongs to another action.");
        favoriteSource.Complete(new(new FakeTexture()));
        for (var attempt = 0; attempt != 200 && favorite.FavoritePhotoStatus(10, newAction) != PortraitFavoritePhotoStatus.Available; attempt++)
        { favorite.Poll("favorite", identity, 1, newAction, true, contentID: 10); favorite.Draw(); await Task.Delay(5); }
        if (favorite.FavoritePhotoStatus(10, newAction) != PortraitFavoritePhotoStatus.Available || favorite.HasPhotographedRecord(1))
            throw new InvalidOperationException("Favorite-only capture must become durable without falsely photographing another history action.");
        var abandonedSource = new PortraitThumbnailSource();
        using var abandoned = new PortraitHistoryPhotos(Path.Combine(root, "product-abandoned-current.bin"), textures, readback, abandonedSource, 0);
        abandoned.SetRecords([record], 0);
        abandoned.SetFavoriteSnapshots([(10, snapshot)]);
        abandoned.Poll("abandoned", identity, 1, snapshot, true, contentID: 10);
        await Task.Delay(250);
        abandoned.Poll("abandoned", identity, 1, snapshot, true, contentID: 10);
        if (abandonedSource.Calls != 1 || abandoned.GetHistoryPhotoStatus(1).ToString() != "Generating"
            || abandoned.FavoritePhotoStatus(10, snapshot) != PortraitFavoritePhotoStatus.Pending)
            throw new InvalidOperationException("Only an actual qualified pending capture may advertise automatic photo generation.");
        abandoned.Poll("abandoned", identity, 1, snapshot, false, contentID: 10);
        if (abandoned.GetHistoryPhotoStatus(1) != PortraitHistoryPhotoStatus.Missing
            || abandoned.FavoritePhotoStatus(10, snapshot) != PortraitFavoritePhotoStatus.Missing)
            throw new InvalidOperationException("A capture invalidated by lost readiness must stop claiming history and favorite generation before its late provider returns.");
        var abandonedTexture = new FakeTexture();
        abandonedSource.Complete(new(abandonedTexture));
        abandoned.Poll("abandoned", identity, 1, snapshot, false, contentID: 10);
        if (abandoned.HasPhotographedRecord(1) || abandonedTexture.Disposes != 1
            || abandoned.GetHistoryPhotoStatus(1) != PortraitHistoryPhotoStatus.Missing)
            throw new InvalidOperationException("An invalidated late photo must retire once without publishing or reviving pending status.");
        Console.WriteLine("PASS canonical playing representative photographs, exact paused isolation, durable favorite reuse and legacy preservation");
        Console.WriteLine("PASS current favorite-only capture has real pending and durable states without wrong-owner or historical cross-action reuse");
        Console.WriteLine("PASS history generation reports real demand and invalidated late providers cannot impersonate current work");
        return 19;
    }
}
