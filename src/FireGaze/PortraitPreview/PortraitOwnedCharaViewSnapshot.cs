using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

/// <summary>
/// A complete, value-only handoff from the live editor. Source contains identity tokens only;
/// creating or advancing a second scene never dereferences the source's native pointers.
/// Unlike portable history, this includes all fourteen native slots, two facewear slots,
/// the current model IDs, both dye channels and the full company crest.
/// </summary>
internal sealed class PortraitOwnedCharaViewSnapshot
{
    private readonly CharaViewCharacterData characterData;
    private readonly CharaViewModelData modelData;
    private readonly CharaViewItem[] items;

    public PortraitIdentity Source { get; }
    public CharaViewCharacterData CharacterData => characterData;
    public CharaViewModelData ModelData => modelData;
    public byte CrestBits { get; }
    public PortraitComposition Composition { get; }

    public PortraitOwnedCharaViewSnapshot(PortraitIdentity source, CharaViewCharacterData characterData,
        CharaViewModelData modelData, ReadOnlySpan<CharaViewItem> items, byte crestBits,
        PortraitComposition composition)
    {
        if (!source.IsValid || source.Generation == 0 || items.Length != 14 || composition?.IsValid != true)
            throw new ArgumentException("The native portrait snapshot is incomplete.");
        Source = source;
        this.characterData = characterData;
        this.modelData = modelData;
        this.items = items.ToArray();
        CrestBits = crestBits;
        Composition = composition;
    }

    public CharaViewItem[] CopyItems() => (CharaViewItem[])items.Clone();

    public CharaViewCharacterData CreateSetupData()
    {
        var data = characterData;
        // These are the current rendered customization and facewear values. They can be newer
        // than the import payload while the editor is temporarily trying another appearance.
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
        // Setup has already performed the original item lookups on this private view. Reapply
        // the complete editor-local model/item values before native Render starts loading.
        view.ModelData = modelData;
        items.AsSpan().CopyTo(view.Items);
        view.FreeCompanyCrestBitfield = CrestBits;
        view.CharacterDataCopied = true;
    }
}
