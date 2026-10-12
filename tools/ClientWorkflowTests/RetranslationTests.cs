using System.Reflection;
using FireGaze.UIText;

internal static class RetranslationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var type = typeof(UITextFlow).Assembly.GetType("FireGaze.UIText.UITextRetranslation");
        check(type is not null, "mixed-source retranslation has a separate policy instead of clearing translations");
        var select = type!.GetMethod("SelectTargets")!;
        var accept = type.GetMethod("Accept")!;
        var pack = new UITextPack();
        foreach (var (text, source) in new[] { ("Free", "ai:google"), ("Cloud", "library"), ("Model", "ai:deepseek"),
            ("Other model", "ai:llm"), ("Human", "user"), ("Unknown", "old-pack"), ("Empty", "") })
        {
            var e = pack.GetOrAdd(text, "Draw", false); e.Role = "UI"; e.Source = source;
            if (text != "Empty") e.Translated = "旧译文";
        }
        pack.GetOrAdd("Skipped", "Draw", false).Role = "UI"; pack.MarkSkipped("Skipped");
        pack.GetOrAdd("Functional", "GetIpc", false).Role = "Excluded";
        pack.GetOrAdd("Grey", "Draw", false).Role = "Ambiguous";
        pack.GetOrAdd("原生中文", "Draw", false).Role = "UI";
        var original = pack.ToJSON();
        List<UITextTarget> Select(bool upgrade, bool grey = false) => (List<UITextTarget>)select.Invoke(null, [pack, upgrade, grey])!;
        var targets = Select(true);
        check(targets.Select(t => t.Original).Order().SequenceEqual(new[] { "Cloud", "Empty", "Free" }.Order()),
            "upgrade includes empty, free and base-cloud text but retains all LLM, human and unknown sources");
        targets = Select(false);
        check(targets.Count == 6 && targets.All(t => t.Original is not ("Human" or "Skipped" or "Functional" or "Grey" or "原生中文")),
            "explicit non-human retranslation includes old LLM and unknown text, not protected or native Chinese text");
        check(Select(false, true).Any(t => t.Original == "Grey") && pack.ToJSON() == original,
            "grey-list choice is explicit and preview never clears existing translations");
        var resource = pack.GetOrAddResource("res", "free", "Shared"); resource.Translated = "免费"; resource.Source = "ai:caiyun";
        var humanResource = pack.GetOrAddResource("res", "human", "Shared"); humanResource.Translated = "人工"; humanResource.Source = "user";
        var modelResource = pack.GetOrAddResource("res", "model", "Shared"); modelResource.Translated = "模型"; modelResource.Source = "ai:llm";
        var literal = pack.GetOrAdd("Shared", "Draw", false); literal.Role = "UI"; literal.Translated = "人工字面量"; literal.Source = "user";
        var attribute = pack.GetOrAddAttribute("Shared", "Attribute"); attribute.Translated = "模型属性"; attribute.Source = "ai:deepseek";
        var results = new Dictionary<string, string> { ["Shared"] = "新译文" };
        accept.Invoke(null, [pack, Select(true), results, "deepseek"]);
        check(resource.Translated == "新译文" && humanResource.Translated == "人工" && modelResource.Translated == "模型"
            && literal.Translated == "人工字面量" && attribute.Translated == "模型属性",
            "same-text resource, literal and attribute identities cannot bypass per-target source protection");
        accept.Invoke(null, [pack, Select(false), new Dictionary<string, string> { ["Shared"] = "Shared" }, "llm"]);
        check(resource.Translated == "新译文" && modelResource.Translated == "模型", "echoed translations preserve old text during retranslation");
        var placeholder = pack.GetOrAdd("Hello {0}", "Draw", false); placeholder.Role = "UI"; placeholder.Translated = "你好 {0}"; placeholder.Source = "ai:google";
        accept.Invoke(null, [pack, Select(true), new Dictionary<string, string> { ["Hello {0}"] = "坏译文" }, "llm"]);
        check(placeholder.Translated == "你好 {0}", "retranslation placeholder rejection cannot erase an existing translation");
        check(placeholder.Review is null, "invalid replacement cannot mark the valid old translation unusable");
        var root = Path.Combine(Path.GetTempPath(), "fg-rewrite-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var validate = type.GetMethod("ValidatePlan");
            check(validate is not null, "rewrite verifies the confirmed pack, plugin, original baseline and channel before a request");
            var plan = new UITextRewritePlan { PackHash = UITextRetranslation.Hash(pack.ToJSON()), DLLSignature = "dll",
                SourceSignature = "source", ChannelSignature = "channel" };
            validate!.Invoke(null, [plan, null, pack.ToJSON(), "dll", "source", "channel"]);
            foreach (var altered in new[] { new[] { original, "dll", "source", "channel" },
                new[] { pack.ToJSON(), "changed", "source", "channel" }, new[] { pack.ToJSON(), "dll", "changed", "channel" },
                new[] { pack.ToJSON(), "dll", "source", "changed" } })
            {
                var blocked = false;
                try { validate.Invoke(null, [plan, null, altered[0], altered[1], altered[2], altered[3]]); }
                catch (TargetInvocationException e) when (e.InnerException is IOException) { blocked = true; }
                check(blocked, "queued rewrite refuses changed confirmed state: " + Array.IndexOf(altered, "changed"));
            }
            var queue = new UITextJobQueue(Path.Combine(root, "queue.json"));
            check(queue.RequestRewrite("Target", "Target", UITextJobMode.RetranslateNonHuman, plan), "confirmed rewrite enters the serial queue");
            check(!queue.RequestRewrite("Target", "Target", UITextJobMode.UpgradeTranslations, plan), "different rewrite modes cannot duplicate a pending write for one plugin");
            check(!queue.Request("Target", "Target", UITextJobMode.Translate), "normal translation cannot duplicate an already queued rewrite");
            var reloaded = new UITextJobQueue(Path.Combine(root, "queue.json"));
            check(reloaded.Paused && reloaded.Items[0].Rewrite?.ChannelSignature == "channel"
                && reloaded.Items[0].Mode == UITextJobMode.RetranslateNonHuman, "restart retains exact rewrite mode and confirmation without starting paid work");
            var loadRequired = typeof(UITextStore).GetMethod("LoadRequired");
            check(loadRequired is not null, "retranslation has a strict load instead of replacing an unreadable old pack");
            var strictStore = new UITextStore(root);
            var rejectedPack = false;
            try { loadRequired!.Invoke(strictStore, ["missing"]); }
            catch (TargetInvocationException e) when (e.InnerException is IOException) { rejectedPack = true; }
            check(rejectedPack, "missing pack cannot be silently treated as empty for destructive retranslation");
            Directory.CreateDirectory(Path.Combine(root, "uitrans"));
            File.WriteAllText(Path.Combine(root, "uitrans", "broken.json"), "broken");
            rejectedPack = false;
            try { loadRequired!.Invoke(strictStore, ["broken"]); }
            catch (TargetInvocationException e) when (e.InnerException is IOException) { rejectedPack = true; }
            check(rejectedPack && File.ReadAllText(Path.Combine(root, "uitrans", "broken.json")) == "broken",
                "unreadable translation JSON is preserved and refuses destructive rewriting");
            var backup = type.GetMethod("SaveSnapshot")!;
            var restore = type.GetMethod("ReadSnapshot")!;
            var id = Guid.NewGuid().ToString("N");
            backup.Invoke(null, [root, "Target", id, pack.ToJSON(), "dll-state", Array.Empty<string>()]);
            var recovered = restore.Invoke(null, [root, "Target", id, pack.ToJSON(), "dll-state"]);
            check(recovered is not null, "retranslation snapshot is durable and validates pack plus plugin signatures");
            var refused = false;
            try { restore.Invoke(null, [root, "Target", id, original, "dll-state"]); } catch (TargetInvocationException e) when (e.InnerException is IOException) { refused = true; }
            check(refused, "snapshot cannot overwrite translations edited after retranslation");
            refused = false;
            try { restore.Invoke(null, [root, "Target", id, pack.ToJSON(), "new-plugin"]); } catch (TargetInvocationException e) when (e.InnerException is IOException) { refused = true; }
            check(refused, "snapshot refuses rollback after a plugin-file change");
            var before = pack.ToJSON();
            pack.Entries[0].Translated = "更新";
            backup.Invoke(null, [root, "Target", id, pack.ToJSON(), "dll-state", new[] { "delivered" }]);
            var checkpointJSON = pack.ToJSON();
            recovered = restore.Invoke(null, [root, "Target", id, pack.ToJSON(), "dll-state"]);
            check((string)recovered!.GetType().GetProperty("BeforeJSON")!.GetValue(recovered)! == before,
                "later checkpoints keep the original rollback snapshot instead of replacing it");
            var remaining = type.GetMethod("RemainingTargets");
            check(remaining is not null, "interrupted rewrite tracks delivered identities independently of source or model version");
            var chosen = Select(false);
            var identity = type.GetMethod("TargetID")!;
            var completed = (string)identity.Invoke(null, [chosen[0]])!;
            var resumed = (List<UITextTarget>)remaining!.Invoke(null, [pack, false, false,
                chosen.Select(t => (string)identity.Invoke(null, [t])!).ToArray(), new[] { completed }])!;
            check(resumed.Count == chosen.Count - 1 && resumed.All(t => (string)identity.Invoke(null, [t])! != completed),
                "resuming does not pay to translate already delivered identities again");
            chosen[1].Entry!.Source = "user";
            resumed = (List<UITextTarget>)remaining.Invoke(null, [pack, false, false,
                chosen.Select(t => (string)identity.Invoke(null, [t])!).ToArray(), Array.Empty<string>()])!;
            check(resumed.All(t => t.Original != chosen[1].Original), "new human provenance is protected even in a persisted plan");
            var snapshot = (UITextRewriteSnapshot)recovered!;
            var current = pack.ToJSON();
            snapshot.AfterHash = UITextRetranslation.Hash(current); snapshot.DLLSignature = "patched";
            validate.Invoke(null, [plan, snapshot, current, "patched", "source", "channel"]);
            check(true, "saved checkpoint state supersedes the original pack and DLL guard only after verification");
            var seal = type.GetMethod("SealSnapshot");
            check(seal is not null, "patch completion seals both installed files and the new trusted backup baseline");
            seal!.Invoke(null, [root, "Target", id, checkpointJSON, "patched", "verified-backup", new[] { "delivered" }]);
            var sealedSnapshot = restore.Invoke(null, [root, "Target", id, checkpointJSON, "patched"]);
            validate.Invoke(null, [plan, sealedSnapshot, checkpointJSON, "patched", "verified-backup", "channel"]);
            check(true, "resuming an interrupted apply uses the sealed original baseline rather than requesting delivered text again");
            var markRestored = type.GetMethod("MarkRestored");
            check(markRestored is not null, "successful rollback invalidates the old rewrite checkpoint");
            markRestored!.Invoke(null, [root, "Target", id]);
            refused = false;
            try { restore.Invoke(null, [root, "Target", id, checkpointJSON, "patched"]); }
            catch (TargetInvocationException e) when (e.InnerException is IOException) { refused = true; }
            check(refused, "a rolled-back job cannot later resume and silently reuse its old completed set");

            var flowPack = new UITextPack();
            foreach (var text in new[] { "First text", "Second text" })
            { var e = flowPack.GetOrAdd(text, "Draw", false); e.Role = "UI"; e.Source = "ai:llm"; e.Translated = "旧译文"; }
            var flowTargets = UITextRetranslation.SelectTargets(flowPack, false, false);
            using var cancel = new CancellationTokenSource();
            var channel = new Channel(items => { var r = new UITextTranslateResult(); r.Translated[items[0].Text] = "新译文"; cancel.Cancel(); return r; });
            var result = await UITextTranslationSession.TranslateAsync(channel, UITextFlow.BuildTranslateItems(flowTargets), null,
                batch => UITextRetranslation.Accept(flowPack, flowTargets, batch.Translated, channel.Name), cancel.Token, batchSize: 1);
            check(result.Error == "已取消" && channel.Calls == 1 && flowPack.Entries[0].Translated == "新译文" && flowPack.Entries[1].Translated == "旧译文",
                "cancellation after delivery saves valid replacement text without clearing the unprocessed old translation");
            var flowJSON = flowPack.ToJSON();
            channel = new(_ => new() { Error = "HTTP 401" });
            result = await UITextTranslationSession.TranslateAsync(channel, UITextFlow.BuildTranslateItems(flowTargets), null,
                batch => UITextRetranslation.Accept(flowPack, flowTargets, batch.Translated, channel.Name), CancellationToken.None);
            check(channel.Calls == 1 && flowPack.ToJSON() == flowJSON && result.Error == "HTTP 401",
                "authorization failure does not erase old text or trigger another paid session request");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Channel(Func<IReadOnlyList<UITextTranslateItem>, UITextTranslateResult> action) : IUITextChannel
    {
        public int Calls;
        public string Name => "llm";
        public string Description => Name;
        public Task<UITextTranslateResult> TranslateAsync(IReadOnlyList<UITextTranslateItem> items, Action<int, int>? progress, CancellationToken token)
        { Calls++; return Task.FromResult(action(items)); }
    }
}
