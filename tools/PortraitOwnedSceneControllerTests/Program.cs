using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

return ControllerChecks.Run(args);

static class ControllerChecks
{
    private static int count;
    private static void Check(bool value, string message) { count++; if (!value) throw new Exception(message); }
    private static PortraitIdentity Source => new(101, 102, 103, 9, 104);

    internal static int Run(string[] args)
    {
        try
        {
            if (args is ["partial"]) { PartialCreationWaitsForConfirmedRetirement(); return 0; }
            if (args is ["request"]) { SynchronousRequestFailureRetainsOwner(); return 0; }
            if (args is ["image-owner"]) { OwnedImageSourceRejectsForeignOwnerIdentity(); return 0; }
            PartialCreationWaitsForConfirmedRetirement();
            SynchronousRequestFailureRetainsOwner();
            ClosedAdmissionNeverTouchesNative();
            ExplicitDevelopmentCapabilityDoesNotOpenProduction();
            NativeSceneStartsOnlyFromOwnedCallback();
            ReadyBorrowIsSynchronousAndRetiringBlocksIt();
            LostLeaseStopsAndNeverRetriesNative();
            ReplacementCancelsOldGenerationAndRejectsLateCallback();
            DisposeWaitsForRealRetirementCallback();
            ObserverCameraIsDetachedAndBounded();
            OwnedImageSourceRequiresReadyViewAndRetainsCopy();
            OwnedImageSourceInvalidatesStaleDescriptor();
            OwnedImageSourceDrainWaitsForBorrower();
            OwnedImageSourceRejectsForeignOwnerIdentity();
            OwnedImageSourceRejectsForeignRevisionAndSource();
            OwnedImageSourceInvalidatesLateResultOnStop();
            ForeignLeaseEpochCannotBorrowAdvanceOrRetire();
            ObserverGestureIsQueuedUntilTheNextNativeRender();
            InitialObserverWaitsForAPreparedViewAndThenARender();
            ObserverNavigationCases.Run(Check);
            ObserverFeatureCases.Run(Check);
            Console.WriteLine($"PASS: {count} owned-controller checks; native calls only through injected source-phase callbacks; admission remains closed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }

    private static PortraitOwnedCharaViewSnapshot Snapshot(uint generation = 9)
    {
        CharaViewCharacterData character = default;
        CharaViewModelData model = default;
        var items = new CharaViewItem[14];
        return new PortraitOwnedCharaViewSnapshot(new(101, 102, 103, generation, 104), character, model, items, 0,
            new PortraitComposition { Paused = true, CameraDistance = 3, CameraZoomNormalized = .5f });
    }

    private static PortraitOwnedSceneController Create(FakeBroker broker, FakeApi api)
        => new(broker, () => api);

    private static void ClosedAdmissionNeverTouchesNative()
    {
        var broker = new FakeBroker { ProductionAdmission = false };
        using var api = new FakeApi();
        using var controller = Create(broker, api);
        Check(!controller.TryStart(Snapshot(), 3), "closed admission rejects start");
        Check(controller.State == PortraitOwnedSceneControllerState.Off && api.Events.Count == 0 && broker.Requests == 0,
            "closed admission has no allocation or request side effects");
    }
    private static void ExplicitDevelopmentCapabilityDoesNotOpenProduction()
    {
        var broker = new FakeBroker { DevelopmentCapability = true };
        using var runtime = new FakeApi(); using var controller = Create(broker, runtime);
        Check(controller.TryStart(Snapshot(), 3) && !broker.ProductionAdmission,
            "explicit bounded experiment can queue its first request while production remains closed");
        Check(runtime.Events.Count == 0, "development capability is not a native phase");
        controller.Stop(); broker.Retire(new FakeLease(Source, 3));
    }

    private static void NativeSceneStartsOnlyFromOwnedCallback()
    {
        var broker = new FakeBroker { ProductionAdmission = true };
        using var api = new FakeApi();
        using var controller = Create(broker, api);
        Check(controller.TryStart(Snapshot(), 3), "qualified broker accepts detached request");
        Check(api.Events.Count == 0, "request does not construct outside source phase");
        var lease = new FakeLease(Source, 3);
        broker.Advance(lease);
        Check(api.Events.SequenceEqual(new[] { "allocate", "ctor", "setup" }), "first source callback owns allocation/ctor/setup: " + string.Join(',', api.Events) + " / " + controller.LastError);
        Check(controller.State == PortraitOwnedSceneControllerState.Loading, "first callback remains loading");
        api.Ready = true;
        broker.Advance(lease);
        Check(controller.State == PortraitOwnedSceneControllerState.Ready && api.Events.Contains("render"),
            "later native source callback advances the same frame");
    }

    private static void ReadyBorrowIsSynchronousAndRetiringBlocksIt()
    {
        var broker = new FakeBroker { ProductionAdmission = true };
        using var api = new FakeApi();
        using var controller = Create(broker, api);
        controller.TryStart(Snapshot(), 3); var lease = new FakeLease(Source, 3);
        broker.Advance(lease); api.Ready = true; broker.Advance(lease);
        var reads = 0;
        Check(controller.TryUseReady(lease, _ => reads++), "ready view can be borrowed synchronously");
        Check(reads == 1, "ready borrow calls consumer once");
        controller.Stop();
        Check(controller.State == PortraitOwnedSceneControllerState.Retiring, "stop enters retiring without native call");
        Check(!controller.TryUseReady(lease, _ => reads++), "retiring view cannot be borrowed");
        broker.Retire(lease);
        Check(controller.State == PortraitOwnedSceneControllerState.Off && reads == 1, "retirement callback closes generation");
    }

    private static void LostLeaseStopsAndNeverRetriesNative()
    {
        var broker = new FakeBroker { ProductionAdmission = true };
        using var api = new FakeApi();
        using var controller = Create(broker, api); controller.TryStart(Snapshot(), 3);
        var lease = new FakeLease(Source, 3) { Valid = false };
        broker.Advance(lease);
        Check(controller.State == PortraitOwnedSceneControllerState.Faulted && api.Events.Count == 0,
            "invalid lease faults before any native call");
        var countBefore = api.Events.Count; broker.Advance(new FakeLease(Source, 3));
        Check(api.Events.Count == countBefore, "faulted generation rejects late callback");
    }

    private static void ReplacementCancelsOldGenerationAndRejectsLateCallback()
    {
        var broker = new FakeBroker { ProductionAdmission = true };
        using var api = new FakeApi(); using var controller = Create(broker, api);
        Check(controller.TryStart(Snapshot(), 3), "first generation starts");
        var old = new FakeLease(Source, 3); controller.Stop();
        Check(broker.Cancels == 1, "stop cancels request exactly once");
        Check(!controller.TryStart(Snapshot(10), 4), "replacement waits for physical retirement");
        broker.Retire(old);
        Check(controller.State == PortraitOwnedSceneControllerState.Off, "old callback completes retirement");
        Check(controller.TryStart(Snapshot(10), 4), "new generation starts after retirement");
        broker.Advance(old);
        Check(api.Events.Count == 0, "old lease callback cannot enter new generation");
    }

    private static void DisposeWaitsForRealRetirementCallback()
    {
        var broker = new FakeBroker { ProductionAdmission = true }; using var runtime = new FakeApi();
        var controller = Create(broker, runtime); controller.TryStart(Snapshot(), 3);
        var lease = new FakeLease(Source, 3); broker.Advance(lease); controller.Dispose();
        Check(controller.State == PortraitOwnedSceneControllerState.Retiring && broker.Cancels == 1,
            "dispose requests cancellation while retaining the live owner");
        broker.Retire(lease);
        Check(controller.State == PortraitOwnedSceneControllerState.Off && runtime.Events.Count == 5,
            "dispose becomes complete only after the real retirement callback");
    }

    private static void PartialCreationWaitsForConfirmedRetirement()
    {
        foreach (var throws in new[] { false, true })
        {
            var broker = new FakeBroker { ProductionAdmission = true };
            using var runtime = new FakeApi { CreateReturnsFalse = !throws, CreateThrows = throws, RetireSucceeds = false };
            using var controller = Create(broker, runtime);
            controller.TryStart(Snapshot(), 3); var lease = new FakeLease(Source, 3); broker.Advance(lease);
            Check(controller.State == PortraitOwnedSceneControllerState.Faulted && controller.Source is not null,
                "partial creation retains its owner after " + (throws ? "throw" : "false"));
            Check(!broker.Retire(lease) && runtime.RetireCalls == 1,
                "failed/throwing create still requires a real runtime retirement attempt");
            Check(controller.State == PortraitOwnedSceneControllerState.Faulted && controller.Source is not null && runtime.Created,
                "unconfirmed retirement cannot forget a partial native owner");
            runtime.RetireSucceeds = true;
            Check(broker.Retire(lease) && runtime.RetireCalls == 2 && !runtime.Created
                && controller.State == PortraitOwnedSceneControllerState.Off && controller.Source is null,
                "partial creation is forgotten only after runtime retirement is confirmed");
        }
    }

    private static void SynchronousRequestFailureRetainsOwner()
    {
        foreach (var throws in new[] { false, true })
        {
            var broker = new FakeBroker { ProductionAdmission = true, AdvanceDuringRequest = true,
                RequestReturnsFalse = !throws, RequestThrows = throws };
            using var runtime = new FakeApi(); using var controller = Create(broker, runtime);
            Check(!controller.TryStart(Snapshot(), 3), "broker's failed request is reported as unsuccessful");
            Check(runtime.Created && controller.Source is not null
                && controller.State is PortraitOwnedSceneControllerState.Retiring or PortraitOwnedSceneControllerState.Faulted,
                "synchronous callback owner survives broker " + (throws ? "throw" : "false"));
            Check(broker.Cancels == 1 && !controller.TryStart(Snapshot(10), 4),
                "failed synchronous request cancels once and blocks a replacement");
            Check(broker.Retire(new FakeLease(Source, 3)) && runtime.RetireCalls == 1 && !runtime.Created
                && controller.State == PortraitOwnedSceneControllerState.Off,
                "broker failure retains runtime until its real retirement callback");
        }
    }

    private static void ObserverCameraIsDetachedAndBounded()
    {
        var camera = new PortraitOwnedSceneObserverCamera();
        Check(camera.TrySet(new(0, 0, 0), new(0, 0, 1), 0, 0, 1), "valid observer camera accepted");
        var snapshot = camera.Snapshot;
        Check(snapshot.IsValid && snapshot.Position != snapshot.Target, "camera stores detached values");
        Check(!camera.TrySet(new(float.NaN, 0, 0), snapshot.Target, 0, 0, 1), "nonfinite camera rejected");
        Check(camera.Snapshot == snapshot, "invalid camera cannot alter prior state");
        Check(PortraitOwnedSceneObserverCameraSnapshot.TryFromSource(new(.0002f,.1f,1),new(0,.1f,0),out var canonical)
            && canonical.IsNativeSupported && canonical.Position==new System.Numerics.Vector3(.0002f,.1f,1),
            "source observer derives consistent angles from actual source position without moving its camera");
        Check(!PortraitOwnedSceneObserverCameraSnapshot.TryFromSource(new(0,0,1),new(4,0,0),out _),
            "unsupported source target is explicit and cannot silently acquire observer controls");
    }
    private static void ObserverGestureIsQueuedUntilTheNextNativeRender()
    {
        var broker = new FakeBroker { ProductionAdmission = true }; using var runtime = new FakeApi();
        using var controller = Create(broker, runtime); controller.TryStart(Snapshot(), 3);
        var lease = new FakeLease(Source, 3); broker.Advance(lease); runtime.Ready = true; broker.Advance(lease);
        var before = runtime.Events.Count;
        var side = new PortraitOwnedSceneObserverCameraSnapshot(new(1,0,0), default, MathF.PI/2, 0, 1);
        var back = new PortraitOwnedSceneObserverCameraSnapshot(new(0,0,-1), default, MathF.PI, 0, 1);
        Check(controller.TrySetObserverCamera(side) && controller.TrySetObserverCamera(back), "valid observer gestures queue");
        Check(runtime.Events.Count == before && controller.State == PortraitOwnedSceneControllerState.Loading,
            "gesture input cannot mutate native camera or expose an old ready frame");
        broker.Advance(lease);
        Check(runtime.Events.Skip(before).SequenceEqual(new[] { "observer-camera", "render" }) && runtime.Observer == back,
            "the latest detached camera is applied once immediately before the next native Render");
        Check(controller.State == PortraitOwnedSceneControllerState.Ready, "rendered camera revision becomes ready");
        controller.Stop(); Check(!controller.TrySetObserverCamera(side), "retiring generation rejects observer input");
        broker.Retire(lease);
    }
    private static void InitialObserverWaitsForAPreparedViewAndThenARender()
    {
        var broker = new FakeBroker { ProductionAdmission = true }; using var runtime = new FakeApi();
        using var controller = Create(broker, runtime); controller.TryStart(Snapshot(), 3);
        var side = new PortraitOwnedSceneObserverCameraSnapshot(new(1,0,0), default, MathF.PI/2, 0, 1);
        Check(controller.TrySetObserverCamera(side), "initial observer queues before creation");
        var lease = new FakeLease(Source, 3); broker.Advance(lease); runtime.Ready = true; broker.Advance(lease);
        Check(runtime.Observer is null && controller.State == PortraitOwnedSceneControllerState.Loading,
            "first preparation render cannot publish a frame for an unapplied observer");
        broker.Advance(lease);
        Check(runtime.Observer == side && controller.State == PortraitOwnedSceneControllerState.Ready,
            "only a later native render publishes the initial independent observer");
        var calls = runtime.Events.Count;
        Check(!controller.TrySetObserverCamera(side with { Distance = 3 }), "unsupported native camera distance is rejected");
        Check(runtime.Events.Count == calls && controller.State == PortraitOwnedSceneControllerState.Ready,
            "invalid camera input leaves the current rendered view intact");
        controller.Stop(); broker.Retire(lease);
    }

    private static PortraitOwnedSceneImageDescriptor Descriptor(PortraitOwnedSceneController controller, long generation = 1)
        => new(controller.ImageOwner ?? throw new Exception("controller did not bind an image owner"),
            generation, 640, 480, 0, 0, 1, 1);

    private static void OwnedImageSourceRequiresReadyViewAndRetainsCopy()
    {
        var broker = new FakeBroker { ProductionAdmission = true };
        using var runtime = new FakeApi(); using var controller = Create(broker, runtime);
        controller.TryStart(Snapshot(), 3); var lease = new FakeLease(Source, 3); broker.Advance(lease);
        using var source = new PortraitOwnedSceneImageSource<TestTexture>();
        var descriptor = Descriptor(controller);
        Check(source.TrySetDescriptor(controller, descriptor), "valid owned descriptor accepted");
        var calls = 0;
        Check(!source.TryCopy(controller, lease, descriptor, _ => { calls++; return Task.FromResult(new TestTexture()); }) && calls == 0,
            "loading view cannot start a texture copy");
        runtime.Ready = true; broker.Advance(lease);
        Check(source.TryCopy(controller, lease, descriptor, _ => { calls++; return Task.FromResult(new TestTexture()); }),
            "ready owned view starts one detached copy");
        Check(calls == 1 && source.FrameCount == 1 && source.InFlight == 0, "copy completion publishes one owned frame");
        using var borrow = source.TryBorrow() ?? throw new Exception("owned frame was not borrowable");
        Check(borrow.Texture is not null, "borrow exposes only copied texture");
    }

    private static void OwnedImageSourceInvalidatesStaleDescriptor()
    {
        var broker = new FakeBroker { ProductionAdmission = true }; using var runtime = new FakeApi();
        using var controller = Create(broker, runtime); controller.TryStart(Snapshot(), 3); var lease = new FakeLease(Source, 3);
        broker.Advance(lease); runtime.Ready = true; broker.Advance(lease);
        using var source = new PortraitOwnedSceneImageSource<TestTexture>(); var oldDescriptor = Descriptor(controller, 1);
        var newDescriptor = Descriptor(controller, 2); source.TrySetDescriptor(controller, oldDescriptor);
        Check(source.TryCopy(controller, lease, oldDescriptor, _ => Task.FromResult(new TestTexture())), "old descriptor initially copies");
        Check(source.TrySetDescriptor(controller, newDescriptor), "new resource generation replaces descriptor");
        Check(!source.TryCopy(controller, lease, oldDescriptor, _ => Task.FromResult(new TestTexture())), "stale descriptor is rejected");
        Check(source.TryCopy(controller, lease, newDescriptor, _ => Task.FromResult(new TestTexture())), "new descriptor copies");
    }

    private static void OwnedImageSourceDrainWaitsForBorrower()
    {
        var broker = new FakeBroker { ProductionAdmission = true }; using var runtime = new FakeApi();
        using var controller = Create(broker, runtime); controller.TryStart(Snapshot(), 3); var lease = new FakeLease(Source, 3);
        broker.Advance(lease); runtime.Ready = true; broker.Advance(lease);
        using var source = new PortraitOwnedSceneImageSource<TestTexture>(); var descriptor = Descriptor(controller);
        source.TrySetDescriptor(controller, descriptor);
        source.TryCopy(controller, lease, descriptor, _ => Task.FromResult(new TestTexture()));
        var borrow = source.TryBorrow() ?? throw new Exception("borrower was not admitted");
        source.Dispose(); var drain = source.DrainPhysicalAsync();
        Check(!drain.IsCompleted, "physical drain waits for retained draw borrower");
        borrow.Dispose(); drain.GetAwaiter().GetResult(); Check(drain.IsCompleted, "physical drain completes after borrower release");
    }

    private static void OwnedImageSourceRejectsForeignOwnerIdentity()
    {
        var broker = new FakeBroker { ProductionAdmission = true }; using var runtime = new FakeApi();
        using var controller = Create(broker, runtime); controller.TryStart(Snapshot(), 3); var lease = new FakeLease(Source, 3);
        broker.Advance(lease); runtime.Ready = true; broker.Advance(lease);
        using var source = new PortraitOwnedSceneImageSource<TestTexture>();
        var current = Descriptor(controller);
        var foreign = current with { Owner = current.Owner with { Session = Guid.Parse("c8568f71-933a-4158-a51f-e47022f7fd6b") } };
        Check(!source.TrySetDescriptor(controller, foreign), "foreign session owner cannot bind a descriptor");
        source.TrySetDescriptor(controller, current); var calls = 0;
        Check(!source.TryCopy(controller, lease, foreign, _ => { calls++; return Task.FromResult(new TestTexture()); }) && calls == 0,
            "same-slot foreign descriptor cannot start any texture copy without the current controller owner token");
        var otherBroker = new FakeBroker { ProductionAdmission = true }; using var otherRuntime = new FakeApi();
        using var other = Create(otherBroker, otherRuntime); other.TryStart(Snapshot(), 3);
        otherBroker.Advance(lease); otherRuntime.Ready = true; otherBroker.Advance(lease);
        Check(!source.TryCopy(other, lease, current, _ => { calls++; return Task.FromResult(new TestTexture()); }) && calls == 0,
            "another ready controller for the same slot cannot authorize the bound image source");
    }

    private static void OwnedImageSourceRejectsForeignRevisionAndSource()
    {
        var broker = new FakeBroker { ProductionAdmission = true }; using var runtime = new FakeApi();
        using var controller = Create(broker, runtime); controller.TryStart(Snapshot(), 3); var lease = new FakeLease(Source, 3);
        broker.Advance(lease); runtime.Ready = true; broker.Advance(lease);
        using var source = new PortraitOwnedSceneImageSource<TestTexture>(); var current = Descriptor(controller);
        source.TrySetDescriptor(controller, current); var calls = 0;
        var owners = new[]
        {
            current.Owner with { Revision = current.Owner.Revision + 1 },
            current.Owner with { Source = Source with { Generation = 10 } },
            current.Owner with { Source = Source with { Character = 105 } },
            current.Owner with { LeaseEpoch = 2 },
        };
        foreach (var owner in owners)
        {
            var foreign = current with { Owner = owner };
            Check(!source.TrySetDescriptor(controller, foreign), "foreign revision/source/epoch cannot replace a bound descriptor");
            Check(!source.TryCopy(controller, lease, foreign, _ => { calls++; return Task.FromResult(new TestTexture()); }),
                "foreign revision/source/epoch cannot trigger a texture read");
        }
        Check(calls == 0 && source.TryCopy(controller, lease, current, _ => { calls++; return Task.FromResult(new TestTexture()); })
            && calls == 1 && source.FrameCount == 1, "only exact controller owner and lease can publish a copied frame");
    }

    private static void OwnedImageSourceInvalidatesLateResultOnStop()
    {
        var broker = new FakeBroker { ProductionAdmission = true }; using var runtime = new FakeApi();
        using var controller = Create(broker, runtime); controller.TryStart(Snapshot(), 3); var lease = new FakeLease(Source, 3);
        broker.Advance(lease); runtime.Ready = true; broker.Advance(lease);
        using var source = new PortraitOwnedSceneImageSource<TestTexture>(); var descriptor = Descriptor(controller);
        source.TrySetDescriptor(controller, descriptor); var pending = new TaskCompletionSource<TestTexture>();
        Check(source.TryCopy(controller, lease, descriptor, _ => pending.Task), "current owner starts one pending copy");
        controller.Stop();
        Check(controller.ImageOwner is null && !source.TryCopy(controller, lease, descriptor, _ => pending.Task),
            "stop invalidates image authority before native retirement");
        var late = new TestTexture(); pending.SetResult(late);
        Check(late.Disposed && source.FrameCount == 0 && source.TryBorrow() is null,
            "copy finishing after owner invalidation is disposed and never published");
        broker.Retire(lease);
    }

    private static void ForeignLeaseEpochCannotBorrowAdvanceOrRetire()
    {
        var broker = new FakeBroker { ProductionAdmission = true }; using var runtime = new FakeApi();
        using var controller = Create(broker, runtime); controller.TryStart(Snapshot(), 3); var lease = new FakeLease(Source, 3);
        broker.Advance(lease); runtime.Ready = true; broker.Advance(lease);
        var foreign = new FakeLease(Source, 3) { LeaseEpoch = 2 }; var reads = 0; var eventCount = runtime.Events.Count;
        Check(!controller.TryUseReady(foreign, _ => reads++) && reads == 0, "another epoch cannot borrow the current ready view");
        broker.Advance(foreign);
        Check(runtime.Events.Count == eventCount, "another epoch cannot advance the current owner");
        Check(!broker.Retire(foreign) && controller.Source is not null && runtime.Created,
            "another epoch cannot retire or forget the current owner");
        controller.Stop(); Check(broker.Retire(lease), "original epoch retains retirement authority");
    }

    private sealed class TestTexture : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }

    private sealed class FakeBroker : IPortraitOwnedSceneControllerBroker
    {
        internal bool ProductionAdmission, DevelopmentCapability, AdvanceDuringRequest, RequestReturnsFalse, RequestThrows; internal int Requests, Cancels;
        private Action<IPortraitOwnedSceneLease>? advance; private Func<IPortraitOwnedSceneLease, bool>? retire;
        public bool IsFrameworkThread => true;
        public bool StartAdmission => ProductionAdmission || DevelopmentCapability;
        bool IPortraitOwnedSceneControllerBroker.ProductionAdmission => ProductionAdmission;
        public bool TryRequest(PortraitIdentity source, uint slot, Action<IPortraitOwnedSceneLease> onAdvance, Func<IPortraitOwnedSceneLease, bool> onRetire)
        {
            Requests++; advance = onAdvance; retire = onRetire;
            if (AdvanceDuringRequest) onAdvance(new FakeLease(source, slot));
            if (RequestThrows) throw new InvalidOperationException("request failed after callback");
            return StartAdmission && !RequestReturnsFalse;
        }
        public void Cancel(string reason) { Cancels++; }
        internal void Advance(IPortraitOwnedSceneLease lease) => advance?.Invoke(lease);
        internal bool Retire(IPortraitOwnedSceneLease lease) => retire?.Invoke(lease) ?? false;
    }

    private sealed class FakeLease(PortraitIdentity source, uint slot) : IPortraitOwnedSceneLease
    {
        public uint Slot { get; } = slot; public PortraitIdentity Source { get; } = source; public long Epoch => LeaseEpoch;
        internal long LeaseEpoch = 1;
        public uint NativeFrame => 7; public bool IsCurrent => Valid; public bool Valid = true;
        public bool CanCreate => Valid; public bool TryBindCreatedView(nint view) => Valid;
        public bool CanAccess(nint view) => Valid; public bool CanReleaseView(nint view) => Valid; public bool MarkReleased(nint view) => Valid;
        public IDisposable? EnterOwnedCall(nint view) => Valid ? new Scope() : null;
        public bool TryBeginRetirement(nint view) => Valid; private sealed class Scope : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeApi : IPortraitOwnedSceneRuntime, IDisposable
    {
        internal readonly List<string> Events = []; internal bool Ready; internal bool Created;
        internal bool CreateReturnsFalse, CreateThrows, RetireSucceeds = true; internal int RetireCalls;
        internal PortraitOwnedSceneObserverCameraSnapshot? Observer;
        public bool TryApplyObserverCamera(IPortraitOwnedSceneLease lease, PortraitOwnedSceneObserverCameraSnapshot value)
        { if (!Created || !Ready || !lease.IsCurrent) return false; Observer = value; Events.Add("observer-camera"); return true; }
        bool IPortraitOwnedSceneRuntime.IsReady => Ready;
        public bool TryCreate(PortraitOwnedCharaViewSnapshot source, IPortraitOwnedSceneLease lease)
        {
            if (!lease.IsCurrent || !lease.CanCreate) return false;
            Created = true; Events.AddRange(["allocate", "ctor", "setup"]);
            if (CreateThrows) throw new InvalidOperationException("partial creation failed");
            return !CreateReturnsFalse;
        }
        public bool Advance(IPortraitOwnedSceneLease lease, uint frame)
        { if (!Created || !lease.IsCurrent) return false; Events.Add("render"); return true; }
        public bool UseReadyView(IPortraitOwnedSceneLease lease, Action<nint> read)
        { if (!Ready || !Created || !lease.IsCurrent) return false; read(0x1234); return true; }
        public bool TryRetire(IPortraitOwnedSceneLease lease)
        {
            RetireCalls++;
            if (!Created || !lease.IsCurrent || !RetireSucceeds) return false;
            Events.AddRange(["release", "dtor"]); Created = false; Ready = false; return true;
        }
        public void Dispose() { }
    }
}
