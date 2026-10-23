using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;

internal static unsafe class BindingCases
{
    private const string Exe = "958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9";
    private const string SDK = "5A7A8EA71E84D7410DBAD8A5391D4759C2A38B1A55F4F3BB745B22B556A6F778";
    public static int Run(Action<bool, string> check, Action<string, Action> test)
    {
        var module = unchecked((nint)0x140000000L);
        PortraitNativePhaseEntryEvidence[] Evidence(nint baseAddress) => [
            new(PortraitNativeObservedPhase.ManagerRender, baseAddress + 0x2BA030, 0x2BA030, 1, Convert.FromHexString("405357415441554883EC5865488B0425580000004C8BE98B1573689602488B1D"), typeof(RenderManager.Delegates.Render), baseAddress + 0x2BA030),
            new(PortraitNativeObservedPhase.ManagerUpdateCandidate, baseAddress + 0x2B9A40, 0x2B9A40, 1, Convert.FromHexString("40564883EC70488BF1440F295424208B894C830300440F28D183F9FF744C8BC1"), typeof(PortraitNativeManagerUpdateCandidate), 0),
            new(PortraitNativeObservedPhase.SceneCameraUpdate, baseAddress + 0x421D60, 0x421D60, 1, Convert.FromHexString("4C8BDC49895B104989731849897B2055498DAB28FFFFFF4881ECD0010000488B"), typeof(PortraitNativeSceneCameraUpdate), 0),
            new(PortraitNativeObservedPhase.SourceCharaViewRender, baseAddress + 0x9CD4D0, 0x9CD4D0, 1, Convert.FromHexString("48897C242041564883EC2083790800448BF2488BF90F840A0300008B510C85D2"), typeof(CharaView.Delegates.Render), baseAddress + 0x9CD4D0)
        ];
        bool Bind(PortraitNativePhaseEntryEvidence[] evidence, out IReadOnlyList<PortraitNativePhaseBoundTarget> targets)
            => PortraitNativePhaseObservationBinding.TryBind(module, Exe, SDK, evidence, out targets, out _);
        test("exact detached target inventory and SDK ABI", () => {
            check(PortraitNativePhaseObservationBinding.SDKAbiMatches(), "render and source wrappers use the actual SDK delegate signatures");
            var entries = Evidence(module);
            check(Bind(entries, out var targets) && targets.Count == 4 && targets[0].Address == module + 0x2BA030
                && targets[1].DelegateType == typeof(PortraitNativeManagerUpdateCandidate), "only the four audited typed targets bind as inert metadata");
            entries[0].Prefix[0] = 0;
            check(targets[0].Address == module + 0x2BA030, "bound metadata does not retain caller-owned mutable evidence arrays");
            var relocated = unchecked((nint)0x180000000L);
            check(PortraitNativePhaseObservationBinding.TryBind(relocated, Exe, SDK, Evidence(relocated), out _, out _), "ASLR relocation preserves audited RVAs without resolving or calling native functions");
            check(PortraitNativePhaseObservationBinding.TryBind(module, Exe,
                "5726E301EE5456D526F524E9F88659F38A8717CB527FE4A86D83D1ADE0AB9798", Evidence(module), out var current, out _)
                && current.Count == 4, "audited current SDK can bind the same detached four-target contract");
        });
        test("old update address cannot masquerade as render", () => {
            var wrong = Evidence(module); wrong[0] = wrong[0] with { Address = module + 0x2B9A40, FunctionRootRVA = 0x2B9A40 };
            check(!Bind(wrong, out var targets) && targets.Count == 0, "2B9A40 is rejected as a void-this Render target");
            wrong = Evidence(module); wrong[1] = wrong[1] with { DelegateType = typeof(RenderManager.Delegates.Render) };
            check(!Bind(wrong, out _), "the float-bearing update candidate rejects a void-this delegate");
        });
        test("identity and boundary evidence fail closed", () => {
            check(!PortraitNativePhaseObservationBinding.TryBind(module, "wrong", SDK, Evidence(module), out _, out _)
                && !PortraitNativePhaseObservationBinding.TryBind(module, Exe, "wrong", Evidence(module), out _, out _), "module and SDK hashes must both match the audited build");
            check(!PortraitNativePhaseObservationBinding.TryBind(0, Exe, SDK, Evidence(module), out _, out _), "missing module base cannot bind addresses");
            foreach (var index in Enumerable.Range(0, 4)) {
                var wrong = Evidence(module); wrong[index] = wrong[index] with { PrefixMatches = 2 };
                check(!Bind(wrong, out _), "nonunique target prefix refuses binding at index " + index);
                wrong = Evidence(module); wrong[index] = wrong[index] with { FunctionRootRVA = wrong[index].FunctionRootRVA + 1 };
                check(!Bind(wrong, out _), "non-entry body address refuses binding at index " + index);
                wrong = Evidence(module); wrong[index].Prefix[^1] ^= 1;
                check(!Bind(wrong, out _), "changed native prefix refuses binding at index " + index);
            }
            var sdkMismatch = Evidence(module); sdkMismatch[3] = sdkMismatch[3] with { SDKResolvedAddress = module + 0x9CD4D1 };
            check(!Bind(sdkMismatch, out _), "SDK source-render resolution must match the audited entry");
        });
        test("complete inventory cannot omit or duplicate phases", () => {
            var valid = Evidence(module);
            check(!Bind(valid[..3], out _), "a missing phase cannot produce a partial bound target inventory");
            check(!Bind([valid[0], valid[0], valid[2], valid[3]], out _), "duplicate entries cannot stand in for a missing phase");
            check(!Bind([.. valid, valid[0]], out _), "extra targets outside the explicit inventory are refused");
        });
        return 0;
    }
}
