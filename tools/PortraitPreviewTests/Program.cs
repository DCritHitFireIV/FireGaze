#if !HAS_PORTRAIT_SESSION
Console.Error.WriteLine("FAIL portrait preview has no isolated session or save guard");
return 1;
#else
using FireGaze.PortraitPreview;

var passed = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new InvalidOperationException(label);
    passed++;
    Console.WriteLine("PASS " + label);
}
PortraitLook Look(uint head) => new(Enumerable.Range(0, 13)
    .Select(i => new PortraitPiece(i == 2 ? head : (uint)(100+i), 0, 0)).ToArray(), true, true, false);
try
{
    var menu = PortraitPreviewMenuState.Create(false, false, false, false, null);
    Check(menu.Status == PortraitPreviewMenuStatus.EditorUnavailable && !menu.CanOpenPreview && !menu.CanOpenWindow,
        "portrait menu reports an unopened editor and disables preview");
    menu = PortraitPreviewMenuState.Create(true, false, false, false, null);
    Check(menu.Status == PortraitPreviewMenuStatus.Loading && !menu.CanOpenPreview,
        "portrait menu reports a loading editor until identity and save guard are ready");
    menu = PortraitPreviewMenuState.Create(true, true, true, false, null);
    Check(menu.Status == PortraitPreviewMenuStatus.Ready && menu.CanOpenPreview,
        "portrait menu enables preview only after editor identity and save guard are ready");
    menu = PortraitPreviewMenuState.Create(true, true, true, true, null);
    Check(menu.Status == PortraitPreviewMenuStatus.Active && !menu.CanOpenEditor && menu.CanOpenWindow,
        "active preview keeps the editor entry locked and exposes the active state");
    menu = PortraitPreviewMenuState.Create(true, true, true, false, "本地铭牌预览尚未还原");
    Check(menu.Status == PortraitPreviewMenuStatus.RestoreWarning && !menu.CanOpenPreview,
        "restore warning blocks a new preview until the local mirror is safe");

    var manifest = PortraitDesignManifest.Build(
        new[] { new PortraitDesignEntry("head", "头部", "预览", 11, "装备栏", 1001) },
        new[] { new PortraitDesignEntry("head", "头部", "实际穿着", 10, "当前装备", 1000) },
        new[] { new PortraitDesignEntry("face", "脸型", "美容师", 2, "美容师数据", 0, PortraitDesignEntryKind.Appearance) });
    Check(manifest.Items.Count == 3 && manifest.Items[0].Name == "头部" && manifest.Items[0].Icon == 1001,
        "design manifest preserves preview, equipped and appearance entries with icon and source");
    var filtered = manifest.Hide("head");
    Check(filtered.Items.Count == 1 && filtered.Items[0].Kind == PortraitDesignEntryKind.Appearance,
        "design manifest can hide a selected display item without changing source data");
    Check(PortraitDesignEntrySlotMap.TryMap(11, out var rightRing) && rightRing == PortraitSlot.RightRing
        && PortraitDesignEntrySlotMap.TryMap(12, out var leftRing) && leftRing == PortraitSlot.LeftRing
        && !PortraitDesignEntrySlotMap.TryMap(5, out _),
        "design manifest maps both equipped rings to their native slots and skips the waist slot");

    var equippedSource = new OwnedSourceKey(OwnedSourceKind.Equipped);
    var inventorySource = new OwnedSourceKey(OwnedSourceKind.Inventory);
    var equippedPieces = PortraitDesignEntrySlotMap.ReadEquipped([
        new(701, [new(equippedSource, 1_000_701, 1000, 11, 1, 7, 8, 901, true, OwnedReadStatus.Current),
                  new(equippedSource, 701, 1000, 12, 1, 3, 4, 0, false, OwnedReadStatus.Current),
                  new(inventorySource, 701, 0, 2, 1, 0, 0, 0, false, OwnedReadStatus.Current)]),
        new(702, [new(equippedSource, 702, 1000, 3, 1, 0, 0, 0, false, OwnedReadStatus.PossiblyStale)]),
    ], completeRead: true);
    Check(equippedPieces.Count == 12
        && equippedPieces.Single(piece => piece.Slot == PortraitSlot.RightRing) == new PortraitDesignEquippedPiece(PortraitSlot.RightRing, 901, 7, 8)
        && equippedPieces.Single(piece => piece.Slot == PortraitSlot.LeftRing) == new PortraitDesignEquippedPiece(PortraitSlot.LeftRing, 701, 3, 4),
        "design list resolves current worn glamour and HQ base items while preserving both ring dyes and excluding stale or inventory rows");

    var fullDesign = PortraitDesignManifest.Build([
        new("preview:Head", "头", "预览", 100, "背包", 1, PortraitDesignEntryKind.Preview, 2, 3),
        new("preview:RightRing", "右戒指", "预览", 200, "兵装库", 2, PortraitDesignEntryKind.Preview, 0, 0),
        new("preview:LeftRing", "左戒指", "预览", 201, "兵装库", 3, PortraitDesignEntryKind.Preview, 0, 0),
    ], [
        new("equipped:Head", "头", "实际穿着", 100, "身上装备", 1, PortraitDesignEntryKind.Equipped, 2, 4),
        new("equipped:RightRing", "右戒指", "实际穿着", 200, "身上装备", 2, PortraitDesignEntryKind.Equipped, 0, 0),
        new("equipped:LeftRing", "左戒指", "实际穿着", 299, "身上装备", 3, PortraitDesignEntryKind.Equipped, 0, 0),
    ], [
        new("appearance:preview:hair", "发型 1", "预览美容师", 1, "美容师", 4, PortraitDesignEntryKind.Appearance),
        new("appearance:actual:hair", "发型 2", "实际外貌", 2, "美容师", 5, PortraitDesignEntryKind.Appearance),
        new("appearance:preview:lip", "唇色 1", "预览美容师", 1, "美容师", 0, PortraitDesignEntryKind.Appearance),
        new("appearance:actual:lip", "唇色 1", "实际外貌", 1, "美容师", 0, PortraitDesignEntryKind.Appearance),
    ]);
    Check(fullDesign.ForShowcase().Items.Select(row => row.Key).SequenceEqual(new[] { "preview:Head", "preview:RightRing", "preview:LeftRing", "appearance:preview:hair", "appearance:preview:lip" }),
        "shareable showcase displays only the rendered portrait wardrobe and beautician values without duplicating actual baselines");
    var designDiff = fullDesign.Differences();
    Check(designDiff.Items.Select(row => row.Key).SequenceEqual(new[] { "preview:Head", "preview:LeftRing", "equipped:Head", "equipped:LeftRing", "appearance:preview:hair", "appearance:actual:hair" }),
        "design differences include both sides of equipment, second dye and beautician changes without confusing the two ring slots");

    var unknownDiff = PortraitDesignManifest.Build([
        new("preview:Head", "头部", "肖像装备", 100, "位置未读取", 1),
        new("preview:Facewear", "眼镜", "肖像装备", 42, "位置未读取", 1),
    ], [], [new("appearance:preview:hair", "发型 1", "预览美容师", 1, "美容师", 1, PortraitDesignEntryKind.Appearance)],
        [new(equippedSource, OwnedReadStatus.PossiblyStale, null, 2, "记录过期")]);
    Check(unknownDiff.Differences().Items.Count == 0 && unknownDiff.Sources.Single().Status == OwnedReadStatus.PossiblyStale
        && PortraitDesignEntrySlotMap.ReadEquipped([], completeRead: false).Count == 0,
        "unread or stale actual baselines cannot become confirmed equipment, facewear or beautician differences and retain source state");
    var emptyActual = PortraitDesignEntrySlotMap.ReadEquipped([], completeRead: true);
    var knownEmpty = PortraitDesignManifest.Build([new("preview:Head", "帽子", "肖像装备", 100, "背包", 1)],
        [new("equipped:Head", "头部 · 未穿戴", "实际穿着", 0, "身上装备", 0, PortraitDesignEntryKind.Equipped)], []);
    Check(emptyActual.Count == 12 && emptyActual.All(piece => piece.ItemID == 0) && knownEmpty.Differences().Items.Count == 2,
        "an authoritative complete equipped read proves empty slots and keeps both sides of a newly previewed item");
    var hiddenLook = Look(111) with { HeadVisible = false, WeaponVisible = false };
    Check(!PortraitDesignEntrySlotMap.IsRendered(hiddenLook, PortraitSlot.Head)
        && !PortraitDesignEntrySlotMap.IsRendered(hiddenLook, PortraitSlot.MainHand)
        && !PortraitDesignEntrySlotMap.IsRendered(hiddenLook, PortraitSlot.OffHand)
        && PortraitDesignEntrySlotMap.IsRendered(hiddenLook, PortraitSlot.Body),
        "the shareable wardrobe omits native hidden head and weapon slots while keeping rendered armor");
    var namedBeauty = PortraitDesignManifest.AppearanceEntries([
        new("hair", "发型", 128, [new(128, "发型 128", 808, Reason: "不要解析此描述", UnlockItemName: "发型样式：战场短发", UnlockItemID: 12345)]),
        new("hairColor", "发色", 3, [new(3, "颜色 4", Color: 0xFF1122EE)]),
    ], "preview", "预览美容师", "美容师").ToArray();
    Check(namedBeauty[0].Name == "发型 · 发型样式：战场短发" && namedBeauty[0].Icon == 808
        && namedBeauty[1].Name == "发色 · 颜色 4" && namedBeauty[1].Color == 0xFF1122EE,
        "beautician showcase uses structured Chinese unlock names and keeps CMP RGBA color bytes unchanged");
    foreach (var width in new[] { 120f, 220f, 300f, 640f })
    foreach (var scale in new[] { 1f, 1.5f, 2f })
    {
        var rowLayout = PortraitDesignShowcaseLayout.Row(width, scale);
        Check(rowLayout.TextWidth > 0 && rowLayout.TextX >= rowLayout.IconSize
            && rowLayout.TextX + rowLayout.TextWidth <= rowLayout.HideX
            && rowLayout.HideX + rowLayout.HideSize <= width,
            $"narrow showcase row preserves a wrapped text region and separate hide hit target at {width}/{scale}");
    }

    var target = new Target { Look = Look(111) };
    var session = new PortraitPreviewSession(target);
    Check(!session.IsActive && !session.BlockSave(target.Identity), "inactive preview permits native saving");
    Check(session.Open(target.Identity) && session.IsActive && session.Original?.Pieces[2].ItemID == 111,
        "explicit open captures the editor's wardrobe baseline for the design list");
    Check(session.BlockSave(target.Identity), "opening alone blocks save before any wardrobe change");
    Check(!session.BlockSave(target.Identity with { Editor = 99 }), "unrelated editor saves are not intercepted");
    target.Available = false;
    session.Validate();
    Check(session.IsActive && session.BlockSave(target.Identity), "temporary unreadability retains the preview lease and save guard after Framework validation");
    Check(!session.ObserveLook(Look(999)) && session.Current!.Pieces[2].ItemID==111,
        "an unreadable owner cannot publish a new observed appearance");
    var unreadyCalls=target.Applies;
    Check(!session.Replace(PortraitSlot.Head,new(999,0,0)) && !session.SetVisibility(false,false,false)
        && session.IsActive && target.Applies==unreadyCalls,"unready native mutations are refused without retiring the lease");
    Check(!session.Close() && session.IsActive && session.BlockSave(target.Identity) && target.Applies==unreadyCalls,
        "close during temporary unreadability retains restoration and save protection for retry");
    Check(session.BlockSave(target.Identity) && session.IsActive, "save event preserves active guard until Framework lifecycle validation runs");
    target.Available = true;
    Check(session.Replace(PortraitSlot.Head, new(222, 3, 4)), "an owned head item can replace the portrait piece");
    Check(target.Look.Pieces[2] == new PortraitPiece(222, 3, 4), "both dye channels reach the preview target");
    Check(target.RealHead == 111 && target.Pose == 9, "changing portrait equipment leaves real character and pose untouched");
    Check(session.Replace(PortraitSlot.Head, new(333, 0, 0)) && session.Open(target.Identity), "reopening an active session does not replace its original baseline");
    Check(session.Replace(PortraitSlot.MainHand, default), "clearing a preview slot is an explicit zero appearance");
    Check(session.SetVisibility(false, false, true), "head weapon and visor presentation can change independently");
    target.Pose = 17;
    session.Close();
    Check(!session.IsActive && target.Look.Pieces[2].ItemID == 111 && target.Look.Pieces[0].ItemID == 100,
        "close restores entry wardrobe after repeated item choices and clear");
    Check(target.Pose == 17 && target.Look.HeadVisible && target.Look.WeaponVisible && !target.Look.Visor,
        "restore affects wardrobe flags while preserving later portrait camera or pose edits");
    var calls = target.Applies;
    session.Close();
    Check(target.Applies == calls && !session.BlockSave(target.Identity), "close is idempotent and releases save blocking");

    session.Open(target.Identity);
    session.Replace(PortraitSlot.Facewear, new(8, 2, 0));
    Check(target.Look.Pieces[12].ItemID == 8, "facewear has an independent presentation slot");
    target.Identity = target.Identity with { Model = 91, Generation = 2 };
    target.Look = Look(444);
    calls = target.Applies;
    session.Validate();
    Check(!session.IsActive && target.Applies == calls && target.Look.Pieces[2].ItemID == 444,
        "replaced preview allocation invalidates the session without restoring into its successor");
    Check(!session.Replace(PortraitSlot.Body, new(555, 0, 0)), "stale input cannot apply after model replacement");
    Check(session.Open(target.Identity), "a replacement editor can open a fresh independent session");
    target.Refuse = true;
    Check(!session.Replace(PortraitSlot.Head, new(999, 0, 0)) && session.Current!.Pieces[2].ItemID == 444,
        "failed native change cannot replace the current desired appearance");
    var beforeFailedClose = target.Applies;
    session.Close();
    Check(session.IsActive && session.BlockSave(target.Identity) && target.Applies == beforeFailedClose,
        "failed restore retains preview and save guard for an explicit retry");
    var replacementIdentity = target.Identity with { Editor = 101 };
    Check(!session.Open(replacementIdentity) && session.IsActive, "failed cleanup cannot be replaced by a new preview capture");
    target.Refuse = false;
    target.ThrowApply = true;
    session.Close();
    Check(session.IsActive && session.BlockSave(target.Identity), "throwing restore retains preview and save guard without leaking exception");
    target.ThrowApply = false;
    session.Close();
    Check(!session.IsActive && !session.BlockSave(target.Identity), "successful restore retry releases save guard");
    target.Available = false;
    Check(!session.Open(target.Identity) && !session.IsActive, "unavailable model cannot activate save blocking");
    target.Available = true;
    Check(!session.Open(default), "zero owner identity is refused");
    target.DuringApply = () => target.Identity = target.Identity with { Character = 77 };
    Check(session.Open(target.Identity), "valid entry starts before callback replacement");
    Check(!session.Replace(PortraitSlot.Head, new(555, 0, 0)) && !session.IsActive,
        "identity replacement during native apply invalidates subsequent operations");
    target.DuringApply=null;target.Available=true;
    Check(session.Open(target.Identity),"same-owner post-write unreadiness fixture opens");
    target.DuringApply=()=>target.Available=false;
    Check(!session.Replace(PortraitSlot.Head,new(777,0,0)) && session.IsActive && session.BlockSave(target.Identity),
        "readiness lost during a native write retains the restoration lease");
    target.Available=true;target.DuringApply=null;
    Check(session.Close() && !session.IsActive,"same-owner readiness recovery allows the original wardrobe to restore");
    Console.WriteLine($"{passed}/{passed} portrait session checks passed");
    return 0;
}
catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }

sealed class Target : IPortraitPreviewTarget
{
    public PortraitIdentity Identity = new(1, 2, 3, 4, 5);
    public PortraitLook Look = null!;
    public bool Available = true;
    public bool Refuse;
    public bool ThrowApply;
    public int Applies;
    public int RealHead = 111;
    public int Pose = 9;
    public Action? DuringApply;
    public bool IsCurrent(PortraitIdentity identity) => Available && identity == Identity;
    public bool IsEditorOwnerDestroyed(PortraitIdentity identity) => identity != Identity;
    public PortraitLook? Capture(PortraitIdentity identity) => IsCurrent(identity) ? Look.Copy() : null;
    public bool Apply(PortraitIdentity identity, PortraitLook look)
    {
        if (!IsCurrent(identity) || Refuse) return false;
        if (ThrowApply) throw new InvalidOperationException("simulated restore failure");
        Applies++;
        Look = look.Copy();
        DuringApply?.Invoke();
        return true;
    }
}
#endif
