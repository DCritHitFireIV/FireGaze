using System.Collections;
using FireGaze.PortraitPreview;

internal static class OpeningAvailabilityCases
{
    internal static void Run(Action<bool, string> check)
    {
        var method = typeof(PortraitEditorOpeningController).GetMethod("ReadAvailability");
        check(method is not null, "opening rejection exposes a cached player-readable availability instead of one generic baseline error");
        object Availability(Fixture fixture) => method!.Invoke(fixture.Receipt, null)!;
        string[] Codes(object value) => ((IEnumerable)value.GetType().GetProperty("Issues")!.GetValue(value)!)
            .Cast<object>().Select(issue => (string)issue.GetType().GetProperty("Code")!.GetValue(issue)!).ToArray();
        string Text(object value) => (string)value.GetType().GetProperty("PrimaryMessage")!.GetValue(value)!;
        bool CanOpen(object value) => (bool)value.GetType().GetProperty("CanOpenEditor")!.GetValue(value)!;

        var transient = new Fixture();
        transient.Current = transient.Current with { Conditions = Fixture.Flags(1, 45, 26, 27),
            Source = transient.Current.Source! with { InCombat = true, IsCasting = true, CastActionID = 5, IsMoving = true } };
        check(!transient.Open() && transient.Calls == 0, "transition rejection never invokes native opening");
        var blocked = Availability(transient);
        check(!CanOpen(blocked) && Codes(blocked).SequenceEqual(new[] { "ChangingArea", "InCombat", "Teleporting", "Moving" }),
            "all simultaneous player blockers remain visible in a stable actionable priority");
        check(Text(blocked).Contains("切换区域", StringComparison.Ordinal) && !Text(blocked).Contains("baseline", StringComparison.OrdinalIgnoreCase),
            "transition rejection tells players to wait for the area rather than exposing a baseline");
        var reads = transient.Reads; _ = Availability(transient); _ = Availability(transient);
        check(transient.Reads == reads, "reading availability never samples native state");
        var failureMethod = typeof(PortraitEditorOpeningController).GetMethod("ReadLastFailure");
        check(failureMethod is not null && failureMethod.Invoke(transient.Receipt, null) is not null,
            "rejected opening keeps its own failure evidence");
        var failed = failureMethod!.Invoke(transient.Receipt, null);
        transient.Receipt.Invalidate("Territory change revoked source ownership.");
        transient.Current = Fixture.Closed();
        transient.Receipt.GetType().GetMethod("RefreshAvailability")!.Invoke(transient.Receipt, new object?[] { "plate" });
        check(CanOpen(Availability(transient)) && transient.Calls == 0,
            "standing after a transition restores the button without automatically opening an editor");
        var retained = (PortraitOpeningFailure)failureMethod.Invoke(transient.Receipt, null)!;
        var originalFailure = (PortraitOpeningFailure)failed!;
        check(retained.Serial == originalFailure.Serial && retained.TechnicalReason == originalFailure.TechnicalReason
            && retained.Availability.Issues.Select(issue => issue.Code).SequenceEqual(originalFailure.Availability.Issues.Select(issue => issue.Code))
            && retained.Observed!.Conditions.SequenceEqual(originalFailure.Observed!.Conditions),
            "later lifecycle invalidation and recovery do not erase the original rejected attempt");
        check(retained.Stage == "Preflight" && retained.CanRetry, "transient failure identifies its stage and allows manual retry");

        var guarded = new PortraitEditorOpeningController(() => Fixture.Closed(), () => 0,
            () => new[] { PortraitOpeningAvailability.Issue("Restoring") });
        var calls = 0;
        check(!guarded.Open("plate", Guid.NewGuid().ToString("D"), () => calls++) && calls == 0,
            "fresh execution obeys the same supplementary blockers as the menu");

        foreach (var (flag, code) in new[] { (5,"Crafting"), (6,"Gathering"), (4,"Mounted"),
            (58,"Cutscene"), (50,"RetainerInteraction"), (94,"FashionAccessory"), (111,"RestrictedState") })
        {
            var fixture = new Fixture(); fixture.Current = fixture.Current with { Conditions = Fixture.Flags(1, flag) };
            check(!fixture.Open() && Codes(Availability(fixture)).Contains(code),
                "known restricted condition has its own recovery guidance and unknown conditions remain honest: " + code);
        }
        foreach (var dutyFlags in new[] { Fixture.Flags(1, 34), Fixture.Flags(1, 56), Fixture.Flags(1, 34, 56) })
        {
            var fixture = new Fixture(); fixture.Current = fixture.Current with { Conditions = dutyFlags };
            check(!fixture.Open() && fixture.Calls == 0 && Codes(Availability(fixture)).SequenceEqual(new[] { "DutyBound" })
                && Text(Availability(fixture)) == "当前副本或任务场景暂不支持打开铭牌，请离开后再试。",
                "duty restrictions have one precise player reason, including both simultaneous CN flags");
        }
        var preview = new FireGaze.PortraitPreview.PortraitPreviewFeature();
        preview.Open("plate", Guid.NewGuid().ToString("D"));
        preview.Current = preview.Current with { ReadyIdentity = new(10,20,30,7,40), AgentActive = true };
        preview.Tick();
        var original = preview.Receipt;
        preview.Open("plate", Guid.NewGuid().ToString("D"));
        check(preview.Calls == 1 && preview.Receipt.Serial == original.Serial && preview.Receipt.Epoch == original.Epoch
            && preview.Receipt.Status == original.Status && preview.Receipt.ReadyIdentity == original.ReadyIdentity && preview.Errors == 0,
            "opening the already open same editor is an informative no-op and preserves its receipt");
        preview.Open("gearset", Guid.NewGuid().ToString("D"));
        check(preview.Calls == 1 && preview.Errors == 1 && preview.Receipt.Serial == original.Serial
            && preview.OpeningAvailability.Issues.Any(issue => issue.Code == "OtherEditorOpen"),
            "a different editor entry is blocked with specific guidance without replacing the existing receipt");
        preview.Open("gearset", Guid.NewGuid().ToString("D"));
        check(preview.Calls == 1 && preview.Errors == 1, "repeated blocked clicks do not spam player chat");
        preview.Release();
    }
}
