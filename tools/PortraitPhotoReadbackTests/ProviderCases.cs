using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Bindings.ImGui;
#if HAS_PHOTO_PROVIDER
using FireGaze.PortraitPreview;
#endif

static class ProviderCases
{
    public static async Task Run()
    {
#if !HAS_PHOTO_PROVIDER
        throw new InvalidOperationException("FAIL: production photo provider must route raw and PNG reads through nonblocking pixel acquisition.");
#else
        var checks=0;
        void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message);checks++; }
        var calls=0;
        var pending=new TaskCompletionSource<(RawImageSpecification,byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider=new PortraitPhotoReadbackProvider((wrap,token)=> {calls++;return pending.Task;});
        var source=new Texture();
        var read=provider.GetRawImageAsync(source,leaveWrapOpen:true);
        Check(calls==1&&!read.IsCompleted&&source.Disposals==0,"raw acquisition retains synchronously and never blocks caller");
        pending.SetResult((new(1,1,28,4),new byte[]{10,20,30,255}));
        Check((await read).RawData.SequenceEqual(new byte[]{10,20,30,255})&&source.Disposals==0,"raw read preserves detached pixels and caller ownership");
        using var pngStream=new MemoryStream();
        await provider.SaveToStreamAsync(source,new("1b7cfaf4-713f-473c-bbcd-6137425faeaf"),pngStream,
            leaveWrapOpen:true,leaveStreamOpen:true);
        Check(calls==2&&pngStream.Length>40&&pngStream.CanWrite&&source.Disposals==0,"PNG uses same bounded acquisition plus CPU encoder with explicit ownership");
        await provider.SaveToStreamAsync(source,new("1b7cfaf4-713f-473c-bbcd-6137425faeaf"),new MemoryStream());
        Check(source.Disposals==1,"completed PNG disposes transferred wrapper exactly once");
        var invalid=new Texture();var refused=false;
        try {await provider.GetRawImageAsync(invalid,new TextureModificationArgs{Uv1=new Vector2(.5f,1)},true);} catch(NotSupportedException) {refused=true;}
        Check(refused&&calls==3&&invalid.Disposals==0,"unsupported crop cannot fall back to blocking SDK path");
        var failed=new Texture();
        var broken=new PortraitPhotoReadbackProvider((wrap,token)=>Task.FromException<(RawImageSpecification,byte[])>(new IOException("probe")));
        using var failureStream=new MemoryStream();
        try {await broken.SaveToStreamAsync(failed,new("1b7cfaf4-713f-473c-bbcd-6137425faeaf"),failureStream);}catch(IOException){}
        Check(failed.Disposals==1&&!failureStream.CanWrite,"acquisition failure preserves stream/wrapper ownership contract");
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        var cancellationSource=new Texture();var cancelled=false;
        try {await provider.GetRawImageAsync(cancellationSource,leaveWrapOpen:true,cancellationToken:canceled.Token);}catch(OperationCanceledException){cancelled=true;}
        Check(cancelled&&calls==3&&cancellationSource.Disposals==0,"canceled work does not acquire GPU input");
        Console.WriteLine($"PASS: {checks} production photo-provider checks");
#endif
    }
    private sealed class Texture:IDalamudTextureWrap
    {
        public int Width=>1;public int Height=>1; public int Disposals;
        public ImTextureID Handle=>throw new InvalidOperationException("provider must delegate texture ownership to readback backend");
        public IDalamudTextureWrap CreateWrapSharingLowLevelResource()=>throw new InvalidOperationException("backend owns retention");
        public void Dispose()=>Disposals++;
    }
}
