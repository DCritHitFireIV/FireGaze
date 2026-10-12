using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FireGaze;
using FireGaze.Discovery;
using FireGaze.Internal.Configuration;
using FireGaze.RepoAudit;
using FireGaze.Translate;
using FireGaze.UIText;

internal static class FullCoverageTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var failures = new List<string>();
        void Assert(bool ok, string name)
        {
            if (ok) check(true, name);
            else { failures.Add(name); Console.WriteLine("FAIL " + name); }
        }
        async Task Case(string name, Func<Task> action)
        {
            try { await action(); }
            catch (Exception e) { failures.Add(name + ": " + e.GetBaseException().Message); Console.WriteLine("FAIL " + failures[^1]); }
        }
        var root = Path.Combine(Path.GetTempPath(), "firegaze-full-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Case("slow UI diagnostic boundaries", () =>
            {
                var type = typeof(UITextJobQueue).Assembly.GetType("FireGaze.Diagnostics.SlowUiDiagnostics");
                Assert(type is not null, "UI stalls expose bounded phase timing rather than only a whole-plugin hitch");
                if (type is null) return Task.CompletedTask;
                var now = 0L; var messages = new List<string>();
                var monitor = Activator.CreateInstance(type, [(Action<string>)messages.Add, (Func<long>)(() => now)])!;
                var report = type.GetMethod("Report")!;
                void Record(string phase, double milliseconds) => report.Invoke(monitor, [phase, milliseconds]);
                Record("Rows", 99);
                Assert(messages.Count == 0, "normal UI frames produce no performance log traffic");
                Record("Rows", 100);
                Assert(messages.Count == 1 && messages[0].Contains("Rows") && messages[0].Contains("100"), "slow phase reports its own name and elapsed time");
                for (var i = 0; i < 1200; i++) Record("Rows", 200);
                Assert(messages.Count == 1, "repeated stalls are throttled per phase across 1200 frames");
                Record("List", 200);
                Assert(messages.Count == 2, "throttling one phase cannot suppress a different bottleneck");
                now = 29_999; Record("Rows", 200);
                Assert(messages.Count == 2, "slow diagnostic cooldown has an exact lower boundary");
                now = 30_000; Record("Rows", 200);
                Assert(messages.Count == 3, "slow phase can report again after thirty seconds");
                var broken = Activator.CreateInstance(type, [(Action<string>)(_ => throw new IOException("blocked log")), (Func<long>)(() => now)])!;
                report.Invoke(broken, ["Rows", 200d]);
                Assert(true, "diagnostic sink failure cannot crash the game UI");
                return Task.CompletedTask;
            });
            await Case("queue malformed-state boundaries", () =>
            {
                foreach (var data in new[] { "[null]", "[{\"InternalName\":\"Invalid\",\"Mode\":99}]", "[{\"InternalName\":null}]", "broken", "{}" })
                {
                    var path = Path.Combine(root, Guid.NewGuid() + ".json"); File.WriteAllText(path, data);
                    var queue = new UITextJobQueue(path);
                    Assert(queue.Error is not null && queue.Items.Count == 0 && queue.Paused,
                        "malformed queue is rejected before it can reach UI or paid work: " + data);
                    Assert(File.ReadAllText(path) == data, "loading malformed queue does not destroy the original file");
                    Assert(queue.Request("Fresh", "Fresh", UITextJobMode.Translate)
                        && Directory.GetFiles(root, Path.GetFileName(path) + ".invalid-*").Any(p => File.ReadAllText(p) == data),
                        "explicit fresh work preserves an independent copy of malformed queue data");
                }
                var queuePath = Path.Combine(root, "queue-disk-failure.json"); Directory.CreateDirectory(queuePath);
                var nullablePath = Path.Combine(root, "nullable-queue.json");
                File.WriteAllText(nullablePath, "[{\"InternalName\":\"Valid\",\"DisplayName\":null,\"Message\":null}]");
                var nullableQueue = new UITextJobQueue(nullablePath);
                Assert(nullableQueue.Error is null && nullableQueue.Items.Single().Message == "" && nullableQueue.Items.Single().DisplayName == "Valid",
                    "optional null queue labels are normalized before the UI reads their length");
                var blocked = new UITextJobQueue(queuePath);
                Assert(!blocked.Request("Target", "Target", UITextJobMode.Translate) && blocked.Paused && blocked.Error is not null && blocked.BeginNext() is null,
                    "blocked queue destination cannot start unpersisted work");
                var good = new UITextJobQueue(Path.Combine(root, "transitions.json"));
                foreach (var mode in Enum.GetValues<UITextJobMode>())
                {
                    var name = mode.ToString();
                    if (mode is UITextJobMode.UpgradeTranslations or UITextJobMode.RetranslateNonHuman or UITextJobMode.RestoreTranslations)
                        good.RequestRewrite(name, name, mode, new UITextRewritePlan { Upgrade = mode == UITextJobMode.UpgradeTranslations });
                    else good.Request(name, name, mode);
                }
                var modes = new List<UITextJobMode>();
                while (good.BeginNext() is { } job)
                { modes.Add(job.Mode); good.Complete("wrong-name", true, "ignored"); Assert(good.Active == job, "wrong-plugin completion cannot close a running " + job.Mode); good.Complete(job.InternalName, true, "done"); }
                Assert(modes.SequenceEqual(Enum.GetValues<UITextJobMode>()), "all six queue operation modes remain ordered and serial");
                return Task.CompletedTask;
            });

            await Case("discovery week and callback boundaries", () =>
            {
                for (var year = 2020; year <= 2030; year++)
                {
                    var dates = new[] { new DateTimeOffset(year, 1, 1, 15, 59, 59, TimeSpan.Zero), new DateTimeOffset(year, 12, 31, 16, 0, 0, TimeSpan.Zero) };
                    Assert(dates.All(d => DiscoveryWeek.Label(d) == $"{ISOWeek.GetYear(d.AddHours(8).Date)}-W{ISOWeek.GetWeekOfYear(d.AddHours(8).Date):00}"),
                        "Beijing week and ISO year boundaries match standard library: " + year);
                }
                var current = DiscoveryWeek.Current(); var previous = DiscoveryWeek.Label(DateTimeOffset.UtcNow.AddDays(-7));
                var state = new DiscoveryStateStore(Path.Combine(root, "weeks"));
                state.MarkLiked("Target", previous);
                var old = state.PeekPending();
                // Preserve a detached old request regardless of whether obsolete likes are pruned on peek.
                old ??= new DiscoveryPendingAction { Type = "like", Plugin = "Target", Week = previous, AtUTC = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                state.MarkLiked("Target", current);
                Assert(state.PeekPending()?.Week == current, "new-week click replaces an obsolete unsent like with the current week");
                state.CompletePending(old);
                Assert(state.PeekPending()?.Week == current, "late old-week success cannot remove the newly confirmed weekly action");
                state = new DiscoveryStateStore(Path.Combine(root, "weeks"));
                Assert(state.LikedThisWeek("Target", current) && state.PeekPending()?.Week == current, "weekly confirmation and pending action survive restart together");
                state = new DiscoveryStateStore(Path.Combine(root, "stale-week")); state.MarkLiked("Other", previous);
                Assert(state.PeekPending() is null, "obsolete weekly intent is not silently sent as a like in a new week");
                var concurrent = new DiscoveryStateStore(Path.Combine(root, "concurrent"));
                Parallel.For(0, 500, i => concurrent.MarkAdds(["Plugin" + i % 10]));
                var finished = 0;
                while (concurrent.PeekPending() is { } action) { concurrent.CompletePending(action); finished++; }
                Assert(finished == 10 && new DiscoveryStateStore(Path.Combine(root, "concurrent")).PeekPending() is null,
                    "concurrent recommendation producers persist exactly one pending action per plugin");
                foreach (var data in new[] { "{\"Pending\":[null]}", "{\"Pending\":[{\"Type\":\"unknown\",\"Plugin\":\"Target\"}]}" })
                {
                    var directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                    var statePath = Path.Combine(directory, "discovery-state.json"); File.WriteAllText(statePath, data);
                    var damaged = new DiscoveryStateStore(directory);
                    Assert(damaged.TakePersistFailure() && damaged.PeekPending() is null, "malformed discovery pending action is rejected without crashing or fabricating a like");
                    damaged.MarkAdds(["Fresh"]);
                    Assert(Directory.GetFiles(directory, "discovery-state.json.invalid-*").Any(p => File.ReadAllText(p) == data), "fresh discovery work preserves an independent copy of damaged state");
                }
                var cacheDir = Path.Combine(root, "null-counts"); Directory.CreateDirectory(cacheDir);
                File.WriteAllText(Path.Combine(cacheDir, "discovery-state.json"), "{\"Stats\":{\"WeeklyLikes\":null,\"TotalLikes\":null,\"Recommends\":null}}");
                var cache = new DiscoveryStateStore(cacheDir);
                Assert(cache.CachedStats!.WeeklyOf("Missing") == 0 && cache.CachedStats.TotalOf("Missing") == 0 && cache.CachedStats.RecommendsOf("Missing") == 0,
                    "legacy null statistics dictionaries render as zero rather than crashing discovery");
                return Task.CompletedTask;
            });

            await Case("network transport boundaries", async () =>
            {
                var fetch = typeof(RepoScanner).GetMethod("FetchOneAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
                using var stalled = new StalledContent();
                using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = stalled }));
                using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                using var cancel = new CancellationTokenSource();
                var task = (Task)fetch.Invoke(null, [client, "https://offline.invalid/slow.json", cancel.Token])!;
                await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                cancel.Cancel();
                var stopped = await Task.WhenAny(task, Task.Delay(250)) == task;
                stalled.Release.TrySetResult();
                var cancelled = false;
                try { await task.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { cancelled = true; }
                Assert(stopped && cancelled, "repository cancellation also interrupts a stalled response body after headers");

                var checkOne = typeof(RepoScanner).GetMethod("CheckOneAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
                foreach (var (status, expected) in new[] { (401, RepoStatus.Blocked), (403, RepoStatus.Blocked), (429, RepoStatus.Blocked),
                    (404, RepoStatus.Dead), (410, RepoStatus.Dead), (500, RepoStatus.Unreachable), (503, RepoStatus.Unreachable) })
                {
                    using var transport = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("response") }));
                    using var http = new HttpClient(transport);
                    var item = new RepoAuditItem { URL = "https://offline.invalid/" + status };
                    await (Task)checkOne.Invoke(null, [http, item, CancellationToken.None])!;
                    Assert(item.Status == expected && item.HTTPStatus == status && item.IsAutoSelected == (expected == RepoStatus.Dead),
                        "repository HTTP classification does not mistake access or outage for dead link: " + status);
                }
                var valid = "[{\"InternalName\":\"Target\",\"Name\":\"Target\",\"AssemblyVersion\":\"1.0.0.0\",\"DalamudApiLevel\":15}]";
                foreach (var (body, expected) in new[] { (valid, RepoStatus.Ok), ("[]", RepoStatus.Empty), ("<html>proxy page</html>", RepoStatus.Invalid),
                    ("{}", RepoStatus.Invalid), ("[{\"InternalName\":\"NoName\"}]", RepoStatus.Ok) })
                {
                    using var transport = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
                    using var http = new HttpClient(transport);
                    var item = new RepoAuditItem { URL = "https://offline.invalid/" + Guid.NewGuid() };
                    await (Task)checkOne.Invoke(null, [http, item, CancellationToken.None])!;
                    Assert(item.Status == expected, "repository body contract follows native manifest parser: " + body);
                }
                using var racing = new Handler(async (request, token) =>
                {
                    if (request.RequestUri!.Host == "github.com") await Task.Delay(60, token);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.Host == "github.com" ? valid : "<html>mirror gate</html>") };
                });
                using var racingHttp = new HttpClient(racing);
                var raceItem = new RepoAuditItem { URL = "https://github.com/offline/fixture/releases/latest/download/repo.json" };
                await (Task)checkOne.Invoke(null, [racingHttp, raceItem, CancellationToken.None])!;
                Assert(raceItem.Status == RepoStatus.Ok && raceItem.Channel == "直连" && !raceItem.IsAutoSelected,
                    "a fast HTTP-200 mirror error cannot hide a slower valid repository and mark it for deletion");
                var cacheRaceURL = "https://github.com/offline/" + Guid.NewGuid() + "/repo.json";
                var phase = 0;
                using var staleMirror = new Handler(async (request, token) =>
                {
                    if (request.RequestUri!.Host == "github.com")
                    {
                        await Task.Delay(60, token);
                        return new HttpResponseMessage(phase == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(valid) };
                    }
                    var r = new HttpResponseMessage(phase == 0 ? HttpStatusCode.OK : HttpStatusCode.NotModified) { Content = new StringContent("<html>cached mirror error</html>") };
                    r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"bad-mirror\""); return r;
                });
                using var staleHttp = new HttpClient(staleMirror);
                await (Task)checkOne.Invoke(null, [staleHttp, new RepoAuditItem { URL = cacheRaceURL }, CancellationToken.None])!;
                phase = 1;
                var revised = new RepoAuditItem { URL = cacheRaceURL };
                await (Task)checkOne.Invoke(null, [staleHttp, revised, CancellationToken.None])!;
                Assert(revised.Status == RepoStatus.Ok && revised.Channel == "直连", "304 of a previously invalid mirror cannot hide a now-valid primary repository");
                var checker = typeof(RepoSubmitChecker).GetMethod("CheckFromClientAsync", BindingFlags.NonPublic | BindingFlags.Static);
                Assert(checker is not null, "repository submission checker supports offline transport boundary tests");
                if (checker is not null)
                {
                    var submit = await (Task<RepoSubmitCheck>)checker.Invoke(null, [racingHttp, raceItem.URL, CancellationToken.None])!;
                    Assert(submit.Ok && submit.PluginCount == 1, "submission check also ignores a fast invalid mirror without uploading anything");
                    using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel();
                    var didCancel = false;
                    try { await (Task<RepoSubmitCheck>)checker.Invoke(null, [racingHttp, raceItem.URL, preCancelled.Token])!; } catch (OperationCanceledException) { didCancel = true; }
                    Assert(didCancel, "cancelling a submission precheck preserves cancellation instead of misreporting bad repository");
                }
                var calls = 0;
                using var etags = new Handler((request, _) =>
                {
                    calls++; var response = new HttpResponseMessage(request.Headers.IfNoneMatch.Count == 0 ? HttpStatusCode.OK : HttpStatusCode.NotModified) { Content = new StringContent(valid) };
                    response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"fixture\"");
                    if (calls == 2) Assert(request.Headers.IfNoneMatch.Any(e => e.Tag == "\"fixture\""), "repeat audit sends the cached ETag");
                    return Task.FromResult(response);
                });
                using var etagHttp = new HttpClient(etags);
                var cacheURL = "https://offline.invalid/cache-" + Guid.NewGuid();
                await (Task)checkOne.Invoke(null, [etagHttp, new RepoAuditItem { URL = cacheURL }, CancellationToken.None])!;
                var cached = new RepoAuditItem { URL = cacheURL };
                await (Task)checkOne.Invoke(null, [etagHttp, cached, CancellationToken.None])!;
                Assert(cached.Status == RepoStatus.Ok && cached.PluginCount == 1 && cached.NotModifiedThisRun,
                    "304 restores the full valid audit result rather than returning unknown or zero plugins");
                var submitAfterAudit = await RepoSubmitChecker.CheckFromClientAsync(etagHttp, cacheURL, CancellationToken.None);
                Assert(submitAfterAudit.Ok && submitAfterAudit.PluginCount == 1,
                    "submission after an ETag-cached audit requests a body instead of mistaking 304 for unavailable repository");
            });

            await Case("configuration migration boundaries", () =>
            {
                foreach (var channel in new[] { "llm", "caiyun", "deepl", "public-caiyun", "auto", "google", "mymemory" })
                {
                    var cfg = new Configuration { Version = 3, UITextChannel = channel, UITextLLMKeyProtected = "protected-key",
                        UITextLLMModel = "chosen-model", UITextTranslateGreyList = true };
                    Assert(ConfigurationMigrator.Migrate(cfg) == 3 && cfg.Version == 4 && cfg.UITextLLMKeyProtected == "protected-key"
                        && cfg.UITextLLMModel == "chosen-model" && cfg.UITextTranslateGreyList
                        && cfg.UITextChannel == (channel is "auto" or "google" or "mymemory" ? "public-caiyun" : channel),
                        "migration preserves paid key/model/grey preferences and applies only historical free-default rule: " + channel);
                }
                foreach (var version in new[] { -1, 0, 1, 2, 4 })
                { var cfg = new Configuration { Version = version }; ConfigurationMigrator.Migrate(cfg); Assert(cfg.Version == 4, "configuration reaches current schema from " + version); }
                return Task.CompletedTask;
            });

            await Case("pack, contribution and diagnostic boundaries", () =>
            {
                var mirror = Path.Combine(root, "mirror"); var store = new UITextStore(Path.Combine(root, "packs"), mirror);
                var pack = new UITextPack(); var literal = pack.GetOrAdd("Player text", "Draw", true); literal.Role = "UI"; literal.Source = "user"; literal.Translated = "玩家校对";
                var resource = pack.GetOrAddResource("file:I18N/zh-CN.json", "/a~1b", "Resource text"); resource.Source = "user"; resource.Translated = "资源校对";
                pack.MarkSkipped("Skipped text");
                Assert(store.Save("Target", pack, out _) && File.Exists(Path.Combine(mirror, "Target.json")), "pack write keeps the durable mirror and primary together");
                File.Delete(Path.Combine(store.DirectoryPath, "Target.json"));
                var restored = store.LoadRequired("Target");
                Assert(restored.Find("Player text")?.IsUserSource == true && restored.IsSkipped("Skipped text")
                    && restored.Resources[0].Key == "/a~1b" && restored.Resources[0].Translated == "资源校对", "missing primary restores exact manual provenance, resource keys and skip decisions from mirror");
                Assert(store.Delete("Target", out _) && !store.Exists("Target") && !File.Exists(Path.Combine(mirror, "Target.json")), "explicit pack deletion cannot silently resurrect a stale mirror on next load");
                foreach (var source in new[] { "user", "USER", "ai:google", "ai:caiyun", "ai:llm", "ai:deepseek", "library", "old-pack" })
                {
                    var sample = new UITextPack();
                    foreach (var (text, role) in new[] { ("Interface text", "UI"), ("Grey text", "Ambiguous"), ("Functional text", "Excluded"), ("本来就是中文", "UI") })
                    { var e = sample.GetOrAdd(text, "Draw", false); e.Role = role; e.Source = source; e.Translated = "旧译文"; }
                    var skipped = sample.GetOrAdd("Skipped text", "Draw", false); skipped.Role = "UI"; skipped.Source = source; skipped.Translated = "不翻旧文"; sample.MarkSkipped(skipped.Original);
                    var targets = UITextRetranslation.SelectTargets(sample, false, true);
                    UITextRetranslation.Accept(sample, targets, sample.Entries.ToDictionary(e => e.Original, _ => "新译文"), "llm");
                    var human = source.Equals("user", StringComparison.OrdinalIgnoreCase);
                    Assert(sample.Find("Interface text")!.Translated == (human ? "旧译文" : "新译文")
                        && sample.Find("Grey text")!.Translated == (human ? "旧译文" : "新译文")
                        && sample.Find("Functional text")!.Translated == "旧译文" && sample.Find("本来就是中文")!.Translated == "旧译文"
                        && sample.Find("Skipped text")!.Translated == "不翻旧文", "source/property matrix preserves human, excluded, Chinese and skipped text: " + source);
                }
                var contributions = new ContributionsStore(Path.Combine(root, "contributions"));
                ContributionRecord Record(string name, string text) => new() { ID = Guid.NewGuid().ToString("N"), InternalName = name, Field = "Description", Original = "Original", Translated = text, Previous = "baseline" };
                contributions.AddOrReplace(Record("Target", "第一版")); contributions.Remove("Target", "description");
                contributions.AddOrReplace(Record("Target", "后来校对"));
                Assert(contributions.Undo(out _).Count == 0 && contributions.Find("Target", "Description")!.Translated == "后来校对", "undo of old contribution removal cannot replace later proofreading");
                contributions.ClearAll(); contributions.AddOrReplace(Record("Other", "新条目")); contributions.Undo(out _);
                Assert(contributions.Count == 2 && new ContributionsStore(Path.Combine(root, "contributions")).Count == 2, "clear/undo merges removed entries without losing newer records and persists restart state");
                using var exported = JsonDocument.Parse(contributions.BuildJSON());
                Assert(exported.RootElement.GetProperty("contributions").GetArrayLength() == 2, "contribution JSON remains structured and round-trippable without submitting it");
                foreach (var field in new[] { "Name", "Punchline", "Description" })
                { var limit = ContributionRules.LimitOf(field); Assert(ContributionRules.Check(field, new string('a', limit)) is null && ContributionRules.Check(field, new string('a', limit + 1)) is not null, "contribution validation preserves the exact field boundary: " + field); }
                foreach (var bad in new[] { "<script>content</script>", "https://example.com", "text\u0000hidden" })
                    Assert(ContributionRules.Check("Description", bad) is not null, "contribution validation rejects unsafe markup/link/control input");
                foreach (var secret in new[] { "sk-" + new string('A', 24), "sk-proj-" + new string('A', 32) + "_" + new string('B', 20),
                    "ghp_" + new string('C', 30), "github_pat_" + new string('D', 30), "Authorization: Bearer " + new string('E', 32) })
                    Assert(!FireGaze.Diagnostics.ActivityLog.Redact("request " + secret).Contains(secret, StringComparison.Ordinal), "feedback export redacts token form: " + secret.Split(' ')[0][..Math.Min(14, secret.Split(' ')[0].Length)]);
                foreach (var userPath in new[] { @"C:\Users\Example Player\Documents\fixture.txt", "C:/Users/Example Player/Documents/fixture.txt", "/home/example/Documents/fixture.txt" })
                    Assert(!FireGaze.Diagnostics.ActivityLog.Redact(userPath).Contains(userPath.Contains("Example Player") ? "Player" : "example", StringComparison.Ordinal), "feedback export does not expose username through path separators or spaces");
                var oldMirror = FireGaze.Diagnostics.ActivityLog.Mirror;
                var mirrored = new List<string>();
                var fakeKey = "sk-proj-" + new string('F', 50);
                try
                {
                    FireGaze.Diagnostics.ActivityLog.Mirror = (_, text) => mirrored.Add(text);
                    FireGaze.Diagnostics.ActivityLog.Error("fixture", @"C:\Users\Example Player\fixture " + fakeKey, new IOException("Bearer " + fakeKey));
                    var raw = FireGaze.Diagnostics.ActivityLog.Snapshot(1, FireGaze.Diagnostics.ActivityLevel.Trace, redact: false);
                    Assert(!raw.Contains(fakeKey) && mirrored.All(s => !s.Contains(fakeKey)) && raw.Contains(@"C:\Users\Example Player"),
                        "known credentials are masked before ring buffer, disk queue and native mirror while local diagnostic paths remain useful");
                }
                finally { FireGaze.Diagnostics.ActivityLog.Mirror = oldMirror; }
                return Task.CompletedTask;
            });

            await Case("probe source link portability", () =>
            {
                var repo = Environment.GetEnvironmentVariable("FIREGAZE_REPO_ROOT") ?? Directory.GetCurrentDirectory();
                var project = Path.Combine(repo, "tools", "UITextProbe", "UITextProbe.csproj");
                var xml = System.Xml.Linq.XDocument.Load(project);
                foreach (var include in xml.Descendants("Compile").Select(e => (string)e.Attribute("Include")!).Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include));
                    var actual = Directory.EnumerateFiles(Path.GetDirectoryName(full)!).FirstOrDefault(p => Path.GetFileName(p).Equals(Path.GetFileName(full), StringComparison.OrdinalIgnoreCase));
                    Assert(actual is not null && Path.GetFileName(actual).Equals(Path.GetFileName(full), StringComparison.Ordinal), "probe source link works on case-sensitive CI: " + Path.GetFileName(full));
                }
                return Task.CompletedTask;
            });

            await Case("session fault boundaries", async () =>
            {
                foreach (var error in new[] { "HTTP 401", "HTTP 403", "HTTP 429", "HTTP 500", "invalid JSON", "quota exhausted" })
                {
                    var pack = new UITextPack(); var entry = pack.GetOrAdd("Original text", "Draw", false); entry.Role = "UI"; entry.Translated = "旧译文"; entry.Source = "ai:google";
                    var before = pack.ToJSON(); var channel = new Channel(_ => new() { Error = error });
                    var result = await UITextTranslationSession.TranslateAsync(channel, [new("Original text", null)], null,
                        batch => UITextRetranslation.Accept(pack, UITextRetranslation.SelectTargets(pack, false, false), batch.Translated, channel.Name), CancellationToken.None);
                    Assert(channel.Calls == 1 && result.Error == error && pack.ToJSON() == before, "fatal rewrite channel error preserves entire old pack and stops extra session requests: " + error);
                }
                var responseKeys = new List<string>();
                var noisy = new Channel(items => { var r = new UITextTranslateResult(); r.Translated[items[0].Text] = "有效译文"; r.Translated["Human text"] = "不该覆盖"; return r; });
                var safe = await UITextTranslationSession.TranslateAsync(noisy, [new("Only requested text", null)], null,
                    batch => responseKeys.AddRange(batch.Translated.Keys), CancellationToken.None);
                Assert(safe.Translated.Keys.SequenceEqual(["Only requested text"]) && responseKeys.SequenceEqual(["Only requested text"]),
                    "unsolicited channel output never reaches an old human translation through a normal checkpoint");
            });
        }
        finally { Directory.Delete(root, true); }
        if (failures.Count > 0) throw new AggregateException(failures.Select(s => new InvalidOperationException(s)));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
    private sealed class StalledContent : HttpContent
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        { Started.TrySetResult(); await Release.Task.WaitAsync(token); await stream.WriteAsync(Encoding.UTF8.GetBytes("[]"), token); }
    }
    private sealed class Channel(Func<IReadOnlyList<UITextTranslateItem>, UITextTranslateResult> action) : IUITextChannel
    {
        public int Calls;
        public string Name => "offline-test";
        public string Description => Name;
        public Task<UITextTranslateResult> TranslateAsync(IReadOnlyList<UITextTranslateItem> items, Action<int, int>? progress, CancellationToken token)
        { Calls++; return Task.FromResult(action(items)); }
    }
}
