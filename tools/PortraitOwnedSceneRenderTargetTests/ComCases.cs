using FireGaze.PortraitPreview;
using System.Runtime.InteropServices;

static class ComChecks
{
    static int count;
    static void Check(bool value,string name){count++;if(!value)throw new Exception(name);}
    internal static int Run()
    {
        var api=new FakeCom();var reader=Reader(api);var kernel=Kernel();
        Check(reader.TryRead(kernel,()=>true,out var descriptor),"COM SRV/resource/public-device canonical identity must be established");
        Check(descriptor.CanonicalResource==900&&descriptor.CanonicalDevice==901&&descriptor.Texture.Width==512
            &&descriptor.View.Format==29&&descriptor.IsValid,"checked native descriptor preserved");
        Check(api.Held==0&&api.MaxHeld<=14&&api.Releases==api.Acquisitions&&!api.Released.Contains(10),
            "temporary AddRefs release exactly once; borrowed UI device never released");
        Check(api.Released.SequenceEqual(api.Acquired.AsEnumerable().Reverse()),
            "each owned reference including canonical aliases releases once in reverse acquisition order");
        // Different typed pointers are intentionally accepted ONLY when their QI(IUnknown) agrees.
        foreach(var identity in new[]{(4,999),(6,902),(7,903)}) {
            var failed=new FakeCom{DifferentIdentity=identity};var probe=Reader(failed);
            Check(!probe.TryRead(kernel,()=>true,out _),"mismatched canonical resource or device must reject");
            Check(failed.Held==0&&failed.Acquisitions==failed.Releases,"identity mismatch releases all temporary refs");
        }
        for(var fail=1;fail<=16;fail++){
            var failed=new FakeCom{FailAt=fail};var probe=Reader(failed);
            Check(!probe.TryRead(kernel,()=>true,out _),"each COM failure rejects descriptor");
            Check(failed.Held==0&&failed.Releases==failed.Acquisitions&&failed.Calls==fail,
                "failed COM boundary stops immediately and releases only proven ownership");
        }
        for(var invalidate=1;invalidate<=16;invalidate++){
            var valid=true;var failed=new FakeCom();failed.OnCall=n=>{if(n==invalidate)valid=false;};
            var probe=Reader(failed);
            Check(!probe.TryRead(kernel,()=>valid,out _),"owner revocation at each COM boundary rejects");
            Check(failed.Held==0&&failed.Calls==invalidate,"revoked prefix has bounded cleanup and no more COM reads");
        }
        foreach(var mutate in new Func<PortraitOwnedD3D11Texture2DDesc,PortraitOwnedD3D11Texture2DDesc>[]{
            d=>d with{Width=511},d=>d with{Height=511},d=>d with{MipLevels=0},d=>d with{ArraySize=2},
            d=>d with{SampleCount=4},d=>d with{SampleQuality=1},d=>d with{BindFlags=8},d=>d with{Format=28}
        }){
            var failed=new FakeCom{TextureTransform=(_,d)=>mutate(d)};var probe=Reader(failed);
            Check(!probe.TryRead(kernel,()=>true,out _)&&failed.Held==0,"invalid or incompatible D3D resource descriptor rejected");
        }
        foreach(var mutate in new Func<PortraitOwnedD3D11SrvDesc,PortraitOwnedD3D11SrvDesc>[]{
            d=>d with{Format=91},d=>d with{Dimension=5},d=>d with{MostDetailedMip=1},d=>d with{MipLevels=2},d=>d with{MipLevels=0}
        }){
            var failed=new FakeCom{ViewTransform=(_,d)=>mutate(d)};var probe=Reader(failed);
            Check(!probe.TryRead(kernel,()=>true,out _)&&failed.Held==0,"invalid SRV view range/format rejected");
        }
        var changingTexture=new FakeCom{TextureTransform=(n,d)=>n==2?d with{Width=511}:d};
        Check(!Reader(changingTexture).TryRead(kernel,()=>true,out _)&&changingTexture.Held==0,"two texture descriptor reads must match");
        var changingView=new FakeCom{ViewTransform=(n,d)=>n==2?d with{Format=28}:d};
        Check(!Reader(changingView).TryRead(kernel,()=>true,out _)&&changingView.Held==0,"two SRV descriptor reads must match");
        var handleReads=0;var changingUi=new FakeCom();
        var changedUiReader=new PortraitOwnedSceneRenderTargetComReader(changingUi,()=>++handleReads==1?10:11,()=>true);
        Check(!changedUiReader.TryRead(kernel,()=>true,out _)&&changingUi.Held==0,"public UI backend handle replacement invalidates prefix");
        var denied=new FakeCom();
        Check(!new PortraitOwnedSceneRenderTargetComReader(denied,()=>throw new Exception("borrowed getter touched")).TryRead(kernel,()=>true,out _)
            &&denied.Calls==0,"default COM reader admission denies calls and UI getter");
        var nullUi=new FakeCom();
        Check(!new PortraitOwnedSceneRenderTargetComReader(nullUi,()=>0,()=>true).TryRead(kernel,()=>true,out _)&&nullUi.Held==0,
            "missing UI authority device fails closed");
        var releaseFailure=new FakeCom{ReleaseFailAt=1};var releaseReader=Reader(releaseFailure);
        Check(ThrowsCleanup(()=>releaseReader.TryRead(kernel,()=>true,out _))&&!releaseReader.IsPhysicallyClean,
            "uncertain Release never yields accepted descriptor");
        Check(!releaseReader.DrainPhysicalAsync().IsCompleted,"uncertain COM ref remains physically quarantined");
        // Specifically catches a return inside try that would bypass the post-finally failure check.
        var earlyReleaseFailure=new FakeCom{FailAt=7,ReleaseFailAt=1};var earlyReader=Reader(earlyReleaseFailure);
        Check(ThrowsCleanup(()=>earlyReader.TryRead(kernel,()=>true,out _))&&!earlyReader.IsPhysicallyClean,
            "early COM query failure must still propagate cleanup quarantine");
        foreach (var malformedBoundary in new[]{1,2,7}) {
            var malformed=new FakeCom{FailAt=malformedBoundary,MalformedFailure=true};var malformedReader=Reader(malformed);
            Check(ThrowsCleanup(()=>malformedReader.TryRead(kernel,()=>true,out _))
                &&!malformed.Released.Contains(888)&&!malformedReader.IsPhysicallyClean,
                "failed QI/GetResource/GetDevice with non-null pointer is ambiguous ownership and never Released");
            Check(!malformedReader.DrainPhysicalAsync().IsCompleted,
                "ambiguous returned reference prevents false physical drain completion");
        }
        var native=new DeniedNative();var concrete=new PortraitOwnedSceneRenderTargetComNativeApi(native);
        Check(concrete.QueryInterface(123,PortraitOwnedSceneRenderTargetComReader.Unknown,out _)<0
            &&!concrete.GetDevice(123,out _)&&!concrete.GetResource(123,out _)
            &&!concrete.GetTextureDesc(123,out _)&&!concrete.GetViewDesc(123,out _)&&!concrete.Release(123)
            &&native.Reads==0,"actual native COM binding default gate prevents every call; unowned Release refused");
        Check(PortraitOwnedSceneRenderTargetComNativeApi.LayoutMatches(),"actual managed COM descriptor ABI has exact installed sizes");
        var textureWords = Enumerable.Range(1,11).Select(n=>(uint)n).ToArray();
        var layoutTexture = MemoryMarshal.Read<PortraitOwnedD3D11Texture2DDesc>(MemoryMarshal.AsBytes(textureWords.AsSpan()));
        Check(layoutTexture == new PortraitOwnedD3D11Texture2DDesc(1,2,3,4,5,6,7,8,9,10,11),
            "native descriptor byte offsets map every Texture2D field including DXGI_SAMPLE_DESC");
        var viewWords = Enumerable.Range(1,6).Select(n=>(uint)n).ToArray();
        var layoutView = MemoryMarshal.Read<PortraitOwnedD3D11SrvDesc>(MemoryMarshal.AsBytes(viewWords.AsSpan()));
        Check(layoutView == new PortraitOwnedD3D11SrvDesc(1,2,3,4,5,6),
            "native descriptor byte offsets map SRV prefix and all four union words");
        Console.WriteLine("COM checks: "+count);return count;
    }
    static bool ThrowsCleanup(Action action){try{action();return false;}catch(PortraitCaptureCleanupException){return true;}}
    static PortraitOwnedSceneRenderTargetComReader Reader(FakeCom api)=>new(api,()=>10,()=>true);
    internal static PortraitOwnedSceneRenderTargetRead Kernel()
    {
        var owner=new PortraitOwnedSceneImageOwner(Guid.NewGuid(),1,Checks.Source,3,8);
        var phase=new PortraitOwnedSceneSlotPhase(Checks.Source,new(1,2,3,4,5),2,4,17,6,Environment.CurrentManagedThreadId,true,true);
        return new(owner,new(401,402,7,403,404),phase,501,1,4,504,505,512,512,400,450,0x1450,0,1,1);
    }
}

sealed class FakeCom : IPortraitOwnedSceneRenderTargetComApi
{
    internal int Held,MaxHeld,Releases,Acquisitions,Calls,FailAt,ReleaseFailAt,TextureReads,ViewReads;
    internal bool MalformedFailure;
    internal (int Source,int Identity)? DifferentIdentity;
    internal Action<int>? OnCall;
    internal Func<int,PortraitOwnedD3D11Texture2DDesc,PortraitOwnedD3D11Texture2DDesc>? TextureTransform;
    internal Func<int,PortraitOwnedD3D11SrvDesc,PortraitOwnedD3D11SrvDesc>? ViewTransform;
    internal List<nint> Released=[];
    internal List<nint> Acquired=[];
    readonly Dictionary<nint,int> owned=[];
    bool Hit(){Calls++;OnCall?.Invoke(Calls);return Calls!=FailAt;}
    int Add(nint value,out nint result){result=value;Acquired.Add(value);owned[value]=owned.GetValueOrDefault(value)+1;Acquisitions++;Held++;MaxHeld=Math.Max(MaxHeld,Held);return 0;}
    public int QueryInterface(nint source,Guid iid,out nint result)
    {
        if(!Hit()){result=MalformedFailure?888:0;return unchecked((int)0x80004002);}
        nint target=iid==PortraitOwnedSceneRenderTargetComReader.Unknown
            ? DifferentIdentity is {} changed&&source==changed.Source?changed.Identity
                :source is 3 or 4?900:source is 5 or 6 or 7?901:0
            :iid==PortraitOwnedSceneRenderTargetComReader.Srv&&source==1?2
            :iid==PortraitOwnedSceneRenderTargetComReader.Texture2D&&source is 3 or 4?source
            :iid==PortraitOwnedSceneRenderTargetComReader.Device&&source==10?7:0;
        if(target==0){result=0;return unchecked((int)0x80004002);}return Add(target,out result);
    }
    public bool GetResource(nint srv,out nint resource){
        if(!Hit()){resource=MalformedFailure?888:0;return false;}
        if(srv!=2)throw new Exception("GetResource requires QI-retained typed SRV");Add(3,out resource);return true;
    }
    public bool GetDevice(nint child,out nint device){
        if(!Hit()){device=MalformedFailure?888:0;return false;}
        if(child is not(2 or 3))throw new Exception("GetDevice requires owned typed object");Add(child==2?5:6,out device);return true;
    }
    public bool GetTextureDesc(nint texture,out PortraitOwnedD3D11Texture2DDesc descriptor){
        if(!Hit()){descriptor=default;return false;}
        if(texture!=3)throw new Exception("GetDesc must use SRV.GetResource queried texture");
        descriptor=new(512,512,1,1,27,1,0,0,0x28,0,0);
        TextureReads++;if(TextureTransform is not null)descriptor=TextureTransform(TextureReads,descriptor);return true;
    }
    public bool GetViewDesc(nint srv,out PortraitOwnedD3D11SrvDesc descriptor){
        if(!Hit()){descriptor=default;return false;}
        if(srv!=2)throw new Exception("GetDesc must use typed retained SRV");
        descriptor=new(29,4,0,1);ViewReads++;if(ViewTransform is not null)descriptor=ViewTransform(ViewReads,descriptor);return true;
    }
    public bool Release(nint value){
        Releases++;Released.Add(value);
        if(owned.GetValueOrDefault(value)<=0)throw new Exception("COM Release attempted without that owned AddRef");
        if(Releases==ReleaseFailAt)return false;
        owned[value]--;Held--;return true;
    }
}
