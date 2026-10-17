namespace FireGaze.PortraitPreview;

internal enum OwnedSourceKind { Equipped, Inventory, Armoury, Saddlebag, PremiumSaddlebag, GlamourDresser, Armoire, RetainerInventory, RetainerEquipped }
internal enum OwnedReadStatus { Unread, Current, PossiblyStale }
internal readonly record struct OwnedCharacterKey(ulong AccountID, ulong ContentID);
internal readonly record struct OwnedSourceKey(OwnedSourceKind Kind, ulong RetainerID = 0);
internal sealed record OwnedItemRecord(uint ItemID, uint ContainerID, int Slot, uint Quantity = 1, byte Stain1 = 0, byte Stain2 = 0, uint GlamourID = 0, bool HighQuality = false);
internal sealed record OwnedItemVariant(OwnedSourceKey Source, uint RawItemID, uint ContainerID, int Slot, uint Quantity, byte Stain1, byte Stain2, uint GlamourID, bool HighQuality, OwnedReadStatus Status);
internal sealed record OwnedEquipmentEntry(uint ItemID, IReadOnlyList<OwnedItemVariant> Variants);
internal sealed record OwnedSourceView(OwnedSourceKey Source, OwnedReadStatus Status, DateTimeOffset? ReadAt, int OwnedItemCount, string Reason);
internal sealed record OwnedEquipmentView(OwnedCharacterKey? Character, IReadOnlyList<OwnedEquipmentEntry> Items, IReadOnlyList<OwnedSourceView> Sources);
