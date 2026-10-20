using System.Text;
using System.Text.Json;
using FireGaze.PortraitPreview;

internal static class OwnedCacheMigrationFileTests
{
    public static (int Passed, int Failed) Run()
    {
        var passed = 0; var failed = 0;
        const string cacheName = "portrait-owned-cache-v1.json";
        var now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");
        var character = new OwnedCharacterKey(44, 101);
        var bag = new OwnedSourceKey(OwnedSourceKind.Inventory);
        var dresser = new OwnedSourceKey(OwnedSourceKind.GlamourDresser);
        var legacy = MigrationFixtures.Legacy(
            MigrationFixtures.Owner(11, 101, MigrationFixtures.Source(dresser, now, new OwnedItemRecord(123, 7, 0))),
            MigrationFixtures.Owner(22, 101, MigrationFixtures.Source(dresser, now.AddMinutes(1), new OwnedItemRecord(234, 7, 0))));
        var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(legacy)).ToArray();
        OwnedEquipmentCatalog New()
        {
            var catalog = new OwnedEquipmentCatalog(id => id is >= 100 and <= 999 ? id : null);
            catalog.ActivateCharacter(character); catalog.ApplyRead(character, bag, now, [new(345, 0, 0)]); return catalog;
        }
        void Check(bool condition, string name) { if (condition) passed++; else { failed++; Console.WriteLine($"FAIL owned-cache-migration {name}"); } }
        void Case(string name, Action<string> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "firegaze-owned-migration-test-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(directory); test(directory); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL owned-cache-migration {name}: {ex.GetType().Name} {ex.Message}"); }
            finally
            {
                var absolute = Path.GetFullPath(directory);
                if (absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("firegaze-owned-migration-test-", StringComparison.Ordinal) && Directory.Exists(absolute)) Directory.Delete(absolute, true);
            }
        }

        Case("current cache rotates one recoverable generation and protects it on corrupt load", directory =>
        {
            var path = Path.Combine(directory, cacheName); var backup = path + ".bak";
            var catalog = New(); var cache = new OwnedEquipmentCacheFile(directory);
            Check(cache.Save(catalog, out _) && !File.Exists(backup), "first current-schema save has no fabricated prior generation");
            var firstGeneration = File.ReadAllBytes(path);
            catalog.ApplyRead(character, bag, now.AddSeconds(1), [new(456, 0, 0)]);
            Check(cache.Save(catalog, out _), "second current-schema generation commits");
            var secondGeneration = File.ReadAllBytes(path);
            Check(File.Exists(backup) && File.ReadAllBytes(backup).SequenceEqual(firstGeneration), "normal replacement keeps the exact preceding owned cache");
            catalog.ApplyRead(character, bag, now.AddSeconds(2), [new(567, 0, 0)]);
            Check(cache.Save(catalog, out _) && File.Exists(backup) && File.ReadAllBytes(backup).SequenceEqual(secondGeneration), "third normal save rotates one backup to the second generation");
            var recovery = New();
            Check(File.Exists(backup) && recovery.ImportJSON(File.ReadAllText(backup)) && recovery.BuildView(now).Items.Single().ItemID == 456,
                "owned backup independently imports the previous complete source contents");
            File.WriteAllText(path, "broken current cache");
            var refused = new OwnedEquipmentCacheFile(directory);
            Check(!refused.Load(catalog, out _), "corrupt current cache is refused despite a valid backup");
            catalog.ApplyRead(character, bag, now.AddSeconds(3), [new(678, 0, 0)]);
            Check(!refused.Save(catalog, out _) && File.ReadAllText(path) == "broken current cache"
                && File.Exists(backup) && File.ReadAllBytes(backup).SequenceEqual(secondGeneration),
                "fresh observations cannot overwrite a corrupt owned primary or its valid backup");
            var preserved = New();
            Check(File.Exists(backup) && preserved.ImportJSON(File.ReadAllText(backup)) && preserved.BuildView(now).Items.Single().ItemID == 456,
                "owned backup remains independently readable after primary corruption");
            Check(Directory.GetFiles(directory).Count(file => Path.GetFileName(file) != ".portrait-storage.lock") == 2, "ordinary owned saves retain only primary and a single backup plus the zero-byte coordination lease");
        });

        Case("legacy migration commits backup and v2 before importing", directory =>
        {
            var path = Path.Combine(directory, cacheName); var backup = path + ".v1.bak"; File.WriteAllBytes(path, original);
            var catalog = New(); var cache = new OwnedEquipmentCacheFile(directory);
            Check(cache.Load(catalog, out _), "valid legacy load succeeds");
            Check(File.Exists(backup) && File.ReadAllBytes(backup).SequenceEqual(original), "migration preserves byte exact legacy including BOM");
            using (var doc = JsonDocument.Parse(File.ReadAllText(path))) Check(doc.RootElement.GetProperty("Version").GetInt32() == 2 && !File.ReadAllText(path).Contains("AccountID", StringComparison.Ordinal), "primary file migrates to account independent schema");
            var view = catalog.BuildView(now.AddMinutes(2));
            Check(view.Character == character && view.Items.Single().ItemID == 234 && view.Sources.Single().Status == OwnedReadStatus.PossiblyStale, "only committed merged sources become visible stale");
            var beforeReload = File.ReadAllBytes(path); Check(cache.Load(catalog, out _) && File.ReadAllBytes(path).SequenceEqual(beforeReload), "v2 reload does not repeat migration");
            Check(!cache.SaveJSON(legacy, out _) && File.ReadAllBytes(path).SequenceEqual(beforeReload), "queued v1 snapshot cannot downgrade migrated primary");
            catalog.ApplyRead(character, dresser, now.AddMinutes(3), [new(456, 7, 0)]);
            Check(cache.Save(catalog, out _) && File.ReadAllBytes(backup).SequenceEqual(original), "normal v2 writes preserve immutable legacy backup");
            Check(Directory.GetFiles(directory).Count(file => Path.GetFileName(file) != ".portrait-storage.lock") == 3 && File.Exists(path + ".bak"), "normal post-migration writes retain one rolling backup alongside immutable legacy recovery");
        });

        Case("backup failure blocks migration and later writer", directory =>
        {
            var path = Path.Combine(directory, cacheName); File.WriteAllBytes(path, original); Directory.CreateDirectory(path + ".v1.bak");
            var catalog = New(); var cache = new OwnedEquipmentCacheFile(directory);
            Check(!cache.Load(catalog, out _) && catalog.BuildView(now).Items.Single().ItemID == 345, "failed backup cannot accept legacy import");
            Check(File.ReadAllBytes(path).SequenceEqual(original), "failed backup keeps original bytes");
            Check(!cache.Save(catalog, out _) && File.ReadAllBytes(path).SequenceEqual(original), "periodic writer cannot overwrite unbacked legacy");
            Directory.Delete(path + ".v1.bak");
            Check(cache.Load(catalog, out _) && catalog.BuildView(now).Items.Single().ItemID == 234, "explicit retry may complete once backup available");
        });

        Case("replace failure keeps disk and live cache", directory =>
        {
            var path = Path.Combine(directory, cacheName); File.WriteAllBytes(path, original);
            var catalog = New(); var cache = new OwnedEquipmentCacheFile(directory);
            using (var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(!cache.Load(catalog, out _) && catalog.BuildView(now).Items.Single().ItemID == 345, "failed atomic replace cannot accept migrated cache");
                Check(File.ReadAllBytes(path).SequenceEqual(original), "replace failure preserves original bytes");
                Check(!cache.Save(catalog, out _), "writer remains blocked after failed replace");
                Check(Directory.GetFiles(directory).Count(file => Path.GetFileName(file) != ".portrait-storage.lock") == 2 && File.ReadAllBytes(path + ".v1.bak").SequenceEqual(original), "replace failure keeps backup and removes temporary output");
            }
            Check(cache.Load(catalog, out _) && catalog.BuildView(now).Items.Single().ItemID == 234, "replace retry reuses exact backup safely");
        });

        Case("existing unequal backup cannot be silently replaced", directory =>
        {
            var path = Path.Combine(directory, cacheName); File.WriteAllBytes(path, original); File.WriteAllText(path + ".v1.bak", "older recovery bytes");
            var catalog = New(); var cache = new OwnedEquipmentCacheFile(directory);
            Check(!cache.Load(catalog, out _) && !cache.Save(catalog, out _), "unequal backup fails closed and blocks writer");
            Check(File.ReadAllBytes(path).SequenceEqual(original) && File.ReadAllText(path + ".v1.bak") == "older recovery bytes", "both original and existing recovery copy preserved");
        });

        foreach (var truncate in new[] { true, false })
            Case("new backup must read back byte exact", directory =>
            {
                var path = Path.Combine(directory, cacheName); File.WriteAllBytes(path, original);
                var io = new BackupFaultIO((backupPath, bytes) =>
                {
                    var broken = truncate ? bytes[..^7] : bytes.ToArray();
                    if (!truncate) broken[^1] ^= 1;
                    File.WriteAllBytes(backupPath, broken);
                });
                var catalog = New(); var cache = new OwnedEquipmentCacheFile(directory, io);
                Check(!cache.Load(catalog, out _) && catalog.BuildView(now).Items.Single().ItemID == 345, "new partial or altered backup cannot authorize migration");
                Check(File.ReadAllBytes(path).SequenceEqual(original), "backup readback failure preserves legacy primary bytes");
                Check(!cache.Save(catalog, out _) && File.ReadAllBytes(path).SequenceEqual(original), "backup readback failure keeps periodic writer blocked");
            });

        foreach (var disappear in new[] { false, true })
            Case("primary changes during backup cannot be overwritten", directory =>
            {
                var path = Path.Combine(directory, cacheName); File.WriteAllBytes(path, original);
                var changed = Encoding.UTF8.GetBytes(MigrationFixtures.Legacy(MigrationFixtures.Owner(33, 101, MigrationFixtures.Source(dresser, now.AddMinutes(2), new OwnedItemRecord(456, 7, 0)))));
                var io = new BackupFaultIO((_, _) => { if (disappear) File.Delete(path); else File.WriteAllBytes(path, changed); });
                var catalog = New(); var cache = new OwnedEquipmentCacheFile(directory, io);
                Check(!cache.Load(catalog, out _) && catalog.BuildView(now).Items.Single().ItemID == 345, "changed or missing primary cannot authorize migration");
                Check(disappear ? !File.Exists(path) : File.ReadAllBytes(path).SequenceEqual(changed), "migration leaves concurrently changed source untouched");
                Check(File.ReadAllBytes(path + ".v1.bak").SequenceEqual(original), "original bytes remain in verified recovery copy");
                Check(!cache.Save(catalog, out _) && (disappear ? !File.Exists(path) : File.ReadAllBytes(path).SequenceEqual(changed)), "writer cannot replace changed or recreate missing source after aborted migration");
                Check(Directory.GetFiles(directory).All(file => !file.EndsWith(".tmp", StringComparison.Ordinal)), "aborted source comparison removes temporary output");
            });

        foreach (var bad in new[] { "not json", "[]", "null", "{\"Version\":false,\"Characters\":[]}", "{\"Version\":\"2\",\"Characters\":[]}", "{\"Version\":2}", "{\"Version\":2,\"Characters\":[{\"ContentID\":0,\"Sources\":[]}]}" })
            Case("bad file blocks writer", directory =>
            {
                var path = Path.Combine(directory, cacheName); var bytes = Encoding.UTF8.GetBytes(bad); File.WriteAllBytes(path, bytes);
                var catalog = New(); var cache = new OwnedEquipmentCacheFile(directory);
                Check(!cache.Load(catalog, out _) && catalog.BuildView(now).Items.Single().ItemID == 345, "invalid file cannot replace observations");
                Check(!cache.Save(catalog, out _) && File.ReadAllBytes(path).SequenceEqual(bytes), "invalid original protected from later writer");
            });

        Case("uninspected existing file cannot be overwritten", directory =>
        {
            var path = Path.Combine(directory, cacheName); File.WriteAllBytes(path, original); var cache = new OwnedEquipmentCacheFile(directory);
            Check(!cache.Save(New(), out _) && File.ReadAllBytes(path).SequenceEqual(original), "writer must load existing data before replacing it");
        });

        Case("invalid detached v2 snapshot cannot replace last good primary", directory =>
        {
            var catalog = New(); var cache = new OwnedEquipmentCacheFile(directory); Check(cache.Save(catalog, out _), "new primary can be created");
            var path = Path.Combine(directory, cacheName); var prior = File.ReadAllBytes(path);
            var invalid = "{\"Version\":2,\"Characters\":[{\"ContentID\":101,\"Sources\":[{\"Source\":{\"Kind\":7,\"RetainerID\":0},\"ReadAt\":null,\"Items\":[]}]}]}";
            Check(!cache.SaveJSON(invalid, out _) && File.ReadAllBytes(path).SequenceEqual(prior), "structurally invalid source cannot overwrite valid cache");
        });

        Case("oversize original cannot be erased by periodic writer", directory =>
        {
            var path = Path.Combine(directory, cacheName); using (var stream = new FileStream(path, FileMode.CreateNew)) stream.SetLength(32_000_001);
            var catalog = New(); var cache = new OwnedEquipmentCacheFile(directory);
            Check(!cache.Load(catalog, out _) && !cache.Save(catalog, out _) && new FileInfo(path).Length == 32_000_001, "oversize original protected");
        });
        return (passed, failed);
    }

    private sealed class BackupFaultIO(Action<string, byte[]> afterBackup) : OwnedCacheMigrationFileIO
    {
        public override void CreateBackup(string path, byte[] contents)
        {
            base.CreateBackup(path, contents);
            afterBackup(path, contents);
        }
    }
}
