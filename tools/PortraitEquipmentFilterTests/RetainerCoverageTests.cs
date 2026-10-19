using FireGaze.PortraitPreview;

internal static class RetainerCoverageTests
{
    internal static (int Passed, int Failed) Run()
    {
        var passed = 0; var failed = 0;
        var now = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
        var alice = new OwnedCharacterKey(1, 101); var bob = new OwnedCharacterKey(1, 202);
        OwnedSourceView Source(ulong id, OwnedReadStatus status = OwnedReadStatus.Current, int count = 3)
            => new(new(OwnedSourceKind.RetainerInventory, id), status, status == OwnedReadStatus.Unread ? null : now, count, "");
        OwnedRetainerRoster Roster(OwnedCharacterKey owner, params ulong[] ids) => new(owner, ids, OwnedReadStatus.Current);
        PortraitEquipmentSourceChoice Choice(OwnedCharacterKey owner, OwnedRetainerRoster? roster, params OwnedSourceView[] sources)
            => PortraitEquipmentFilterSources.Build(sources, owner, roster).Single(choice => choice.Kind == OwnedSourceKind.RetainerInventory);
        void Check(bool condition, string name) { if (condition) passed++; else { failed++; Console.WriteLine("FAIL retainer-coverage " + name); } }
        void Case(string name, Action test) { try { test(); } catch (Exception ex) { failed++; Console.WriteLine($"FAIL retainer-coverage {name}: {ex.Message}"); } }
        Case("two hired retainers", () =>
        {
            var choice = Choice(alice, Roster(alice, 11, 22), Source(11), Source(22, OwnedReadStatus.Current, 0), Source(99));
            Check(choice.ReadSourceCount == 2 && choice.TotalSourceCount == 2 && choice.CurrentSourceCount == 2, "read empty counts as 2/2 and dismissed cached retainer cannot inflate count");
            Check(choice.StatusText == "已读取 2/2" && choice.CanSelect && !choice.HasUnread, "filter presents read fraction instead of item count");
            var summary = PortraitEquipmentSourceSummary.Build(new(alice, [], [Source(11), Source(22), Source(99)]), Roster(alice, 11, 22));
            Check(summary.Count(line => line.StartsWith("雇员背包")) == 1 && summary.Single().Contains("已读取 2/2"), "source summary presents exactly one retainer row");
        });
        Case("ten hired retainers", () =>
        {
            var roster = Roster(alice, 11, 22, 33, 44, 55, 66, 77, 88, 99, 110);
            var choice = Choice(alice, roster, Source(11), Source(22, OwnedReadStatus.PossiblyStale), Source(33));
            Check(choice.ReadSourceCount == 3 && choice.TotalSourceCount == 10 && choice.CurrentSourceCount == 2 && choice.CachedSourceCount == 1, "3/10 uses actual roster not three cached rows");
            Check(choice.StatusText.Contains("已读取 3/10") && choice.HasUnread && choice.HasCached, "partial and saved data remain distinguishable");
            Check(PortraitEquipmentSourceSummary.Build(new(alice, [], [Source(11), Source(22, OwnedReadStatus.PossiblyStale), Source(33)]), roster)
                .Single().Contains("已保存 1"), "summary identifies cached member of mixed current and stale reads");
        });
        Case("unknown roster and owner changes", () =>
        {
            var unknown = Choice(alice, null, Source(11), Source(22));
            Check(unknown.TotalSourceCount is null && unknown.StatusText.Contains("未读取雇员名单") && !unknown.StatusText.Contains('/'), "cache rows never become a fabricated denominator");
            Check(unknown.CanSelect && unknown.HasCached, "unknown roster retains selectable historical inventory records");
            var otherOwner = Choice(bob, Roster(alice, 11, 22), Source(11));
            Check(otherOwner.TotalSourceCount is null && !otherOwner.StatusText.Contains('/'), "another character roster cannot authorize a denominator");
            Check(Choice(new(2, 101), Roster(alice, 11, 22), Source(11)).TotalSourceCount is null, "changed account epoch cannot reuse old roster");
            Check(PortraitEquipmentSourceSummary.Build(new(bob, [], []), Roster(alice, 11, 22)).Single().Contains("未读取雇员名单"), "new current character gets unknown roster summary");
        });
        Case("stale roster and no hired retainers", () =>
        {
            var stale = Roster(alice, 11, 22) with { Status = OwnedReadStatus.PossiblyStale };
            var choice = Choice(alice, stale, Source(11));
            Check(choice.TotalSourceCount == 2 && choice.ReadSourceCount == 1 && choice.RosterMayBeStale && choice.StatusText.Contains("名单待刷新"), "retained roster fraction is visibly qualified when unavailable");
            var empty = Choice(alice, Roster(alice), Source(99));
            Check(empty.TotalSourceCount == 0 && empty.ReadSourceCount == 0 && !empty.CanSelect && empty.StatusText == "未雇佣雇员", "known empty roster is not unknown or a fake 0/10");
        });
        Case("invalid and retired observations", () =>
        {
            var retired = new OwnedSourceView(new(OwnedSourceKind.RetainerEquipped, 22), OwnedReadStatus.Current, now, 100, "");
            var unknownKind = new OwnedSourceView(new((OwnedSourceKind)99, 22), OwnedReadStatus.Current, now, 100, "");
            var choice = Choice(alice, Roster(alice, 11, 22), Source(11), Source(11), retired, unknownKind);
            Check(choice.ReadSourceCount == 1 && choice.OwnedItemCount == 3 && choice.TotalSourceCount == 2, "duplicate source and retired enum cannot inflate coverage or item count");
            Check(PortraitEquipmentSourceSummary.Build(new(alice, [], [Source(11), retired, unknownKind]), Roster(alice, 11, 22)).Count == 1, "unsupported sources produce no summary rows");
            Check(Choice(alice, Roster(alice, 11, 11), Source(11)).TotalSourceCount is null, "duplicate roster IDs cannot produce a trustworthy denominator");
            Check(Choice(alice, Roster(alice, 0, 11), Source(11)).TotalSourceCount is null, "zero roster ID cannot become a hired retainer");
        });
        Case("other sources retain their own status", () =>
        {
            var dresser = new OwnedSourceView(new(OwnedSourceKind.GlamourDresser), OwnedReadStatus.PossiblyStale, now, 23, "容器未打开");
            var summary = PortraitEquipmentSourceSummary.Build(new(alice, [], [Source(11), dresser, Source(22)]), Roster(alice, 11, 22));
            Check(summary.Count == 2 && summary[0].StartsWith("投影台：记录可能过期（23）") && summary[0].EndsWith("容器未打开"), "retainer aggregation preserves nonretainer source count freshness and reason");
            Check(summary[1] == "雇员背包：已读取 2/2", "summary uses one compact row without individual retainer names or IDs");
            Check(PortraitEquipmentSourceSummary.Build(new(null, [], [Source(11)]), Roster(alice, 11, 22)).Count == 0, "logged out view publishes no retainer status");
        });
        return (passed, failed);
    }
}
