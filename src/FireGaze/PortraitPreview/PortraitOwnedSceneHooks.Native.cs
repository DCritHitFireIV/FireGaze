using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal sealed class DalamudPortraitOwnedSceneHookFactory(IGameInteropProvider interop) : IPortraitOwnedSceneHookFactory
{
    public IPortraitOwnedSceneNativeHook<T> Create<T>(PortraitOwnedSceneNativeTarget target, T detour) where T : Delegate
        => new NativeHook<T>(interop.HookFromAddress(target.Address, detour));
    private sealed class NativeHook<T>(Hook<T> hook) : IPortraitOwnedSceneNativeHook<T> where T : Delegate
    {
        public T Original => hook.Original;
        public bool IsEnabled => hook.IsEnabled;
        public void Enable() => hook.Enable();
        public void Disable() => hook.Disable();
        public void Dispose() => hook.Dispose();
    }
}

internal sealed unsafe partial class PortraitOwnedSceneHooks
{
    internal const string FrozenEXESHA256 = "958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9";
    internal const string FrozenSDKSHA256 = "5A7A8EA71E84D7410DBAD8A5391D4759C2A38B1A55F4F3BB745B22B556A6F778";
    private static readonly (uint Table, uint Dtor, uint Initialize, uint Release, uint Update)[] NativeFamilies = [
        (0x219F838, 0x9D9180, 0x9CD2D0, 0x9CD3E0, 0x9DB610),
        (0x219F8A0, 0x9D9230, 0x9CD2D0, 0x9CD3E0, 0x9CFB10),
        (0x219FAB8, 0x9D9130, 0x9CD2D0, 0x9D3B90, 0x9D3D30),
        (0x21C3800, 0xD15A10, 0x9CD2D0, 0x9CD3E0, 0xC78140),
        (0x21C4388, 0xD159D0, 0xCABB40, 0x9CD3E0, 0xCABB70),
        (0x21C46A8, 0xD15990, 0xCBE5E0, 0x9CD3E0, 0xCBE640),
        (0x21CA220, 0xDE4710, 0xDBB9C0, 0x9CD3E0, 0xDBBC60),
        (0x21F73A0, 0xFA3530, 0x9CD2D0, 0x9CD3E0, 0xF469C0),
        (0x21F8440, 0xFA3570, 0x9CD2D0, 0x9CD3E0, 0x9DB610),
        (0x21FA290, 0x10998E0, 0xFFA470, 0x9CD3E0, 0xFFA780),
        (0x21FB010, 0x1099920, 0x9CD2D0, 0x9CD3E0, 0x9DB610),
        (0x21FDE80, 0x1104600, 0x9CD2D0, 0x10D6C10, 0x10D6C30),
    ];

    internal static IReadOnlyList<PortraitOwnedSceneNativeTarget> FrozenTargets(nint moduleBase)
    {
        var result = new List<PortraitOwnedSceneNativeTarget>();
        Add(PortraitOwnedSceneNativeKind.Dtor, NativeFamilies.Select(family => family.Dtor));
        Add(PortraitOwnedSceneNativeKind.Release, NativeFamilies.Select(family => family.Release));
        Add(PortraitOwnedSceneNativeKind.Initialize, NativeFamilies.Select(family => family.Initialize));
        Add(PortraitOwnedSceneNativeKind.Update, NativeFamilies.Select(family => family.Update));
        Add(PortraitOwnedSceneNativeKind.Setup, [0x9D3960]);
        Add(PortraitOwnedSceneNativeKind.Render, [0x9CD4D0]);
        Add(PortraitOwnedSceneNativeKind.CreateClientObject, [0x1756410]);
        Add(PortraitOwnedSceneNativeKind.DeleteClientObject, [0x1756840]);
        return result.AsReadOnly();
        void Add(PortraitOwnedSceneNativeKind kind, IEnumerable<uint> rvas)
        { foreach (var rva in rvas.Distinct()) result.Add(new(kind, moduleBase + (nint)rva)); }
    }

    private static bool ValidateTargetInventory(IReadOnlyList<PortraitOwnedSceneNativeTarget> targets)
    {
        if (targets.Count == 0 || targets.Any(t => t.Address == 0) || targets.Select(t => t.Address).Distinct().Count() != targets.Count)
            return false;
        var baseAddress = targets.FirstOrDefault(target => target.Kind == PortraitOwnedSceneNativeKind.Render).Address - 0x9CD4D0;
        return baseAddress > 0 && targets.SequenceEqual(FrozenTargets(baseAddress));
    }

    internal static bool NativeAbiMatchesSDK(out string reason)
    {
        foreach (var name in new[] { "Dtor", "Initialize", "Release", "Update" })
        {
            var field = typeof(CharaView.CharaViewVirtualTable).GetField(name);
            var sdkDelegate = typeof(CharaView.Delegates).GetNestedType(name)?.GetMethod("Invoke");
            if (field is null || sdkDelegate is null || !field.FieldType.IsFunctionPointer
                || field.FieldType.GetFunctionPointerReturnType() != sdkDelegate.ReturnType
                || !field.FieldType.GetFunctionPointerParameterTypes().SequenceEqual(sdkDelegate.GetParameters().Select(p => p.ParameterType)))
            { reason = "SDK CharaView virtual ABI mismatch: " + name; return false; }
        }
        // Direct entry signatures are SDK generated delegates, including byte
        // deleting-destructor flags and the Setup 5th/6th stack parameters.
        if (!Signature<CharaView.Delegates.Render>(typeof(void), typeof(CharaView*), typeof(uint))
            || !Signature<CharaViewPortrait.Delegates.Setup>(typeof(void), typeof(CharaViewPortrait*), typeof(uint),
                typeof(CharaViewCharacterData*), typeof(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInterface*), typeof(int), typeof(ulong))
            || !Signature<ClientObjectManager.Delegates.CreateBattleCharacter>(typeof(uint), typeof(ClientObjectManager*), typeof(uint), typeof(byte))
            || !Signature<ClientObjectManager.Delegates.DeleteObjectByIndex>(typeof(void), typeof(ClientObjectManager*), typeof(ushort), typeof(byte)))
        { reason = "SDK CharaView/direct ClientObject ABI mismatch."; return false; }
        reason = ""; return true;
    }

    private static bool Signature<T>(Type result, params Type[] parameters) where T : Delegate
    {
        var invoke = typeof(T).GetMethod("Invoke")!;
        return invoke.ReturnType == result && invoke.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters);
    }

    /// <summary>
    /// Opt-in current-process validation only. This is never called during plugin
    /// construction. Passing hashes/ABI/table checks is necessary but deliberately
    /// insufficient: no production environment can currently issue an admission.
    /// </summary>
    internal static bool TryValidateCurrentProcess(out string reason)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var module = process.MainModule;
            if (module is null || !string.Equals(Path.GetFileName(module.FileName), "ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase)
                || !HashMatches(module.FileName, FrozenEXESHA256)
                || !HashMatches(typeof(CharaView).Assembly.Location, FrozenSDKSHA256))
            { reason = "Current EXE/SDK differs from the frozen native contract."; return false; }
            if (!NativeAbiMatchesSDK(out reason)) return false;
            var moduleBase = module.BaseAddress;
            if (CharaView.Addresses.Render.Value != moduleBase + 0x9CD4D0
                || CharaViewPortrait.Addresses.Setup.Value != moduleBase + 0x9D3960
                || ClientObjectManager.Addresses.CreateBattleCharacter.Value != moduleBase + 0x1756410
                || ClientObjectManager.Addresses.DeleteObjectByIndex.Value != moduleBase + 0x1756840)
            { reason = "Current SDK signature resolution differs from the audited native entry points."; return false; }
            foreach (var family in NativeFamilies)
            {
                var table = (nint*)(moduleBase + (nint)family.Table);
                if (table[0] != moduleBase + (nint)family.Dtor || table[1] != moduleBase + (nint)family.Initialize
                    || table[2] != moduleBase + (nint)family.Release || table[10] != moduleBase + (nint)family.Update)
                { reason = "A native CharaView vtable differs from the frozen allowlist."; return false; }
            }
            reason = string.Join("; ", MissingAdmissionEvidence); return false;
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    internal static IReadOnlyList<string> MissingAdmissionEvidence { get; } = Array.AsReadOnly(new[] {
        "Live official-consumer baseline and slot-idle ownership inventory are not connected",
        "Context configure/cleanup and renderer reset entry points lack a validated typed interception contract",
        "Off-thread takeover and lost-slot retirement have no verified non-destructive retirement path",
        "Native teardown/device-reset phase qualification and owned copy-drain integration are not connected",
    });
    private static bool HashMatches(string path, string expected)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)) == expected; }
}
