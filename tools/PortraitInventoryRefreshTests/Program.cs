using FireGaze.PortraitPreview;

static class Program
{
    static int Main()
    {
        var now = new DateTime(2026, 10, 19, 13, 25, 0, DateTimeKind.Utc);
        Check(!PortraitInventoryRefreshPolicy.ShouldRefresh(true, now, now.AddMinutes(-1)),
            "an active portrait preview never starts the periodic full inventory scan");
        Check(PortraitInventoryRefreshPolicy.ShouldRefresh(false, now, now.AddSeconds(-1)),
            "an inactive editor may refresh an overdue inventory snapshot");
        Check(!PortraitInventoryRefreshPolicy.ShouldRefresh(false, now, now.AddSeconds(1)),
            "an inactive editor does not refresh before its scheduled time");
        Console.WriteLine("PortraitInventoryRefreshTests: 3 passed, 0 failed");
        return 0;
    }

    static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
