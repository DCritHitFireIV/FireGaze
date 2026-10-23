using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

/// <summary>Detached identity for one owned-scene handoff.</summary>
internal readonly record struct PortraitOwnedCharaViewProvenance(
    int SnapshotVersion, PortraitIdentity Source, string GameBuild, string SDKContract, string Digest)
{
    internal bool IsValid => SnapshotVersion == PortraitOwnedCharaViewSnapshot.CurrentVersion
        && Source.IsValid && Source.Generation > 0
        && GameBuild is { Length: > 0 and <= 128 }
        && !string.IsNullOrWhiteSpace(GameBuild) && GameBuild == GameBuild.Trim()
        && !GameBuild.StartsWith("FFCS:", StringComparison.OrdinalIgnoreCase)
        && SDKContract is { Length: > 0 and <= 128 }
        && !string.IsNullOrWhiteSpace(SDKContract) && SDKContract == SDKContract.Trim()
        && Digest is { Length: 64 } && Digest.All(Uri.IsHexDigit);
}

/// <summary>
/// A complete, value-only handoff from the live editor. The digest is built from named scalar
/// fields and bounded arrays instead of native struct bytes, so ABI padding never changes the
/// identity of an otherwise equal appearance.
/// </summary>
internal sealed class PortraitOwnedCharaViewSnapshot
{
    public const int CurrentVersion = 1;
    public const int CurrentSnapshotVersion = CurrentVersion;
    private static readonly string DefaultSDKContract = typeof(CharaViewPortrait).Assembly.GetName().Version
        + "/" + typeof(CharaViewPortrait).Module.ModuleVersionId.ToString("D");

    private readonly CharaViewCharacterData characterData;
    private readonly CharaViewModelData modelData;
    private readonly CharaViewItem[] items;

    public int Version { get; }
    public int SnapshotVersion => Version;
    public PortraitIdentity Source { get; }
    public string GameBuild { get; }
    public string SDKContract { get; }
    public CharaViewCharacterData CharacterData => characterData;
    public CharaViewModelData ModelData => modelData;
    public byte CrestBits { get; }
    public PortraitComposition Composition { get; }
    public bool IsComplete { get; }
    public bool IsDetachedValid { get; }
    public string ContentDigest { get; }
    public string Digest => ContentDigest;
    public PortraitOwnedCharaViewProvenance Provenance { get; }

    public PortraitOwnedCharaViewSnapshot(PortraitIdentity source, CharaViewCharacterData characterData,
        CharaViewModelData modelData, ReadOnlySpan<CharaViewItem> items, byte crestBits,
        PortraitComposition composition, int snapshotVersion = CurrentVersion,
        string? gameBuild = null, string? sdkContract = null)
    {
        if (snapshotVersion != CurrentVersion)
            throw new ArgumentException("The owned portrait snapshot version is not supported.", nameof(snapshotVersion));
        if (!source.IsValid || source.Generation == 0 || items.Length != 14 || composition?.IsValid != true)
            throw new ArgumentException("The native portrait snapshot is incomplete.");

        Version = snapshotVersion;
        Source = source;
        GameBuild = gameBuild ?? string.Empty;
        SDKContract = sdkContract ?? DefaultSDKContract;
        if (GameBuild.Length > 128 || SDKContract.Length > 128)
            throw new ArgumentException("The owned portrait snapshot contract identity is invalid.");
        this.characterData = characterData;
        this.modelData = modelData;
        this.items = items.ToArray();
        CrestBits = crestBits;
        Composition = composition;
        IsComplete = IsCompleteEquipment(this.items);
        ContentDigest = ComputeDigest(Version, Source, GameBuild, SDKContract,
            this.characterData, this.modelData, this.items, CrestBits, Composition);
        Provenance = new(Version, Source, GameBuild, SDKContract, ContentDigest);
        IsDetachedValid = IsComplete && Provenance.IsValid;
    }

    public static bool TryCreate(PortraitIdentity source, CharaViewCharacterData characterData,
        CharaViewModelData modelData, ReadOnlySpan<CharaViewItem> items, byte crestBits,
        PortraitComposition composition, out PortraitOwnedCharaViewSnapshot? snapshot,
        int snapshotVersion = CurrentVersion, string? gameBuild = null, string? sdkContract = null)
    {
        snapshot = null;
        try
        {
            var candidate = new PortraitOwnedCharaViewSnapshot(source, characterData, modelData, items, crestBits,
                composition, snapshotVersion, gameBuild, sdkContract);
            if (!candidate.IsDetachedValid) return false;
            snapshot = candidate;
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    internal bool MatchesSource(PortraitIdentity source)
        => IsDetachedValid && Source == source && source.IsValid && source.Generation > 0;

    public CharaViewItem[] CopyItems() => (CharaViewItem[])items.Clone();

    public CharaViewCharacterData CreateSetupData()
    {
        var data = characterData;
        data.CustomizeData = modelData.CustomizeData;
        modelData.GlassesIds.CopyTo(data.GlassesIds);
        Span<bool> visited = stackalloc bool[14];
        visited.Clear();
        foreach (var item in items)
        {
            if (item.SlotId >= 14 || visited[item.SlotId]) continue;
            visited[item.SlotId] = true;
            var glamour = item.GlamourItemId != 0;
            data.ItemIds[item.SlotId] = glamour ? item.GlamourItemId : item.ItemId;
            data.ItemStain0Ids[item.SlotId] = glamour ? item.GlamourStain0Id : item.Stain0Id;
            data.ItemStain1Ids[item.SlotId] = glamour ? item.GlamourStain1Id : item.Stain1Id;
        }
        return data;
    }

    internal void CopyAppearanceTo(ref CharaViewPortrait view)
    {
        view.ModelData = modelData;
        items.AsSpan().CopyTo(view.Items);
        view.FreeCompanyCrestBitfield = CrestBits;
        view.CharacterDataCopied = true;
    }

    private static bool IsCompleteEquipment(ReadOnlySpan<CharaViewItem> values)
    {
        if (values.Length != 14) return false;
        Span<bool> seen = stackalloc bool[14];
        seen.Clear();
        foreach (var item in values)
        {
            if (item.SlotId >= seen.Length || seen[item.SlotId]) return false;
            seen[item.SlotId] = true;
        }
        return true;
    }

    private static string ComputeDigest(int version, PortraitIdentity source, string gameBuild,
        string sdkContract, CharaViewCharacterData character, CharaViewModelData model,
        IReadOnlyList<CharaViewItem> values, byte crestBits, PortraitComposition composition)
    {
        // JSON is used only at capture time. Anonymous objects give stable explicit field order,
        // escape contract strings, and avoid native padding or pointer bytes.
        var payload = new
        {
            Version = version,
            GameBuild = gameBuild,
            SDKContract = sdkContract,
            Source = new { Editor = (long)source.Editor, State = (long)source.State,
                Model = (long)source.Model, source.Generation, source.Character },
            Character = new
            {
                Customize = character.CustomizeData.Data.ToArray(),
                Items = character.ItemIds.ToArray(),
                Stain0 = character.ItemStain0Ids.ToArray(),
                Stain1 = character.ItemStain1Ids.ToArray(),
                Glasses = character.GlassesIds.ToArray(),
                character.ClassJobId, character.HeadgearHidden, character.WeaponHidden,
                character.VisorClosed, character.HideVieraEars,
            },
            Model = new
            {
                Customize = model.CustomizeData.Data.ToArray(),
                Equipment = Array.ConvertAll(model.EquipmentModelIds.ToArray(), static x => new
                    { x.Id, x.Variant, x.Stain0, x.Stain1 }),
                Weapons = Array.ConvertAll(model.WeaponModelIds.ToArray(), static x => new
                    { x.Id, x.Type, x.Variant, x.Stain0, x.Stain1 }),
                Glasses = model.GlassesIds.ToArray(),
                model.HeadgearHidden, model.WeaponHidden, model.VisorClosed, model.VieraEarsHidden,
                Crest = model.FreeCompanyCrestData.Data,
            },
            Equipment = values.Select(x => new
            {
                x.SlotId, x.EquipSlotCategory, x.GlamourEquipSlotCategory, x.ItemId, x.Stain0Id, x.Stain1Id,
                x.GlamourItemId, x.GlamourStain0Id, x.GlamourStain1Id, x.ApplyCompanyCrest, x.ModelMain, x.ModelSub,
            }).ToArray(),
            CrestBits = crestBits,
            Composition = new
            {
                CameraPosition = new { composition.CameraPosition.X, composition.CameraPosition.Y,
                    composition.CameraPosition.Z, composition.CameraPosition.W },
                CameraTarget = new { composition.CameraTarget.X, composition.CameraTarget.Y,
                    composition.CameraTarget.Z, composition.CameraTarget.W },
                composition.CameraYaw, composition.CameraPitch, composition.CameraDistance,
                composition.CameraZoomNormalized, composition.ImageRotation, composition.CameraZoom,
                composition.BannerTimeline, composition.AnimationProgress, composition.Expression,
                HeadDirection = new { composition.HeadDirection.X, composition.HeadDirection.Y },
                EyeDirection = new { composition.EyeDirection.X, composition.EyeDirection.Y },
                composition.DirectionalRed, composition.DirectionalGreen, composition.DirectionalBlue,
                composition.DirectionalBrightness, composition.DirectionalVerticalAngle, composition.DirectionalHorizontalAngle,
                composition.AmbientRed, composition.AmbientGreen, composition.AmbientBlue, composition.AmbientBrightness,
                composition.Background, composition.Frame, composition.Decoration, composition.PoseClassJob,
                composition.HeadFacingCamera, composition.EyesFacingCamera, composition.Paused, composition.CharacterVisible,
            },
        };
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
    }
}
