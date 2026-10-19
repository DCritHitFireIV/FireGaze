using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(sdk, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
#if !HAS_SOFTWARE_COMPOSITOR
Console.Error.WriteLine("FAIL: no CPU compositor exists; portrait composition still requires unsafe native draw-list rendering.");
return 1;
#else
return await Cases.Run();

static class Cases
{
    static int checks;
    static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        checks++; Console.WriteLine("PASS " + message);
    }
    static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, message); return; }
        throw new InvalidOperationException(message + " (did not throw " + typeof(T).Name + ")");
    }
    static void Pixels(FakeGpu gpu, byte[] expected, string message)
        => Check(gpu.UploadBytes is not null && gpu.UploadBytes.SequenceEqual(expected), message);
    static async Task Wait(Func<bool> predicate)
    {
        for (var i = 0; i < 3000 && !predicate(); i++) await Task.Delay(1);
        if (!predicate()) throw new InvalidOperationException("Boundary did not settle.");
    }
    public static async Task<int> Run()
    {
        // Pixel expectations are literal, hand-derived fixtures. Only the two public GPU service
        // boundaries are replaced; production models, geometry, ownership and compositor are linked.
        foreach (var (format, bytes, expected) in new[]
        {
            (28, new byte[]{20,80,240,255}, new byte[]{20,80,240,255}),
            (87, new byte[]{240,80,20,255}, new byte[]{20,80,240,255}),
            (29, new byte[]{128,0,255,255}, new byte[]{55,0,255,255}),
            (91, new byte[]{255,0,128,255}, new byte[]{55,0,255,255}),
        })
        {
            var gpu = new FakeGpu(); var source = new Texture(1, 1, bytes, format);
            using var output = await gpu.Compositor.ComposeAsync(1, 1, [new(source)]);
            Pixels(gpu, expected, $"DXGI {format} channel order and transfer function produce linear RGBA28");
            Check(gpu.UploadSpec.DxgiFormat == 28 && gpu.UploadSpec.Pitch == 4 && output.Width == 1 && output.Height == 1,
                "single opaque layer is uploaded with the requested output dimensions and explicit RGBA28 pitch");
            Check(source.Disposes == 0 && source.Shares.Count == 1 && source.Shares[0].Disposes == 1,
                "successful composition releases its independent reference and leaves caller texture open");
        }
        {
            var gpu = new FakeGpu(); var source = new Texture(1, 2, [255,0,0,255,99,99,99,99,0,0,255,255], pitch:8);
            using var output = await gpu.Compositor.ComposeAsync(1, 2, [new(source)]);
            Pixels(gpu, [255,0,0,255,0,0,255,255], "row pitch skips padding and accepts a final row without unused trailing padding");
        }
        {
            var gpu = new FakeGpu(); var source = new Texture(2, 2, [255,0,0,255,0,255,0,255,0,0,255,255,255,255,255,255]);
            using var output = await gpu.Compositor.ComposeAsync(1, 1, [new(source)]);
            Pixels(gpu, [128,128,128,255], "bilinear filtering averages four neighbors at the image center");
            Check(gpu.ReadArguments.All(a => a.DxgiFormat == 28 && a.Uv0 == Vector2.Zero && a.Uv1 == Vector2.One && a.NewWidth == 0 && a.NewHeight == 0),
                "public readback requests official RGBA28 normalization with full source UV and unchanged dimensions");
        }
        {
            var gpu = new FakeGpu(); var source = new Texture(2, 1, [255,0,0,0,0,0,255,255]);
            using var output = await gpu.Compositor.ComposeAsync(1, 1, [new(source)]);
            Pixels(gpu, [0,0,255,128], "bilinear filtering uses premultiplied linear color so transparent red cannot pollute blue edges");
        }
        {
            var gpu = new FakeGpu(); var source = new Texture(4, 1, [255,0,0,255,0,255,0,255,0,0,255,255,255,255,255,255]);
            using var output = await gpu.Compositor.ComposeAsync(2, 1, [new(source, UV0:new(.25f,0), UV1:new(.75f,1))]);
            Pixels(gpu, [0,255,0,255,0,0,255,255], "UV crop selects the middle two texels without rescaling the readback");
        }
        {
            var gpu = new FakeGpu(); var source = new Texture(2, 1, [255,0,0,255,0,0,255,255]);
            using var output = await gpu.Compositor.ComposeAsync(2, 1, [new(source, UV0:Vector2.One, UV1:Vector2.Zero)]);
            Pixels(gpu, [0,0,255,255,255,0,0,255], "reversed UV endpoints flip the image");
        }
        {
            var gpu = new FakeGpu(); var source = new Texture(4, 1, [255,0,0,255,0,255,0,255,0,0,255,255,255,255,255,255]);
            using var output = await gpu.Compositor.ComposeAsync(4, 1, [new(source,new(-.5f,0,1,1))]);
            Pixels(gpu, [0,0,255,255,255,255,255,255,0,0,0,0,0,0,0,0], "clipping preserves original layer UV mapping instead of stretching the clipped image");
        }
        {
            var gpu = new FakeGpu(); var source = new Texture(1, 1, [255,0,0,255]);
            using var output = await gpu.Compositor.ComposeAsync(4, 1, [new(source,new(.25f,0,.5f,1))]);
            Pixels(gpu, [0,0,0,0,255,0,0,255,255,0,0,255,0,0,0,0], "normalized placement changes only covered pixel centers");
        }
        {
            var gpu = new FakeGpu(); var blue = new Texture(1,1,[0,0,255,255]); var red = new Texture(1,1,[255,0,0,128]);
            using var output = await gpu.Compositor.ComposeAsync(1,1,[new(blue),new(red)]);
            Pixels(gpu, [128,0,127,255], "ordered source-over blends half-alpha red over opaque blue");
            Check(gpu.MaximumActiveReads == 1 && gpu.ReadCalls == 2, "source layers are read sequentially rather than allocating simultaneous raw images");
        }
        {
            var gpu = new FakeGpu(); var source = new Texture(1,1,[200,100,50,255]);
            using var output = await gpu.Compositor.ComposeAsync(1,1,[new(source,Tint:0x8080FFFF)]);
            Pixels(gpu, [200,100,25,128], "ImGui ABGR tint multiplies RGB and opacity while output remains straight alpha");
        }
        {
            var gpu = new FakeGpu(); var source = new Texture(1,1,[200,100,50,0]);
            using var output = await gpu.Compositor.ComposeAsync(1,1,[new(source)]);
            Pixels(gpu, [0,0,0,0], "fully transparent output has zero RGB and no hidden source color");
        }
        {
            var gpu = new FakeGpu();
            using var output = await gpu.Compositor.ComposeAsync(2,1,[]);
            Pixels(gpu, [0,0,0,0,0,0,0,0], "an empty layer set produces an owned transparent canvas");
        }
        foreach (var size in new[]{(0,1),(-1,1),(1,0),(1025,1024),(int.MaxValue,2)})
        {
            var gpu = new FakeGpu();var source = new Texture(1,1,[0,0,0,255]);
            await Throws<ArgumentException>(() => gpu.Compositor.ComposeAsync(size.Item1,size.Item2,[new(source)]), "invalid or over-budget output dimensions fail before texture acquisition");
            Check(gpu.ReadCalls == 0 && gpu.UploadCalls == 0 && source.Shares.Count == 0,"invalid output cannot reach a GPU boundary or retain references");
        }
        foreach (var source in new[]{new Texture(0,1,[]),new Texture(4097,4096,[]),new Texture(20000,1,[])})
        {
            var gpu=new FakeGpu();
            await Throws<ArgumentException>(()=>gpu.Compositor.ComposeAsync(1,1,[new(source)]),"invalid or over-16M source dimensions fail before GPU readback");
            Check(gpu.ReadCalls==0&&gpu.UploadCalls==0,"over-budget source never reaches either GPU boundary");
        }
        foreach(var layer in new[]{new PortraitCaptureLayer(new Texture(1,1,[0,0,0,255]),new(float.NaN,0,1,1)),
            new(new Texture(1,1,[0,0,0,255]),new(0,0,0,1)),new(new Texture(1,1,[0,0,0,255]),UV0:new(float.PositiveInfinity,0))})
        {
            var gpu=new FakeGpu();
            await Throws<ArgumentException>(()=>gpu.Compositor.ComposeAsync(1,1,[layer]),"nonfinite or degenerate layer mapping is rejected");
            Check(gpu.ReadCalls==0&&gpu.UploadCalls==0,"invalid layer geometry cannot invoke GPU work");
        }
        foreach(var source in new[]{new Texture(2,1,[1,2,3,4,5,6,7],pitch:8),new Texture(2,1,[1,2,3,4,5,6,7,8],pitch:4),new Texture(1,1,[1,2,3,4],format:10)})
        {
            var gpu=new FakeGpu();
            await Throws<InvalidOperationException>(()=>gpu.Compositor.ComposeAsync(1,1,[new(source)]),"truncated, invalid-pitch or unsupported raw pixels cannot be uploaded");
            Check(gpu.UploadCalls==0&&source.Shares.Single().Disposes==1,"malformed readback releases retained source exactly once");
        }
        {
            var gpu=new FakeGpu();var source=new Texture(4096,4096,new byte[4096*4096*4]);
            using var output=await gpu.Compositor.ComposeAsync(1,1,[new(source)]);
            Pixels(gpu,[0,0,0,0],"exactly 16M source pixels are accepted without allocating a decoded full-size float image");
        }
        {
            var gpu=new FakeGpu();using var output=await gpu.Compositor.ComposeAsync(1024,1024,[]);
            Check(gpu.UploadBytes?.Length==4*1024*1024,"exactly 1M output pixels fit the bounded canvas");
        }
        {
            var gpu=new FakeGpu();var source=new Texture(1,1,[10,20,30,128]);
            using var layers=new PortraitCaptureLayers(1,1,[new(source),new(source)]);
            using var output=await gpu.Compositor.ComposeAsync(layers.Width,layers.Height,layers.Layers);
            Check(source.Shares.Count==2&&source.Shares.All(s=>s.Disposes==1)&&source.Disposes==0,"duplicate input textures acquire independent layer references without double-disposing caller ownership");
            layers.Dispose();layers.Dispose();Check(source.Disposes==1,"real CaptureLayers disposal remains independent and idempotent");
        }
        {
            var gpu=new FakeGpu();var one=new Texture(1,1,[1,2,3,255]);var two=new Texture(1,1,[4,5,6,255]){FailShare=true};
            await Throws<InvalidOperationException>(()=>gpu.Compositor.ComposeAsync(1,1,[new(one),new(two)]),"partial synchronous retain failure faults composition");
            Check(one.Shares.Single().Disposes==1&&one.Disposes==0&&gpu.ReadCalls==0,"retain failure unwinds earlier shares before any asynchronous GPU work");
        }
        {
            var gpu=new FakeGpu{ReadFailure=new InvalidOperationException("GPU read failed")};var source=new Texture(1,1,[1,2,3,255]);
            await Throws<InvalidOperationException>(()=>gpu.Compositor.ComposeAsync(1,1,[new(source)]),"readback failure propagates");
            Check(source.Shares.Single().Disposes==1&&gpu.UploadCalls==0,"readback failure releases its owned reference without upload");
        }
        {
            var gpu=new FakeGpu{UploadFailure=new InvalidOperationException("GPU upload failed")};var source=new Texture(1,1,[1,2,3,255]);
            await Throws<InvalidOperationException>(()=>gpu.Compositor.ComposeAsync(1,1,[new(source)]),"upload failure propagates");
            Check(source.Shares.Single().Disposes==1&&source.Disposes==0,"upload failure releases sources without touching caller ownership");
        }
        {
            var gpu=new FakeGpu{PendingRead=new(TaskCreationOptions.RunContinuationsAsynchronously)};
            var one=new Texture(1,1,[1,2,3,255]);var two=new Texture(1,1,[4,5,6,255]);
            var task=gpu.Compositor.ComposeAsync(1,1,[new(one),new(two)]);
            Check(one.Shares.Count==1&&two.Shares.Count==1,"all layer references are acquired synchronously before the first awaited read");
            one.Dispose();two.Dispose();gpu.PendingRead.SetResult((new(1,1,28,4),new byte[]{1,2,3,255}));
            using var output=await task;
            Check(one.Shares.Single().Disposes==1&&two.Shares.Single().Disposes==1,"caller may immediately dispose originals while asynchronous composition still completes safely");
        }
        {
            using var token=new CancellationTokenSource();token.Cancel();var gpu=new FakeGpu();var source=new Texture(1,1,[1,2,3,255]);
            await Throws<OperationCanceledException>(()=>gpu.Compositor.ComposeAsync(1,1,[new(source)],token.Token),"pre-cancelled request never starts work");
            Check(source.Shares.Count==0&&gpu.ReadCalls==0&&gpu.UploadCalls==0,"pre-cancellation creates neither ownership nor GPU work");
        }
        {
            using var token=new CancellationTokenSource();var gpu=new FakeGpu{PendingRead=new(TaskCreationOptions.RunContinuationsAsynchronously)};
            var source=new Texture(1,1,[1,2,3,255]);var task=gpu.Compositor.ComposeAsync(1,1,[new(source)],token.Token);
            await Wait(()=>gpu.ReadCalls==1);token.Cancel();source.Dispose();await Task.Delay(15);
            Check(!task.IsCompleted&&source.Shares.Single().Disposes==0,"cancel cannot release a texture still used by a late physical read");
            gpu.PendingRead.SetResult((new(1,1,28,4),new byte[]{1,2,3,255}));
            await Throws<OperationCanceledException>(async()=>await task,"late successful read observes cancellation before upload");
            Check(gpu.UploadCalls==0&&source.Shares.Single().Disposes==1,"cancelled late read releases owned source once and never publishes");
        }
        {
            using var token=new CancellationTokenSource();var gpu=new FakeGpu{PendingUpload=new(TaskCreationOptions.RunContinuationsAsynchronously)};
            var source=new Texture(1,1,[1,2,3,255]);var task=gpu.Compositor.ComposeAsync(1,1,[new(source)],token.Token);
            await Wait(()=>gpu.UploadCalls==1);token.Cancel();await Task.Delay(15);
            Check(!task.IsCompleted&&source.Shares.Single().Disposes==0,"upload cancellation awaits late physical completion and retains its sources");
            var late=new Texture(1,1,[1,2,3,255]);gpu.PendingUpload.SetResult(late);
            await Throws<OperationCanceledException>(async()=>await task,"late upload cannot publish after cancellation");
            Check(late.Disposes==1&&source.Shares.Single().Disposes==1,"late uploaded texture and retained input are each disposed exactly once");
        }
        {
            var uploaded=new Texture(1,1,[1,2,3,255]);var gpu=new FakeGpu{UploadResult=uploaded};
            var one=new Texture(1,1,[1,2,3,255]){SharedDisposeFailure=new InvalidOperationException("release failed")};
            var two=new Texture(1,1,[4,5,6,255]);
            await Throws<InvalidOperationException>(()=>gpu.Compositor.ComposeAsync(1,1,[new(one),new(two)]),"source release failure faults successful composition");
            Check(uploaded.Disposes==1&&one.Shares.Single().Disposes==1&&two.Shares.Single().Disposes==1,
                "failed source release still disposes the unpublished uploaded output and every retained reference");
        }
        {
            var gpu=new FakeGpu{ReadFailure=new ArgumentException("original read failure")};
            var one=new Texture(1,1,[1,2,3,255]){SharedDisposeFailure=new InvalidOperationException("release failed")};
            var two=new Texture(1,1,[4,5,6,255]);
            await Throws<ArgumentException>(()=>gpu.Compositor.ComposeAsync(1,1,[new(one),new(two)]),"cleanup failure preserves the original readback failure");
            Check(one.Shares.Single().Disposes==1&&two.Shares.Single().Disposes==1,"original failure cleanup still attempts every retained texture");
        }
        {
            var gpu=new FakeGpu();var one=new Texture(1,1,[1,2,3,255]){SharedDisposeFailure=new ArgumentException("release failed")};
            var two=new Texture(1,1,[4,5,6,255]){FailShare=true};
            await Throws<InvalidOperationException>(()=>gpu.Compositor.ComposeAsync(1,1,[new(one),new(two)]),"partial retain cleanup preserves the original share failure");
        }
        {
            var gpu=new FakeGpu();var source=new Texture(1,1,[1,2,3,255]);
            await Throws<ArgumentException>(()=>gpu.Compositor.ComposeAsync(1,1,[new(source),new(source),new(source),new(source)]),
                "more than the portrait, frame and accent layer budget is rejected");
            Check(source.Shares.Count==0&&gpu.ReadCalls==0&&gpu.UploadCalls==0,"layer budget is checked before resource acquisition or GPU work");
        }
        foreach(var size in new[]{(2,1),(1,2),(0,1)})
        {
            var uploaded=new Texture(size.Item1,size.Item2,[]);var gpu=new FakeGpu{UploadResult=uploaded};
            var source=new Texture(1,1,[1,2,3,255]);
            await Throws<InvalidOperationException>(()=>gpu.Compositor.ComposeAsync(1,1,[new(source)]),"unexpected upload dimensions cannot be published");
            Check(uploaded.Disposes==1&&source.Shares.Single().Disposes==1,"invalid uploaded dimensions dispose both output and retained input");
        }
        {
            var gpu=new FakeGpu{ReadFailure=new ArgumentException("second read failed"),ReadFailureAt=2};
            var one=new Texture(1,1,[1,2,3,255]);var two=new Texture(1,1,[4,5,6,255]);
            await Throws<ArgumentException>(()=>gpu.Compositor.ComposeAsync(1,1,[new(one),new(two)]),"a later layer readback failure propagates after prior CPU composition");
            Check(gpu.ReadCalls==2&&gpu.UploadCalls==0&&one.Shares.Single().Disposes==1&&two.Shares.Single().Disposes==1,
                "later readback failure releases all retained layers without publishing a partial composition");
        }
        {
            var normalized=Enumerable.Range(0,16).SelectMany(_=>new byte[]{20,40,80,255}).ToArray();
            var gpu=new FakeGpu{ReadResultOverride=(new(4,4,28,16),normalized)};
            var compressed=new Texture(4,4,new byte[8],format:71,pitch:8);
            using var output=await gpu.Compositor.ComposeAsync(1,1,[new(compressed)]);
            Pixels(gpu,[20,40,80,255],"compressed source composition consumes the official provider's normalized RGBA pixels");
            Check(gpu.ReadArguments.Single().DxgiFormat==28&&compressed.Shares.Single().Disposes==1,
                "compressed sources request official format conversion and retain ownership through composition");
        }
        Console.WriteLine($"PortraitSoftwareCompositionTests: {checks} passed, 0 failed");return 0;
    }
}

sealed class FakeGpu
{
    bool pendingReadUsed;
    public int ReadCalls,UploadCalls,ActiveReads,MaximumActiveReads;
    public byte[]? UploadBytes;
    public RawImageSpecification UploadSpec;
    public List<TextureModificationArgs> ReadArguments=[];
    public Exception? ReadFailure,UploadFailure;
    public int ReadFailureAt=1;
    public (RawImageSpecification Specification,byte[] RawData)? ReadResultOverride;
    public IDalamudTextureWrap? UploadResult;
    public TaskCompletionSource<(RawImageSpecification Specification,byte[] RawData)>? PendingRead;
    public TaskCompletionSource<IDalamudTextureWrap>? PendingUpload;
    public PortraitCaptureSoftwareCompositor Compositor {get;}
    public FakeGpu()
    {
        var read=Proxy<ITextureReadbackProvider>((method,args)=>
        {
            if(method.Name!=nameof(ITextureReadbackProvider.GetRawImageAsync))throw new InvalidOperationException("Forbidden readback service call: "+method.Name);
            if(args is null||args[0] is not Texture texture||texture.Disposes!=0||texture.Parent is null||args[2] is not true)
                throw new InvalidOperationException("Readback must use an owned, undisposed independent reference with leaveWrapOpen=true.");
            ReadArguments.Add((TextureModificationArgs)args[1]!);
            ReadCalls++;ActiveReads++;MaximumActiveReads=Math.Max(MaximumActiveReads,ActiveReads);
            if(ReadFailure is not null&&ReadCalls==ReadFailureAt){ActiveReads--;return Task.FromException<(RawImageSpecification,byte[])>(ReadFailure);}
            if(PendingRead is { } pending && !pendingReadUsed){pendingReadUsed=true;return AwaitRead(pending.Task);}
            ActiveReads--;return Task.FromResult(ReadResultOverride??(texture.Specification,texture.Bytes));
        });
        var textures=Proxy<ITextureProvider>((method,args)=>
        {
            if(method.Name!=nameof(ITextureProvider.CreateFromRawAsync)||args is null||args.Length!=4||args[1] is not ReadOnlyMemory<byte> bytes)
                throw new InvalidOperationException("Forbidden texture service call: "+method.Name);
            UploadCalls++;UploadSpec=(RawImageSpecification)args[0]!;UploadBytes=bytes.ToArray();
            if(UploadFailure is not null)return Task.FromException<IDalamudTextureWrap>(UploadFailure);
            return PendingUpload?.Task??Task.FromResult(UploadResult??new Texture(UploadSpec.Width,UploadSpec.Height,UploadBytes));
        });
        Compositor=new(textures,read);
    }
    async Task<(RawImageSpecification,byte[])> AwaitRead(Task<(RawImageSpecification,byte[])> pending)
    {try{return await pending;}finally{ActiveReads--;}}
    static T Proxy<T>(Func<MethodInfo,object?[]?,object?> invoke) where T:class
    {var proxy=DispatchProxy.Create<T,BoundaryProxy>();((BoundaryProxy)(object)proxy).InvokeBoundary=invoke;return proxy;}
}
public class BoundaryProxy:DispatchProxy
{
    public Func<MethodInfo,object?[]?,object?> InvokeBoundary=null!;
    protected override object? Invoke(MethodInfo? targetMethod,object?[]? args)=>InvokeBoundary(targetMethod!,args);
}
sealed class Texture(int width,int height,byte[] bytes,int format=28,int? pitch=null):IDalamudTextureWrap
{
    public int Width=>width;public int Height=>height;
    public byte[] Bytes=>bytes;
    public RawImageSpecification Specification=>new(width,height,format,pitch??Math.Max(0,width*4));
    public ImTextureID Handle=>throw new InvalidOperationException("CPU compositor must never inspect an ImGui texture handle.");
    public int Disposes;
    public bool FailShare;
    public Exception? DisposeFailure,SharedDisposeFailure;
    public Texture? Parent;
    public List<Texture> Shares=[];
    public IDalamudTextureWrap CreateWrapSharingLowLevelResource()
    {if(FailShare)throw new InvalidOperationException("Share failed");if(Disposes!=0)throw new ObjectDisposedException(nameof(Texture));var shared=new Texture(width,height,bytes,format,pitch){Parent=this,DisposeFailure=SharedDisposeFailure};Shares.Add(shared);return shared;}
    public void Dispose(){Interlocked.Increment(ref Disposes);if(DisposeFailure is {} error)throw error;}
}
#endif
