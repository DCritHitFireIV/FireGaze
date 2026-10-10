using FireGaze.UIText;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
    checks++;
}

var path = Path.Combine(Path.GetTempPath(), "firegaze-client-tests-" + Guid.NewGuid(), "queue.json");
try
{
    var queue = new UITextJobQueue(path);
    queue.Enqueue("A", "Alpha", UITextJobMode.Translate);
    queue.Enqueue("A", "Alpha", UITextJobMode.Translate);
    queue.Enqueue("B", "Beta", UITextJobMode.CheckUpdates);
    Check(queue.Items.Count == 2, "duplicate unfinished jobs are not enqueued");
    queue.SetPaused(false);
    Check(queue.BeginNext()?.InternalName == "A", "queue starts the first job");
    Check(queue.BeginNext() is null, "a running job prevents a second write task");
    queue.SetPaused(true);
    queue.Complete("A", false, "failed");
    Check(queue.BeginNext() is null, "pausing lets the current job finish without starting another");
    Check(queue.RetryFailures() == 1, "only failed jobs are retried");
    queue.SetPaused(false);
    Check(queue.BeginNext()?.InternalName == "A", "retry preserves job order");
    var recovered = new UITextJobQueue(path);
    Check(recovered.Paused && recovered.Items[0].State == UITextJobState.Pending,
        "restart pauses and recovers an interrupted job without silently starting translation");
    recovered.SetPaused(false);
    recovered.BeginNext();
    recovered.Complete("A", true, "done");
    Check(recovered.BeginNext()?.InternalName == "B", "failure and restart do not discard later jobs");
    recovered.SkipActive("B");
    Check(recovered.Items[1].State == UITextJobState.Skipped, "skipped jobs have a distinct terminal state");
    recovered.Enqueue("C", "Gamma", UITextJobMode.Translate);
    recovered.Enqueue("D", "Delta", UITextJobMode.Translate);
    recovered.BeginNext();
    Check(recovered.CancelWaiting() == 1 && recovered.Active?.InternalName == "C",
        "clearing waiting jobs does not interrupt a running DLL operation");
    var cancelQueue = typeof(UITextJobQueue).GetMethod("Cancel");
    Check(cancelQueue is not null, "queue has a single cancel operation");
    cancelQueue!.Invoke(recovered, null);
    Check(recovered.Paused && recovered.BeginNext() is null && recovered.Active?.InternalName == "C",
        "cancel stops future jobs but leaves the active write operation intact");
    var cancelled = new UITextJobQueue(path);
    cancelled.SetPaused(false);
    Check(cancelled.Items.All(j => j.InternalName != "D" || j.State == UITextJobState.Skipped),
        "cancelled waiting jobs stay cancelled after reload");
    Check(Enum.TryParse<UITextJobMode>("ApplyUpdates", out var applyMode), "batch update is a distinct queue operation, never a full retranslation");
    recovered.Complete("C", true, "done");
    recovered.Enqueue("E", "Epsilon", applyMode);
    recovered.SetPaused(false);
    Check(recovered.BeginNext()?.Mode == applyMode, "update jobs are serial and retain their operation on disk");
    var request = typeof(UITextJobQueue).GetMethod("Request");
    Check(request is not null, "row actions can request queued work while another plugin is running");
    Check((bool)request!.Invoke(recovered, ["F", "Phi", UITextJobMode.Translate])!,
        "clicking another plugin's translation action accepts it into the active queue");
    Check(!(bool)request.Invoke(recovered, ["F", "Phi", UITextJobMode.Translate])!,
        "clicking an already queued row cannot duplicate its translation job");
    Check(recovered.Active?.InternalName == "E", "requesting another plugin never replaces the active DLL operation");
    recovered.Complete("E", true, "done");
    Check(recovered.BeginNext()?.InternalName == "F", "a requested row starts automatically after the active plugin completes");

    var deferRecovery = typeof(UITextJobQueue).GetMethod("DeferRecovery");
    Check(deferRecovery is not null, "unavailable originals defer automatic recovery in the persistent queue");
    var retryQueue = new UITextJobQueue(Path.Combine(Path.GetDirectoryName(path)!, "recovery-queue.json"));
    retryQueue.Request("Repair", "Repair", UITextJobMode.CheckUpdates);
    retryQueue.BeginNext();
    deferRecovery!.Invoke(retryQueue, ["Repair", DateTime.UtcNow.AddMinutes(2), "等待原始包，自动重试"]);
    Check(retryQueue.Active is null && retryQueue.BeginNext() is null, "automatic recovery waits for its retry time without spinning");
    retryQueue.Request("Other", "Other", UITextJobMode.CheckUpdates);
    Check(retryQueue.BeginNext()?.InternalName == "Other", "deferred recovery does not block checks for other plugins");
    retryQueue.Complete("Other", true, "checked");
    retryQueue = new UITextJobQueue(Path.Combine(Path.GetDirectoryName(path)!, "recovery-queue.json"));
    retryQueue.SetPaused(false);
    Check(retryQueue.Items[0].Message.Contains("自动重试") && retryQueue.BeginNext() is null, "recovery retry and delay survive a FireGaze reload");
    typeof(UITextJob).GetProperty("RetryAt")!.SetValue(retryQueue.Items[0], DateTime.UtcNow.AddSeconds(-1));
    Check(retryQueue.BeginNext()?.InternalName == "Repair", "due automatic recovery resumes through the same serial queue");
    deferRecovery.Invoke(retryQueue, ["Repair", DateTime.UtcNow.AddMinutes(2), "等待原始包，自动重试"]);
    retryQueue.Cancel();
    Check(retryQueue.Items[0].State == UITextJobState.Skipped && retryQueue.BeginNext() is null, "cancelling the queue also cancels deferred automatic recovery");

    var discovery = new FireGaze.Discovery.DiscoveryStateStore(Path.GetDirectoryName(path)!);
    var watchInstall = typeof(FireGaze.Discovery.DiscoveryStateStore).GetMethod("WatchInstallation");
    var completeInstalls = typeof(FireGaze.Discovery.DiscoveryStateStore).GetMethod("CompleteInstallations");
    Check(watchInstall is not null && completeInstalls is not null, "recommendations track individual installation intent");
    watchInstall!.Invoke(discovery, ["Target"]);
    Check(((IReadOnlyList<string>)completeInstalls!.Invoke(discovery, [new[] { "OtherInSameRepo" }])!).Count == 0,
        "installing another plugin from the same repository does not recommend it");
    discovery = new FireGaze.Discovery.DiscoveryStateStore(Path.GetDirectoryName(path)!);
    var installedTargets = (IReadOnlyList<string>)completeInstalls.Invoke(discovery, [new[] { "Target", "OtherInSameRepo" }])!;
    Check(installedTargets.SequenceEqual(new[] { "Target" }) && discovery.PeekPending()?.Plugin == "Target",
        "only a successfully installed chosen plugin queues a recommendation after reload");
    Check(((IReadOnlyList<string>)completeInstalls.Invoke(discovery, [new[] { "Target" }])!).Count == 0,
        "refreshing the installed list cannot count the same intent twice");
    var watchMissing = typeof(FireGaze.Discovery.DiscoveryStateStore).GetMethod("WatchMissingInstallation");
    Check(watchMissing is not null, "installation recommendations validate a fresh installed baseline");
    watchMissing!.Invoke(discovery, ["AlreadyInstalled", new[] { "AlreadyInstalled" }]);
    Check(((IReadOnlyList<string>)completeInstalls.Invoke(discovery, [new[] { "AlreadyInstalled" }])!).Count == 0,
        "stale discovery state cannot recommend a plugin that was already installed");
    var forgetInstall = discovery.GetType().GetMethod("ForgetInstallation");
    Check(forgetInstall is not null, "failed native installations can discard recommendation intent");
    discovery.WatchInstallation("FailedTarget");
    forgetInstall!.Invoke(discovery, ["FailedTarget"]);
    discovery = new FireGaze.Discovery.DiscoveryStateStore(Path.GetDirectoryName(path)!);
    Check(discovery.CompleteInstallations(["FailedTarget"]).Count == 0,
        "a failed discovery install never credits a later unrelated installation after restart");

    var pack = new UITextPack();
    var ambiguous = pack.GetOrAdd("Play", "Draw", false);
    ambiguous.Role = "Ambiguous";
    var skipped = pack.GetOrAdd("Mode", "Draw", false);
    pack.Skipped.Add("Mode");
    var translated = pack.GetOrAdd("Settings", "Draw", false);
    translated.Translated = "设置";
    translated.Source = "user";
    var extraction = new UITextExtraction();
    extraction.Entries.Add(new UITextEntry { Original = "Play", Role = UITextRole.Ambiguous, Reason = "also compared" });
    extraction.Entries.Add(new UITextEntry { Original = "Mode", Role = UITextRole.UI });
    extraction.Entries.Add(new UITextEntry { Original = "Settings", Role = UITextRole.UI });
    extraction.Entries.Add(new UITextEntry { Original = "New option", Role = UITextRole.UI });
    extraction.Entries.Add(new UITextEntry { Original = "configKey", Role = UITextRole.Excluded, Reason = "localization key" });
    pack.Skipped.Add("Ignored without entry");
    extraction.Entries.Add(new UITextEntry { Original = "Ignored without entry", Role = UITextRole.UI });
    Check(UITextMaintenance.NewCandidates(pack, extraction) == 1,
        "new-text count excludes old ambiguous words, intentionally skipped text and functional keys");
    Check(UITextMaintenance.Diagnose(pack, extraction, "Play", true, false)[0].Kind == UITextDiagnosticKind.KeptOriginal,
        "ambiguous single words are explained rather than prompted for machine translation");
    Check(UITextMaintenance.Diagnose(pack, extraction, "Mode", true, false)[0].Kind == UITextDiagnosticKind.KeptOriginal,
        "player skip decisions are respected by diagnostics");
    Check(UITextMaintenance.Diagnose(pack, extraction, "configKey", true, false)[0].Kind == UITextDiagnosticKind.Excluded,
        "functional keys never become diagnostic translation targets");
    Check(UITextMaintenance.Diagnose(pack, extraction, "Settings", true, true)[0].Kind == UITextDiagnosticKind.ReadyToApply,
        "diagnostics distinguish translated text that has not been applied");
    Check(UITextMaintenance.Diagnose(pack, extraction, "Not extracted", true, false).Count == 0,
        "a missing phrase is not misreported as untranslated or safe to translate");
    translated.Review = "placeholder mismatch";
    Check(UITextMaintenance.Diagnose(pack, extraction, "Settings", true, false)[0].Kind == UITextDiagnosticKind.NeedsReview,
        "rejected translation is shown as needing review, not applied");
    Check(UITextMaintenance.AppliedLabel(60, 63, false) == "已汉化", "collapsed status hides partial translation counts");
    Check(UITextMaintenance.AppliedLabel(60, 63, true) == "已汉化 60/63 条", "expanded status retains detailed counts");
    Check(translated.Translated == "设置" && pack.IsSkipped("Mode"), "checking and diagnosing never mutate player content");
    var updatePolicy = typeof(UITextMaintenance).GetMethod("CanApplyUpdate");
    Check(updatePolicy is not null, "update availability uses real translation differences");
    bool CanUpdate(UITextPack current, UITextPack candidate, string? warning = null) => (bool)updatePolicy!.Invoke(null,
        [UITextFlow.PreviewMerge(current, candidate), warning, 0])!;
    var unchanged = UITextPack.FromJSON(pack.ToJSON(), out _)!;
    Check(!CanUpdate(pack, unchanged), "unchanged public translations do not show an update action");
    var changedPack = UITextPack.FromJSON(pack.ToJSON(), out _)!;
    changedPack.Find("Settings")!.Translated = "被覆盖的设置";
    changedPack.Find("Settings")!.Source = "library";
    Check(!CanUpdate(pack, changedPack), "protected manual translations do not create a fake update action");
    changedPack.GetOrAdd("New option", "Draw", false).Translated = "新选项";
    Check(CanUpdate(pack, changedPack), "new public translations enable the batch update action");
    Check(!CanUpdate(pack, changedPack, "download failed"), "a partial failed library check is not offered for application");
    var checkpoints = new List<int>();
    var progressCounts = new List<int>();
    var channel = new InterruptingChannel();
    var session = await UITextTranslationSession.TranslateAsync(channel,
        [new("One", null), new("Two", null), new("Three", null)],
        (done, _) => progressCounts.Add(done), batch => checkpoints.Add(batch.Translated.Count),
        CancellationToken.None, batchSize: 2);
    Check(checkpoints.SequenceEqual(new[] { 2 }), "completed batches checkpoint before a later batch is interrupted");
    Check(session.Translated.Count == 2 && session.Error == "已取消", "interruption returns translated results without inventing success");
    Check(progressCounts.Contains(2) && progressCounts.All(n => n <= 3), "batch progress uses overall totals");
    var companion = Path.Combine(Path.GetDirectoryName(path)!, "companion.dll");
    File.WriteAllText(companion, "before");
    var signature = UITextMaintenance.FileSignature([path, companion]);
    File.WriteAllText(companion, "after");
    Check(signature != UITextMaintenance.FileSignature([path, companion]),
        "changing a companion file invalidates an update preview even if the main file is unchanged");
    var installer = new FixtureInstaller();
    var installerInterface = new FixtureInstallerInterface(installer);
    Check(FireGaze.Internal.PluginInstallerBridge.OpenFromInterface(installerInterface, "Browsingway", out var installerError)
        && installer.Page == FixtureInstallerPage.AllPlugins && installer.Query == "Browsingway" && installerError is null,
        "installer navigation selects available plugins and preserves the target query");
    Check(!FireGaze.Internal.PluginInstallerBridge.OpenFromInterface(new object(), "Browsingway", out installerError)
        && installerError is not null, "an unsupported installer contract reports failure instead of claiming navigation succeeded");
    var selectionType = typeof(UITextMaintenance).Assembly.GetType("FireGaze.UI.MainTabSelection");
    Check(selectionType is not null, "tab selection retains navigation requests made during another tab's draw");
    var selection = Activator.CreateInstance(selectionType!, true)!;
    var requestTab = selectionType!.GetMethod("Request")!;
    var takeTab = selectionType.GetMethod("Take")!;
    Check(takeTab.Invoke(selection, null) is null, "a frame without navigation does not force a tab");
    requestTab.Invoke(selection, [FireGaze.UI.MainTab.UIText]);
    Check((FireGaze.UI.MainTab?)takeTab.Invoke(selection, null) == FireGaze.UI.MainTab.UIText,
        "discovery translation navigation reaches the translation tab on the following frame");
    requestTab.Invoke(selection, [FireGaze.UI.MainTab.Discovery]);
    selectionType.GetMethod("RequestDefault")!.Invoke(selection, [FireGaze.UI.MainTab.UIText]);
    Check((FireGaze.UI.MainTab?)takeTab.Invoke(selection, null) == FireGaze.UI.MainTab.Discovery,
        "tab navigation is not overwritten by default selection");
    Check(takeTab.Invoke(selection, null) is null, "a consumed tab request does not trap users on that tab");
    await InstallerTests.Run(Check);
    await DiscoveryUiTests.Run(Check);
    Console.WriteLine($"ALL PASS {checks}");
}

finally
{
    if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
}

file sealed class InterruptingChannel : IUITextChannel
{
    private int requests;
    public string Name => "fixture";
    public string Description => "fixture";
    public Task<UITextTranslateResult> TranslateAsync(IReadOnlyList<UITextTranslateItem> items, Action<int, int>? progress, CancellationToken token)
    {
        if (++requests == 2) throw new OperationCanceledException();
        var result = new UITextTranslateResult();
        foreach (var item in items) result.Translated[item.Text] = "译文";
        progress?.Invoke(items.Count, items.Count);
        return Task.FromResult(result);
    }
}

file enum FixtureInstallerPage { InstalledPlugins, AllPlugins }
file sealed class FixtureInstaller
{
    public FixtureInstallerPage Page;
    public string Query = "";
    public void SetSearchText(string text) => Query = text;
}
file sealed class FixtureInstallerInterface
{
    private readonly FixtureInstaller pluginWindow;
    public FixtureInstallerInterface(FixtureInstaller window) => pluginWindow = window;
    public void OpenPluginInstallerTo(FixtureInstallerPage page) => pluginWindow.Page = page;
}
