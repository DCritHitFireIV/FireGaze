using System.Text;
using System.Text.Json.Nodes;
using FireGaze.PortraitPreview;

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException("FAIL " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}
#if !HAS_FAVORITES
Check(false, "favorites persist complete portable snapshots independently of history");
#else
var root = Path.Combine(Path.GetTempPath(), "FireGazeFavoritesTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    checks += await FavoriteSharedBudgetTests.Run(root, Snapshot());
    checks += TrashFavoriteTests.Run(root, Snapshot());
    checks += FavoriteMasonryTests.Run();
    var now = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.FromHours(8));
    var path = Path.Combine(root, "shared", "portrait-favorites.json");
    var store = new PortraitFavoritesStore(path, () => now);
    var snapshot = Snapshot();
    var plateStore = new PortraitFavoritesStore(Path.Combine(root, "plate.json"), () => now);
    Check(plateStore.Add(Snapshot() with { EntryPoint = 2 }, 123, "角色一", "铭牌肖像", []).Success,
        "adventurer plate snapshots are valid favorite sources");
    var add = store.Add(snapshot, 123, "角色一", "  午后  ", ["暖色", "暖色", "  日常 "]);
    Check(add.Success && add.Entry is { Name: "午后" } && add.Entry.Tags.SequenceEqual(["暖色", "日常"]), "add normalizes labels and stable distinct tags");
    var id = add.Entry!.ID;
    Check(id != Guid.Empty && add.Entry.CreatedAt == now && add.Entry.SourceContentID == 123, "favorite has a stable ID and provenance without a history reference");
    var reopened = new PortraitFavoritesStore(path);
    Check(reopened.Entries.Count == 1 && reopened.Entries[0].Snapshot.ToJSON() == snapshot.ToJSON(), "successful add is immediately durable without Dispose or game logout");
    Check(reopened.Entries[0].ID == id && reopened.Entries[0].SourceName == "角色一", "a different character can browse the shared collection and original provenance");
    snapshot.Customize[2] = 90; snapshot.Look.Pieces[3] = new(9999, 0, 0);
    add.Entry.Snapshot.Customize[3] = 80;
    var exposed = store.Entries[0]; exposed.Snapshot.Look.Pieces[0] = new(7, 0, 0); exposed.Tags[0] = "污染";
    Check(store.Entries[0].Snapshot.Customize[2] == 0 && store.Entries[0].Snapshot.Customize[3] == 0
        && store.Entries[0].Snapshot.Look.Pieces[3].ItemID == 1003 && store.Entries[0].Snapshot.Look.Pieces[0].ItemID == 1000
        && store.Entries[0].Tags[0] == "暖色", "input snapshots and returned entry arrays cannot mutate stored favorites");
    now = now.AddMinutes(5);
    Check(store.Rename(id, "傍晚").Success && store.SetTags(id, ["正式", "暖色"]).Success, "rename and tags can be saved as independent edits");
    reopened = new PortraitFavoritesStore(path);
    Check(reopened.Entries[0].Name == "傍晚" && reopened.Entries[0].Tags.SequenceEqual(["正式", "暖色"])
        && reopened.Entries[0].ID == id && reopened.Entries[0].CreatedAt != reopened.Entries[0].UpdatedAt, "metadata edits persist immediately while retaining identity and creation time");
    var before = File.ReadAllBytes(path);
    foreach (var invalid in new[] { "", " ", new string('字', 121), "换\n行" })
        Check(!store.Rename(id, invalid).Success, "invalid favorite name is rejected");
    Check(!store.SetTags(id, [new string('字', 33)]).Success && !store.SetTags(id, Enumerable.Range(0, 33).Select(i => "t" + i)).Success,
        "oversize tags and tag count are rejected");
    Check(!store.Add(Snapshot() with { Customize = new byte[2] }, 2, "二", "坏数据", []).Success
        && !store.Add(Snapshot() with { Composition = new() { CameraYaw = float.NaN } }, 2, "二", "坏数据", []).Success,
        "malformed and nonfinite portable snapshots cannot be stored");
    Check(!store.Rename(Guid.NewGuid(), "不存在").Success && !store.Remove(Guid.NewGuid()).Success,
        "missing favorites return explicit failure");
    Check(before.SequenceEqual(File.ReadAllBytes(path)) && store.Entries.Count == 1, "invalid edits preserve both memory and existing durable data");

    var export = store.Export([id]);
    var other = new PortraitFavoritesStore(Path.Combine(root, "import.json"));
    Check(other.Import(export).Success && other.Entries[0].ID == id && other.Entries[0].Snapshot.ToJSON() == store.Entries[0].Snapshot.ToJSON(),
        "portable export and import preserve the complete snapshot and stable identity");
    Check(other.Import(export).Success && other.Entries.Count == 1, "repeated identical import does not duplicate favorites");
    var conflict = JsonNode.Parse(export)!; conflict["Entries"]![0]!["Name"] = "冲突";
    Check(!other.Import(conflict.ToJsonString()).Success && other.Entries[0].Name == "傍晚", "conflicting imported IDs never overwrite a local favorite silently");
    var second = store.Add(Snapshot(42), 999, "角色二", "夜晚", ["冷色"]);
    Check(second.Success && store.Entries.Count == 2 && other.Import(store.Export()).Success && other.Entries.Count == 2,
        "one shared library can retain snapshots from multiple characters and import new entries atomically");
    var invalidImport = JsonNode.Parse(store.Export())!; invalidImport["Entries"]![0]!["Snapshot"]!["Customize"] = "";
    var importedBefore = File.ReadAllBytes(Path.Combine(root, "import.json"));
    Check(!other.Import(invalidImport.ToJsonString()).Success && importedBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "import.json"))),
        "one malformed imported entry rejects the entire import without partial persistence");
    Check(!other.Import(new string('x', PortraitFavoritesStore.MaximumBytes + 1)).Success, "oversize import is rejected before decoding");
    Check(store.Remove(id).Success && new PortraitFavoritesStore(path).Entries.All(entry => entry.ID != id), "remove is immediately durable and leaves other favorites intact");

    var backupPath = Path.Combine(root, "backed-up.json");
    var backed = new PortraitFavoritesStore(backupPath, () => now);
    var backedEntry = backed.Add(Snapshot(), 1, "一", "第一版", []).Entry!;
    var firstGeneration = File.ReadAllBytes(backupPath);
    Check(backed.Rename(backedEntry.ID, "第二版").Success
        && File.Exists(backupPath + ".bak") && firstGeneration.SequenceEqual(File.ReadAllBytes(backupPath + ".bak")),
        "a durable favorite edit immediately preserves the exact previous valid document");
    var secondGeneration = File.ReadAllBytes(backupPath);
    Check(backed.SetTags(backedEntry.ID, ["定稿"]).Success
        && secondGeneration.SequenceEqual(File.ReadAllBytes(backupPath + ".bak"))
        && Directory.GetFiles(root, "backed-up.json.bak*").Length == 1,
        "favorite backups rotate a single bounded previous generation across metadata edits");
    var backupBytes = File.ReadAllBytes(backupPath + ".bak");
    File.WriteAllText(backupPath, "{damaged-current");
    var damagedCurrent = new PortraitFavoritesStore(backupPath);
    Check(damagedCurrent.IsReadOnly && !damagedCurrent.Rename(backedEntry.ID, "不得覆盖").Success
        && File.ReadAllText(backupPath) == "{damaged-current"
        && backupBytes.SequenceEqual(File.ReadAllBytes(backupPath + ".bak"))
        && new PortraitFavoritesStore(backupPath + ".bak").Entries.Single().Name == "第二版",
        "corrupt current favorites preserve original bytes and a usable previous snapshot backup");

    var files = new ControlledFiles();
    var failing = new PortraitFavoritesStore("fake.json", () => now, files);
    var first = failing.Add(Snapshot(), 1, "一", "保留", []);
    Check(first.Success, "controlled persistence accepts the initial durable document");
    var saved = files.Bytes!.ToArray();
    files.FailWrite = true;
    Check(!failing.Rename(first.Entry!.ID, "不可写").Success && failing.Entries[0].Name == "保留" && saved.SequenceEqual(files.Bytes!),
        "failed rename leaves memory and durable contents unchanged");
    Check(!failing.Add(Snapshot(), 2, "二", "不加入", []).Success && failing.Entries.Count == 1,
        "failed add cannot create an in-memory phantom favorite");
    Check(!failing.SetTags(first.Entry.ID, ["不保存"]).Success && failing.Entries[0].Tags.Length == 0,
        "failed tag edit cannot show unsaved state as successful");
    Check(!failing.Remove(first.Entry.ID).Success && failing.Entries.Count == 1,
        "failed delete keeps the previous favorite");
    files.FailWrite = false;
    Check(failing.Rename(first.Entry.ID, "恢复可写").Success, "a transient write error can be retried without reloading the store");

    foreach (var damaged in new[] { "{", "{\"Version\":2,\"Entries\":[]}", "{\"Version\":1,\"Entries\":null}" })
    {
        var corruptPath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(corruptPath, damaged);
        var corrupt = new PortraitFavoritesStore(corruptPath);
        Check(corrupt.IsReadOnly && !corrupt.Add(Snapshot(), 1, "一", "不覆盖", []).Success && File.ReadAllText(corruptPath) == damaged,
            "corrupt or future documents become read-only without destroying the original file");
    }
    var duplicate = JsonNode.Parse(store.Export())!;
    var existing = duplicate["Entries"]![0]!.DeepClone(); duplicate["Entries"]!.AsArray().Add(existing);
    var duplicatePath = Path.Combine(root, "duplicate.json"); File.WriteAllText(duplicatePath, duplicate.ToJsonString());
    Check(new PortraitFavoritesStore(duplicatePath).IsReadOnly, "duplicate stable IDs make an on-disk document read-only instead of dropping entries");

    var lockPath = Path.Combine(root, "locked.json");
    var locked = new PortraitFavoritesStore(lockPath);
    var lockEntry = locked.Add(Snapshot(), 1, "一", "原件", []).Entry!;
    using (var exclusive = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        Check(!locked.Rename(lockEntry.ID, "应失败").Success && locked.Entries[0].Name == "原件", "a real locked destination cannot replace the original or advance memory");
    Check(new PortraitFavoritesStore(lockPath).Entries[0].Name == "原件"
        && !Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(),
        "atomic persistence leaves no temporary files after replacement failure");
    Check(locked.Rename(lockEntry.ID, "可重试").Success, "unlocking the destination permits a later atomic replacement");
}
finally { Directory.Delete(root, recursive: true); }

static PortraitEditorSnapshot Snapshot(uint offset = 0) => new()
{
    GameBuild = "2026.10.19", EntryPoint = 0,
    Look = new(Enumerable.Range(0, 13).Select(index => new PortraitPiece((uint)(1000 + index) + offset, (byte)index, (byte)(index + 1))).ToArray(), true, false, true),
    Composition = new() { CameraYaw = 1.25f, CameraDistance = 4.5f, Frame = 25, Decoration = 18, Paused = true, BannerTimeline = 44 },
};
#endif
Console.WriteLine($"PortraitFavoritesTests: {checks} passed, 0 failed");

#if HAS_FAVORITES
sealed class ControlledFiles : IPortraitFavoritesFiles
{
    public byte[]? Bytes;
    public bool FailWrite;
    public byte[]? Read(string path, int maximumBytes) => Bytes?.ToArray();
    public void WriteAtomically(string path, byte[] document)
    {
        if (FailWrite) throw new IOException("test volume is unavailable");
        Bytes = document.ToArray();
    }
}
#endif
