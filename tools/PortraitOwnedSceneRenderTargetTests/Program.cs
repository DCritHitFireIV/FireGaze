using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

var presentationResult = await OwnedBrokerPresentationCases.Run();
if(presentationResult != 0)return presentationResult;
await ViewportDiagnosticCases.Run();
await FeatureDisposeCases.Run();
return await Checks.Run();

static class Checks
{
    static int count;
    static void Check(bool value, string text) { count++; if (!value) throw new Exception(text); }
    internal static readonly PortraitIdentity Source = new(101, 102, 103, 9, 104);
    public static async Task<int> Run()
    {
        try {
            Check(PortraitOwnedSceneRenderTargetNativeApi.LayoutMatches(), "actual installed SDK layout must match copied descriptor fields");
            using var f = new Fixture();
            Check(f.Adapter.TryCopy(f.Controller, f.Lease), "ready source callback must start one retained provider copy");
            Check(f.Read.Calls >= 3 && f.Runtime.ReadDepth == 0 && f.Runtime.MaxReadDepth == 1,
                "descriptor reads occur inside exactly one ready callback");
            Check(f.Textures.Input is { Disposed: false } && f.Adapter.Images.InFlight == 1,
                "independent input survives asynchronous provider work");
            var copied = new Wrap(900);
            f.Textures.Completion.SetResult(copied);
            await Wait(() => f.Adapter.Images.InFlight == 0);
            using (var borrow = f.Adapter.Images.TryBorrow()) Check(borrow?.Texture == copied, "confirmed owned copy publishes");
            Check(f.Textures.Input!.Disposed, "retained input released only after provider completion");
            await f.Adapter.DrainPhysicalAsync();
            Check(copied.Disposed, "physical drain releases published copied image");
            count += ComChecks.Run();
            await Boundaries();
            Console.WriteLine($"PASS: {count} RT-adapter checks"); return 0;
        } catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }
    internal static async Task Wait(Func<bool> condition)
    {
        for (var i=0;i<200 && !condition();i++) await Task.Delay(2);
        Check(condition(), "bounded asynchronous completion");
    }

    static async Task Boundaries()
    {
        using (var f = new Fixture(false)) {
            Check(!f.Adapter.TryCopy(f.Controller,f.Lease) && f.Read.Calls == 0 && f.Textures.Copies == 0,
                "default admission denies all native/provider work");
        }
        using (var f = new Fixture()) {
            f.Lease.IsCurrent = false;
            Check(!f.Adapter.TryCopy(f.Controller,f.Lease) && f.Read.Calls == 0, "expired lease never reaches RT reader");
        }
        using (var f = new Fixture()) {
            f.Textures.OnRetain = () => f.Read.ResourceEpoch++;
            Check(!f.Adapter.TryCopy(f.Controller,f.Lease) && f.Textures.Copies == 0 && f.Textures.Input!.Disposed,
                "same-address resource epoch replacement during Retain releases input without copy");
        }
        foreach (var mutate in new Func<PortraitOwnedSceneRenderTargetRead,PortraitOwnedSceneRenderTargetRead>[] {
            x=>x with{Srv=777},x=>x with{Format=0x1451},x=>x with{ActualWidth=399},
            x=>x with{DeviceForwarder=778},x=>x with{KernelDevice=779},
            x=>x with{NativeOwner=x.NativeOwner with{CharacterGeneration=8}},
            x=>x with{NativeOwner=x.NativeOwner with{Context=780}},
            x=>x with{NativeOwner=x.NativeOwner with{Camera=781}}
        }) {
            using var f = new Fixture();
            f.Read.Transform = x => f.Read.Calls >= 2 ? mutate(x) : x;
            Check(!f.Adapter.TryCopy(f.Controller,f.Lease) && f.Textures.Copies == 0 && f.Textures.Input!.Disposed,
                "full owned token and RT descriptor are rechecked after independent Retain");
        }
        using (var f = new Fixture()) {
            f.Textures.OnCopy = () => f.Read.ResourceEpoch++;
            Check(!f.Adapter.TryCopy(f.Controller,f.Lease), "provider synchronous prefix resource change rejects publication");
            Check(f.Textures.Input is {Disposed:false} && f.Adapter.Images.InFlight==1,
                "rejected operation still retains input until real provider task finishes");
            var result = new Wrap(901); f.Textures.Completion.SetResult(result);
            await Wait(()=>f.Adapter.Images.InFlight==0);
            Check(result.Disposed && f.Textures.Input!.Disposed && f.Adapter.Images.TryBorrow() is null,
                "late rejected result and source release without publication");
            await f.Adapter.DrainPhysicalAsync();
        }
        using (var f = new Fixture()) {
            var result = new Wrap(902); f.Textures.Completion.SetResult(result);
            f.Textures.OnCopy=()=>f.Read.ResourceEpoch++;
            Check(!f.Adapter.TryCopy(f.Controller,f.Lease), "already-completed copy also waits final source confirmation");
            await Wait(()=>f.Adapter.Images.InFlight==0);
            Check(result.Disposed && f.Adapter.Images.TryBorrow() is null,"synchronous completed stale result never becomes current");
        }
        using (var f = new Fixture()) {
            f.Textures.OnCopy=()=>f.Controller.Stop("reentrant close");
            Check(!f.Adapter.TryCopy(f.Controller,f.Lease),"owner close inside provider prefix rejects copy");
            var result=new Wrap(903);f.Textures.Completion.SetResult(result);
            await Wait(()=>f.Adapter.Images.InFlight==0);
            Check(result.Disposed && f.Textures.Input!.Disposed,"reentrant close keeps physical provider lifetime");
        }
        using (var f = new Fixture()) {
            Check(f.Adapter.TryCopy(f.Controller,f.Lease),"start pending copy for close");
            Check(!f.Adapter.TryCopy(f.Controller,f.Lease)&&f.Textures.Retains==1,"one pending copy prevents another native acquire");
            var drain=f.Adapter.DrainPhysicalAsync();
            Check(!drain.IsCompleted && !f.Textures.Input!.Disposed,"drain cannot finish before real provider task");
            var result=new Wrap(904);f.Textures.Completion.SetResult(result);await drain;
            Check(result.Disposed&&f.Textures.Input!.Disposed,"close disposes late image and retained provider input");
        }
        using (var f = new Fixture()) {
            Check(f.Adapter.TryCopy(f.Controller,f.Lease),"start provider fault case");
            f.Textures.Completion.SetException(new IOException("copy failure"));
            await Wait(()=>f.Adapter.Images.InFlight==0);
            Check(f.Textures.Input!.Disposed&&f.Adapter.Images.TryBorrow() is null,"provider failure releases retained input");
            await f.Adapter.DrainPhysicalAsync();
        }
        using (var f = new Fixture()) {
            Check(f.Adapter.TryCopy(f.Controller,f.Lease),"start pending copy before resource ingress");
            f.Read.ResourceEpoch++;
            f.Adapter.RevokeResourceEpoch(); // Same active ingress must call this BEFORE Original.
            var result=new Wrap(906);f.Textures.Completion.SetResult(result);
            await Wait(()=>f.Adapter.Images.InFlight==0);
            Check(result.Disposed&&f.Adapter.Images.TryBorrow() is null,
                "resource ingress during pending official task invalidates late result without polling native");
            await f.Adapter.DrainPhysicalAsync();
        }
        using (var f = new Fixture()) {
            f.Textures.InputDisposeFailure=true;
            Check(f.Adapter.TryCopy(f.Controller,f.Lease),"start source cleanup failure case");
            var result=new Wrap(905);f.Textures.Completion.SetResult(result);
            await Wait(()=>f.Adapter.Images.InFlight==0);
            Check(result.Disposed&&!f.Adapter.IsPhysicallyClean,"failed input Dispose quarantines source and copied result");
            Check(!f.Adapter.DrainPhysicalAsync().IsCompleted,"uncertain source release prevents physical drain");
        }
        using (var f = new Fixture()) {
            // The real ready runtime catches callback exceptions and returns false.
            // COM quarantine must enter adapter ownership before that catch can hide it.
            var failed = new FakeCom { FailAt=7, ReleaseFailAt=1 };
            var comReader = new PortraitOwnedSceneRenderTargetComReader(failed,()=>10,()=>true);
            f.Runtime.SwallowReadExceptions=true;
            f.Read.BeforeRead=()=>comReader.TryRead(ComChecks.Kernel(),()=>true,out _);
            Check(!f.Adapter.TryCopy(f.Controller,f.Lease)&&f.Textures.Retains==0,
                "COM cleanup exception inside a swallowed runtime callback never retains image");
            Check(!f.Adapter.IsPhysicallyClean&&!f.Adapter.DrainPhysicalAsync().IsCompleted,
                "adapter owns COM quarantine even when actual runtime swallows the callback exception");
        }
        var native = new DeniedNative();
        var concrete = new PortraitOwnedSceneRenderTargetNativeApi(native,null!,null!);
        Check(!concrete.TryRead(null!,null!,0,out _)&&native.Reads==0,"actual concrete reader default denies before any native read");
    }
}

sealed class Fixture : IDisposable
{
    internal readonly Broker Broker = new();
    internal readonly Runtime Runtime = new();
    internal readonly Lease Lease = new();
    internal readonly PortraitOwnedSceneController Controller;
    internal readonly ReadApi Read;
    internal readonly TextureApi Textures = new();
    internal readonly PortraitOwnedSceneRenderTargetAdapter Adapter;
    internal Fixture(bool admitted = true)
    {
        Controller = new(Broker, () => Runtime);
        var snapshot = new PortraitOwnedCharaViewSnapshot(Checks.Source, default(CharaViewCharacterData),
            default(CharaViewModelData), new CharaViewItem[14], 0, new PortraitComposition());
        if (!Controller.TryStart(snapshot, 3)) throw new Exception("offline fixture controller start");
        Broker.Advance!(Lease); Broker.Advance(Lease);
        Read = new(Runtime);
        Adapter = admitted ? new(Read, Textures, () => true) : new(Read, Textures);
    }
    public void Dispose() { Adapter.Dispose(); Controller.Dispose(); }
}
sealed class Broker : IPortraitOwnedSceneControllerBroker
{
    public bool IsFrameworkThread => true;
    public bool ProductionAdmission => true; // Offline only; never passed to a native hook.
    internal Action<IPortraitOwnedSceneLease>? Advance;
    public bool TryRequest(PortraitIdentity source,uint slot,Action<IPortraitOwnedSceneLease> advance,Func<IPortraitOwnedSceneLease,bool> retire)
    { Advance = advance; return true; }
    public void Cancel(string reason) { }
}
sealed class Runtime : IPortraitOwnedSceneRuntime
{
    public bool IsReady => true;
    internal int ReadDepth,MaxReadDepth;
    internal bool SwallowReadExceptions;
    public bool TryCreate(PortraitOwnedCharaViewSnapshot snapshot,IPortraitOwnedSceneLease lease) => true;
    public bool Advance(IPortraitOwnedSceneLease lease,uint frame) => true;
    public bool TryRetire(IPortraitOwnedSceneLease lease) => true;
    public bool UseReadyView(IPortraitOwnedSceneLease lease,Action<nint> read)
    {
        if (ReadDepth != 0 || !lease.IsCurrent) return false;
        ReadDepth++; MaxReadDepth=Math.Max(MaxReadDepth,ReadDepth);
        try { read(401); return lease.IsCurrent; }
        catch (Exception) when (SwallowReadExceptions) { return false; }
        finally { ReadDepth--; }
    }
}
sealed class Lease : IPortraitOwnedSceneLease
{
    public uint Slot => 3;
    public PortraitIdentity Source => Checks.Source;
    public long Epoch => 8;
    public uint NativeFrame => 17;
    public bool IsCurrent {get;set;} = true;
    public bool CanCreate => false;
    public bool TryBindCreatedView(nint view) => false;
    public bool CanAccess(nint view) => IsCurrent && view == 401;
    public bool CanReleaseView(nint view) => false;
    public bool MarkReleased(nint view) => false;
    public IDisposable? EnterOwnedCall(nint view) => null;
}
sealed class ReadApi(Runtime runtime) : IPortraitOwnedSceneRenderTargetReadApi
{
    internal int Calls;
    internal Action? BeforeRead;
    internal Func<PortraitOwnedSceneRenderTargetRead,PortraitOwnedSceneRenderTargetRead>? Transform;
    internal long ResourceEpoch = 4;
    public bool TryRead(PortraitOwnedSceneImageOwner owner,IPortraitOwnedSceneLease lease,nint view,out PortraitOwnedSceneRenderTargetRead value)
    {
        Calls++;
        BeforeRead?.Invoke();
        if (runtime.ReadDepth != 1) throw new Exception("RT read escaped the synchronous ready callback");
        var phase = new PortraitOwnedSceneSlotPhase(Checks.Source,new(1,2,3,4,5),2,ResourceEpoch,17,6,
            Environment.CurrentManagedThreadId,true,true);
        var com = new PortraitOwnedSceneRenderTargetComDescription(503,505,new(512,512,1,1,27,1,0,0,0x28,0,0),new(29,4,0,1));
        value = new(owner,new(view,402,7,403,404),phase,501,502,503,504,505,512,512,400,450,0x1450,0,1,1,com);
        if(Transform is not null)value=Transform(value);
        return lease.IsCurrent;
    }
}
sealed class TextureApi : IPortraitOwnedSceneRenderTargetTextures
{
    internal TaskCompletionSource<IDalamudTextureWrap> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Wrap? Input;
    internal Action? OnRetain,OnCopy;
    internal int Copies;
    internal int Retains;
    internal bool InputDisposeFailure;
    public IDalamudTextureWrap Retain(PortraitOwnedSceneRenderTargetRead value) { Retains++; OnRetain?.Invoke(); return Input = new(502){ThrowDispose=InputDisposeFailure}; }
    public Task<IDalamudTextureWrap> Copy(IDalamudTextureWrap retained,TextureModificationArgs args)
    {
        Copies++; OnCopy?.Invoke();
        if (retained != Input || args.Uv1.X != 400f/512 || args.Uv1.Y != 450f/512)
            throw new Exception("provider input is not retained own-slot resource/crop");
        return Completion.Task;
    }
}
sealed class Wrap(nint id) : IDalamudTextureWrap
{
    public ImTextureID Handle => new(id);
    public int Width => 512;
    public int Height => 512;
    internal bool Disposed;
    internal bool ThrowDispose;
    public void Dispose() { if(ThrowDispose)throw new IOException("input release failed"); if (Disposed) throw new Exception("double release"); Disposed=true; }
}

sealed class DeniedNative : IPortraitOwnedSceneSlotNativeApi
{
    internal int Reads;
    public bool TryReadRoots(out PortraitOwnedSceneSlotRoots roots){Reads++;roots=default;return false;}
    public bool TryRead<T>(nint address,out T value)where T:unmanaged {Reads++;value=default;return false;}
}
