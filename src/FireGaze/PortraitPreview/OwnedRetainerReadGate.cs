using FFXIVClientStructs.FFXIV.Client.Game;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Authorizes retainer dye metadata only after every required container was observed unloaded, then loaded,
/// during the same account/character/selected-retainer epoch. Item-ID equality alone is insufficient.
/// </summary>
internal sealed class OwnedRetainerReadGate
{
    private OwnedCharacterKey? owner;
    private ulong retainerID;
    private readonly HashSet<uint> seenUnloaded = [];
    private readonly HashSet<uint> currentlyLoaded = [];

    public void Observe(OwnedCharacterKey? character, ulong selectedRetainerID, IReadOnlyDictionary<uint, bool> loaded)
    {
        if (character is not { AccountID: > 0, ContentID: > 0 } || selectedRetainerID == 0)
        {
            owner = null;
            retainerID = 0;
            seenUnloaded.Clear();
            currentlyLoaded.Clear();
            return;
        }
        if (owner != character || retainerID != selectedRetainerID)
        {
            owner = character;
            retainerID = selectedRetainerID;
            seenUnloaded.Clear();
        }
        currentlyLoaded.Clear();
        foreach (var (containerID, isLoaded) in loaded)
        {
            if (isLoaded) currentlyLoaded.Add(containerID);
            else seenUnloaded.Add(containerID);
        }
    }

    public bool CanRead(OwnedCharacterKey character, OwnedSourceKey source) => owner == character && CanRead(source);

    public bool CanRead(OwnedSourceKey source)
    {
        if (owner is null || retainerID == 0 || source.RetainerID != retainerID) return false;
        var required = source.Kind switch
        {
            OwnedSourceKind.RetainerInventory => new[] { InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
                InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7 },
            OwnedSourceKind.RetainerEquipped => [InventoryType.RetainerEquippedItems],
            _ => [],
        };
        return required.Length > 0 && required.All(type => seenUnloaded.Contains((uint)type) && currentlyLoaded.Contains((uint)type));
    }
}
