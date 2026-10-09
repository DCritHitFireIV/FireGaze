using System.Net;
using System.Net.Sockets;
using System.Reflection;
using FireGaze.Translate;
using FireGaze.RepoAudit;

internal static class DiscoveryUiTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var type = typeof(TranslationIndexEntry).Assembly.GetType("FireGaze.UI.DiscoveryIconQueue");
        check(type is not null, "all discovery icon entry points share one retry-aware queue");
        var queue = Activator.CreateInstance(type!, true)!;
        object? Call(string name, params object[] args) => type!.GetMethod(name)!.Invoke(queue, args);
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var bartender = new TranslationIndexEntry { InternalName = "Bartender", DisplayName = "Bartender", DeclaresIcon = true, IconURL = "https://example.com/icon.png" };
        var other = new TranslationIndexEntry { InternalName = "Other", DisplayName = "Other", DeclaresIcon = true, IconURL = "https://example.com/other.png" };
        check((bool)Call("TryEnqueue", bartender, now)!, "a missing visible icon is accepted for download");
        check(!(bool)Call("TryEnqueue", bartender, now)!, "preload and draw cannot enqueue the same icon twice");
        Call("Dequeue");
        Call("Complete", bartender, false, now);
        check(Enumerable.Range(0, 1200).All(i => !(bool)Call("TryEnqueue", bartender, now.AddMilliseconds(i * 100))!),
            "failed icons stay in cooldown across 1200 draw/preload attempts instead of retrying every 100ms");
        check((bool)Call("TryEnqueue", bartender, now.AddMinutes(2))!, "an icon can retry when its cooldown expires");
        Call("TryEnqueue", other, now.AddMinutes(2));
        Call("RetainWaiting", (object)new[] { "Other" });
        check((int)type!.GetProperty("Count")!.GetValue(queue)! == 1 && ((TranslationIndexEntry)Call("Dequeue")!).InternalName == "Other",
            "search removes queued downloads that no longer match without changing the surviving target");
        Call("Complete", other, true, now.AddMinutes(2));
        check(!(bool)Call("TryEnqueue", other, now.AddHours(1))!, "successfully cached icons are not downloaded again while their texture becomes ready");
        Call("TryEnqueue", bartender, now.AddMinutes(2));
        Call("Dequeue");
        Call("Complete", bartender, false, now.AddMinutes(2));
        Call("RetainWaiting", (object)Array.Empty<string>());
        check(!(bool)Call("TryEnqueue", bartender, now.AddMinutes(3))!, "changing search cannot reset a failed icon's cooldown");
        Call("Reset");
        check((bool)Call("TryEnqueue", bartender, now.AddMinutes(3))!, "an explicit cloud refresh permits a fresh icon attempt");

        var layout = typeof(TranslationIndexEntry).Assembly.GetType("FireGaze.UI.PluginListLayout");
        check(layout is not null, "list geometry is measured independently from workflow behavior");
        var tab = typeof(TranslationIndexEntry).Assembly.GetType("FireGaze.UI.UITextTab")!;
        check(tab.GetField("selectedPlugins", BindingFlags.Instance | BindingFlags.NonPublic) is null,
            "translation rows no longer keep checkbox selection state");
        check(tab.GetMethod("EnqueueSelection", BindingFlags.Instance | BindingFlags.NonPublic) is null,
            "the removed translate-selected command cannot enqueue hidden selections");
        var columns = layout!.GetMethod("MeasureLegacyColumns");
        check(columns is not null, "lists restore the 1.4.0.7 width and scrollbar spacing rule");
        foreach (var width in new[] { 684f, 1184f })
        {
            var result = columns!.Invoke(null, [width, 14f, 220f])!;
            float Read(string name) => (float)result.GetType().GetProperty(name)!.GetValue(result)!;
            check(Read("Content") == width - 282f && Read("Actions") == 220f && Read("Gap") == 62f,
                "legacy columns retain the original action width and scrollbar plus 48px spacing at width " + width);
        }
        var actions = layout.GetMethod("MeasureDiscoveryActions");
        check(actions is not null, "discovery anchors actions to the real clipped column edge instead of wrapping a right-aligned group");
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        foreach (var widths in new[] { (Navigate: 80f, Library: 0f), (Navigate: 0f, Library: 100f),
            (Navigate: 80f, Library: 100f), (Navigate: 0f, Library: 0f) })
        {
            var left = 100f;
            var right = left + 240f * scale;
            var like = 40f * scale;
            var navigate = widths.Navigate * scale;
            var library = widths.Library * scale;
            var spacing = 8f * scale;
            var result = actions!.Invoke(null, [right, like, navigate, library, spacing])!;
            float Read(string name) => (float)result.GetType().GetProperty(name)!.GetValue(result)!;
            var next = navigate > 0 ? Read("NavigateX") : Read("LikeX");
            check(Read("LikeX") + like == right
                && (navigate == 0 || Read("NavigateX") + navigate + spacing == Read("LikeX"))
                && (library == 0 || Read("LibraryX") >= left && Read("LibraryX") + library + spacing == next),
                "hearts share one right edge and buttons cannot shift to another line, scale " + scale + " actions " + widths);
        }
        var split = layout.GetMethod("SplitRows");
        check(split is not null, "expanded details are excluded from fixed-height clipped row ranges");
        foreach (var expanded in new[] { -1, 0, 12, 24, 99 })
        {
            var ranges = split!.Invoke(null, [25, expanded])!;
            int Read(string name) => (int)ranges.GetType().GetProperty(name)!.GetValue(ranges)!;
            var expected = expanded is >= 0 and < 25 ? expanded : -1;
            check(Read("BeforeCount") + Read("AfterCount") + (Read("ExpandedIndex") >= 0 ? 1 : 0) == 25
                && Read("ExpandedIndex") == expected && Read("AfterStart") == (expected < 0 ? 25 : expected + 1),
                "clipped segments preserve every row exactly once around expansion " + expanded);
        }
        await VerifyStalledBodyCancellation(check);
    }

    private static async Task VerifyStalledBodyCancellation(Action<bool, string> check)
    {
        var fetch = typeof(IconDownloader).GetMethod("FetchOneAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancel = new CancellationTokenSource();
        using var release = new CancellationTokenSource();
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(release.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 10000\r\nContent-Type: image/png\r\n\r\nx"));
            bodyStarted.SetResult();
            try { await Task.Delay(Timeout.Infinite, release.Token); } catch (OperationCanceledException) { }
        });
        var download = (Task)fetch.Invoke(null, [$"http://127.0.0.1:{port}/icon.png", cancel.Token])!;
        try
        {
            await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(50);
            cancel.Cancel();
            await Task.WhenAny(download, Task.Delay(1000));
            check(download.IsCompleted, "cancellation also interrupts a stalled icon body after response headers arrive");
            check(download.IsCanceled, "a cancelled icon body read preserves cancellation for its caller");
            try { await download; } catch (Exception) { }
        }
        finally
        {
            release.Cancel();
            await server;
            try { await download; } catch (OperationCanceledException) { }
        }
    }
}
