using System.Text.Json;
using FireGaze.PortraitPreview;

internal static class RetiredSourceTests
{
    public static (int Passed, int Failed) Run()
    {
        var passed = 0; var failed = 0;
        var now = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
        var character = new OwnedCharacterKey(11, 101);
        var retired = new OwnedSourceKey(OwnedSourceKind.RetainerEquipped, 501);
        var bag = new OwnedSourceKey(OwnedSourceKind.RetainerInventory, 501);
        OwnedEquipmentCatalog New() => new(id => id);
        void Check(bool condition, string name) { if (condition) passed++; else { failed++; Console.WriteLine($"FAIL retired-source {name}"); } }
        void Case(string name, Action test) { try { test(); } catch (Exception ex) { failed++; Console.WriteLine($"FAIL retired-source {name}: {ex.GetType().Name} {ex.Message}"); } }
        var sources = new[]
        {
            MigrationFixtures.Source(new(OwnedSourceKind.Equipped), now, new OwnedItemRecord(101, 0, 0)),
            MigrationFixtures.Source(new(OwnedSourceKind.Inventory), now, new OwnedItemRecord(102, 0, 0)),
            MigrationFixtures.Source(new(OwnedSourceKind.Armoury), now, new OwnedItemRecord(103, 0, 0)),
            MigrationFixtures.Source(new(OwnedSourceKind.Saddlebag), now, new OwnedItemRecord(104, 0, 0)),
            MigrationFixtures.Source(new(OwnedSourceKind.PremiumSaddlebag), now, new OwnedItemRecord(105, 0, 0)),
            MigrationFixtures.Source(new(OwnedSourceKind.GlamourDresser), now, new OwnedItemRecord(106, 0, 0)),
            MigrationFixtures.Source(new(OwnedSourceKind.Armoire), now, new OwnedItemRecord(107, 0, 0)),
            MigrationFixtures.Source(bag, now, new OwnedItemRecord(108, 10000, 0, 2, 3, 4, 555, true)),
            MigrationFixtures.Source(retired, now, new OwnedItemRecord(999, 11000, 0), new OwnedItemRecord(108, 11000, 1, 1, 7, 8)),
        };
        string V2(params MigrationFixtures.LegacySource[] contentSources) => JsonSerializer.Serialize(new
        {
            Version = 2,
            Characters = new[]
            {
                new { ContentID = 101UL, Sources = contentSources.Length == 0 ? sources : contentSources },
                new { ContentID = 202UL, Sources = new[] { MigrationFixtures.Source(new(OwnedSourceKind.RetainerInventory, 502), now, new OwnedItemRecord(209, 10000, 0)) } },
            },
        });
        bool HasRetiredSource(string text)
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("Characters").EnumerateArray()
                .SelectMany(owner => owner.GetProperty("Sources").EnumerateArray())
                .Any(source => source.GetProperty("Source").GetProperty("Kind").GetInt32() == 8);
        }

        Case("retired live callbacks cannot recreate source", () =>
        {
            var catalog = New(); catalog.ActivateCharacter(character); var revision = catalog.ContentRevision;
            catalog.RegisterSource(retired);
            Check(!catalog.ApplyRead(character, retired, now, [new(999, 11000, 0)]), "retired complete reads rejected");
            Check(!catalog.MarkUnavailable(character, retired, "old callback"), "retired unavailable callbacks ignored");
            Check(catalog.BuildView(now).Sources.Count == 0 && catalog.BuildView(now).Items.Count == 0 && catalog.ContentRevision == revision,
                "retired callbacks create no source, item, or save revision");
            Check(catalog.ApplyRead(character, bag, now, [new(108, 10000, 0, 2, 3, 4)]), "retainer inventory remains accepted");
            Check(catalog.BuildView(now).Items.Single().Variants.Single().Stain2 == 4, "retainer inventory dual dye metadata remains intact");
        });
        foreach (var text in new[] { V2(), MigrationFixtures.Legacy(MigrationFixtures.Owner(11, 101, sources)) })
            Case("old caches retain all supported sources", () =>
            {
                var catalog = new OwnedEquipmentCatalog(id => id == 999 ? throw new InvalidOperationException("Retired item resolved") : id);
                Check(catalog.ImportJSON(text), "old cache imports without resolving retired item metadata");
                catalog.ActivateCharacter(character); var view = catalog.BuildView(now);
                Check(view.Sources.Count == 8 && view.Items.Select(item => item.ItemID).SequenceEqual(new uint[] { 101, 102, 103, 104, 105, 106, 107, 108 }),
                    "all eight retained sources survive without retired-only item");
                var variants = view.Items.Single(item => item.ItemID == 108).Variants;
                Check(variants.Count == 1 && variants[0].Source == bag && variants[0].Quantity == 2 && variants[0].Stain1 == 3 && variants[0].Stain2 == 4
                    && variants[0].GlamourID == 555 && variants[0].HighQuality, "duplicate equipped variant cannot replace surviving inventory metadata");
                Check(!HasRetiredSource(catalog.ExportJSON()), "export cannot reintroduce retired source");
            });

        var root = Path.Combine(Path.GetTempPath(), "firegaze-retired-source-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Case("v2 disk cache is durably cleaned before import", () =>
            {
                var directory = Path.Combine(root, "valid"); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "portrait-owned-cache-v1.json"); var original = System.Text.Encoding.UTF8.GetBytes(V2()); File.WriteAllBytes(path, original);
                var file = new OwnedEquipmentCacheFile(directory); var catalog = New();
                Check(file.Load(catalog, out _), "existing v2 cache loads");
                Check(!HasRetiredSource(File.ReadAllText(path)), "primary is cleaned without waiting for another inventory edit or logout");
                Check(Directory.GetFiles(directory).Where(filePath => filePath != path).Any(filePath => File.ReadAllBytes(filePath).SequenceEqual(original)),
                    "byte exact old primary remains recoverable");
                Check(file.SaveJSON(V2(), out _) && !HasRetiredSource(File.ReadAllText(path)), "queued old v2 worker snapshot cannot recreate retired source on disk");
                var restored = New(); Check(new OwnedEquipmentCacheFile(directory).Load(restored, out _), "cleaned cache survives restart");
                restored.ActivateCharacter(character); Check(restored.BuildView(now).Items.Count == 8 && restored.BuildView(now).Sources.Count == 8,
                    "restart retains other sources and retainer inventory");
                restored.ActivateCharacter(new(11, 202)); Check(restored.BuildView(now).Items.Single().ItemID == 209,
                    "sanitizing one character does not erase another character retainer inventory");
                var reintroduced = sources.Select(source => source.Source == bag
                    ? source with { Items = [new(108, 10000, 0, 3, 3, 4, 555, true)] } : source).ToArray();
                var laterOriginal = V2(reintroduced); File.WriteAllText(path, laterOriginal);
                Check(file.Load(restored, out _) && !HasRetiredSource(File.ReadAllText(path)),
                    "an older plugin build may reintroduce equipped data without blocking later cleanup");
                restored.ActivateCharacter(character); Check(restored.BuildView(now).Items.Single(item => item.ItemID == 108).Variants.Single().Quantity == 3,
                    "second cleanup keeps newer retained-source observations");
                Check(Directory.GetFiles(directory).Where(filePath => filePath != path).Any(filePath => File.ReadAllBytes(filePath).SequenceEqual(original)),
                    "second cleanup preserves the first immutable recovery copy");
            });
            Case("retired-source backup failure preserves primary and active view", () =>
            {
                var directory = Path.Combine(root, "backup-failure"); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "portrait-owned-cache-v1.json"); var original = V2(); File.WriteAllText(path, original);
                var catalog = New(); catalog.ActivateCharacter(character); catalog.ApplyRead(character, new(OwnedSourceKind.Inventory), now, [new(222, 0, 0)]);
                var file = new OwnedEquipmentCacheFile(directory, new FailingBackup());
                Check(!file.Load(catalog, out _) && File.ReadAllText(path) == original && catalog.BuildView(now).Items.Single().ItemID == 222,
                    "backup failure cannot partially publish cleanup or erase active observations");
                Check(!file.Save(catalog, out _) && File.ReadAllText(path) == original, "failed cleanup blocks later writer from erasing unimported sources");
            });
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            if (absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(absolute).StartsWith("firegaze-retired-source-tests-", StringComparison.Ordinal) && Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
        return (passed, failed);
    }

    private sealed class FailingBackup : OwnedCacheMigrationFileIO
    {
        public override void CreateBackup(string path, byte[] contents) => throw new IOException("Injected backup failure");
    }
}
