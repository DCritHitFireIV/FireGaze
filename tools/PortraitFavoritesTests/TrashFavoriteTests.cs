using FireGaze.PortraitPreview;
using System.Text.Json.Nodes;

internal static class TrashFavoriteTests
{
    internal static int Run(string root, PortraitEditorSnapshot snapshot)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAIL " + name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        var path = Path.Combine(root, "trash", "portrait-favorites-v1.json");
        var trashPath = Path.Combine(root, "trash", "portrait-favorites-trash-v1.json");
        var store = new PortraitFavoritesStore(path);
        var added = store.Add(snapshot, 11, "角色", "可恢复肖像", ["日常"]).Entry!;
        var trash = new PortraitFavoritesTrashStore(trashPath);
        Check(trash.Add(added).Success, "deleted favorite is durably added to the recoverable trash");
        Check(trash.Entries.Count == 1 && trash.Entries[0].ID == added.ID
            && trash.Entries[0].Name == "可恢复肖像", "trash keeps the complete favorite snapshot and identity");
        var reopened = new PortraitFavoritesTrashStore(trashPath);
        Check(reopened.Entries.Count == 1 && reopened.Entries[0].Snapshot.ToJSON() == snapshot.ToJSON(),
            "trash survives restart with the same portrait snapshot");
        Check(reopened.Remove(added.ID).Success && new PortraitFavoritesTrashStore(trashPath).Entries.Count == 0,
            "trash item can be permanently removed after explicit cleanup");
        Check(store.AddExisting(added).Success && new PortraitFavoritesStore(path).Entries.Single().ID == added.ID,
            "a trashed favorite can be restored without changing its stable identity");

        trash = new PortraitFavoritesTrashStore(trashPath);
        Check(trash.Add(added).Success, "trash accepts a complete valid favorite for conflict checks");
        Check(trash.Add(added.Copy()).Status == PortraitFavoritesWriteStatus.Unchanged,
            "retrying the same complete tombstone is idempotent");
        var committed = File.ReadAllBytes(trashPath);
        Check(trash.Add(added with { Name = "different same ID" }).Status == PortraitFavoritesWriteStatus.Conflict
            && committed.SequenceEqual(File.ReadAllBytes(trashPath)) && trash.Entries.Single().Name == added.Name,
            "same-ID trash conflict cannot hide or overwrite a different saved favorite");

        var invalidEntries = new PortraitFavoriteEntry[]
        {
            added with { ID = Guid.NewGuid(), Name = null! },
            added with { ID = Guid.NewGuid(), Name = "" },
            added with { ID = Guid.NewGuid(), SourceName = null! },
            added with { ID = Guid.NewGuid(), SourceName = "角色\n" },
            added with { ID = Guid.NewGuid(), Name = new string('字', 121) },
            added with { ID = Guid.NewGuid(), Tags = null! },
            added with { ID = Guid.NewGuid(), Tags = [new string('x', 33)] },
            added with { ID = Guid.NewGuid(), Tags = ["same", "same"] },
            added with { ID = Guid.NewGuid(), CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(-1) },
            added with { ID = Guid.NewGuid(), UpdatedAt = added.CreatedAt.AddSeconds(-1) },
            added with { ID = Guid.NewGuid(), Snapshot = snapshot with { EntryPoint = 3 } },
        };
        foreach (var invalid in invalidEntries)
            Check(trash.Add(invalid).Status == PortraitFavoritesWriteStatus.InvalidInput
                && trash.Entries.Count == 1 && committed.SequenceEqual(File.ReadAllBytes(trashPath)),
                "invalid complete tombstone is rejected before memory or disk publication");

        for (var index = 0; index < invalidEntries.Length; index++)
        {
            var invalid = invalidEntries[index];
            var damagedPath = Path.Combine(root, "trash", "invalid-entry-" + index + ".json");
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                Version = 1,
                Entries = new[] { new { invalid.ID, DeletedAt = DateTimeOffset.UtcNow, Favorite = invalid } },
            });
            File.WriteAllBytes(damagedPath, bytes);
            var corrupt = new PortraitFavoritesTrashStore(damagedPath);
            Check(corrupt.IsReadOnly && corrupt.Entries.Count == 0 && !corrupt.Remove(invalid.ID).Success
                && bytes.SequenceEqual(File.ReadAllBytes(damagedPath)),
                "invalid persisted full tombstone is preserved read-only instead of reaching gallery rendering");
        }

        var validJson = JsonNode.Parse(File.ReadAllText(trashPath))!;
        foreach (var field in new[] { "Name", "SourceName", "Tags", "Snapshot" })
        {
            var damaged = validJson.DeepClone();
            damaged["Entries"]![0]!["Favorite"]![field] = null;
            var damagedPath = Path.Combine(root, "trash", "invalid-" + field + ".json");
            var bytes = System.Text.Encoding.UTF8.GetBytes(damaged.ToJsonString());
            File.WriteAllBytes(damagedPath, bytes);
            var corrupt = new PortraitFavoritesTrashStore(damagedPath);
            Check(corrupt.IsReadOnly && corrupt.Entries.Count == 0 && !corrupt.Add(added).Success
                && bytes.SequenceEqual(File.ReadAllBytes(damagedPath)),
                "corrupt trash " + field + " becomes read-only and preserves exact original bytes");
        }

        var controlled = new ControlledFiles();
        var faulted = new PortraitFavoritesTrashStore(Path.Combine(root, "trash-fault.json"), files: controlled);
        Check(faulted.Add(added).Success, "trash write-failure fixture has a committed full snapshot");
        var retained = controlled.Bytes!.ToArray();
        controlled.FailWrite = true;
        Check(!faulted.Remove(added.ID).Success && faulted.Entries.Single().ID == added.ID
            && retained.SequenceEqual(controlled.Bytes!), "failed trash removal preserves the only recoverable copy");
        Check(!faulted.Add(added with { ID = Guid.NewGuid() }).Success && faulted.Entries.Count == 1
            && retained.SequenceEqual(controlled.Bytes!), "failed trash addition cannot publish an in-memory tombstone");
        controlled.FailWrite = false;
        Check(faulted.Remove(added.ID).Success && faulted.Entries.Count == 0, "trash operation can retry after storage recovery");

        var limitedDir = Path.Combine(root, "trash-budget");
        var limitedBudget = new PortraitStorageBudget(limitedDir, 128);
        var limited = new PortraitFavoritesTrashStore(Path.Combine(limitedDir, "portrait-favorites-trash-v1.json"), storage: limitedBudget);
        Check(limited.Add(added).Status == PortraitFavoritesWriteStatus.TooLarge && limited.Entries.Count == 0
            && limitedBudget.Snapshot.UsedBytes == 0 && limitedBudget.Snapshot.ReservedBytes == 0,
            "trash shares the portrait budget and refuses over-capacity metadata without a phantom record");
        return checks;
    }
}
