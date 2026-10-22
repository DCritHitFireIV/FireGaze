using FireGaze.PortraitPreview;

internal static class PortraitActionOwnerCases
{
    public static void Run(string root, Action<bool, string> check)
    {
        using var feature = new PortraitPreviewFeature(Path.Combine(root, "action-stale-facewear.json"));
        feature.Sample(); feature.OpenPreview();
        var oldOwner = feature.Owner;
        feature.SetOwnedItems(new(oldOwner, [], []), PortraitSlot.Facewear, [new(71, "上一角色眼镜", 1, 0, true, "")]);
        feature.Owner = new(99, 88);
        var calls = feature.EquipDelegations;
        var nativeCalls = feature.Native.ApplyCalls;
        var reads = feature.NativeOwnerReads;
        var threw = false;
        try { feature.EquipFromSources(PortraitSlot.Facewear, 71, []); }
        catch (InvalidOperationException) { threw = true; }
        check(!threw && feature.EquipDelegations == calls && feature.Native.ApplyCalls == nativeCalls
            && feature.NativeOwnerReads > reads && feature.Status.Contains("角色已改变"),
            "facewear action rejects an unpublished character change before delegating to native equip");
        check(feature.GetItems(PortraitSlot.Facewear).Single().ID == 71,
            "rejecting a stale facewear action preserves the detached getter snapshot until Framework publication");
    }
}
