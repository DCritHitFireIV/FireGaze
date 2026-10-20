using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

// The actual compiled runtime detours execute through unmanaged U1 callbacks.
// Only the native patch backend and game Original are replaced; no game attach,
// Hook factory/backend, native code patching, or installed data write occurs.
internal static unsafe class Program
{
    private static NativeFixture current = null!;
    private static nint row, parameter;
    private static int assertions;
    public static int Main(string[] args)
    {
        var option = Array.IndexOf(args, "--sqpack");
        var sqpack = option >= 0 && option + 1 < args.Length ? args[option + 1] : args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sqpack)) { Console.Error.WriteLine("Required: --sqpack <current client sqpack directory>"); return 1; }
        using var game = new Lumina.GameData(sqpack,
            new Lumina.LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified });
        var data = new GameDataFixture(game);
        var cases = new List<(string Name, System.Action Run)>();
        foreach (var action in new[] { false, true })
        {
            var route = action;
            foreach (var eligible in new[] { false, true })
            {
            var useSource = eligible;
            cases.Add(($"resolved {(action ? "action" : "single")} {(eligible ? "eligible" : "bypass")} Original exception is never retried", () => {
                using var f = new NativeFixture(data); current = f;
                var field = NativeFixture.RuntimeType.GetField(route ? "resolvedActionTimelinePlay" : "resolvedTimelinePlay", NativeFixture.All)!;
                var type = field.FieldType.GetGenericArguments()[0];
                var method = typeof(Program).GetMethod(route ? nameof(ThrowAction) : nameof(ThrowSingle), NativeFixture.All)!;
                Install(f, field, method.CreateDelegate(type));
                try
                {
                    NativeFixture.RuntimeType.GetMethod(route ? "ResolvedActionTimelinePlayDetour" : "ResolvedTimelinePlayDetour", NativeFixture.All)!.Invoke(f.Runtime,
                        route ? [Pointer.Box(useSource ? &f.Source->Timeline.TimelineSequencer : null, typeof(ActionTimelineSequencer*)), (ushort)5798, (ushort)0, Pointer.Box(null, typeof(void*))]
                            : [Pointer.Box(useSource ? &f.Source->Timeline.TimelineSequencer : null, typeof(ActionTimelineSequencer*)), (ushort)5798, Pointer.Box(null, typeof(void*)), Pointer.Box(null, typeof(void*))]);
                }
                catch (TargetInvocationException) { }
                Check(f.OriginalCalls == 1, "managed boundary failure cannot retry native Original");
                Check(f.Accepted == 0 && f.ExternalEmotes == 0, "exception without accepted execution cannot fabricate last action");
            }));
            }
        }
        foreach (var action in new[] { false, true })
        {
            var actionRoute = action;
            foreach (var succeeds in new[] { false, true })
            {
                var nativeAccepted = succeeds;
                cases.Add(($"resolved {(action ? "action" : "single")} unmanaged AL {succeeds} and four arguments", () => {
                    using var f = new NativeFixture(data); f.TimelineNativeResult = nativeAccepted ? (byte)1 : (byte)0;
                    var result = Invoke(f, actionRoute, &f.Source->Timeline.TimelineSequencer, 5798, 0, (void*)0x10112233, (void*)0x50667788);
                    Check(result == (nativeAccepted ? 1 : 0) && f.OriginalCalls == 1, "original AL and one invocation");
                    Check(f.LastPointer == (nint)(&f.Source->Timeline.TimelineSequencer) && f.LastIntro == 5798 && parameter == 0x50667788,
                        "exact sequence/id/parameter forwarding");
                    if (!actionRoute) Check(row == 0x10112233, "resolved row remains a borrowed third parameter");
                    Check(f.Accepted == (nativeAccepted ? 1 : 0) && f.Runtime.HasActivePreview == !nativeAccepted, "only accepted execution owns handoff");
                    Check(f.ExternalEmotes == (nativeAccepted ? 1 : 0) && (!nativeAccepted || f.ExternalEmote == 143), "rejected call cannot become last accepted action");
                }));
            }
            foreach (var bypass in new[] { "foreign", "clone", "null", "worker", "own call", "disposed", "no preview", "binding changed", "native local changed", "idle", "override sentinel", "unrelated", "unadmitted" })
            {
                var scenario = bypass;
                cases.Add(($"resolved {(action ? "action" : "single")} {bypass} forwards once and cannot accept", () => {
                    using var f = new NativeFixture(data);
                    Character* actor = f.Source; ushort id = 5798;
                    switch (scenario)
                    {
                        case "foreign": actor = f.Foreign; break;
                        case "clone": actor = f.Clone; break;
                        case "null": actor = null; break;
                        case "worker": f.FrameworkThread = false; break;
                        case "own call": NativeFixture.Set(f.Runtime, "ownCall", true); break;
                        case "disposed": NativeFixture.Set(f.Runtime, "disposed", true); break;
                        case "no preview": f.Effects.Clear(); break;
                        case "binding changed": f.Territory = 2; break;
                        case "native local changed": f.ChangeLocal(f.Foreign); break;
                        case "idle": id = 34; break;
                        case "override sentinel": id = 65001; break;
                        case "unrelated": id = ushort.MaxValue; break;
                    }
                    var result = Invoke(f, actionRoute, actor == null ? null : &actor->Timeline.TimelineSequencer, id, admitted: scenario != "unadmitted");
                    Check(result == 1 && f.OriginalCalls == 1, "bypass forwards original AL once");
                    Check(f.Accepted == 0 && f.ExternalEmotes == 0, "bypass cannot mutate accepted history");
                }));
            }
            foreach (var reentry in new[] { "generation", "revision", "territory", "native local", "bound source" })
            {
                var scenario = reentry;
                cases.Add(($"resolved {(action ? "action" : "single")} native reentry changes {reentry} without stale acceptance", () => {
                    using var f = new NativeFixture(data);
                    f.DuringOriginal = () => {
                        switch (scenario)
                        {
                            case "generation": NativeFixture.Generation(f.Source, 999); break;
                            case "revision": NativeFixture.Set(f.Runtime, "revision", 9L); break;
                            case "territory": f.Territory = 9; break;
                            case "native local": f.ChangeLocal(f.Foreign); break;
                            case "bound source": NativeFixture.Set(f.Runtime, "actorId", 999UL); break;
                        }
                    };
                    var result = Invoke(f, actionRoute, &f.Source->Timeline.TimelineSequencer, 5798);
                    Check(result == 1 && f.OriginalCalls == 1, "reentry does not retry or change original result");
                    Check(f.Accepted == 0 && f.ExternalEmotes == 0, "stale binding/generation never becomes accepted last action");
                }));
            }
            cases.Add(($"resolved {(action ? "action" : "single")} ambiguous known animation ends preview without fake identity", () => {
                using var f = new NativeFixture(data);
                NativeFixture.Set(f.Runtime, "emoteTimelineIdentities", new Dictionary<ushort, ushort> { [5798] = 0 });
                var result = Invoke(f, actionRoute, &f.Source->Timeline.TimelineSequencer, 5798);
                Check(result == 1 && f.OriginalCalls == 1 && f.Accepted == 1, "known ambiguous animation still counts as native input");
                Check(f.ExternalEmotes == 0, "ambiguous animation cannot invent history identity");
            }));
            cases.Add(($"resolved {(action ? "action" : "single")} null borrowed payload is forwarded unchanged", () => {
                using var f = new NativeFixture(data);
                var result = Invoke(f, actionRoute, &f.Source->Timeline.TimelineSequencer, 5798, borrowedRow: null, borrowedParameter: null);
                Check(result == 1 && f.OriginalCalls == 1 && parameter == 0 && (actionRoute || row == 0), "borrowed payload is neither dereferenced nor substituted");
            }));
            cases.Add(($"resolved {(action ? "action" : "single")} rejected input cannot restore scalars into recycled source generation", () => {
                using var f = new NativeFixture(data);
                NativeFixture.Set(f.Runtime, "visualActor", null);
                f.Source->EmoteController.EmoteId = 71;
                f.TimelineNativeResult = 0;
                f.DuringOriginal = () => { NativeFixture.Generation(f.Source, 999); f.Source->EmoteController.EmoteId = 42; };
                var result = Invoke(f, actionRoute, &f.Source->Timeline.TimelineSequencer, 5798);
                Check(result == 0 && f.OriginalCalls == 1 && f.Accepted == 0, "generation replacement preserves native rejection");
                Check(f.Source->EmoteController.EmoteId == 42, "old presentation snapshot must never write into recycled actor memory");
            }));
        }
        cases.Add(("resolved action loop-only adjusted emote is accepted", () => {
            using var f = new NativeFixture(data);
            var result = Invoke(f, true, &f.Source->Timeline.TimelineSequencer, 0, 5798);
            Check(result == 1 && f.Accepted == 1 && f.ExternalEmote == 143, "adjusted loop is recognized with intro absent");
        }));
        foreach (var (timeline, expectedEmote) in new (ushort, ushort)[] { (708,18), (683,18), (737,40), (590,40), (4798,143), (4799,143), (5795,143), (5796,143), (5797,143), (5798,143) })
        {
            var id = timeline; var expected = expectedEmote;
            cases.Add(($"resolved actual CN timeline {id} keeps identity {expected}", () => {
                using var f = new NativeFixture(data);
                var result = Invoke(f, false, &f.Source->Timeline.TimelineSequencer, id);
                Check(result == 1 && f.Accepted == 1 && f.ExternalEmotes == 1 && f.ExternalEmote == expected,
                    "the actual Emote + ActionTimelineReplace rows must resolve the native adjusted ID without stale EmoteId");
            }));
        }
        cases.Add(("resolved action conflicting intro and loop cannot invent unique last action", () => {
            using var f = new NativeFixture(data);
            NativeFixture.Set(f.Runtime, "emoteTimelineIdentities", new Dictionary<ushort, ushort> { [5798] = 143, [642] = 50 });
            var result = Invoke(f, true, &f.Source->Timeline.TimelineSequencer, 5798, 642);
            Check(result == 1 && f.Accepted == 1 && f.ExternalEmotes == 0, "conflicting known IDs cancel without guessing");
        }));
        foreach (var innerAccepted in new[] { false, true })
        {
            var innerResult = innerAccepted;
            cases.Add(($"resolved inner AL {innerAccepted} survives rejected outer AL", () => {
                using var f = new NativeFixture(data);
                f.TimelineNativeResult = 0;
                f.DuringOriginal = () => {
                    f.DuringOriginal = null; f.TimelineNativeResult = innerResult ? (byte)1 : (byte)0;
                    _ = Invoke(f, false, &f.Source->Timeline.TimelineSequencer, 5798);
                    f.TimelineNativeResult = 0;
                };
                var result = Invoke(f, true, &f.Source->Timeline.TimelineSequencer, 5798);
                Check(result == 0 && f.OriginalCalls == 2, "both native calls run once and outer false is preserved");
                Check(f.Accepted == (innerResult ? 1 : 0) && f.ExternalEmotes == (innerResult ? 1 : 0), "only nested real success commits input/history");
            }));
        }
        cases.AddRange(ShutdownPassThroughTests.Cases(data));
        var passed = 0;
        foreach (var (name, run) in cases)
        {
            try { run(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        Console.WriteLine($"NativeResolvedTimelineTests: {passed}/{cases.Count} cases; {assertions} assertions.");
        return passed == cases.Count ? 0 : 1;
    }

    private static byte Invoke(NativeFixture fixture, bool action, ActionTimelineSequencer* sequence, ushort intro, ushort loop = 0,
        void* borrowedRow = null, void* borrowedParameter = null, bool admitted = true)
    {
        current = fixture;
        var name = action ? "resolvedActionTimelinePlay" : "resolvedTimelinePlay";
        var field = NativeFixture.RuntimeType.GetField(name, NativeFixture.All);
        Check(field is not null, "missing reviewed internal timeline observer; SDK-mismatched wrappers stay unavailable");
        var type = field!.FieldType.GetGenericArguments()[0];
        nint original = action
            ? (nint)(delegate* unmanaged<ActionTimelineSequencer*, ushort, ushort, void*, byte>)&ActionOriginal
            : (nint)(delegate* unmanaged<ActionTimelineSequencer*, ushort, void*, void*, byte>)&SingleOriginal;
        Install(fixture, field, Marshal.GetDelegateForFunctionPointer(original, type));
        NativeFixture.Set(fixture.Runtime, action ? "resolvedActionTimelineAdmitted" : "resolvedTimelineAdmitted", admitted);
        var detour = NativeFixture.RuntimeType.GetMethod(action ? "ResolvedActionTimelinePlayDetour" : "ResolvedTimelinePlayDetour", NativeFixture.All)!.CreateDelegate(type, fixture.Runtime);
        var pointer = Marshal.GetFunctionPointerForDelegate(detour);
        var result = action
            ? ((delegate* unmanaged<ActionTimelineSequencer*, ushort, ushort, void*, byte>)pointer)(sequence, intro, loop, borrowedParameter)
            : ((delegate* unmanaged<ActionTimelineSequencer*, ushort, void*, void*, byte>)pointer)(sequence, intro, borrowedRow, borrowedParameter);
        GC.KeepAlive(detour);
        return result;
    }
    private static void Install(NativeFixture fixture, FieldInfo field, Delegate original)
    {
        var hookType = typeof(Dalamud.Hooking.Hook<>).Assembly.GetType("Dalamud.Hooking.Internal.FunctionPointerVariableHook`1")!.MakeGenericType(original.GetType());
        var hook = RuntimeHelpers.GetUninitializedObject(hookType);
        NativeFixture.Set(hook, "originalDelegate", original);
        field.SetValue(fixture.Runtime, hook);
        NativeFixture.Set(fixture.Runtime, field.Name == "resolvedActionTimelinePlay" ? "resolvedActionTimelineAdmitted" : "resolvedTimelineAdmitted", true);
    }
    private static bool ThrowSingle(ActionTimelineSequencer* sequence, ushort id, void* row, void* parameter)
    { current.OriginalCalls++; throw new InvalidOperationException("native fixture failure"); }
    private static bool ThrowAction(ActionTimelineSequencer* sequence, ushort intro, ushort loop, void* parameter)
    { current.OriginalCalls++; throw new InvalidOperationException("native fixture failure"); }
    [UnmanagedCallersOnly] private static byte SingleOriginal(ActionTimelineSequencer* sequence, ushort id, void* borrowedRow, void* borrowedParameter)
    {
        row = (nint)borrowedRow;
        return Record(sequence, id, 0, borrowedParameter);
    }
    [UnmanagedCallersOnly] private static byte ActionOriginal(ActionTimelineSequencer* sequence, ushort intro, ushort loop, void* borrowedParameter)
        => Record(sequence, intro, loop, borrowedParameter);
    private static byte Record(ActionTimelineSequencer* sequence, ushort intro, ushort loop, void* borrowedParameter)
    {
        current.OriginalCalls++; current.LastPointer = (nint)sequence; current.LastIntro = intro; current.LastLoop = loop; parameter = (nint)borrowedParameter;
        if (sequence != null) sequence->TimelineIds[0] = intro;
        current.DuringOriginal?.Invoke();
        return current.TimelineNativeResult;
    }
    private static void Check(bool accepted, string reason) { assertions++; if (!accepted) throw new InvalidOperationException(reason); }
}
