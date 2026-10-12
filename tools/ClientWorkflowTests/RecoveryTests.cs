using System.Net;
using System.Reflection;
using System.Text.Json;
using FireGaze.Translate;
using FireGaze.UIText;

internal static class RecoveryTests
{
    public static async Task Run(Action<bool, string> check)
    {
        using var product = dnlib.DotNet.ModuleDefMD.Load(typeof(UITextMaintenance).Assembly.Location);
        var editor = product.GetTypes().Single(t => t.FullName == "FireGaze.UI.UITextEditorWindow");
        check(editor.Methods.Single(m => m.Name == "SaveIfDirty").MethodSig.RetType.FullName == "System.Boolean",
            "editor saving exposes failure so destructive actions cannot overwrite its status and continue");
        foreach (var name in new[] { "StartApply", "StartRestore", "OpenFor", "OnClose" })
        {
            var method = editor.Methods.Single(m => m.Name == name);
            var index = method.Body.Instructions.ToList().FindIndex(i => i.Operand is dnlib.DotNet.IMethod m && m.Name == "SaveIfDirty");
            check(index >= 0 && method.Body.Instructions[index + 1].OpCode.FlowControl == dnlib.DotNet.Emit.FlowControl.Cond_Branch,
                "editor " + name + " checks save success before proceeding");
        }
        var calls = new List<string[]>();
        var saved = new List<string>();
        var channel = new Channel((items, attempt) =>
        {
            calls.Add(items.Select(i => i.Text).ToArray());
            var result = new UITextTranslateResult();
            foreach (var item in items)
                if (attempt > 1 || item.Text == "One") result.Translated[item.Text] = "译文";
                else result.Failed.Add(item.Text);
            return result;
        });
        var outcome = await UITextTranslationSession.TranslateAsync(channel,
            [new("One", null), new("Two", null)], null,
            part => saved.AddRange(part.Translated.Keys), CancellationToken.None);
        check(outcome.Failed.Count == 0 && calls.Count == 2 && calls[1].SequenceEqual(["Two"])
            && saved.SequenceEqual(["One", "Two"]), "partial translation checkpoints then retries only missing responses once");
        channel = new((items, attempt) =>
        {
            var r = new UITextTranslateResult();
            if (attempt > 1) foreach (var i in items) r.Translated[i.Text] = "译文";
            return r;
        });
        outcome = await UITextTranslationSession.TranslateAsync(channel, [new("Two", null)], null, _ => { }, CancellationToken.None);
        check(channel.Calls == 2 && outcome.Translated.Count == 1,
            "an omitted provider response recovers even if the provider forgot its failed-text list");
        channel = new((items, _) => { var r = new UITextTranslateResult(); r.Failed.AddRange(items.Select(i => i.Text)); return r; });
        outcome = await UITextTranslationSession.TranslateAsync(channel, [new("Two", null)], null, _ => { }, CancellationToken.None);
        check(channel.Calls == 2 && outcome.Failed.SequenceEqual(["Two"]), "persistently missing translations stop after one recovery attempt");
        foreach (var error in new[] { "HTTP 401", "HTTP 429", "quota exhausted" })
        {
            channel = new((_, _) => new() { Error = error });
            outcome = await UITextTranslationSession.TranslateAsync(channel, [new("Two", null)], null, _ => { }, CancellationToken.None);
            check(channel.Calls == 1 && outcome.Error == error, "channel-level failure is not retried or hidden: " + error);
        }
        using (var cts = new CancellationTokenSource())
        {
            channel = new((items, _) => { var r = new UITextTranslateResult(); r.Translated[items[0].Text] = "译文"; r.Failed.Add("Two"); return r; });
            outcome = await UITextTranslationSession.TranslateAsync(channel, [new("One", null), new("Two", null)], null, _ => cts.Cancel(), cts.Token);
            check(channel.Calls == 1 && outcome.Error == "已取消" && outcome.Translated.Count == 1,
                "cancelling at checkpoint stops recovery but preserves delivered translations");
        }
        channel = new((items, _) => { var r = new UITextTranslateResult(); r.Translated[items[0].Text] = "译文"; return r; });
        var saveFailed = false;
        try { await UITextTranslationSession.TranslateAsync(channel, [new("One", null)], null, _ => throw new IOException("disk full"), CancellationToken.None); }
        catch (IOException) { saveFailed = true; }
        check(saveFailed && channel.Calls == 1, "checkpoint write failure is not swallowed or retried as a network request");
        var failure = typeof(UITextTranslationSession).GetMethod("IsFailure");
        check(failure is not null, "editor and one-click translation share the same partial-failure classification");
        check(!(bool)failure!.Invoke(null, [new UITextTranslateResult { Error = "已取消" }, 0, 0, 0])!,
            "player cancellation is a neutral stopped state, not a red translation failure");
        foreach (var counts in new (int Applied, int Rejected, int Unchanged, int Failed, bool Fatal)[] { (0, 0, 10, 1, false),
            (0, 0, 0, 1, true), (2, 0, 0, 1, false), (0, 0, 0, 0, false), (0, 1, 0, 1, true) })
        {
            var r = new UITextTranslateResult(); r.Failed.AddRange(Enumerable.Repeat("missing", counts.Failed));
            check((bool)failure!.Invoke(null, [r, counts.Applied, counts.Rejected, counts.Unchanged])! == counts.Fatal,
                "partial-result severity follows delivery, not write count alone: " + counts);
        }
        await TableRecovery(check);
        var fetch = typeof(UITextMaintenance).GetMethod("FetchLibraryCandidateAsync");
        check(fetch is not null, "public-library checks have a bounded isolated-candidate recovery path");
        var seed = new UITextPack(); seed.GetOrAdd("Original", "Draw", false).Translated = "本机译文";
        var seedJson = seed.ToJSON();
        var attempt = 0;
        Func<UITextPack, CancellationToken, Task<(int Changes, string? Warning)>> merge = (pack, token) =>
        {
            pack.GetOrAdd(++attempt == 1 ? "Partial" : "Complete", "Draw", false).Translated = "公共译文";
            return Task.FromResult((1, attempt == 1 ? "unavailable" : (string?)null));
        };
        var recovered = await (Task<(UITextPack Candidate, int Changes, string? Warning)>)fetch!.Invoke(null, [seed, merge, CancellationToken.None, 0])!;
        check(attempt == 2 && recovered.Warning is null && recovered.Candidate.Entries.All(e => e.Original != "Partial")
            && recovered.Candidate.Entries.Any(e => e.Original == "Complete") && seed.ToJSON() == seedJson,
            "library retry discards partial candidate content and preserves the local baseline");
        attempt = 0;
        merge = (pack, _) => { attempt++; pack.GetOrAdd("Partial", "Draw", false).Translated = "部分"; return Task.FromResult((1, (string?)"offline")); };
        recovered = await (Task<(UITextPack Candidate, int Changes, string? Warning)>)fetch.Invoke(null, [seed, merge, CancellationToken.None, 0])!;
        check(attempt == 2 && recovered.Warning == "offline" && recovered.Changes == 0 && recovered.Candidate.ToJSON() == seedJson,
            "exhausted library recovery returns only the unchanged baseline, not a partial update");
    }

    private static async Task TableRecovery(Action<bool, string> check)
    {
        var update = typeof(TranslationTable).GetMethod("UpdateFromClientAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        check(update is not null, "cloud table recovery can be tested with an offline HTTP transport");
        var root = Path.Combine(Path.GetTempPath(), "firegaze-recovery-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        string Table(string date, string value) => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["_meta"] = new { updatedAt = date }, ["Target"] = new { Name = new { Original = "Target", Translated = value } },
        });
        try
        {
            var path = Path.Combine(root, "translations.json");
            var local = Table("2026-10-12", "本机");
            File.WriteAllText(path, local);
            var table = new TranslationTable(root, root); table.Load(out _);
            async Task<bool> Run(Transport handler, CancellationToken token = default)
            {
                using var client = new HttpClient(handler);
                var task = (Task<(bool Ok, string Message)>)update!.Invoke(table, [client, token])!;
                return (await task).Ok;
            }
            var transport = new Transport((_, n) => new(HttpStatusCode.OK) { Content = new StringContent(Table(n == 1 ? "2026-10-11" : "2026-10-13", "新译文")) });
            check(await Run(transport) && transport.Calls == 2 && table.MaintainedAt == "2026-10-13",
                "an old primary cache does not prevent a newer mirror from updating the table");
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
            {
                transport = new((_, n) => n == 1 ? new(status) : new(HttpStatusCode.OK) { Content = new StringContent(Table("2026-10-14", "镜像")) });
                check(await Run(transport) && transport.Calls == 2, "HTTP " + (int)status + " falls back to the next cloud mirror");
            }
            transport = new((_, n) => new(HttpStatusCode.OK) { Content = new StringContent(n == 1 ? "broken JSON" : Table("2026-10-15", "有效镜像")) });
            check(await Run(transport) && transport.Calls == 2, "malformed cloud JSON falls back without replacing saved data");
            var before = File.ReadAllText(path);
            transport = new((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(Table("2026-10-10", "过期")) });
            check(!await Run(transport) && transport.Calls == 3 && File.ReadAllText(path) == before,
                "all stale mirrors preserve local data and never claim a completed update");
            transport = new((_, _) => new(HttpStatusCode.ServiceUnavailable));
            check(!await Run(transport) && transport.Calls == 3 && File.ReadAllText(path) == before,
                "all offline mirrors preserve the usable cache and stop at the route limit");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            transport = new((_, _) => new(HttpStatusCode.OK));
            var cancelled = false;
            try { await Run(transport, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && transport.Calls == 0, "cancelled cloud refresh does not start more mirror requests");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Channel(Func<IReadOnlyList<UITextTranslateItem>, int, UITextTranslateResult> action) : IUITextChannel
    {
        public int Calls;
        public string Name => "offline-fixture";
        public string Description => Name;
        public Task<UITextTranslateResult> TranslateAsync(IReadOnlyList<UITextTranslateItem> items, Action<int, int>? progress, CancellationToken token)
            => Task.FromResult(action(items, ++Calls));
    }
    private sealed class Transport(Func<HttpRequestMessage, int, HttpResponseMessage> action) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(action(request, ++Calls));
    }
}
