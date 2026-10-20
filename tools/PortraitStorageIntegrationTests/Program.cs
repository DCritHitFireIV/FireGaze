using FireGaze.PortraitPreview;

var root = Path.Combine(Path.GetTempPath(), "firegaze-storage-integration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;
async Task Case(string name, Func<string, Task> run)
{
    var directory = Path.Combine(root, name);
    Directory.CreateDirectory(directory);
    try { await run(directory); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    finally { Directory.Delete(directory, true); }
}
void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}
void FillBudget(string directory)
{
    using var file = new FileStream(Path.Combine(directory, "portrait-favorites-v1.json.bak"), FileMode.CreateNew);
    file.SetLength(128L * 1024 * 1024);
}
try
{
    await Case("history-full-budget", async directory =>
    {
        FillBudget(directory);
        var path = Path.Combine(directory, "portrait-edit-history-v1.json");
        using var history = new PortraitHistoryStore(path);
        var accepted = history.Record(new(20, PortraitHistoryEntryPoint.GearsetPortrait, 0, "test"), "{\"pose\":1}");
        Check(accepted.Status == PortraitHistoryWriteStatus.Added, "history accepts into the bounded worker queue");
        Check(!await history.FlushAsync(), "full shared budget must reject physical history publication");
        Check(!File.Exists(path) && history.PersistenceError is not null, "failed physical write stays visibly undurable");
        Check(new FileInfo(Path.Combine(directory, "portrait-favorites-v1.json.bak")).Length == 128L * 1024 * 1024,
            "protected preexisting backup is retained");
    });
    await Case("owned-full-budget", directory =>
    {
        FillBudget(directory);
        var catalog = new OwnedEquipmentCatalog(id => id);
        var owner = new OwnedCharacterKey(1, 20);
        catalog.ActivateCharacter(owner);
        catalog.ApplyRead(owner, new(OwnedSourceKind.Inventory), DateTimeOffset.UtcNow, [new(123, 1, 0)]);
        var cache = new OwnedEquipmentCacheFile(directory);
        Check(!cache.Save(catalog, out var reason), "full shared budget must reject owned-cache publication");
        Check(!File.Exists(Path.Combine(directory, "portrait-owned-cache-v1.json")) && reason.Contains("128"),
            "owned-cache capacity reason is explicit and primary remains absent");
        return Task.CompletedTask;
    });
    await Case("glamour-full-budget", async directory =>
    {
        FillBudget(directory);
        var catalog = new PortraitGlamourPlateCatalog();
        catalog.ActivateCharacter(new(1, 20));
        Check(catalog.ApplyRead(new(1, 20), DateTimeOffset.UtcNow,
            Enumerable.Range(0, 20).Select(index => new PortraitGlamourPlate(index,
                Enumerable.Repeat(new PortraitPiece(0, 0, 0), 12).ToArray())).ToArray()), "complete native-free fixture advances the revision");
        using var cache = new PortraitGlamourPlateCache(directory);
        Check(cache.QueueIfNeeded(catalog), "glamour update reaches the existing bounded writer");
        Check(!await cache.FlushAsync(), "full shared budget must reject glamour-cache publication");
        Check(cache.LastWriteSucceeded == false && !File.Exists(Path.Combine(directory, PortraitGlamourPlateCache.FileName)),
            "failed glamour publication cannot become a durable revision");
    });
}
finally
{
    var absolute = Path.GetFullPath(root);
    if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(absolute).StartsWith("firegaze-storage-integration-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing cleanup outside this test's exact temporary root.");
    Directory.Delete(absolute, true);
}
Console.WriteLine($"Storage integration: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
