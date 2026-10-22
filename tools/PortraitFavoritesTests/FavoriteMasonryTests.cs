using FireGaze.PortraitPreview;

internal static class FavoriteMasonryTests
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAIL " + name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        var wide = PortraitFavoriteMasonryLayout.For(760, 1);
        Check(wide.Columns >= 3 && wide.CardWidth >= 140, "wide favorite library uses a multi-column card grid");
        var narrow = PortraitFavoriteMasonryLayout.For(420, 1);
        Check(narrow.Columns >= 2 && narrow.CardWidth >= 140, "narrow favorite library keeps cards readable");
        Check(PortraitFavoriteMasonryLayout.For(120, 1).Columns == 1, "very narrow favorite library collapses to one card column");
        Check(PortraitFavoriteMasonryLayout.For(120, 1).CardWidth <= 120, "card never exceeds the available lane width");
        var flow = PortraitFavoriteMasonryLayout.For(320, 1).Place([300, 200, 100, 100]);
        Check(flow[2].Column == 1 && flow[2].Y == 212 && flow[3].Column == 0 && flow[3].Y == 312,
            "unequal cards fill the shortest column without waiting for a table row");
        foreach (var scale in new[] { .75f, 1f, 1.5f, 2f })
        foreach (var width in new[] { 120f, 420f, 760f, 1040f })
        {
            var layout = PortraitFavoriteMasonryLayout.For(width * scale, scale);
            var cards = layout.Place(Enumerable.Range(0, 100).Select(index => (200 + index % 4 * 20) * scale).ToArray());
            Check(cards.All(card => card.X >= 0 && card.X + layout.CardWidth <= width * scale + 1),
                "all waterfall cards stay inside the viewport across DPI and widths");
            Check(cards.GroupBy(card => card.Column).All(column => column.Zip(column.Skip(1)).All(pair => pair.First.Y + pair.First.Height < pair.Second.Y)),
                "waterfall cards never overlap within a column");
        }
        return checks;
    }
}
