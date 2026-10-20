using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.PortraitPreview;

internal static class OwnedBrokerPresentationCases
{
    internal static async Task<int> Run()
    {
        var passed = 0; var failed = 0;
        foreach (var (name, run) in new (string, Func<Task>)[] {
            ("real ready lease drives the owned RT copy", Ready),
            ("stop revokes image before native retirement and drains retained draw", DrawDrain),
            ("late provider task keeps source alive after native retirement", PendingDrain),
            ("unused session cancellation needs no future native callback", Unused),
            ("Framework update completes unused cancellation after a foreign Original returns", DeferredUnused),
            ("provider release failure holds combined physical quarantine", CleanupFailure),
            ("natural native ownership loss revokes published image and retains quarantine", NaturalLoss),
            ("unused source loss clears only the never-created owner", UnusedSourceLoss),
            ("observer input revokes pending image without touching native from Framework", ObserverInput),
            ("session factory shares one broker with copy and physical retirement", BrokerFactory),
            ("joint experiment holds pending and published frame until observer input", JointPendingFrame),
        }) {
            try { await run(); passed++; Console.WriteLine("PASS owned presentation " + name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL owned presentation " + name + ": " + error.Message); }
        }
        Console.WriteLine($"{passed} owned presentation passed; {failed} failed"); return failed == 0 ? 0 : 1;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Ready()
    {
        using var f = new SceneFixture();
        Check(f.Session.TryStart(f.Native.Snapshot(), 3), "native request rejected"); f.Native.Render(1);
        Check(f.Read.Reads == 0 && f.Textures.Copies == 0, "creation copied an unready native view");
        f.Native.Render(2);
        Check(f.Read.Reads >= 3 && f.Textures.Copies == 1 && f.Native.Runtime.ReadDepth == 0,
            "ready lease did not synchronously initiate one owned RT copy");
        var image = new Wrap(801); f.Textures.Completion.SetResult(image);
        await Checks.Wait(() => f.Session.Images.InFlight == 0);
        using (var held = f.Session.Images.TryBorrow()) Check(held?.Texture == image, "owned copy did not publish");
        f.Session.Dispose(); f.Native.Render(3); f.Session.Update();
        await f.Session.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(image.Disposed && f.Textures.Input!.Disposed, "completed session did not release source and image");
    }
    private static async Task DrawDrain()
    {
        using var f = new SceneFixture(); f.Start(); var image = new Wrap(802); f.Textures.Completion.SetResult(image);
        await Checks.Wait(() => f.Session.Images.InFlight == 0);
        var draw = f.Session.Images.TryBorrow() ?? throw new Exception("published image could not be borrowed");
        var retiredWithNoImage = false;
        f.Native.Runtime.BeforeRetire = () => retiredWithNoImage = f.Session.Controller.ImageOwner is null
            && f.Session.Images.TryBorrow() is null;
        f.Session.Stop();
        Check(f.Session.Images.TryBorrow() is null && f.Native.Runtime.Retirements == 0 && !image.Disposed,
            "stop failed to revoke image or destroyed a retained draw");
        f.Session.Dispose(); var drain = f.Session.DrainPhysicalAsync();
        Check(!drain.IsCompleted, "combined drain forgot its native scene and draw borrower");
        f.Native.Render(3); f.Session.Update();
        Check(retiredWithNoImage && f.Native.Runtime.Retirements == 1 && !drain.IsCompleted && !image.Disposed,
            "native retirement did not revoke image first or forgot independent draw lifetime");
        draw.Dispose(); await drain.WaitAsync(TimeSpan.FromSeconds(2));
        Check(image.Disposed, "last draw release did not finish physical retirement");
    }
    private static async Task PendingDrain()
    {
        using var f = new SceneFixture(); f.Start(); f.Session.Dispose(); var drain = f.Session.DrainPhysicalAsync();
        f.Native.Render(3); f.Session.Update();
        Check(f.Native.Runtime.Retirements == 1 && !drain.IsCompleted && f.Textures.Input is { Disposed: false },
            "provider source was released before its actual task completion");
        var late = new Wrap(803); f.Textures.Completion.SetResult(late);
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
        Check(late.Disposed && f.Textures.Input!.Disposed && f.Session.Images.TryBorrow() is null,
            "closed session published a late provider result or leaked its input");
    }
    private static async Task Unused()
    {
        using var f = new SceneFixture(); Check(f.Session.TryStart(f.Native.Snapshot(), 3), "request rejected");
        f.Session.Dispose(); await f.Session.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(f.Session.Controller.State == PortraitOwnedSceneControllerState.Off
            && f.Native.Runtime.Events.Count == 0 && f.Textures.Copies == 0, "unused close manufactured native work");
    }
    private static async Task CleanupFailure()
    {
        using var f = new SceneFixture(); f.Textures.InputDisposeFailure = true; f.Start();
        f.Session.Dispose(); f.Native.Render(3); f.Session.Update();
        var image = new Wrap(804); f.Textures.Completion.SetResult(image);
        await Checks.Wait(() => f.Session.Images.InFlight == 0);
        Check(image.Disposed && !f.Session.ImageResourcesClean && !f.Session.DrainPhysicalAsync().IsCompleted,
            "uncertain provider input release certified combined physical retirement");
    }
    private static async Task DeferredUnused()
    {
        using var f = new SceneFixture(); Check(f.Session.TryStart(f.Native.Snapshot(), 3), "request rejected");
        f.Native.Factory.OnSourceOriginal = () => { f.Native.Framework = false; f.Session.Dispose(); };
        f.Native.Render(1);
        Check(f.Session.Controller.State == PortraitOwnedSceneControllerState.Retiring && f.Native.Runtime.Events.Count == 0,
            "foreign Original performed native scene cleanup");
        f.Native.Framework = true; f.Session.Update();
        await f.Session.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(f.Session.Controller.State == PortraitOwnedSceneControllerState.Off && f.Native.Runtime.Events.Count == 0,
            "unused cancellation never completed after the foreign Original returned");
    }
    private static async Task NaturalLoss()
    {
        using var f = new SceneFixture(); f.Start(); var image = new Wrap(805); f.Textures.Completion.SetResult(image);
        await Checks.Wait(() => f.Session.Images.InFlight == 0);
        using (var held = f.Session.Images.TryBorrow()) Check(held?.Texture == image, "fixture never published an image");
        f.Native.SlotOwner = 0x777; f.Native.Render(3);
        Check(f.Session.Controller.ImageOwner is null && f.Session.Images.TryBorrow() is null,
            "native ownership loss left current image authority alive");
        Check(f.Native.Runtime.Retirements == 0 && f.Native.SlotOwner == 0x777 && !f.Session.DrainPhysicalAsync().IsCompleted,
            "native ownership loss destroyed foreign slot or lost physical quarantine");
    }
    private static async Task UnusedSourceLoss()
    {
        using var f = new SceneFixture(); Check(f.Session.TryStart(f.Native.Snapshot(), 3), "request rejected");
        f.Native.SourceCurrent = false; f.Native.Render(1); f.Session.Update();
        await f.Session.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(f.Session.Controller.State == PortraitOwnedSceneControllerState.Off && f.Native.Runtime.Events.Count == 0,
            "never-created source loss retained a native scene or fabricated native retirement");
    }
    private static async Task ObserverInput()
    {
        using var f = new SceneFixture(); f.Start();
        var before = f.Native.Runtime.Events.Count;
        var side = new PortraitOwnedSceneObserverCameraSnapshot(new(1,0,0), default, MathF.PI/2, 0, 1);
        Check(f.Session.TrySetObserverCamera(side), "valid observer input was rejected");
        Check(f.Native.Runtime.Events.Count == before, "Framework observer input made a native call");
        var late = new Wrap(806); f.Textures.Completion.SetResult(late);
        await Checks.Wait(() => f.Session.Images.InFlight == 0);
        Check(late.Disposed && f.Session.Images.TryBorrow() is null && f.Textures.Input!.Disposed,
            "pending copy from the previous observer published after its camera changed");
        f.Session.Dispose(); f.Native.Render(3); f.Session.Update();
        await f.Session.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }
    private static async Task BrokerFactory()
    {
        using var f = new SceneFixture(true); f.Start();
        var image = new Wrap(807); f.Textures.Completion.SetResult(image);
        await Checks.Wait(() => f.Session.Images.InFlight == 0);
        using (var draw = f.Session.Images.TryBorrow()) Check(draw?.Texture == image, "factory broker never copied its ready owner");
        f.Session.Dispose(); f.Native.Render(3); f.Session.Update();
        await f.Session.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(f.Native.Runtime.Retirements == 1 && image.Disposed, "factory broker did not finish exact physical retirement");
    }
    private static async Task JointPendingFrame()
    {
        var textures = new TextureApi();
        using var adapter = new PortraitOwnedSceneRenderTargetAdapter(new JointRead(),textures,()=>true);
        Action<IPortraitOwnedSceneLease>? copy=null;
        using (var f=new OwnedExperimentBrokerCases.Fixture(true,()=>adapter.Images.InFlight==0 && !adapter.Images.HasCurrentFrame,
            lease=>copy!(lease)))
        {
            using var session=new PortraitOwnedSceneSession((after,_)=>{copy=after;return f.Broker;},()=>f.Runtime,adapter);
            f.Runtime.BeforeRender=adapter.RevokeResourceEpoch;
            Check(session.TryStart(f.SourceSnapshot,3),"joint pending fixture starts");
            f.Render(1);f.Render(2);
            Check(textures.Copies==1 && adapter.Images.InFlight==1,"ready joint callback did not start real RT adapter copy");
            var side=new PortraitOwnedSceneObserverCameraSnapshot(new(1,0,0),default,MathF.PI/2,0,1);
            Check(session.TrySetObserverCamera(side),"pending observer input was rejected");
            f.Render(3);
            var stale=new Wrap(808);textures.Completion.SetResult(stale);
            await Checks.Wait(()=>adapter.Images.InFlight==0);
            Check(stale.Disposed && adapter.Images.TryBorrow() is null && f.Runtime.Renders==1,
                "observer change during a pending provider must retire the old copy without another native Render");
            textures.Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Render(4);f.Render(5);
            var image=new Wrap(809);textures.Completion.SetResult(image);
            await Checks.Wait(()=>adapter.Images.InFlight==0);
            using(var draw=adapter.Images.TryBorrow())Check(draw?.Texture==image,
                "own Render on the next source frame starved the independent pending provider copy");
            f.Render(6);
            using(var draw=adapter.Images.TryBorrow())Check(draw?.Texture==image && f.Runtime.Renders==2,
                "published independent geometry was revoked before a UI frame could draw it");
            var back=new PortraitOwnedSceneObserverCameraSnapshot(new(0,0,-1),default,MathF.PI,0,1);
            Check(session.TrySetObserverCamera(back) && adapter.Images.TryBorrow() is null,
                "published image stayed current after a new observer input");
            textures.Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Render(7);var refreshed=new Wrap(810);textures.Completion.SetResult(refreshed);
            await Checks.Wait(()=>adapter.Images.InFlight==0);
            using(var draw=adapter.Images.TryBorrow())Check(draw?.Texture==refreshed && image.Disposed && f.Runtime.Renders==3,
                "observer input did not resume the real joint render/copy or release the replaced copied image");
            session.Dispose();f.Render(8);
            await adapter.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await f.Broker.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Check(refreshed.Disposed && textures.Input!.Disposed,"joint pending experiment did not drain retained provider ownership");
        }
    }
    private sealed class JointRead:IPortraitOwnedSceneRenderTargetReadApi
    {
        public bool TryRead(PortraitOwnedSceneImageOwner owner,IPortraitOwnedSceneLease lease,nint view,
            out PortraitOwnedSceneRenderTargetRead value)
        {
            var phase=new PortraitOwnedSceneSlotPhase(OwnedExperimentBrokerCases.Source,new(1,2,3,4,5),7,1,
                lease.NativeFrame,6,Environment.CurrentManagedThreadId,true,true);
            var com=new PortraitOwnedSceneRenderTargetComDescription(503,505,new(512,512,1,1,27,1,0,0,0x28,0,0),new(29,4,0,1));
            value=new(owner,new(view,402,7,403,404),phase,501,502,503,504,505,512,512,400,450,0x1450,0,1,1,com);
            return lease.IsCurrent&&lease.CanAccess(view);
        }
    }
    private sealed class SceneFixture : IDisposable
    {
        internal readonly BrokerNativeHost Native = new();
        internal readonly TextureApi Textures = new();
        internal readonly SceneRead Read;
        internal readonly PortraitOwnedSceneSession Session;
        internal SceneFixture(bool useFactory = false)
        {
            Read = new(Native);
            var adapter = new PortraitOwnedSceneRenderTargetAdapter(Read, Textures, () => true);
            Session = useFactory
                ? new((after, revoked) => Native.CreateBroker(after), () => Native.Runtime, adapter)
                : new(() => Native.Framework, Native.CreateEnvironment, Native.Factory, () => Native.Runtime, adapter);
        }
        internal void Start() { Check(Session.TryStart(Native.Snapshot(), 3), "request rejected"); Native.Render(1); Native.Render(2); }
        public void Dispose() { Session.Dispose(); Native.Dispose(); }
    }
    private sealed class SceneRead(BrokerNativeHost native) : IPortraitOwnedSceneRenderTargetReadApi
    {
        internal int Reads;
        public bool TryRead(PortraitOwnedSceneImageOwner owner, IPortraitOwnedSceneLease lease, nint view,
            out PortraitOwnedSceneRenderTargetRead value)
        {
            Reads++;
            if (native.Runtime.ReadDepth != 1 || !lease.IsCurrent || !lease.CanAccess(view))
                throw new Exception("owned native RT read escaped the actual ready lease");
            var phase = new PortraitOwnedSceneSlotPhase(BrokerNativeHost.Source, new(1,2,3,4,5), 2, 4,
                lease.NativeFrame, 6, Environment.CurrentManagedThreadId, true, true);
            var com = new PortraitOwnedSceneRenderTargetComDescription(503,505,
                new(512,512,1,1,27,1,0,0,0x28,0,0),new(29,4,0,1));
            value = new(owner,new(view,402,7,403,404),phase,501,502,503,504,505,512,512,400,450,0x1450,0,1,1,com);
            return true;
        }
    }
}
