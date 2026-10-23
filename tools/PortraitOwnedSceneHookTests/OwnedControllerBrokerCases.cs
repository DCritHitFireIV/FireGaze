using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

internal static class OwnedControllerBrokerCases
{
    internal static int Run()
    {
        var failed = 0; var passed = 0;
        foreach (var (name, run) in new (string, Action)[] {
            ("denied production creates neither runtime nor hooks", Denied),
            ("unused cancellation closes without waiting for another source render", Unused),
            ("cancellation inside the source Original retires on that genuine return", CancelDuringOriginal),
            ("ready observers run inside the exact live lease after controller advance", Ready),
            ("active stop retains native owner until a real source callback", Retire),
            ("lost slot preserves quarantine and forbids late native cleanup", LostSlot),
            ("failed enable releases returned hook batch before a fresh request", FailedInstall),
            ("ready callback failure still retires native owner before hooks", ReadyFailure),
        }) {
            try { run(); passed++; Console.WriteLine("PASS controller broker " + name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL controller broker " + name + ": " + error.Message); }
        }
        Console.WriteLine($"{passed} controller broker passed; {failed} failed"); return failed == 0 ? 0 : 1;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static bool Clean(PortraitOwnedSceneControllerBroker broker)
        => broker.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(2));
    private static void Denied()
    {
        using var host = new BrokerNativeHost { Admitted = false };
        using var broker = host.CreateBroker(); var runtimes = 0;
        using var controller = new PortraitOwnedSceneController(broker, () => { runtimes++; return host.Runtime; });
        Check(!controller.TryStart(host.Snapshot(), 3) && runtimes == 0 && host.Factory.Created.Count == 0,
            "closed native admission reached a runtime factory or native hook Create");
    }
    private static void Unused()
    {
        using var host = new BrokerNativeHost(); using var broker = host.CreateBroker();
        using var controller = new PortraitOwnedSceneController(broker, () => host.Runtime);
        Check(controller.TryStart(host.Snapshot(), 3), "request rejected"); var reads = host.SlotReads;
        controller.Stop(); broker.Update();
        Check(controller.State == PortraitOwnedSceneControllerState.Off && controller.Source is null,
            "unused request waits for a source callback");
        Check(host.Runtime.Events.Count == 0 && host.SlotReads == reads && Clean(broker),
            "unused cancellation read native state, allocated, or retained hooks");
        Check(controller.TryStart(host.Snapshot(), 3), "clean broker cannot construct a fresh generation");
        controller.Stop(); broker.Update();
    }
    private static void CancelDuringOriginal()
    {
        using var host = new BrokerNativeHost(); using var broker = host.CreateBroker();
        using var controller = new PortraitOwnedSceneController(broker, () => host.Runtime);
        Check(controller.TryStart(host.Snapshot(), 3), "request rejected");
        host.Factory.OnSourceOriginal = () => controller.Stop(); host.Render(40); broker.Update();
        Check(controller.State == PortraitOwnedSceneControllerState.Off && host.Runtime.Events.Count == 0,
            "cancellation inside Original forgot the unused controller or performed native work");
        Check(host.Factory.SourceOriginals == 1 && Clean(broker),
            "source Original or physical retirement count changed");
    }
    private static void Ready()
    {
        using var host = new BrokerNativeHost(); PortraitOwnedSceneController? controller = null;
        var copies = 0; IPortraitOwnedSceneLease? observed = null;
        using var broker = host.CreateBroker(lease => {
            observed = lease;
            if (controller!.TryUseReady(lease, _ => { Check(lease.IsCurrent, "copy escaped native lease"); copies++; }))
                Check(host.Runtime.ReadDepth == 0, "ready scope leaked");
        });
        using (controller = new(broker, () => host.Runtime)) {
            Check(controller.TryStart(host.Snapshot(), 3), "request rejected"); host.Render(41);
            Check(copies == 0 && controller.State == PortraitOwnedSceneControllerState.Loading, "creation copied an unready view");
            host.Render(42);
            Check(copies == 1 && controller.State == PortraitOwnedSceneControllerState.Ready, "ready native callback did not copy");
            Check(observed is not null && !observed.IsCurrent, "lease escaped its native return");
            controller.Stop(); host.Render(43); broker.Update();
        }
    }
    private static void Retire()
    {
        using var host = new BrokerNativeHost(); using var broker = host.CreateBroker();
        using var controller = new PortraitOwnedSceneController(broker, () => host.Runtime);
        Check(controller.TryStart(host.Snapshot(), 3), "request rejected"); host.Render(44); host.Render(45);
        controller.Stop(); broker.Update();
        Check(!broker.DrainPhysicalAsync().IsCompleted && host.SlotOwner == BrokerNativeHost.Owned
            && host.Runtime.Retirements == 0 && controller.ImageOwner is null,
            "stop released an owner outside its native phase or retained its image permission");
        host.Render(46); broker.Update();
        Check(controller.State == PortraitOwnedSceneControllerState.Off && host.SlotOwner == 0
            && host.Runtime.Retirements == 1 && Clean(broker),
            "native retirement did not precede physical hook drain");
    }
    private static void LostSlot()
    {
        using var host = new BrokerNativeHost(); using var broker = host.CreateBroker();
        using var controller = new PortraitOwnedSceneController(broker, () => host.Runtime);
        Check(controller.TryStart(host.Snapshot(), 3), "request rejected"); host.Render(47);
        host.SlotOwner = 0x777; controller.Stop(); host.Render(48); broker.Update();
        Check(host.Runtime.Retirements == 0 && host.SlotOwner == 0x777 && !broker.DrainPhysicalAsync().IsCompleted,
            "lost slot was cleaned up or claimed physically retired");
    }
    private static void FailedInstall()
    {
        using var host = new BrokerNativeHost(); using var broker = host.CreateBroker();
        using var controller = new PortraitOwnedSceneController(broker, () => host.Runtime);
        host.Factory.FailEnable = true;
        Check(!controller.TryStart(host.Snapshot(), 3), "failed enable admitted a controller"); broker.Update();
        Check(host.Factory.Created.Count > 0 && host.Factory.Created.All(h => h.IsDisposed)
            && Clean(broker), "failed install lost hook ownership");
        host.Factory.FailEnable = false;
        Check(controller.TryStart(host.Snapshot(), 3), "confirmed rollback did not permit a fresh environment");
        controller.Stop(); broker.Update();
    }
    private static void ReadyFailure()
    {
        using var host = new BrokerNativeHost();
        using var broker = host.CreateBroker(_ => throw new IOException("ready consumer failed"));
        using var controller = new PortraitOwnedSceneController(broker, () => host.Runtime);
        Check(controller.TryStart(host.Snapshot(), 3), "request rejected"); host.Render(49); broker.Update();
        Check(controller.State == PortraitOwnedSceneControllerState.Off && host.Runtime.Retirements == 1
            && Clean(broker), "ready failure lost native retirement owner");
    }
}

// Only external native operations are substituted. Broker, controller, lease arbitration,
// native callback gateway and retirement checks are the production implementations.
internal sealed unsafe class BrokerNativeHost : IDisposable
{
    internal static readonly PortraitIdentity Source = new(0x100, 0x200, 0x300, 7, 0x400);
    internal const nint Owned = 0x500;
    internal bool Admitted = true, Framework = true, SourceCurrent = true;
    internal int SlotReads;
    internal nint SlotOwner;
    internal readonly NativeFactory Factory;
    internal readonly NativeRuntime Runtime;
    internal BrokerNativeHost() { Factory = new(); Runtime = new(this); }
    internal PortraitOwnedSceneControllerBroker CreateBroker(Action<IPortraitOwnedSceneLease>? ready = null)
        => new(() => Framework, CreateEnvironment, Factory, afterAdvance: ready);
    internal IPortraitOwnedSceneHookEnvironment CreateEnvironment() => new NativeEnvironment(this);
    internal PortraitOwnedCharaViewSnapshot Snapshot() => new(Source, default(CharaViewCharacterData),
        default(CharaViewModelData), CompleteItems(), 0, new PortraitComposition(), gameBuild: "fixture");
    private static CharaViewItem[] CompleteItems()
    {
        var items = new CharaViewItem[14];
        for (byte i = 0; i < items.Length; i++) items[i].SlotId = i;
        return items;
    }
    internal void Render(uint frame) => Factory.Get<CharaView.Delegates.Render>()((CharaView*)Source.Model, frame);
    public void Dispose() { }
    private sealed class NativeEnvironment(BrokerNativeHost host) : IPortraitOwnedSceneHookEnvironment
    {
        private static long module = 0x300000000;
        private readonly nint address = (nint)Interlocked.Add(ref module, 0x1000000);
        public bool IsFrameworkThread => host.Framework;
        public bool TryValidateInstallation(out string reason) { reason = host.Admitted ? "" : "native evidence missing"; return host.Admitted; }
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(address);
        public bool IsSourceCurrent(PortraitIdentity source) => host.SourceCurrent && source == Source;
        public bool IsSlotIdle(uint slot) { host.SlotReads++; return slot == 3 && host.SlotOwner == 0; }
        public bool IsSlotOwnedBy(uint slot, nint view) { host.SlotReads++; return slot == 3 && view == Owned && host.SlotOwner == view; }
        public bool TryReadViewSlot(nint view, out uint slot) { host.SlotReads++; slot = view == Source.Model ? 4u : 3; return view == Source.Model || view == Owned; }
    }
    internal sealed class NativeFactory : IPortraitOwnedSceneHookFactory
    {
        internal readonly List<NativeHook> Created = [];
        private readonly Dictionary<Type, Delegate> detours = [];
        internal bool FailEnable;
        internal Action? OnSourceOriginal;
        internal int SourceOriginals;
        public IPortraitOwnedSceneNativeHook<T> Create<T>(PortraitOwnedSceneNativeTarget target, T detour) where T : Delegate
        {
            detours[typeof(T)] = detour;
            Delegate original = target.Kind == PortraitOwnedSceneNativeKind.Render
                ? (CharaView.Delegates.Render)((view, _) => { if ((nint)view == Source.Model) { SourceOriginals++; OnSourceOriginal?.Invoke(); } })
                : detour;
            var hook = new NativeHook<T>((T)original, () => FailEnable); Created.Add(hook); return hook;
        }
        internal T Get<T>() where T : Delegate => (T)detours[typeof(T)];
    }
    internal abstract class NativeHook : IPortraitOwnedSceneNativeHook
    {
        public bool IsEnabled { get; protected set; }
        public bool IsDisposed { get; private set; }
        public abstract void Enable();
        public void Disable() => IsEnabled = false;
        public void Dispose() { IsEnabled = false; IsDisposed = true; }
    }
    private sealed class NativeHook<T>(T original, Func<bool> fail) : NativeHook, IPortraitOwnedSceneNativeHook<T> where T : Delegate
    {
        public T Original => original;
        public override void Enable() { if (fail()) throw new IOException("native enable failed"); IsEnabled = true; }
    }
    internal sealed class NativeRuntime(BrokerNativeHost host) : IPortraitOwnedSceneRuntime
    {
        internal readonly List<string> Events = [];
        internal int Retirements, ReadDepth;
        internal Action? BeforeRetire { get; set; }
        public bool IsReady { get; private set; }
        public bool TryCreate(PortraitOwnedCharaViewSnapshot source, IPortraitOwnedSceneLease lease)
        {
            if (!lease.TryBindCreatedView(Owned)) return false;
            using (var ctor = lease.EnterCreationCall(Owned, PortraitOwnedSceneCreationCall.Construct))
                if (ctor is null || !lease.ConfirmConstructed(Owned)) return false;
            using (var setup = lease.EnterCreationCall(Owned, PortraitOwnedSceneCreationCall.Setup)) {
                if (setup is null) return false; host.SlotOwner = Owned;
                if (!lease.ConfirmSetup(Owned)) return false;
            }
            Events.Add("create"); return true;
        }
        public bool Advance(IPortraitOwnedSceneLease lease, uint frame)
        {
            using var call = lease.EnterOwnedCall(Owned); if (call is null) return false;
            host.Factory.Get<CharaView.Delegates.Render>()((CharaView*)Owned, frame);
            IsReady = true; Events.Add("render"); return true;
        }
        public bool UseReadyView(IPortraitOwnedSceneLease lease, Action<nint> read)
        {
            if (!IsReady || !lease.CanAccess(Owned) || ReadDepth != 0) return false;
            using var call = lease.EnterOwnedCall(Owned); if (call is null) return false;
            ReadDepth++;
            try { read(Owned); return lease.CanAccess(Owned); } finally { ReadDepth--; }
        }
        public bool TryRetire(IPortraitOwnedSceneLease lease)
        {
            BeforeRetire?.Invoke(); using var call = lease.EnterOwnedCall(Owned);
            if (call is null || !lease.TryBeginRetirement(Owned)) return false;
            host.SlotOwner = 0;
            if (!lease.MarkReleased(Owned)) return false;
            Retirements++; IsReady = false; Events.Add("retire"); return true;
        }
    }
}
