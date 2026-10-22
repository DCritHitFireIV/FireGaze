using FireGaze.LocalPreview.Interaction;

internal static class MarketItemMenuCases
{
    public static void Run(Action<bool, string> check)
    {
        var assembly = typeof(PreviewKind).Assembly;
        var contextType = assembly.GetType("FireGaze.LocalPreview.Interaction.MarketItemMenuContext");
        var resolverType = assembly.GetType("FireGaze.LocalPreview.Interaction.MarketItemMenuResolver");
        check(contextType is not null && resolverType is not null,
            "market item menus expose a current native-context resolver");
        var resolve = resolverType!.GetMethod("TryResolve")!;

        // These are two independently observed native menu routes. ItemSearch
        // binds its own current menu item; ItemSearchResult binds its item event
        // handler and the exact quoted inventory item, never the last hover alone.
        object?[] listing = [true, "ItemSearch", 81u, (nint)100, (nint)100, 81u,
            (nint)200, true, false, 81u, (nint)264, 52272L, 0u, 0u, 0u, 0u, (short)0];
        object?[] result = [true, "ItemSearchResult", 82u, (nint)100, (nint)100, 82u,
            (nint)200, false, true, 81u, (nint)0, 0L, 52289u, 3u, 52289u, 9999u, (short)3];
        void Expect(object?[] fields, bool expected, uint expectedItem, string label)
        {
            object?[] arguments = [Activator.CreateInstance(contextType!, fields), 12345u];
            var success = (bool)resolve.Invoke(null, arguments)!;
            check(success == expected && (uint)arguments[1]! == expectedItem, label);
        }
        object?[] Change(object?[] fields, int index, object? value)
        {
            var copy = fields.ToArray();
            copy[index] = value;
            return copy;
        }

        Expect(listing, true, 52272, "market listing uses its menu-bound startup key id");
        Expect(Change(listing, 11, 52289L), true, 52289,
            "a later listing menu cannot inherit the previous startup key");
        Expect(Change(listing, 11, 1052272L), true, 52272, "market listing normalizes HQ item encoding");
        Expect(Change(listing, 11, 552272L), true, 52272, "market listing normalizes collectible item encoding");
        Expect(result, true, 52289, "market quote resolves the exact selected fashion item");
        var highQuality = Change(result, 14, 1052289u);
        Expect(highQuality, true, 52289, "market quote matches HQ item against its base result item");
        foreach (var source in new[] { listing, result })
        {
            foreach (var (index, value, label) in new (int, object?, string)[]
            {
                (0, false, "inventory menu"), (1, null, "world menu"), (1, "ChatLog", "chat menu"),
                (1, "ItemSearchResultHistory", "purchase history menu"), (2, 0u, "missing parent addon"),
                (3, (nint)101, "different menu agent"), (4, (nint)0, "missing context agent"),
                (5, 83u, "different context owner"), (6, (nint)0, "missing market agent"),
            })
                Expect(Change(source, index, value), false, 0, "market context rejects " + label);
        }
        foreach (var (index, value, label) in new (int, object?, string)[]
        {
            (7, false, "missing listing callback"), (9, 82u, "different market addon"),
            (10, (nint)0, "missing current menu updater"), (10, (nint)265, "foreign menu updater"),
            (11, 0L, "cleared menu item"), (11, -1L, "negative menu item"),
            (11, 500000L, "empty collectible id"), (11, 1000000L, "empty HQ id"),
            (11, 2052272L, "event item"), (11, long.MaxValue, "out-of-range menu item"),
        })
            Expect(Change(listing, index, value), false, 0, "market listing rejects " + label);
        foreach (var (index, value, label) in new (int, object?, string)[]
        {
            (8, false, "missing quote item callback"), (12, 0u, "empty current result"),
            (12, 52272u, "previous result item"), (13, 4u, "previous selected index"),
            (13, uint.MaxValue, "invalid selected index"), (14, 0u, "empty quoted item"),
            (14, 2052289u, "event item quote"), (15, 0u, "real inventory slot"),
            (16, (short)-1, "invalid quote slot"),
        })
            Expect(Change(result, index, value), false, 0, "market quote rejects " + label);
        Expect(Change(listing, 1, "ItemSearchResult"), false, 0,
            "a listing snapshot cannot pass as a quote snapshot");
        Expect(Change(result, 1, "ItemSearch"), false, 0,
            "a quote snapshot cannot pass as a listing snapshot");
    }
}
