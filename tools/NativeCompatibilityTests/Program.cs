using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using Action = System.Action;

unsafe static class Program
{
    static GameDataFixture data = null!;
    static ushort emoteTimeline;
    const ushort EmoteId = 21;
    const ulong Flags = 0xFEDCBA987654321FUL;

    static int Main(string[] args)
    {
        if (args.Contains("--timeline-abi-metadata")) return TimelineAbiMetadata.Write();
        var sqpack = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? @"C:\Program Files\上海数龙科技有限公司\最终幻想XIV\game\sqpack";
        using var game = new Lumina.GameData(sqpack, new Lumina.LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified });
        data = new GameDataFixture(game);
        emoteTimeline = (ushort)game.GetExcelSheet<Emote>()!.GetRow(EmoteId).ActionTimeline.First(t => t.RowId != 0).RowId;
        var cases = new List<(string Name, Action Run)> {
            ("timeline native delegate arguments match SDK and preserve audited U1 returns", TimelineAbiMetadata.VerifyProduction),
            ("current clone bit2 clears before Original; other bits and Original writes survive", CurrentClone),
            ("current clone bit2 clear is idempotent", AlreadyVisible),
            ("source DisableDraw cleans and restores visibility before Original", SourceDisable),
            ("source Update reasserts hidden visibility after native refresh", SourceUpdateReasserts),
        };
        cases.AddRange(TimelineHookInstallationTests.Cases());
        foreach (var scenario in new[] { "source", "foreign", "null", "protection disabled", "runtime disposed", "display disposed", "no display", "source generation reused", "clone generation reused", "clone slot reused", "clone index changed", "local player changed", "manager changed" })
        {
            var captured = scenario;
            cases.Add(($"Update leaves {captured} unchanged and calls Original once", () => UpdateBypass(captured)));
        }
        foreach (var scenario in new[] { "clone", "foreign", "null", "own call", "runtime disposed", "no preview", "binding changed" })
        {
            var captured = scenario;
            cases.Add(($"DisableDraw for {captured} preserves preview and calls Original once", () => DisableBypass(captured)));
        }
        foreach (var accessory in new[] { false, true })
        {
            var slot = accessory ? "ornament" : "companion";
            cases.Add(($"source {slot} accepted setup ends preview after one Original", () => SetupSource(accessory, true)));
            cases.Add(($"source {slot} rejected setup keeps preview after one Original", () => SetupSource(accessory, false)));
            cases.Add(($"source {slot} zero teardown is forwarded once and accepted", () => SetupSource(accessory, true, 0)));
            cases.Add(($"source {slot} signed native id is forwarded without truncating arguments", () => SetupSource(accessory, true, -1)));
            foreach (var scenario in new[] { "clone", "foreign", "null", "own call", "runtime disposed", "no preview", "binding changed" })
            {
                var captured = scenario;
                cases.Add(($"{slot} {captured} preserves preview and forwards Original once", () => SetupBypass(accessory, captured)));
            }
        }
        cases.Add(("companion child BaseId accepts native setup while cached CompanionId remains zero", CompanionChildAccepted));
        cases.Add(("companion positive canonical id accepts pending native setup without a child", CompanionCanonicalPending));
        cases.Add(("companion same-id direct setup accepts matching actual child", CompanionSameChild));
        cases.Add(("companion teardown accepts after native removal of child and cached id", CompanionChildRemoved));
        cases.Add(("companion teardown rejects unchanged child despite cached zero", CompanionChildTeardownRejected));
        cases.Add(("companion cached requested id cannot accept an unrelated old child", CompanionUnrelatedChild));
        cases.Add(("companion unchanged cached requested id without child cannot prove acceptance", CompanionCanonicalNoOp));
        foreach (var scenario in new[] { "owner changed", "local binding changed", "native local changed", "source generation reused", "territory changed", "revision changed" })
        {
            var captured = scenario;
            cases.Add(($"companion native reentry {captured} cannot accept old binding or overwrite new state", () => CompanionReentry(captured)));
        }
        foreach (var actionTimeline in new[] { false, true })
        {
            var kind = actionTimeline ? "PlayActionTimeline" : "PlayTimeline";
            foreach (var succeeds in new[] { false, true })
            {
                var captured = succeeds;
                cases.Add(($"native U1 caller of source {kind} receives {succeeds} and commits only success", () => NativeTimelineResult(actionTimeline, captured, "source")));
                foreach (var scenario in new[] { "foreign", "null", "own call", "runtime disposed", "no preview", "binding changed", "unrelated timeline", "worker" })
                {
                    var capturedScenario = scenario;
                    cases.Add(($"native U1 caller of {kind} {scenario} preserves {succeeds} without handoff", () => NativeTimelineResult(actionTimeline, captured, capturedScenario)));
                }
            }
            cases.Add(($"nested {kind} false inside outer false never accepts preview handoff", () => NestedTimelineResult(actionTimeline, false)));
            cases.Add(($"nested {kind} true remains execution evidence when outer returns false", () => NestedTimelineResult(actionTimeline, true)));
            cases.Add(($"source {kind} matching Emote row ends preview after one Original", () => TimelineSource(actionTimeline)));
            if (actionTimeline) cases.Add(("source PlayActionTimeline recognizes loop-only match", () => TimelineSource(true, loopOnly: true)));
            foreach (var scenario in new[] { "clone", "foreign", "null", "own call", "runtime disposed", "no preview", "binding changed", "zero timeline", "unrelated timeline" })
            {
                var captured = scenario;
                cases.Add(($"{kind} {captured} preserves preview and forwards Original once", () => TimelineBypass(actionTimeline, captured)));
            }
            cases.Add(($"HMS direct {kind} with cleared EmoteId still hands off", () => TimelineSource(actionTimeline, sourceEmote: 0)));
            cases.Add(($"HMS direct {kind} with stale EmoteId resolves the actual unique timeline", () => TimelineSource(actionTimeline, sourceEmote: 71)));
            cases.Add(($"HMS direct {kind} with missing metadata row resolves the actual unique timeline", () => TimelineSource(actionTimeline, sourceEmote: ushort.MaxValue)));
            cases.Add(($"HMS direct {kind} ambiguous timeline cancels without invented emote identity", () => AmbiguousTimeline(actionTimeline, 0)));
            cases.Add(($"HMS direct {kind} ambiguous timeline ignores potentially stale matching metadata", () => AmbiguousTimeline(actionTimeline, 288)));
            foreach (ushort clip in new ushort[] { 1, 3, 34, 13, 22, 30, 31, 33 })
            {
                var captured = clip;
                cases.Add(($"HMS source {kind} ordinary idle/motion timeline {clip} preserves preview", () => OrdinaryTimeline(actionTimeline, captured)));
            }
        }
        foreach (var hook in new[] { "Update", "Disable", "Companion", "Ornament", "Timeline", "ActionTimeline" })
        {
            var captured = hook;
            cases.Add(($"worker {hook} forwards once without changing preview or flags", () => Worker(captured)));
        }
        var passed = 0;
        foreach (var (name, run) in cases)
        {
            try { run(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex)
            {
                while (ex.InnerException is not null) ex = ex.InnerException;
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
            }
        }
        Console.WriteLine($"{passed}/{cases.Count} compiled production native compatibility cases passed");
        return passed == cases.Count ? 0 : 1;
    }

    static void Eq<T>(T actual, T expected, string why)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"{why}: expected {expected}; got {actual}");
    }
    static void Once(NativeFixture f, nint pointer)
    { Eq(f.OriginalCalls, 1, "Original count"); Eq(f.LastPointer, pointer, "Original pointer"); }
    static void Kept(NativeFixture f, bool active = true)
    {
        Eq(f.Runtime.HasActivePreview, active, "preview state"); Eq(f.Deletes, 0, "display deletion count");
        Eq(f.Accepted, 0, "native accepted events"); Eq(f.Display.IsDisposed, false, "display ownership survives");
    }
    static void Ended(NativeFixture f, int accepted = 1)
    {
        Eq(f.Runtime.HasActivePreview, false, "preview removed"); Eq(f.Deletes, 1, "only owned display deleted");
        Eq(f.Display.IsDisposed, true, "display ownership disposed"); Eq(f.Accepted, accepted, "native accepted events");
        Eq(f.Source->DrawObject->IsVisible, true, "source visibility restored");
        Eq((nint)f.Manager->BattleCharas[207].BattleChara, 0, "owned manager slot released");
    }
    static void CurrentClone()
    {
        using var f = new NativeFixture(data); f.Runtime.SetHmsVisibilityProtection(true);
        f.Clone->RenderFlags = (VisibilityFlags)Flags;
        f.DuringOriginal = () => f.Clone->RenderFlags = (VisibilityFlags)((ulong)f.Clone->RenderFlags ^ 0x20UL);
        f.Update(f.Clone);
        Once(f, (nint)f.Clone); Eq(f.FlagsAtOriginal, Flags & ~2UL, "only bit2 cleared before native consumer");
        Eq((ulong)f.Clone->RenderFlags, (Flags & ~2UL) ^ 0x20UL, "native Original side effect preserved");
        Eq(f.Runtime.HmsVisibilityRepairs, 1L, "one visibility repair"); Kept(f);
    }
    static void AlreadyVisible()
    {
        using var f = new NativeFixture(data); f.Runtime.SetHmsVisibilityProtection(true);
        f.Clone->RenderFlags = (VisibilityFlags)(Flags & ~2UL); f.Update(f.Clone);
        Once(f, (nint)f.Clone); Eq((ulong)f.Clone->RenderFlags, Flags & ~2UL, "all other bits preserved");
        Eq(f.Runtime.HmsVisibilityRepairs, 0L, "no spurious repair");
    }
    static void UpdateBypass(string scenario)
    {
        using var f = new NativeFixture(data); f.Runtime.SetHmsVisibilityProtection(true);
        var target = f.Clone;
        switch (scenario)
        {
            case "source": target = f.Source; break;
            case "foreign": target = f.Foreign; break;
            case "null": target = null; break;
            case "protection disabled": f.Runtime.SetHmsVisibilityProtection(false); break;
            case "runtime disposed": NativeFixture.Set(f.Runtime, "disposed", true); break;
            case "display disposed": NativeFixture.Set(f.Display, "disposed", true); break;
            case "no display": NativeFixture.Set(f.Runtime, "visualActor", null); break;
            case "source generation reused": NativeFixture.Generation(f.Source, 999); break;
            case "clone generation reused": NativeFixture.Generation(f.Clone, 999); break;
            case "clone slot reused": f.Manager->BattleCharas[207].BattleChara = (BattleChara*)f.Foreign; break;
            case "clone index changed": f.Clone->ObjectIndex = 208; break;
            case "local player changed": f.ChangeLocal(f.Foreign); break;
            case "manager changed": ClientObjectManager.Addresses.Instance.Value = (nint)f.Foreign; break;
        }
        if (target != null) target->RenderFlags = (VisibilityFlags)Flags;
        f.Update(target); Once(f, (nint)target);
        Eq(f.FlagsAtOriginal, target == null ? 0UL : Flags, "Original receives unmodified flags");
        if (target != null) Eq((ulong)target->RenderFlags, Flags, "unowned flags unmodified");
        Eq(f.Runtime.HmsVisibilityRepairs, 0L, "no visibility repair"); Eq(f.Deletes, 0, "no deletion during read-only update");
    }
    static void SourceDisable()
    {
        using var f = new NativeFixture(data); f.PrepareVisibility();
        var cleanedAtOriginal = false;
        f.DuringOriginal = () => cleanedAtOriginal = !f.Runtime.HasActivePreview && f.Display.IsDisposed
            && f.Source->DrawObject->IsVisible && f.Deletes == 1 && f.Manager->BattleCharas[207].BattleChara == null;
        f.Disable(f.Source); Once(f, (nint)f.Source); Eq(cleanedAtOriginal, true, "cleanup completed before Original"); Ended(f, 0);
    }
    static void SourceUpdateReasserts()
    {
        using var f = new NativeFixture(data); f.PrepareVisibility();
        f.DuringOriginal = () => f.Source->DrawObject->Flags |= 1;
        f.Update(f.Source); Once(f, (nint)f.Source);
        Eq((ulong)f.Source->DrawObject->Flags & 1UL, 0UL, "source update leaked the real body");
        Kept(f);
    }
    static void DisableBypass(string scenario)
    {
        using var f = new NativeFixture(data); var target = f.Source;
        switch (scenario)
        {
            case "clone": target = f.Clone; break;
            case "foreign": target = f.Foreign; break;
            case "null": target = null; break;
            default: ConfigureBypass(f, scenario); break;
        }
        f.Disable(target); Once(f, (nint)target); Kept(f, scenario != "no preview");
    }
    static void ConfigureBypass(NativeFixture f, string scenario)
    {
        switch (scenario)
        {
            case "own call": NativeFixture.Set(f.Runtime, "ownCall", true); break;
            case "runtime disposed": NativeFixture.Set(f.Runtime, "disposed", true); break;
            case "no preview": f.Effects.Clear(); break;
            case "binding changed": NativeFixture.Set(f.Runtime, "actorId", 999UL); break;
        }
    }
    static void SetupSource(bool accessory, bool accepts, short id = 327)
    {
        using var f = new NativeFixture(data); f.PrepareVisibility(); f.SetupAccepts = accepts;
        var activeAtOriginal = false; f.DuringOriginal = () => activeAtOriginal = f.Runtime.HasActivePreview;
        if (accessory) f.Ornament(&f.Source->OrnamentData, id); else f.Companion(&f.Source->CompanionData, id);
        Once(f, accessory ? (nint)(&f.Source->OrnamentData) : (nint)(&f.Source->CompanionData));
        Eq(f.LastId, id, "native signed id preserved"); Eq(f.LastExtra, 0x12345678u, "native param preserved");
        Eq(activeAtOriginal, true, "Original runs before accepted fallback cleanup");
        if (accepts) Ended(f); else Kept(f);
        Eq(accessory ? f.Source->OrnamentData.OrnamentId : f.Source->CompanionData.CompanionId,
            accepts ? unchecked((ushort)id) : (ushort)0, "new native state preserved");
    }
    static void SetupBypass(bool accessory, string scenario)
    {
        using var f = new NativeFixture(data); var actor = scenario == "clone" ? f.Clone : scenario == "foreign" ? f.Foreign : f.Source;
        ConfigureBypass(f, scenario); nint container;
        if (accessory)
        { var pointer = scenario == "null" ? null : &actor->OrnamentData; container = (nint)pointer; f.Ornament(pointer, 327); }
        else
        { var pointer = scenario == "null" ? null : &actor->CompanionData; container = (nint)pointer; f.Companion(pointer, 327); }
        Once(f, container); Kept(f, scenario != "no preview"); Eq(f.LastId, (short)327, "id preserved"); Eq(f.LastExtra, 0x12345678u, "param preserved");
    }
    static void CompanionChildAccepted()
    {
        using var f = new NativeFixture(data); f.PrepareVisibility();
        var child = f.NewCompanion(255);
        f.SetupWritesCompanionId = false; f.SetupWritesCompanionObject = true; f.SetupCompanionObject = child;
        f.Companion(&f.Source->CompanionData,255); Once(f,(nint)(&f.Source->CompanionData)); Ended(f);
        Eq(f.Source->CompanionData.CompanionId,(ushort)0,"cached zero remains authoritative native result");
        Eq((nint)f.Source->CompanionData.CompanionObject,(nint)child,"accepted child is preserved");
        Eq(child->BaseId,255u,"native child identity survives cleanup");
    }
    static void CompanionCanonicalPending()
    {
        using var f = new NativeFixture(data); f.PrepareVisibility();
        f.Companion(&f.Source->CompanionData,255); Once(f,(nint)(&f.Source->CompanionData)); Ended(f);
        Eq(f.Source->CompanionData.CompanionId,(ushort)255,"positive canonical request preserved");
        Eq((nint)f.Source->CompanionData.CompanionObject,(nint)0,"native pending object remains absent");
    }
    static void CompanionSameChild()
    {
        using var f = new NativeFixture(data); f.PrepareVisibility();
        var child=f.NewCompanion(255); f.Source->CompanionData.CompanionObject=child;
        f.SetupWritesCompanionId=false;
        f.Companion(&f.Source->CompanionData,255); Once(f,(nint)(&f.Source->CompanionData)); Ended(f);
        Eq((nint)f.Source->CompanionData.CompanionObject,(nint)child,"same-id setup preserves existing actual child");
    }
    static void CompanionChildRemoved()
    {
        using var f = new NativeFixture(data); f.PrepareVisibility();
        f.Source->CompanionData.CompanionObject=f.NewCompanion(255); f.Source->CompanionData.CompanionId=255;
        f.SetupWritesCompanionObject=true; f.SetupCompanionObject=null;
        f.Companion(&f.Source->CompanionData,0); Once(f,(nint)(&f.Source->CompanionData)); Ended(f);
        Eq((nint)f.Source->CompanionData.CompanionObject,(nint)0,"accepted native removal is not restored");
        Eq(f.Source->CompanionData.CompanionId,(ushort)0,"native removal cached id remains zero");
    }
    static void CompanionChildTeardownRejected()
    {
        using var f = new NativeFixture(data); f.PrepareVisibility();
        var child=f.NewCompanion(255); f.Source->CompanionData.CompanionObject=child; f.SetupAccepts=false;
        f.Companion(&f.Source->CompanionData,0); Once(f,(nint)(&f.Source->CompanionData)); Kept(f);
        Eq((nint)f.Source->CompanionData.CompanionObject,(nint)child,"rejected removal preserves actual child");
    }
    static void CompanionUnrelatedChild()
    {
        using var f = new NativeFixture(data); f.PrepareVisibility();
        var child=f.NewCompanion(77); f.Source->CompanionData.CompanionObject=child; f.Source->CompanionData.CompanionId=255; f.SetupAccepts=false;
        f.Companion(&f.Source->CompanionData,255); Once(f,(nint)(&f.Source->CompanionData)); Kept(f);
        Eq(child->BaseId,77u,"unrelated actual child unchanged");
    }
    static void CompanionCanonicalNoOp()
    {
        using var f = new NativeFixture(data); f.PrepareVisibility();
        f.Source->CompanionData.CompanionId=255; f.SetupAccepts=false;
        f.Companion(&f.Source->CompanionData,255); Once(f,(nint)(&f.Source->CompanionData)); Kept(f);
        Eq(f.Source->CompanionData.CompanionId,(ushort)255,"unchanged cached id preserved");
    }
    static void CompanionReentry(string scenario)
    {
        using var f = new NativeFixture(data); f.PrepareVisibility();
        var child=f.NewCompanion(255); f.SetupWritesCompanionObject=true; f.SetupCompanionObject=child;
        f.DuringOriginal=()=> {
            switch(scenario)
            {
                case "owner changed": f.Source->CompanionData.OwnerObject=f.Foreign; break;
                case "local binding changed": NativeFixture.Set(f.Runtime,"actorId",999UL); break;
                case "native local changed": f.ChangeLocal(f.Foreign); break;
                case "source generation reused": NativeFixture.Generation(f.Source,999); break;
                case "territory changed": f.Territory=2; break;
                case "revision changed": NativeFixture.Set(f.Runtime,"revision",99L); break;
            }
        };
        f.Companion(&f.Source->CompanionData,255); Once(f,(nint)(&f.Source->CompanionData)); Kept(f);
        Eq((nint)f.Source->CompanionData.CompanionObject,(nint)child,"new native child not restored or removed");
        Eq(child->BaseId,255u,"new native child identity remains");
    }
    static void TimelineSource(bool actionTimeline, bool loopOnly = false, ushort sourceEmote = EmoteId)
    {
        using var f = new NativeFixture(data); f.PrepareVisibility(); f.Source->EmoteController.EmoteId = sourceEmote;
        if (actionTimeline) f.ActionTimeline(&f.Source->Timeline, loopOnly ? (ushort)65535 : emoteTimeline, loopOnly ? emoteTimeline : (ushort)65535);
        else f.Timeline(&f.Source->Timeline.TimelineSequencer, emoteTimeline);
        Once(f, actionTimeline ? (nint)(&f.Source->Timeline) : (nint)(&f.Source->Timeline.TimelineSequencer));
        Eq(f.LastIntro, loopOnly ? (ushort)65535 : emoteTimeline, "intro argument preserved");
        if (actionTimeline) Eq(f.LastLoop, loopOnly ? emoteTimeline : (ushort)65535, "loop argument preserved");
        Eq(f.LastParam, (nint)0x12345678, "native parameter preserved"); Ended(f);
        Eq(f.ExternalEmotes, 1, "one external emote event"); Eq(f.ExternalEmote, EmoteId, "external emote identity");
        Eq(f.Source->Timeline.TimelineSequencer.TimelineIds[0], loopOnly ? (ushort)65535 : emoteTimeline, "native timeline result survives cleanup");
    }
    static void NativeTimelineResult(bool actionTimeline, bool succeeds, string scenario)
    {
        using var f = new NativeFixture(data); f.PrepareVisibility();
        f.Source->EmoteController.EmoteId = EmoteId;
        f.TimelineNativeResult = succeeds ? (byte)1 : (byte)0;
        if (scenario == "worker") f.FrameworkThread = false;
        else ConfigureBypass(f, scenario);
        var actor = scenario == "foreign" ? f.Foreign : scenario == "null" ? null : f.Source;
        ushort intro = scenario == "unrelated timeline" ? ushort.MaxValue : emoteTimeline;
        byte result = f.NativeTimeline(actionTimeline, actor, intro, 65535);
        Eq(result, succeeds ? (byte)1 : (byte)0, "native caller sees the original U1 result");
        Once(f, actor == null ? 0 : actionTimeline ? (nint)(&actor->Timeline) : (nint)(&actor->Timeline.TimelineSequencer));
        Eq(f.LastIntro, intro, "native intro forwarded");
        Eq(f.LastParam, (nint)0x12345678, "native param forwarded");
        if (actionTimeline) Eq(f.LastLoop, (ushort)65535, "native loop forwarded");
        if (scenario == "source" && succeeds)
        {
            Ended(f); Eq(f.ExternalEmotes, 1, "accepted timeline records one emote");
            Eq(f.ExternalEmote, EmoteId, "accepted timeline records its actual identity");
        }
        else
        {
            Kept(f, scenario != "no preview");
            Eq(f.ExternalEmotes, 0, "rejected or unrelated timeline cannot record an emote");
        }
        if (actor != null) Eq(actor->Timeline.TimelineSequencer.TimelineIds[0], intro, "native timeline side effect preserved even on false");
    }
    static void NestedTimelineResult(bool actionTimeline, bool innerSucceeds)
    {
        using var f = new NativeFixture(data); f.PrepareVisibility();
        f.Source->EmoteController.EmoteId = EmoteId;
        byte inner = 255;
        f.DuringOriginal = () => {
            f.DuringOriginal = null;
            f.TimelineNativeResult = innerSucceeds ? (byte)1 : (byte)0;
            inner = f.NativeTimeline(!actionTimeline, f.Source, emoteTimeline);
            f.TimelineNativeResult = 0;
        };
        byte outer = f.NativeTimeline(actionTimeline, f.Source, emoteTimeline);
        Eq(outer, (byte)0, "outer native false is never replaced by nested success");
        Eq(inner, innerSucceeds ? (byte)1 : (byte)0, "inner native U1 result survives its own detour");
        Eq(f.OriginalCalls, 2, "each native original executes once");
        Eq(f.ExternalEmotes, innerSucceeds ? 1 : 0, "only an actually successful native timeline records an emote");
        if (innerSucceeds) Ended(f); else Kept(f);
    }
    static void TimelineBypass(bool actionTimeline, string scenario)
    {
        using var f = new NativeFixture(data); f.Source->EmoteController.EmoteId = EmoteId;
        var actor = scenario == "clone" ? f.Clone : scenario == "foreign" ? f.Foreign : f.Source;
        ConfigureBypass(f, scenario);
        var timeline = scenario == "zero timeline" ? (ushort)0 : scenario == "unrelated timeline" ? ushort.MaxValue : emoteTimeline;
        nint pointer;
        if (actionTimeline)
        { var target = scenario == "null" ? null : &actor->Timeline; pointer = (nint)target; f.ActionTimeline(target, timeline, 0); }
        else
        { var target = scenario == "null" ? null : &actor->Timeline.TimelineSequencer; pointer = (nint)target; f.Timeline(target, timeline); }
        Once(f, pointer); Kept(f, scenario != "no preview"); Eq(f.LastIntro, timeline, "timeline id preserved");
        Eq(f.LastParam, (nint)0x12345678, "parameter preserved"); Eq(f.ExternalEmotes, 0, "no external emote attribution");
    }
    static void OrdinaryTimeline(bool actionTimeline, ushort clip)
    {
        using var f = new NativeFixture(data); f.Source->EmoteController.EmoteId = 0;
        if (actionTimeline) f.ActionTimeline(&f.Source->Timeline, clip, 0);
        else f.Timeline(&f.Source->Timeline.TimelineSequencer, clip);
        Once(f, actionTimeline ? (nint)(&f.Source->Timeline) : (nint)(&f.Source->Timeline.TimelineSequencer));
        Kept(f); Eq(f.ExternalEmotes, 0, "idle/motion is not an emote event"); Eq(f.LastIntro, clip, "native clip preserved");
    }
    static void AmbiguousTimeline(bool actionTimeline, ushort sourceEmote)
    {
        const ushort clip = 8152;
        var sheet = data.GetExcelSheet<Emote>();
        foreach (var row in new uint[] { 288, 289, 290 })
            Eq(sheet.GetRow(row).ActionTimeline.Any(t => t.RowId == clip), true, "sampled CN timeline remains shared");
        using var f = new NativeFixture(data); f.PrepareVisibility(); f.Source->EmoteController.EmoteId = sourceEmote;
        if (actionTimeline) f.ActionTimeline(&f.Source->Timeline, clip, 0);
        else f.Timeline(&f.Source->Timeline.TimelineSequencer, clip);
        Once(f, actionTimeline ? (nint)(&f.Source->Timeline) : (nint)(&f.Source->Timeline.TimelineSequencer));
        Ended(f); Eq(f.LastIntro, clip, "shared native timeline forwarded"); Eq(f.ExternalEmotes, 0, "no invented unique identity for shared timeline");
    }
    static void Worker(string hook)
    {
        using var f = new NativeFixture(data); f.PrepareVisibility(); f.FrameworkThread = false;
        f.Runtime.SetHmsVisibilityProtection(true); f.Clone->RenderFlags = (VisibilityFlags)Flags;
        f.Source->EmoteController.EmoteId = EmoteId;
        var sourceReads = f.ObjectTableReads;
        switch (hook)
        {
            case "Update": f.Update(f.Clone); break;
            case "Disable": f.Disable(f.Source); break;
            case "Companion": f.Companion(&f.Source->CompanionData, 327); break;
            case "Ornament": f.Ornament(&f.Source->OrnamentData, 327); break;
            case "Timeline": f.Timeline(&f.Source->Timeline.TimelineSequencer, emoteTimeline); break;
            case "ActionTimeline": f.ActionTimeline(&f.Source->Timeline, emoteTimeline, 0); break;
        }
        Eq(f.OriginalCalls, 1, "Original count"); Kept(f); Eq(f.Source->DrawObject->IsVisible, false, "worker leaves visibility lease unchanged");
        Eq((ulong)f.Clone->RenderFlags, Flags, "worker leaves flags unchanged"); Eq(f.ExternalEmotes, 0, "worker does not raise emote event");
        Eq(f.ObjectTableReads, sourceReads, "worker does not inspect object table");
    }
}
