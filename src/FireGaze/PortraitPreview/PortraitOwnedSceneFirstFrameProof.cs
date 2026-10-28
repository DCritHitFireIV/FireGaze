using System.Numerics;
using System.Security.Cryptography;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitOwnedSceneRawFrame(byte[] Bytes,int Width,int Height,int Pitch,int DxgiFormat);
internal sealed record PortraitOwnedSceneFirstFrameSnapshot(string State,PortraitOwnedSceneFirstFrameIdentity? Identity=null,
    int Width=0,int Height=0,int Pitch=0,int DxgiFormat=0,int ByteLength=0,string? SHA256=null,string? RawBase64=null,string? Error=null);

/// <summary>One bounded readback of a copied owned wrap. Never reads scene pointers or writes user storage.</summary>
internal sealed class PortraitOwnedSceneFirstFrameProof : IDisposable
{
    private readonly object sync=new();
    private readonly PortraitOwnedSceneImageSource<IDalamudTextureWrap> images;
    private readonly Func<IDalamudTextureWrap,Task<PortraitOwnedSceneRawFrame>> read;
    private readonly Func<bool> admission;
    private bool closed,attempted,pending;
    private Task work=Task.CompletedTask;
    // Drain roots this owner, which must also retain the actual readback awaiter graph.
    private Task capture=Task.CompletedTask;
    private PortraitOwnedSceneFirstFrameSnapshot snapshot=new("None");
    internal PortraitOwnedSceneFirstFrameProof(PortraitOwnedSceneImageSource<IDalamudTextureWrap> images,
        Func<IDalamudTextureWrap,Task<PortraitOwnedSceneRawFrame>> read,
        Func<bool> admission)
    {this.images=images;this.read=read;this.admission=admission;images.FramePublished+=Published;}
    internal bool Pending {get{lock(sync)return pending;}}
    internal PortraitOwnedSceneFirstFrameSnapshot Snapshot {get{lock(sync)return snapshot;}}
    private void Published()
    {
        lock(sync)
        {
            if(closed||attempted||!admission())return;
            var borrow=images.TryBorrow();
            if(borrow is null)return;
            if(borrow.Receipt is not {IsValid:true} stamp){borrow.Dispose();return;}
            attempted=pending=true;snapshot=new("Pending",stamp);
            // Publish physical ownership before the read delegate can synchronously reenter close.
            var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            work=completion.Task;
            capture=CaptureAndComplete(borrow,stamp,completion);
        }
    }
    private async Task CaptureAndComplete(PortraitOwnedSceneTextureLifetime<IDalamudTextureWrap>.Borrow borrow,
        PortraitOwnedSceneFirstFrameIdentity stamp,TaskCompletionSource completion)
    {
        try{await Capture(borrow,stamp).ConfigureAwait(false);completion.TrySetResult();}
        catch(Exception error){completion.TrySetException(error);}
    }
    private async Task Capture(PortraitOwnedSceneTextureLifetime<IDalamudTextureWrap>.Borrow borrow,
        PortraitOwnedSceneFirstFrameIdentity stamp)
    {
        try
        {
            // The original borrow remains owned through the official provider's actual task.
            var raw=await read(borrow.Texture).ConfigureAwait(false);
            if(raw.Width<=0||raw.Height<=0||(long)raw.Width*raw.Height>1_048_576||raw.Pitch<(long)raw.Width*4
                ||(long)raw.Pitch*raw.Height>raw.Bytes.Length||raw.Bytes.Length>4_194_304||raw.DxgiFormat is not(28 or 29 or 87 or 91))
                throw new InvalidOperationException("Unsupported bounded independent portrait readback.");
            var result=new PortraitOwnedSceneFirstFrameSnapshot("Ready",stamp,raw.Width,raw.Height,raw.Pitch,raw.DxgiFormat,
                raw.Bytes.Length,Convert.ToHexString(SHA256.HashData(raw.Bytes)),Convert.ToBase64String(raw.Bytes));
            lock(sync)snapshot=result;
        }
        catch(Exception error){lock(sync)snapshot=new("Failed",stamp,Error:error.GetType().FullName);}
        finally{borrow.Dispose();lock(sync)pending=false;}
    }
    internal Task DrainPhysicalAsync(){Dispose();lock(sync)return PortraitCaptureRetirement.CombineOwned(this,work,capture);}
    public void Dispose(){lock(sync){if(closed)return;closed=true;images.FramePublished-=Published;}}
    internal static async Task<PortraitOwnedSceneRawFrame> ReadOfficialAsync(ITextureReadbackProvider readback,IDalamudTextureWrap texture)
    {
        var raw=await readback.GetRawImageAsync(texture,new TextureModificationArgs{Uv1=Vector2.One},
            leaveWrapOpen:true,cancellationToken:CancellationToken.None).ConfigureAwait(false);
        return new(raw.RawData,raw.Specification.Width,raw.Specification.Height,raw.Specification.Pitch,raw.Specification.DxgiFormat);
    }
}
