using FireGaze.PortraitPreview;
using Lumina.Excel.Sheets;

internal static class EquipmentMetadataCases
{
    internal static void Run(Action<bool, string> check)
    {
        var feature = new PortraitPreviewFeature();
        var source = new MetadataTarget();
        var session = new PortraitPreviewSession(source);
        check(session.Open(source.Identity), "template display fixture opens a real preview session");
        var pieces = new PortraitPiece[12];
        pieces[2] = new(300, 4, 5);
        check(PortraitGlamourPlateMerge.TryMerge(session.Current!, new(0, pieces), out var merged, out _),
            "saved template creates a detached wardrobe replacement");
        source.Look = merged;
        check(session.ObserveLook(merged), "a successfully applied template publishes its actual wardrobe");
        var item = feature.GetItem(PortraitSlot.Head, session.Current!.Pieces[2].ItemID);
        check(item is { ID: 300, Name: "模板头饰", Icon: 3300, DyeChannels: 2, Available: true },
            "template equipment absent from the inventory snapshot immediately has its icon, name and two dye channels");
        check(session.Current.Pieces[2] == new PortraitPiece(300, 4, 5)
            && session.Original!.Pieces[2] == new PortraitPiece(100, 1, 2),
            "display metadata preserves both template dyes and the original restoration baseline");

        source.Look = source.Look.Copy();
        source.Look.Pieces[2] = new(400, 6, 7);
        check(session.ObserveLook(source.Capture(source.Identity)!),
            "a native template change publishes the next observed wardrobe");
        item = feature.GetItem(PortraitSlot.Head, session.Current.Pieces[2].ItemID);
        check(item is { ID: 400, Name: "原生模板头饰", Icon: 4400, DyeChannels: 2 }
            && session.Current.Pieces[2] == new PortraitPiece(400, 6, 7),
            "native template changes resolve newly observed equipment without waiting for inventory polling");

        check(session.ObserveLook(session.Original!), "restoring the original wardrobe can update the display");
        check(feature.GetItem(PortraitSlot.Head, session.Current.Pieces[2].ItemID) is { ID: 100, Name: "原头饰" }
            && session.Close() && source.Look.Pieces[2] == new PortraitPiece(100, 1, 2),
            "display lookup never mutates the rollback wardrobe or its dyes");
        check(feature.GetItem(PortraitSlot.Head, 0) is null && feature.GetItem(PortraitSlot.Head, 99999) is null,
            "unknown and explicitly empty equipment do not borrow another slot's icon");
        check(feature.GetItem(PortraitSlot.Facewear, 7) is { ID: 7, Name: "眼镜", Icon: 700 }
            && feature.GetItem(PortraitSlot.Facewear, 300) is null,
            "facewear uses its own table even when equipment has the same number");
        check(feature.GetItem(PortraitSlot.Head, 500) is { Available: false },
            "newly resolved equipment retains restrictions for the Framework-published race and sex");
        check(feature.GetItem(PortraitSlot.Head, 600) is { Available: false },
            "newly resolved equipment without a model remains unavailable");
    }

    private sealed class MetadataTarget : IPortraitPreviewTarget
    {
        internal PortraitIdentity Identity = new(1, 2, 3, 4, 5);
        internal PortraitLook Look = new(new PortraitPiece[13], true, true, false);
        internal MetadataTarget() => Look.Pieces[2] = new(100, 1, 2);
        public bool IsCurrent(PortraitIdentity identity) => identity == Identity;
        public PortraitLook? Capture(PortraitIdentity identity) => IsCurrent(identity) ? Look.Copy() : null;
        public bool Apply(PortraitIdentity identity, PortraitLook look)
        {
            if (!IsCurrent(identity)) return false;
            Look = look.Copy();
            return true;
        }
    }
}

namespace FireGaze.PortraitPreview
{
    // Only the external Excel row/native service boundary is replaced. The production
    // lookup methods and actual session/plate merge are compiled unchanged in this suite.
    internal sealed unsafe partial class PortraitPreviewFeature
    {
        private readonly Dictionary<uint, PortraitBrowserItem> metadata = new()
        {
            [100] = new(100, "原头饰", 1100, 2, true, ""),
        };
        private readonly Dictionary<uint, Item> equipment = new()
        {
            [300] = new() { RowId = 300, Name = "模板头饰", Icon = 3300, DyeCount = 2, ModelMain = 1 },
            [400] = new() { RowId = 400, Name = "原生模板头饰", Icon = 4400, DyeCount = 2, ModelMain = 1 },
            [500] = new() { RowId = 500, Name = "异族装备", ModelMain = 1,
                EquipRestriction = new(1, new() { Hyur = true, Male = true }) },
            [600] = new() { RowId = 600, Name = "无模型装备" },
        };
        private readonly Dictionary<uint, Glasses> glasses = new() { [7] = new() { Name = "眼镜", Icon = 700 } };
        private readonly byte[] actualCustomize = [8, 1];
    }
}

namespace Lumina.Excel.Sheets
{
    internal readonly record struct TestSheetReference<T>(uint RowId, T Value);
    internal readonly record struct Item
    {
        public uint RowId { get; init; }
        public string Name { get; init; }
        public uint Icon { get; init; }
        public byte DyeCount { get; init; }
        public ulong ModelMain { get; init; }
        public TestSheetReference<EquipRestriction> EquipRestriction { get; init; }
        public TestSheetReference<byte> LevelItem { get; init; }
        public ushort LevelEquip { get; init; }
    }
    internal readonly record struct Glasses
    {
        public string Name { get; init; }
        public int Icon { get; init; }
    }
    internal readonly record struct EquipRestriction
    {
        public bool Hyur { get; init; }
        public bool Elezen { get; init; }
        public bool Lalafell { get; init; }
        public bool Miqote { get; init; }
        public bool Roegadyn { get; init; }
        public bool AuRa { get; init; }
        public bool Hrothgar { get; init; }
        public bool Viera { get; init; }
        public bool Male { get; init; }
        public bool Female { get; init; }
    }
}

namespace FFXIVClientStructs.FFXIV.Client.Game.UI
{
    internal unsafe struct PlayerState
    {
        public byte Race => 0;
        public byte Sex => 0;
        public static PlayerState* Instance() => throw new InvalidOperationException("Display metadata must only read Framework-published values.");
    }
}
