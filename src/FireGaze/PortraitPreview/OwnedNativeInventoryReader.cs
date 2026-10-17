using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal sealed record OwnedCabinetItem(uint CabinetRowID, uint ItemID);
internal sealed record OwnedRetainerIdentity(ulong RetainerID, string Name);
internal sealed record OwnedSourceObservation(OwnedSourceKey Source, IReadOnlyList<OwnedItemRecord>? Records, string Reason);
internal sealed record OwnedNativeCapture(OwnedCharacterKey? Character, IReadOnlyList<OwnedSourceObservation> Sources, IReadOnlyList<OwnedRetainerIdentity> Retainers, string Reason);

/// <summary>
/// Read-only adapter over the installed ClientStructs. Invoke only on Framework and supply a fresh identity.
/// An unavailable group returns null records, never an authoritative empty list. This adapter opens no UI.
/// </summary>
internal sealed unsafe class OwnedNativeInventoryReader(
    Func<bool> isFrameworkThread,
    Func<OwnedCharacterKey?> currentCharacter,
    Func<IEnumerable<OwnedCabinetItem>> cabinetRows,
    Func<ulong, bool>? isRetainerContainerOpen = null)
{
    private readonly OwnedRetainerReadGate retainerLoads = new();

    /// <summary>Call at the start of every Framework update, including when no portrait editor is open.</summary>
    public void ObserveRetainerLoads()
    {
        if (!isFrameworkThread()) throw new InvalidOperationException("Retainer load observations require the Framework thread.");
        var character = currentCharacter();
        if (character is not { AccountID: > 0, ContentID: > 0 })
        {
            retainerLoads.Observe(null, 0, new Dictionary<uint, bool>());
            return;
        }
        var manager = RetainerManager.Instance();
        var inventory = InventoryManager.Instance();
        var selected = manager == null || !manager->IsReady ? null : manager->GetActiveRetainer();
        if (inventory == null || selected == null || selected->RetainerId == 0 || manager->LastSelectedRetainerId != selected->RetainerId)
        {
            retainerLoads.Observe(null, 0, new Dictionary<uint, bool>());
            return;
        }
        var selectedID = selected->RetainerId;
        var loaded = new Dictionary<uint, bool>();
        foreach (var type in Containers(OwnedSourceKind.RetainerInventory).Concat(Containers(OwnedSourceKind.RetainerEquipped)))
        {
            var container = inventory->GetInventoryContainer(type);
            // A missing pointer is unknown. Only the typed IsLoaded=false field arms freshness proof.
            if (container != null && container->Type == type) loaded[(uint)type] = container->IsLoaded;
        }
        var after = manager->GetActiveRetainer();
        if (currentCharacter() != character || after == null || after->RetainerId != selectedID || manager->LastSelectedRetainerId != selectedID)
            retainerLoads.Observe(null, 0, new Dictionary<uint, bool>());
        else retainerLoads.Observe(character, selectedID, loaded);
    }

    public OwnedNativeCapture Capture()
    {
        if (!isFrameworkThread()) throw new InvalidOperationException("Inventory reads require the Framework thread.");
        var character = currentCharacter();
        if (character is not { AccountID: > 0, ContentID: > 0 }) return new(null, [], [], "尚未登录角色");
        var observations = new List<OwnedSourceObservation>();
        var retainers = new List<OwnedRetainerIdentity>();
        var inventory = InventoryManager.Instance();
        if (inventory == null) return new(character, [], [], "物品管理器不可用");

        foreach (var source in new[] { OwnedSourceKind.Equipped, OwnedSourceKind.Inventory, OwnedSourceKind.Armoury, OwnedSourceKind.Saddlebag, OwnedSourceKind.PremiumSaddlebag })
            observations.Add(ReadGroup(inventory, new(source)));

        var mirage = MirageManager.Instance();
        if (mirage == null || !mirage->PrismBoxLoaded)
            observations.Add(new(new(OwnedSourceKind.GlamourDresser), null, "投影台尚未读取"));
        else
        {
            var rows = new List<OwnedItemRecord>();
            for (var index = 0; index < mirage->PrismBoxItemIds.Length; index++)
            {
                var itemID = mirage->PrismBoxItemIds[index];
                if (itemID != 0) rows.Add(new(itemID, 0, index, 1, mirage->PrismBoxStain0Ids[index], mirage->PrismBoxStain1Ids[index]));
            }
            observations.Add(new(new(OwnedSourceKind.GlamourDresser), rows.AsReadOnly(), "已读取投影台记录"));
        }

        var ui = UIState.Instance();
        if (ui == null || !ui->Cabinet.IsCabinetLoaded())
            observations.Add(new(new(OwnedSourceKind.Armoire), null, "收藏柜尚未读取"));
        else
        {
            var rows = new List<OwnedItemRecord>();
            foreach (var row in cabinetRows())
                if (row.ItemID != 0 && ui->Cabinet.IsItemInCabinet(row.CabinetRowID))
                    rows.Add(new(row.ItemID, 0, checked((int)row.CabinetRowID)));
            observations.Add(new(new(OwnedSourceKind.Armoire), rows.AsReadOnly(), "已读取收藏柜记录"));
        }

        var finder = ItemFinderModule.Instance();
        var manager = RetainerManager.Instance();
        if (manager != null && manager->IsReady)
        {
            var activeRetainer = manager->GetActiveRetainer();
            var activeID = activeRetainer == null ? 0 : activeRetainer->RetainerId;
            foreach (var retainer in manager->Retainers)
            {
                if (retainer.RetainerId == 0) continue;
                var name = retainer.Name;
                var length = name.IndexOf((byte)0);
                retainers.Add(new(retainer.RetainerId, System.Text.Encoding.UTF8.GetString(length < 0 ? name : name[..length])));
                foreach (var kind in new[] { OwnedSourceKind.RetainerInventory, OwnedSourceKind.RetainerEquipped })
                {
                    var source = new OwnedSourceKey(kind, retainer.RetainerId);
                    if (activeID != retainer.RetainerId || manager->LastSelectedRetainerId != activeID || isRetainerContainerOpen?.Invoke(activeID) != true)
                        observations.Add(new(source, null, "雇员容器尚未打开，沿用已读取记录"));
                    else if (!retainerLoads.CanRead(character.Value, source))
                        observations.Add(new(source, null, "尚未观察到本轮雇员容器重新加载，保留已读取记录"));
                    else
                    {
                        var read = ReadGroup(inventory, source);
                        var after = manager->GetActiveRetainer();
                        if (after == null || after->RetainerId != activeID || manager->LastSelectedRetainerId != activeID)
                            observations.Add(new(source, null, "读取期间雇员已切换"));
                        else if (read.Records is { } liveRecords)
                        {
                            if (finder == null || finder->CharacterContentId != character.Value.ContentID || !finder->IsRetainerManagerReady
                                || !finder->IsRetainerCurrent(activeID) || !finder->RetainerInventories.TryGetValue(activeID, out var keyed, false)
                                || keyed.Value == null || !MatchesRetainerCache(kind, liveRecords, (nint)keyed.Value))
                                observations.Add(new(source, null, "雇员物品归属尚未完成核验，沿用已读取记录"));
                            else observations.Add(read);
                        }
                        else observations.Add(read);
                    }
                }
            }
        }
        if (currentCharacter() != character) return new(null, [], [], "读取期间角色已切换，已丢弃本次快照");
        return new(character, observations.AsReadOnly(), retainers.AsReadOnly(), "");
    }

    private static OwnedSourceObservation ReadGroup(InventoryManager* manager, OwnedSourceKey source)
    {
        var addresses = new List<nint>();
        foreach (var type in Containers(source.Kind)) addresses.Add((nint)manager->GetInventoryContainer(type));
        return TryReadLoadedContainers(source, addresses, out var records, out var reason)
            ? new(source, records, "已读取物品记录") : new(source, null, reason);
    }

    /// <summary>
    /// Validates a complete caller-selected group using typed ClientStructs fields. The capture layer supplies
    /// all containers for the requested source; this boundary is also independently tested on struct memory.
    /// </summary>
    public static bool TryReadLoadedContainers(OwnedSourceKey source, IReadOnlyList<nint> addresses, out IReadOnlyList<OwnedItemRecord> records, out string reason)
    {
        records = [];
        reason = "来源容器尚未加载";
        if (addresses.Count == 0) return false;
        var allowed = Containers(source.Kind);
        var collected = new List<OwnedItemRecord>();
        var seen = new HashSet<InventoryType>();
        foreach (var address in addresses)
        {
            var container = (InventoryContainer*)address;
            if (container == null || !container->IsLoaded || !allowed.Contains(container->Type) || !seen.Add(container->Type)
                || container->Size < 0 || container->Size > 1_000 || (container->Size != 0 && container->Items == null)) return false;
            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->Items + slot;
                if (item->ItemId == 0) continue;
                if (item->IsSymbolic || item->Quantity < 0 || item->Container != container->Type || item->Slot != slot)
                {
                    reason = "容器物品状态尚未稳定";
                    return false;
                }
                if (item->Quantity == 0) continue;
                collected.Add(new(item->ItemId, (uint)container->Type, slot, (uint)item->Quantity, item->Stains[0], item->Stains[1], item->GlamourId,
                    (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0));
            }
        }
        records = collected.AsReadOnly();
        reason = "";
        return true;
    }

    public static bool MatchesRetainerCache(OwnedSourceKind kind, IReadOnlyList<OwnedItemRecord> records, nint cache)
    {
        if (cache == 0 || kind is not (OwnedSourceKind.RetainerInventory or OwnedSourceKind.RetainerEquipped)) return false;
        var ownerCache = (ItemFinderRetainerInventory*)cache;
        var cached = new Dictionary<uint, ulong>();
        var live = new Dictionary<uint, ulong>();
        var itemIDs = kind == OwnedSourceKind.RetainerInventory ? ownerCache->ItemIds : ownerCache->EquippedItemIds;
        for (var index = 0; index < itemIDs.Length; index++)
        {
            var itemID = itemIDs[index] % 1_000_000;
            if (itemID == 0) continue;
            var quantity = kind == OwnedSourceKind.RetainerInventory ? ownerCache->ItemCount[index] : 1UL;
            cached[itemID] = cached.GetValueOrDefault(itemID) + quantity;
        }
        foreach (var item in records)
        {
            var itemID = item.ItemID % 1_000_000;
            if (itemID == 0 || item.Quantity == 0) continue;
            live[itemID] = live.GetValueOrDefault(itemID) + item.Quantity;
        }
        return cached.Count == live.Count && cached.All(pair => live.GetValueOrDefault(pair.Key) == pair.Value);
    }

    private static InventoryType[] Containers(OwnedSourceKind kind) => kind switch
    {
        OwnedSourceKind.Equipped => [InventoryType.EquippedItems],
        OwnedSourceKind.Inventory => [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4],
        OwnedSourceKind.Armoury => [InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
            InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings],
        OwnedSourceKind.Saddlebag => [InventoryType.SaddleBag1, InventoryType.SaddleBag2],
        OwnedSourceKind.PremiumSaddlebag => [InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2],
        OwnedSourceKind.RetainerInventory => [InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
            InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7],
        OwnedSourceKind.RetainerEquipped => [InventoryType.RetainerEquippedItems],
        _ => [],
    };
}
