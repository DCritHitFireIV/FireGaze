using System.Text.Json;
using FireGaze.PortraitPreview;

internal static class OwnedMigrationTests
{
    public static (int Passed, int Failed) Run()
    {
        var passed = 0; var failed = 0;
        var now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");
        var first = new OwnedCharacterKey(11, 101);
        var alias = new OwnedCharacterKey(22, 101);
        var bag = new OwnedSourceKey(OwnedSourceKind.Inventory);
        var dresser = new OwnedSourceKey(OwnedSourceKind.GlamourDresser);
        var retainerA = new OwnedSourceKey(OwnedSourceKind.RetainerInventory, 501);
        var retainerEquipmentA = new OwnedSourceKey(OwnedSourceKind.RetainerEquipped, 501);
        var retainerB = new OwnedSourceKey(OwnedSourceKind.RetainerInventory, 502);
        OwnedEquipmentCatalog New() => new(id => id is >= 100 and <= 999 ? id : null);
        OwnedItemRecord Item(uint id, int slot = 0) => new(id, 7, slot, 1, 4, 5, 900, true);
        void Check(bool condition, string name) { if (condition) passed++; else { failed++; Console.WriteLine($"FAIL owned-migration {name}"); } }
        void Case(string name, Action test) { try { test(); } catch (Exception ex) { failed++; Console.WriteLine($"FAIL owned-migration {name}: {ex.GetType().Name} {ex.Message}"); } }

        Case("alias reactivation shares only persisted content identity", () =>
        {
            var catalog = New(); catalog.ActivateCharacter(first);
            catalog.ApplyRead(first, dresser, now, [Item(123)]);
            catalog.ApplyRead(first, retainerA, now, [Item(234)]);
            catalog.ActivateCharacter(alias);
            var view = catalog.BuildView(now);
            Check(view.Character == alias, "view retains current full runtime identity");
            Check(view.Items.Select(i => i.ItemID).SequenceEqual(new uint[] { 123, 234 }), "alias sees dresser and retainer records");
            Check(view.Sources.Count == 2 && view.Sources.All(s => s.Status == OwnedReadStatus.PossiblyStale), "alias records require refresh");
            Check(!catalog.ApplyRead(first, dresser, now.AddMinutes(1), [Item(345)]), "old account alias read callback rejected");
            Check(!catalog.MarkUnavailable(first, dresser, "old alias"), "old account alias unavailable callback rejected");
            Check(catalog.ApplyRead(alias, dresser, now.AddMinutes(1), [Item(456)]), "current full identity callback accepted");
            catalog.ActivateCharacter(new(22, 202));
            Check(catalog.BuildView(now).Items.Count == 0, "different content identity remains isolated");
        });

        Case("legacy aliases merge whole latest source snapshots", () =>
        {
            var legacyOwners = new[]
            {
                MigrationFixtures.Owner(11, 101, MigrationFixtures.Source(dresser, now, Item(123)), MigrationFixtures.Source(retainerA, now, Item(234))),
                MigrationFixtures.Owner(22, 101, MigrationFixtures.Source(dresser, now.AddMinutes(1), Item(345)), MigrationFixtures.Source(retainerEquipmentA, now, Item(456))),
                MigrationFixtures.Owner(33, 101, MigrationFixtures.Source(dresser, null), MigrationFixtures.Source(retainerB, now, Item(567))),
                MigrationFixtures.Owner(44, 101, MigrationFixtures.Source(bag, now, Item(678))),
                MigrationFixtures.Owner(11, 202, MigrationFixtures.Source(dresser, now, Item(789))),
            };
            var catalog = New(); Check(catalog.ImportJSON(MigrationFixtures.Legacy(legacyOwners)), "four alias legacy document accepted"); catalog.ActivateCharacter(alias);
            var view = catalog.BuildView(now.AddMinutes(2));
            Check(view.Items.Select(i => i.ItemID).SequenceEqual(new uint[] { 234, 345, 456, 567, 678 }), "latest dresser replaces entire snapshot without item union");
            Check(view.Sources.Count == 5 && view.Sources.All(s => s.Status == OwnedReadStatus.PossiblyStale), "all merged sources imported stale");
            Check(view.Items.Single(i => i.ItemID == 234).Variants.Single().Source == retainerA && view.Items.Single(i => i.ItemID == 456).Variants.Single().Source == retainerEquipmentA && view.Items.Single(i => i.ItemID == 567).Variants.Single().Source == retainerB, "retainer inventories and equipped sources remain separate");
            var metadata = view.Items.Single(i => i.ItemID == 345).Variants.Single();
            Check(metadata.ContainerID == 7 && metadata.Slot == 0 && metadata.Quantity == 1 && metadata.Stain1 == 4 && metadata.Stain2 == 5 && metadata.GlamourID == 900 && metadata.HighQuality, "winning source metadata retained");
            var export = catalog.ExportJSON(); using var doc = JsonDocument.Parse(export);
            Check(doc.RootElement.GetProperty("Version").GetInt32() == 2, "exports current cache schema");
            Check(doc.RootElement.GetProperty("Characters").GetArrayLength() == 2 && !export.Contains("AccountID", StringComparison.Ordinal), "export collapses aliases without persistent account ids");
            var reverse = New(); Check(reverse.ImportJSON(MigrationFixtures.Legacy(legacyOwners.Reverse().ToArray())) && reverse.ExportJSON() == export, "legacy migration independent of alias order");
            var reloaded = New(); Check(reloaded.ImportJSON(export) && reloaded.ExportJSON() == export, "v2 export reload is idempotent");
            reloaded.ActivateCharacter(new(99, 101)); Check(reloaded.BuildView(now).Items.Count == 5, "new session account alias sees imported content records");
            reloaded.ActivateCharacter(new(99, 202)); Check(reloaded.BuildView(now).Items.Single().ItemID == 789, "other content source never merged");
        });

        Case("new empty is authoritative and unknown cannot erase known", () =>
        {
            var catalog = New();
            Check(catalog.ImportJSON(MigrationFixtures.Legacy(
                MigrationFixtures.Owner(11, 101, MigrationFixtures.Source(dresser, now, Item(123)), MigrationFixtures.Source(bag, null)),
                MigrationFixtures.Owner(22, 101, MigrationFixtures.Source(dresser, now.AddSeconds(1)), MigrationFixtures.Source(bag, null)),
                MigrationFixtures.Owner(33, 101, MigrationFixtures.Source(dresser, null)))), "empty and unknown alias sources migrate");
            catalog.ActivateCharacter(alias); var view = catalog.BuildView(now);
            Check(view.Items.Count == 0, "new read empty clears older items");
            Check(view.Sources.Single(s => s.Source == dresser).ReadAt == now.AddSeconds(1) && view.Sources.Single(s => s.Source == dresser).Status == OwnedReadStatus.PossiblyStale, "known empty remains stale read");
            Check(view.Sources.Single(s => s.Source == bag).ReadAt is null && view.Sources.Single(s => s.Source == bag).Status == OwnedReadStatus.Unread, "unknown remains unread after merge");
        });

        Case("timestamp ties require identical complete source contents", () =>
        {
            var catalog = New(); catalog.ActivateCharacter(first); catalog.ApplyRead(first, bag, now, [Item(123)]);
            Check(!catalog.ApplyRead(first, bag, now, [Item(234)]), "live conflicting equal timestamp rejected");
            Check(catalog.BuildView(now).Items.Single().ItemID == 123, "live conflict preserves existing snapshot");
            Check(catalog.ApplyRead(first, bag, now, [Item(123)]), "identical live retry accepted");
            var conflict = MigrationFixtures.Legacy(MigrationFixtures.Owner(11, 101, MigrationFixtures.Source(dresser, now, Item(123))), MigrationFixtures.Owner(22, 101, MigrationFixtures.Source(dresser, now, Item(234))));
            Check(!catalog.ImportJSON(conflict), "legacy equal timestamp conflict fails closed");
            Check(catalog.BuildView(now).Items.Single().ItemID == 123, "import conflict cannot partially replace current catalog");
            var identical = MigrationFixtures.Legacy(MigrationFixtures.Owner(11, 101, MigrationFixtures.Source(dresser, now, Item(123, 0), Item(234, 1))), MigrationFixtures.Owner(22, 101, MigrationFixtures.Source(dresser, now, Item(234, 1), Item(123, 0))));
            Check(catalog.ImportJSON(identical), "identical timestamp snapshots ignore physical enumeration order");
        });

        Case("newer snapshot resolves older timestamp ambiguity independent of owner order", () =>
        {
            var owners = new[]
            {
                MigrationFixtures.Owner(11, 101, MigrationFixtures.Source(dresser, now, Item(123))),
                MigrationFixtures.Owner(22, 101, MigrationFixtures.Source(dresser, now, Item(234))),
                MigrationFixtures.Owner(33, 101, MigrationFixtures.Source(dresser, now.AddSeconds(1), Item(345))),
            };
            var forward = New(); var reverse = New();
            Check(forward.ImportJSON(MigrationFixtures.Legacy(owners)), "latest successful snapshot resolves superseded ambiguity");
            Check(reverse.ImportJSON(MigrationFixtures.Legacy(owners.Reverse().ToArray())) && reverse.ExportJSON() == forward.ExportJSON(), "superseded timestamp conflicts cannot make import order dependent");
            forward.ActivateCharacter(alias); Check(forward.BuildView(now).Items.Single().ItemID == 345, "only latest complete snapshot becomes authoritative");
        });

        Case("invalid migrated sources leave live state untouched", () =>
        {
            var catalog = New(); catalog.ActivateCharacter(first); catalog.ApplyRead(first, bag, now, [Item(123)]);
            var invalid = new[]
            {
                MigrationFixtures.Legacy(MigrationFixtures.Owner(11, 101, MigrationFixtures.Source(bag, null, Item(234)))),
                MigrationFixtures.Legacy(MigrationFixtures.Owner(11, 101, MigrationFixtures.Source(bag, now, Item(234), Item(345)))),
                MigrationFixtures.Legacy(MigrationFixtures.Owner(11, 101, MigrationFixtures.Source(new(OwnedSourceKind.RetainerInventory), now, Item(234)))),
                MigrationFixtures.Legacy(MigrationFixtures.Owner(11, 0, MigrationFixtures.Source(bag, now, Item(234)))),
                MigrationFixtures.Legacy(MigrationFixtures.Owner(11, 101, MigrationFixtures.Source(bag, now, Item(234))), MigrationFixtures.Owner(11, 101, MigrationFixtures.Source(dresser, now, Item(345)))),
                "{\"Version\":2,\"Characters\":[{\"ContentID\":101,\"Sources\":[]},{\"ContentID\":101,\"Sources\":[]}]}",
                "{\"Version\":2,\"Characters\":[{\"ContentID\":0,\"Sources\":[]}]}",
                "[]", "null", "{\"Version\":\"2\",\"Characters\":[]}", "{\"Version\":false,\"Characters\":[]}", "{\"Version\":2}",
                "{\"Version\":2,\"Characters\":[{\"ContentID\":101,\"Sources\":[{\"Source\":{\"Kind\":1,\"RetainerID\":0},\"ReadAt\":\"2026-10-17T12:00:00Z\"}]}]}",
                "{\"Version\":2,\"Characters\":[{\"ContentID\":101,\"Sources\":[{\"Source\":{\"Kind\":1,\"RetainerID\":0},\"Items\":[]}]}]}",
            };
            foreach (var input in invalid)
                Check(!catalog.ImportJSON(input) && catalog.BuildView(now).Items.Single().ItemID == 123, "invalid migration preserves prior state");
        });
        return (passed, failed);
    }
}

internal static class MigrationFixtures
{
    internal sealed record LegacyOwner(OwnedCharacterKey Character, LegacySource[] Sources);
    internal sealed record LegacySource(OwnedSourceKey Source, DateTimeOffset? ReadAt, OwnedItemRecord[] Items);
    internal static LegacyOwner Owner(ulong account, ulong content, params LegacySource[] sources) => new(new(account, content), sources);
    internal static LegacySource Source(OwnedSourceKey source, DateTimeOffset? readAt, params OwnedItemRecord[] items) => new(source, readAt, items);
    internal static string Legacy(params LegacyOwner[] owners) => JsonSerializer.Serialize(new { Version = 1, Characters = owners });
}
