using System.Reflection;
using FireGaze;
using FireGaze.RepoAudit;
using FireGaze.UI;

var failures = new List<string>();
var checks = 0;
void Check(bool value, string name)
{
    checks++;
    Console.WriteLine((value ? "PASS " : "FAIL ") + name);
    if (!value) failures.Add(name);
}
void BindCache(TestImageCache cache)
{
    var flags = BindingFlags.Static | BindingFlags.NonPublic;
    var type = typeof(PluginIconLookup);
    type.GetField("imageCache", flags)!.SetValue(null, cache);
    type.GetField("iconMapField", flags)!.SetValue(null, typeof(TestImageCache).GetField("pluginIconMap"));
    type.GetField("loadedTextureProp", flags)!.SetValue(null, null);
    type.GetField("resolved", flags)!.SetValue(null, true);
    type.GetField("failed", flags)!.SetValue(null, false);
}
InstalledPluginEntry Entry(string name, TestInstalledPlugin? installed = null) => new()
{
    InternalName = name, DisplayName = name, IconURL = "https://example.invalid/" + name + ".png",
    RawPlugin = installed!, IsThirdParty = true,
};
bool IsWrap(TestImageCache cache, string key, TestWrap wrap) => cache.pluginIconMap.TryGetValue(key, out var value)
    && value is TestImageCache.LoadedIcon loaded && ReferenceEquals(loaded.Texture, wrap);

InstallerIconIndexRefreshCases.Run(Check);

var root = Path.Combine(Path.GetTempPath(), "firegaze-icon-reuse-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
    var installed = Entry("Example", new(firstId));
    var installedTwin = Entry("Example", new(secondId));
    var discovery = Entry("Example");
    var cache = new TestImageCache(); BindCache(cache);
    var wrap = new TestWrap(42); Plugin.Textures.Next = wrap;
    var decodeBefore = Plugin.Textures.Decodes;
    using (var store = new IconStore(Path.Combine(root, "opened-first"), () => true))
    {
        store.ScheduleWarmUp([installed, installedTwin]);
        store.WarmUpStep(4); // First installer open has no local files or decoded textures.
        Check(!cache.pluginIconMap.ContainsKey(firstId.ToString()), "empty initial installer cache does not invent an icon");
        Check(store.SaveDownloaded(discovery, [137,80,78,71,13,10,26,10], "image/png"), "discovery result enters the real disk cache");
        Check(store.EnsureTexture(discovery) && store.TryGetHandle(discovery, out var handle) && handle == wrap.Handle,
            "discovery decodes and retains the provider-owned wrap");
        Check(IsWrap(cache, firstId.ToString(), wrap) && IsWrap(cache, secondId.ToString(), wrap),
            "late discovery icon reaches both installed plugin keys after an empty first warmup");
        Check(store.TryGetHandle(installed, out var reused) && reused == wrap.Handle && Plugin.Textures.Decodes == decodeBefore + 1,
            "installer reuses the held discovery texture without another decode");
        var foreign = new TestImageCache.LoadedIcon(new TestWrap(99), DateTime.Now);
        cache.pluginIconMap[firstId.ToString()] = foreign;
        store.TryGetHandle(installed, out _);
        Check(ReferenceEquals(cache.pluginIconMap[firstId.ToString()], foreign), "reusing a held icon never overwrites an existing native cache entry");
        store.Dispose();
        Check(ReferenceEquals(cache.pluginIconMap[firstId.ToString()], foreign)
            && !cache.pluginIconMap.ContainsKey(secondId.ToString()) && !cache.pluginIconMap.ContainsKey("Example"),
            "unload removes only owned injections while retaining a replacement cache entry");
        Check(wrap.Disposals == 0, "IconStore does not double-dispose the texture provider's held wrap");
    }

    cache = new TestImageCache(); BindCache(cache);
    wrap = new TestWrap(64); Plugin.Textures.Next = wrap;
    using (var store = new IconStore(Path.Combine(root, "retired-handle"), () => true))
    {
        store.ScheduleWarmUp([installed]); store.WarmUpStep(4);
        store.SaveDownloaded(discovery, [137,80,78,71,13,10,26,10], "image/png");
        store.EnsureTexture(discovery); store.TryGetHandle(discovery, out _);
        cache.pluginIconMap.Clear(); // Native cache entries can expire independently.
        wrap.Available = false;
        Check(!store.TryGetHandle(installed, out _) && !cache.pluginIconMap.ContainsKey(firstId.ToString()),
            "a held wrap with a retired native handle cannot be injected into an installer cache miss");
    }

    cache = new TestImageCache(); BindCache(cache);
    wrap = new TestWrap(77); Plugin.Textures.Next = wrap;
    using (var store = new IconStore(Path.Combine(root, "discovery-first"), () => true))
    {
        store.SaveDownloaded(discovery, [137,80,78,71,13,10,26,10], "image/png");
        store.EnsureTexture(discovery); store.TryGetHandle(discovery, out _);
        foreach (var image in Directory.EnumerateFiles(store.CacheDirectory, "*.png")) File.Delete(image);
        var before = Plugin.Textures.Decodes;
        store.ScheduleWarmUp([installed]); store.WarmUpStep(4);
        Check(IsWrap(cache, firstId.ToString(), wrap) && Plugin.Textures.Decodes == before,
            "first installer warmup can reuse a held discovery wrap even after its disk file was removed");
    }

    cache = new TestImageCache(); BindCache(cache);
    wrap = new TestWrap(88); Plugin.Textures.Next = wrap;
    using (var store = new IconStore(Path.Combine(root, "installed-later"), () => true))
    {
        store.ScheduleWarmUp([]); store.WarmUpStep(4);
        store.SaveDownloaded(discovery, [137,80,78,71,13,10,26,10], "image/png");
        store.EnsureTexture(discovery); store.TryGetHandle(discovery, out _);
        foreach (var image in Directory.EnumerateFiles(store.CacheDirectory, "*.png")) File.Delete(image);
        var before = Plugin.Textures.Decodes;
        store.ScheduleWarmUp([installed]); store.WarmUpStep(4);
        Check(IsWrap(cache, firstId.ToString(), wrap) && Plugin.Textures.Decodes == before,
            "a newly installed plugin reuses a previously discovered held texture after index refresh");
        store.ScheduleWarmUp([installedTwin]); store.WarmUpStep(4);
        Check(IsWrap(cache, secondId.ToString(), wrap) && Plugin.Textures.Decodes == before,
            "an installed owner key change reuses the same texture after index refresh");
        store.Dispose();
        Check(!cache.pluginIconMap.ContainsKey(firstId.ToString()) && !cache.pluginIconMap.ContainsKey(secondId.ToString()),
            "unload removes owned injections from all refreshed installer snapshots");
    }

    cache = new TestImageCache(); BindCache(cache);
    wrap = new TestWrap(91); Plugin.Textures.Next = wrap;
    using (var store = new IconStore(Path.Combine(root, "inflight-refresh"), () => true))
    {
        var pending = new TaskCompletionSource<Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap>();
        Plugin.Textures.Pending = pending.Task;
        store.SaveDownloaded(discovery, [137,80,78,71,13,10,26,10], "image/png");
        store.ScheduleWarmUp([installed]); store.WarmUpStep(1);
        var before = Plugin.Textures.Decodes;
        store.ScheduleWarmUp([installedTwin]); store.WarmUpStep(1);
        pending.SetResult(wrap);
        store.WarmUpStep(1);
        Check(IsWrap(cache, secondId.ToString(), wrap) && Plugin.Textures.Decodes == before,
            "an index refresh retains an in-flight decode and delivers it to the new owner key");
        Plugin.Textures.Pending = null;
    }

    cache = new TestImageCache(); BindCache(cache);
    using (var store = new IconStore(Path.Combine(root, "bounded-refresh"), () => true))
    {
        var entries = Enumerable.Range(0, 5).Select(i => Entry("Bounded" + i, new(Guid.NewGuid()))).ToArray();
        foreach (var entry in entries)
        {
            var remote = Entry(entry.InternalName);
            store.SaveDownloaded(remote, [137,80,78,71,13,10,26,10], "image/png");
            store.EnsureTexture(remote); store.TryGetHandle(remote, out _);
        }
        cache.pluginIconMap.Clear();
        store.ScheduleWarmUp(entries); store.WarmUpStep(1);
        Check(cache.pluginIconMap.Count == 1, "a refresh processes at most the requested icon budget in one UI frame");
        store.WarmUpStep(4);
        Check(cache.pluginIconMap.Count == 5, "later bounded steps finish the remaining installer icon refresh");
    }
    Console.WriteLine($"IconReuseTests: {checks - failures.Count}/{checks} checks passed");
    return failures.Count == 0 ? 0 : 1;
}
finally { Directory.Delete(root, true); }
