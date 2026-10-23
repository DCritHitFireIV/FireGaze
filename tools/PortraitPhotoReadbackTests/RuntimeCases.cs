using System.Reflection;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.PortraitPreview;

static class RuntimeCases
{
    public static async Task Run()
    {
#if !HAS_PHOTO_RUNTIME
        throw new InvalidOperationException("FAIL: native photo pump must survive feature close until physical completion.");
#else
        var checks=0;
        void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);checks++;}
        var ui=DispatchProxy.Create<IUiBuilder,DrawHost>();
        var host=(DrawHost)(object)ui;
        var backend=new Backend();
        var runtime=new PortraitPhotoReadbackRuntime(ui,backend);
        var wrap=DispatchProxy.Create<IDalamudTextureWrap,UnusedWrap>();
        var early=runtime.Provider.GetRawImageAsync(wrap,leaveWrapOpen:true);
        try{await early;}catch(InvalidOperationException){}
        Check(early.IsFaulted&&backend.Retained==0,"before first Draw acquisition refuses without retaining unavailable device");
        host.Draw!();
        var pending=runtime.Provider.GetRawImageAsync(wrap,leaveWrapOpen:true);
        Check(!pending.IsCompleted&&backend.Retained==1,"ready runtime retains input on caller");
        host.Draw!();
        Check(backend.Submits==1&&backend.Maps==1&&!pending.IsCompleted,"one Draw makes one nonblocking attempt");
        var close=runtime.Close();
        Check(!close.IsCompleted&&host.Draw is not null,"closing feature preserves independent Draw handler for retirement");
        host.Draw!();
        Check(!close.IsCompleted&&backend.Releases==0,"uncompleted GPU cannot release source or claim close");
        backend.Ready=true;host.Draw!();await close;
        try{await pending;}catch(OperationCanceledException){}
        Check(pending.IsCanceled&&backend.Releases==1&&host.Draw is null&&host.Resize is null,
            "physical retirement revokes publication and removes callbacks only after complete");
        var resizeUI=DispatchProxy.Create<IUiBuilder,DrawHost>();var resizeHost=(DrawHost)(object)resizeUI;
        var resizeBackend=new Backend();var resized=new PortraitPhotoReadbackRuntime(resizeUI,resizeBackend);
        resizeHost.Draw!();var old=resized.Provider.GetRawImageAsync(wrap,leaveWrapOpen:true);resizeHost.Draw!();
        resizeHost.Resize!();resizeHost.Draw!();
        Check(!old.IsCompleted&&resizeBackend.Releases==0,"resize preserves retiring old generation");
        resizeBackend.Ready=true;resizeHost.Draw!();
        try{await old;}catch(InvalidOperationException){}catch(OperationCanceledException){}
        Check(!old.IsCompletedSuccessfully&&resizeBackend.Releases==1,"resize cannot publish old-generation pixels");
        var fresh=resized.Provider.GetRawImageAsync(wrap,leaveWrapOpen:true);resizeHost.Draw!();
        Check((await fresh).RawData.SequenceEqual(new byte[]{1,2,3,255}),"new generation recovers after exact old physical drain");
        await resized.Close();
        Check(resizeHost.Draw is null,"idle close removes callbacks immediately");
        Console.WriteLine($"PASS: {checks} photo runtime lifecycle checks");
#endif
    }
    private sealed class Backend:IPortraitPixelReadbackDrawBackend
    {
        public PortraitPixelReadbackIdentity Identity{get;private set;}=new(1,1);
        public int Retained,Releases,Submits,Maps;public bool Ready=false;private bool inDraw;
        public IPortraitPixelReadbackSource CreateSource(IDalamudTextureWrap wrap)=>new Source(this);
        public IDisposable EnterDraw(){inDraw=true;return new Scope(()=>inDraw=false);}
        public void Invalidate()=>Identity=new(1,Identity.Generation+1);
        public IPortraitPixelReadbackTransfer CreateTransfer(IPortraitPixelReadbackLease lease)
        {if(!inDraw)throw new InvalidOperationException("native outside Draw");return new Transfer(this);}
        private sealed class Source(Backend owner):IPortraitPixelReadbackSource
        {public IPortraitPixelReadbackLease Retain(){owner.Retained++;return new Lease(owner,owner.Identity);}}
        private sealed class Lease(Backend owner,PortraitPixelReadbackIdentity identity):IPortraitPixelReadbackLease
        {public PortraitPixelReadbackIdentity Identity=>identity;public int Width=>1;public int Height=>1;public int DxgiFormat=>28;public void Dispose()=>owner.Releases++;}
        private sealed class Transfer(Backend owner):IPortraitPixelReadbackTransfer
        {
            public void Submit()=>owner.Submits++;
            public PortraitPixelReadbackMapStatus TryMap(out IPortraitPixelReadbackMapping? mapping)
            {owner.Maps++;mapping=owner.Ready?new Mapping():null;return owner.Ready?PortraitPixelReadbackMapStatus.Ready:PortraitPixelReadbackMapStatus.StillDrawing;}
            public void Unmap(){}
            public bool TryRetire()=>owner.Ready;
        }
        private sealed class Mapping:IPortraitPixelReadbackMapping
        {public int RowPitch=>4;public int DepthPitch=>0;public void CopyBytes(int offset,Span<byte> destination)=>new byte[]{1,2,3,255}.AsSpan().CopyTo(destination);}
        private sealed class Scope(Action close):IDisposable{public void Dispose()=>close();}
    }
}
public class DrawHost:DispatchProxy
{
    public Action? Draw,Resize;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        switch(method?.Name)
        {
            case "add_Draw":Draw+=(Action)args![0]!;return null;
            case "remove_Draw":Draw-=(Action)args![0]!;return null;
            case "add_ResizeBuffers":Resize+=(Action)args![0]!;return null;
            case "remove_ResizeBuffers":Resize-=(Action)args![0]!;return null;
            default:throw new InvalidOperationException("unexpected UI call: "+method?.Name);
        }
    }
}
public class UnusedWrap:DispatchProxy
{protected override object? Invoke(MethodInfo? method,object?[]? args)=>throw new InvalidOperationException("fake backend owns input retention");}
