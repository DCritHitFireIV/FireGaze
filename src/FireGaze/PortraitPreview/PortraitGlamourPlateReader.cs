using FFXIVClientStructs.FFXIV.Client.Game;

namespace FireGaze.PortraitPreview;

/// <summary>Copies only the saved-template cache. No requests, addon callbacks, or equipment application.</summary>
internal sealed unsafe class PortraitGlamourPlateReader
{
    private readonly Func<bool> isFrameworkThread;
    private readonly Func<OwnedCharacterKey?> currentCharacter;
    private readonly Func<nint> managerAddress;

    public PortraitGlamourPlateReader(Func<bool> isFrameworkThread, Func<OwnedCharacterKey?> currentCharacter, Func<nint>? managerAddress = null)
    {
        this.isFrameworkThread = isFrameworkThread ?? throw new ArgumentNullException(nameof(isFrameworkThread));
        this.currentCharacter = currentCharacter ?? throw new ArgumentNullException(nameof(currentCharacter));
        this.managerAddress = managerAddress ?? (() => (nint)MirageManager.Instance());
    }

    public PortraitGlamourPlateCapture Capture()
    {
        if (!isFrameworkThread()) throw new InvalidOperationException("Saved glamour templates must be read on the Framework thread.");
        var owner = currentCharacter();
        if (owner is not { AccountID: > 0, ContentID: > 0 }) return new(owner, null, "尚未进入角色");
        var address = managerAddress();
        var manager = (MirageManager*)address;
        if (manager is null || !manager->GlamourPlatesLoaded) return new(owner, null, "打开投影模板后即可读取");
        if (manager->IsApplyingGlamourPlate) return new(owner, null, "投影模板正在应用，稍后读取");
        var plates = new PortraitGlamourPlate[20];
        for (var page = 0; page < plates.Length; page++)
        {
            var pieces = new PortraitPiece[12];
            ref var saved = ref manager->GlamourPlates[page];
            for (var slot = 0; slot < pieces.Length; slot++)
            {
                var item = saved.ItemIds[slot] % 1_000_000;
                pieces[slot] = item == 0 ? default : new(item, saved.Stain0Ids[slot], saved.Stain1Ids[slot]);
            }
            plates[page] = new(page, pieces);
        }
        // The game clears this cache while changing zones. Never mistake that for twenty empty templates.
        if (currentCharacter() != owner || managerAddress() != address || !manager->GlamourPlatesLoaded || manager->IsApplyingGlamourPlate)
            return new(owner, null, "角色或投影模板状态已改变，保留上次读取记录");
        return new(owner, plates, "已读取投影模板");
    }
}
