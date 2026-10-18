namespace FireGaze.PortraitPreview;

/// <summary>A complete saved glamour template, with the game's twelve equipment slots.</summary>
internal sealed record PortraitGlamourPlate(int Index, PortraitPiece[] Pieces);

internal sealed record PortraitGlamourPlateCapture(OwnedCharacterKey? Character, IReadOnlyList<PortraitGlamourPlate>? Plates, string Reason);

internal sealed record PortraitGlamourPlateView(OwnedCharacterKey? Character, IReadOnlyList<PortraitGlamourPlate> Plates,
    OwnedReadStatus Status, DateTimeOffset? ReadAt, string Reason);

/// <summary>Builds a portrait-only replacement; it never applies a game glamour template.</summary>
internal static class PortraitGlamourPlateMerge
{
    public static bool TryMerge(PortraitLook current, PortraitGlamourPlate plate, out PortraitLook merged, out string reason)
    {
        merged = current;
        reason = string.Empty;
        if (current is null || current.Pieces is null || current.Pieces.Length != 13 || !IsValid(plate))
        {
            reason = "投影模板数据不完整，请重新打开投影台读取";
            return false;
        }
        if (!plate.Pieces.Any(piece => piece.ItemID != 0))
        {
            reason = "这个投影模板尚未保存装备";
            return false;
        }
        var pieces = new PortraitPiece[13];
        plate.Pieces.CopyTo(pieces, 0);
        pieces[12] = current.Pieces[12];
        merged = new(pieces, current.HeadVisible, current.WeaponVisible, current.Visor);
        return true;
    }

    internal static bool IsValid(PortraitGlamourPlate? plate)
        => plate is not null && (uint)plate.Index < 20 && plate.Pieces is { Length: 12 }
            && plate.Pieces.All(piece => piece.ItemID < 1_000_000
                && (piece.ItemID != 0 || piece.Stain0 == 0 && piece.Stain1 == 0));

    internal static PortraitGlamourPlate Copy(PortraitGlamourPlate plate)
        => new(plate.Index, (PortraitPiece[])plate.Pieces.Clone());
}
