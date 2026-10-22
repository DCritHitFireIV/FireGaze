using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FireGaze.PortraitPreview;
using System.Reflection;

internal static unsafe class OwnedPassiveConsumerEventsCases
{
    private static int checks;
    private static long module = 0x7A00000000;
    private static int setupCalls, createCalls;
    private static nint setupView, setupData, setupAgent, createManager;
    private static uint setupSlot, createIndex;
    private static int setupEventId;
    private static ulong setupEventKind;
    private static byte createParam;
    private static Exception? throwOnSetup;
    private static void Check(bool value, string reason)
    {
        checks++;
        if (!value) throw new InvalidOperationException(reason);
    }

    internal static int Run()
    {
        try
        {
            TransparentAndExactTargetEvents();
            EveryTargetFamilyIsOpaqueAndBounded();
            CapacityAndCloseFreeze();
            NonPassiveDoesNotCollect();
            Console.WriteLine($"PASS {checks} bounded passive consumer event checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void TransparentAndExactTargetEvents()
    {
        var environment = NewEnvironment();
        var provider = new OwnedPhysicalCases.Provider();
        setupCalls = createCalls = 0;
        throwOnSetup = null;
        using var hooks = new PortraitOwnedSceneHooks(environment, nativeThread: () => new(321, 654));
        Check(hooks.TryInstallPassive(new DalamudPortraitOwnedSceneHookFactory(new OriginalProvider(provider), new OwnedPhysicalCases.Proof()), TimeSpan.FromSeconds(1)),
            hooks.LastError ?? "consumer event installation");
        var target = environment.Targets.Single(value => value.Kind == PortraitOwnedSceneNativeKind.Setup);
        var native = provider.Created.Single(value => value.Address == target.Address);
        var setup = As<CharaViewPortrait.Delegates.Setup>(native.Detour);
        var receiver = (CharaViewPortrait*)(nint)(-1);
        var data = (CharaViewCharacterData*)(nint)(-2);
        var agent = (AgentInterface*)(nint)(-3);
        setup(receiver, 7, data, agent, -12, 0x1122334455667788UL);
        var snapshot = hooks.PassiveNativeEvents!;
        Check(snapshot is { Capacity: 256, Dropped: 0, ProductionAdmission: false, Events.Count: 1 },
            "passive native event collector starts bounded and detached");
        var item = snapshot.Events[0];
        Check(item.TargetKind == PortraitOwnedSceneNativeKind.Setup && item.TargetAddress == (long)target.Address
            && item.Receiver == (long)receiver && item.ArgumentCount == 5
            && item.Argument0 == 7 && item.Argument1 == (long)data && item.Argument2 == (long)agent
            && item.Argument3 == -12 && item.Argument4 == unchecked((long)0x1122334455667788UL)
            && item.Result == 0 && item.OriginalReturned
            && item.EntryThread == new PortraitNativeThreadStamp(321, 654)
            && item.ReturnThread == item.EntryThread,
            "setup records exact target, opaque receiver, scalar ABI arguments and native return");
        Check(setupCalls == 1 && setupView == (nint)receiver && setupData == (nint)data && setupAgent == (nint)agent
            && setupSlot == 7 && setupEventId == -12 && setupEventKind == 0x1122334455667788UL,
            $"native setup Original receives exact arguments once without receiver dereference: calls={setupCalls}, view={setupView:X}, data={setupData:X}, agent={setupAgent:X}, slot={setupSlot}, event={setupEventId}, kind={setupEventKind:X}");

        var createTarget = environment.Targets.Single(value => value.Kind == PortraitOwnedSceneNativeKind.CreateClientObject);
        var create = As<ClientObjectManager.Delegates.CreateBattleCharacter>(provider.Created.Single(value => value.Address == createTarget.Address).Detour);
        var manager = (ClientObjectManager*)(nint)(-7);
        var result = create(manager, uint.MaxValue, 0xED);
        var createEvent = hooks.PassiveNativeEvents!.Events[1];
        Check(result == 0xFEDCBA98 && createCalls == 1 && createManager == (nint)manager
            && createIndex == uint.MaxValue && createParam == 0xED
            && createEvent is { TargetKind: PortraitOwnedSceneNativeKind.CreateClientObject, ArgumentCount: 2,
                Argument0: 4294967295, Argument1: 237, Result: 4275878552, OriginalReturned: true },
            "client-object consumer Original result, uint index, byte flag and opaque manager are preserved");

        var expected = new InvalidOperationException("consumer Original failed");
        throwOnSetup = expected;
        Exception? observed = null;
        try { setup(receiver, 0, data, agent, 1, 2); }
        catch (Exception error) { observed = error; }
        throwOnSetup = null;
        Check(ReferenceEquals(observed, expected) && setupCalls == 2
            && hooks.PassiveNativeEvents!.Events[2] is { TargetKind: PortraitOwnedSceneNativeKind.Setup,
                OriginalReturned: false, Result: 0, Receiver: -1 },
            "throwing consumer Original propagates the same exception and records no invented result");
    }

    private static void EveryTargetFamilyIsOpaqueAndBounded()
    {
        var environment = NewEnvironment();
        var provider = new OwnedPhysicalCases.Provider();
        using var hooks = new PortraitOwnedSceneHooks(environment, nativeThread: () => new(11, Environment.CurrentManagedThreadId));
        Check(hooks.TryInstallPassive(new DalamudPortraitOwnedSceneHookFactory(provider, new OwnedPhysicalCases.Proof()), TimeSpan.FromSeconds(1)),
            hooks.LastError ?? "all target event installation");
        foreach (var target in environment.Targets)
        {
            var native = provider.Created.Single(value => value.Address == target.Address);
            InvokeTarget(target.Kind, native.Detour, (nint)(0x1000 + checks));
        }
        var events = hooks.PassiveNativeEvents!;
        Check(events.Events.Count == environment.Targets.Count && events.Dropped == 0,
            "each one of the 39 target families publishes one bounded scalar event");
        Check(events.Events.All(value => value.Receiver >= 0x1000 && value.Receiver < 0x10000
            && value.TargetAddress != 0 && value.EntryThread.NativeID == 11
            && value.EntryThread.ManagedID > 0 && value.EntryThread == value.ReturnThread),
            "consumer receiver remains an opaque numeric value and thread identity is captured at both edges");
        Check(environment.Targets.All(target => events.Events.Any(value => value.TargetKind == target.Kind
            && value.TargetAddress == (long)target.Address)),
            "event target family and exact address are preserved for every frozen target");
    }

    private static void CapacityAndCloseFreeze()
    {
        var environment = NewEnvironment();
        var provider = new OwnedPhysicalCases.Provider();
        using var hooks = new PortraitOwnedSceneHooks(environment);
        Check(hooks.TryInstallPassive(new DalamudPortraitOwnedSceneHookFactory(provider, new OwnedPhysicalCases.Proof()), TimeSpan.FromSeconds(1)),
            hooks.LastError ?? "capacity event installation");
        var target = environment.Targets.Single(value => value.Kind == PortraitOwnedSceneNativeKind.Render);
        var render = As<CharaView.Delegates.Render>(provider.Created.Single(value => value.Address == target.Address).Detour);
        for (var index = 0; index < 300; index++) render((CharaView*)0x2222, (uint)index);
        var full = hooks.PassiveNativeEvents!;
        Check(full.Events.Count == 256 && full.Dropped == 44
            && full.Events.Select(value => value.Sequence).SequenceEqual(Enumerable.Range(1, 256).Select(value => (long)value)),
            "native consumer evidence is fixed-capacity and counts every overflow");
        Check(hooks.TryDispose() && hooks.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)),
            "closing waits for the complete physical observer batch");
        render((CharaView*)0x3333, 999);
        var after = hooks.PassiveNativeEvents!;
        Check(after.Events.Count == 256 && after.Dropped == 44,
            "late stable direct entry cannot append to or revive a closed consumer ledger");
    }

    private static void NonPassiveDoesNotCollect()
    {
        var environment = NewEnvironment(true);
        var provider = new OwnedPhysicalCases.Provider();
        using var hooks = new PortraitOwnedSceneHooks(environment);
        Check(hooks.TryInstall(new DalamudPortraitOwnedSceneHookFactory(provider, new OwnedPhysicalCases.Proof())),
            hooks.LastError ?? "non-passive event installation");
        var target = environment.Targets.Single(value => value.Kind == PortraitOwnedSceneNativeKind.Render);
        As<CharaView.Delegates.Render>(provider.Created.Single(value => value.Address == target.Address).Detour)(null, 1);
        Check(hooks.PassiveNativeEvents is null, "owned production transport has no passive consumer ledger");
    }

    private static void InvokeTarget(PortraitOwnedSceneNativeKind kind, Delegate detour, nint receiver)
    {
        switch (kind)
        {
            case PortraitOwnedSceneNativeKind.Dtor:
                _ = As<CharaView.Delegates.Dtor>(detour)((CharaView*)receiver, 0xA5); break;
            case PortraitOwnedSceneNativeKind.Release:
                As<CharaView.Delegates.Release>(detour)((CharaView*)receiver); break;
            case PortraitOwnedSceneNativeKind.Initialize:
                As<CharaView.Delegates.Initialize>(detour)((CharaView*)receiver, null, 3, 0x4444); break;
            case PortraitOwnedSceneNativeKind.Update:
                As<CharaView.Delegates.Update>(detour)((CharaView*)receiver, 9, (Character*)0x5555); break;
            case PortraitOwnedSceneNativeKind.Setup:
                As<CharaViewPortrait.Delegates.Setup>(detour)((CharaViewPortrait*)receiver, 3, null, null, 17, 19); break;
            case PortraitOwnedSceneNativeKind.Render:
                As<CharaView.Delegates.Render>(detour)((CharaView*)receiver, 11); break;
            case PortraitOwnedSceneNativeKind.CreateClientObject:
                _ = As<ClientObjectManager.Delegates.CreateBattleCharacter>(detour)((ClientObjectManager*)receiver, 0x66, 0x77); break;
            case PortraitOwnedSceneNativeKind.DeleteClientObject:
                As<ClientObjectManager.Delegates.DeleteObjectByIndex>(detour)((ClientObjectManager*)receiver, 0x88, 0x99); break;
            case PortraitOwnedSceneNativeKind.ConfigureContext:
                _ = As<ConfigureContext>(detour)(receiver, 4, 5); break;
            case PortraitOwnedSceneNativeKind.ConfigureDefaultContext:
                _ = As<ConfigureDefaultContext>(detour)(receiver, 6); break;
            case PortraitOwnedSceneNativeKind.CleanupContext:
                As<CleanupContext>(detour)(receiver); break;
            case PortraitOwnedSceneNativeKind.ConstructContext:
                _ = As<ConstructContext>(detour)(receiver); break;
            case PortraitOwnedSceneNativeKind.DestroyContext:
                _ = As<DestroyContext>(detour)(receiver, 0xAB); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static EnvironmentProof NewEnvironment(bool allowInstallation = false)
        => new((nint)Interlocked.Add(ref module, 0x4000000), allowInstallation);

    private static T As<T>(Delegate detour) where T : Delegate
        => (T)Delegate.CreateDelegate(typeof(T), detour.Target, detour.Method);

    private sealed class EnvironmentProof(nint module, bool allowInstallation) : IPortraitOwnedSceneHookEnvironment
    {
        public bool IsFrameworkThread => true;
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool TryValidateInstallation(out string reason)
        {
            reason = "test-only native consumer event qualification";
            return allowInstallation;
        }
        public bool IsSourceCurrent(PortraitIdentity source) => false;
        public bool IsSlotIdle(uint slot) => false;
        public bool IsSlotOwnedBy(uint slot, nint view) => false;
        public bool TryReadViewSlot(nint view, out uint slot) { slot = uint.MaxValue; return false; }
    }

    public static void SetupOriginal(CharaViewPortrait* view, uint slot, CharaViewCharacterData* data,
        AgentInterface* agent, int eventId, ulong eventKind)
    {
        setupCalls++;
        setupView = (nint)view; setupData = (nint)data; setupAgent = (nint)agent;
        setupSlot = slot; setupEventId = eventId; setupEventKind = eventKind;
        if (throwOnSetup is { } error) throw error;
    }

    public static uint CreateOriginal(ClientObjectManager* manager, uint index, byte param)
    {
        createCalls++;
        createManager = (nint)manager; createIndex = index; createParam = param;
        return 0xFEDCBA98;
    }

    private sealed class OriginalProvider(OwnedPhysicalCases.Provider inner) : IPortraitQualifiedNativeHookProvider
    {
        public string Identity => inner.Identity;
        public bool TryQualifyStartDisabled(out string reason) => inner.TryQualifyStartDisabled(out reason);
        public bool QualificationPreserved() => inner.QualificationPreserved();
        public Delegate DirectTarget(nint address, Type type) => inner.DirectTarget(address, type);
        public IPortraitNativePhaseHostHook CreateQualifiedHook(nint address, Type type, Delegate detour, Func<bool> verifyEntry)
        {
            var hook = inner.CreateQualifiedHook(address, type, detour, verifyEntry);
            var invoke = type.GetMethod("Invoke")!;
            var parameters = invoke.GetParameters().Select(value => value.ParameterType).ToArray();
            if (invoke.ReturnType == typeof(void) && parameters.SequenceEqual(new[] { typeof(CharaViewPortrait*), typeof(uint),
                typeof(CharaViewCharacterData*), typeof(AgentInterface*), typeof(int), typeof(ulong) }))
                return new OriginalHook(hook, Delegate.CreateDelegate(type, typeof(OwnedPassiveConsumerEventsCases)
                    .GetMethod(nameof(SetupOriginal), BindingFlags.Public | BindingFlags.Static)!));
            if (invoke.ReturnType == typeof(uint) && parameters.SequenceEqual(new[] { typeof(ClientObjectManager*), typeof(uint), typeof(byte) }))
                return new OriginalHook(hook, Delegate.CreateDelegate(type, typeof(OwnedPassiveConsumerEventsCases)
                    .GetMethod(nameof(CreateOriginal), BindingFlags.Public | BindingFlags.Static)!));
            return hook;
        }
    }

    private sealed class OriginalHook(IPortraitNativePhaseHostHook inner, Delegate original) : IPortraitNativePhaseHostHook
    {
        public nint Address => inner.Address;
        public string BackendName => inner.BackendName;
        public bool IsEnabled => inner.IsEnabled;
        public bool IsDisposed => inner.IsDisposed;
        public Delegate Original => original;
        public void Enable() => inner.Enable();
        public void Disable() => inner.Disable();
        public void Dispose() => inner.Dispose();
    }
}
