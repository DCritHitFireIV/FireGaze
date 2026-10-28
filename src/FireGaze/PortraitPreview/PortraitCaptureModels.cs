using System.Numerics;
using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitCaptureSurface(string GenerationKey,int Width,int Height)
{
 public bool IsValid=>!string.IsNullOrWhiteSpace(GenerationKey)&&Width>0&&Height>0
  &&(long)Width*Height<=PortraitCaptureGeometry.MaximumPixels;
}
internal readonly record struct PortraitCaptureOwner(ulong AccountID,ulong ContentID)
{public bool IsValid=>AccountID!=0&&ContentID!=0;}
// Addresses are opaque equality tokens. An asynchronous consumer must never dereference them.
internal sealed record PortraitPlateCaptureBinding(nint Addon,ushort AddonID,nint Agent,nint Storage,
 ulong AccountID,ulong ContentID,long Generation,PortraitCaptureRect Rect,string VisualKey);
internal sealed record PortraitCaptureSample(PortraitCaptureOwner Owner,PortraitIdentity Editor,string StateKey,
 PortraitImageScope Scope,PortraitCaptureViewport Viewport,PortraitPlateCaptureBinding? Plate=null)
{
 public bool IsValid=>Owner.IsValid&&Editor.IsValid&&Editor.Generation!=0&&!string.IsNullOrWhiteSpace(StateKey)
  &&Scope is PortraitImageScope.Portrait or PortraitImageScope.VisibleAdventurerPlate;
}
internal sealed record PortraitCaptureLayer(IDalamudTextureWrap Texture,PortraitCaptureRect? Placement=null,
 Vector2? UV0=null,Vector2? UV1=null,uint Tint=uint.MaxValue);
internal sealed class PortraitCaptureLayers(int width,int height,IReadOnlyList<PortraitCaptureLayer> layers,bool requiresComposition=true):IDisposable
{
 public int Width{get;}=width;
 public int Height{get;}=height;
 public IReadOnlyList<PortraitCaptureLayer> Layers{get;}=layers;
 public bool RequiresComposition{get;}=requiresComposition;
 private int disposed;
 public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;PortraitCaptureCleanupException.DisposeAll(Layers.Select(x=>(IDisposable)x.Texture).ToArray());}
}
internal sealed class PortraitCapturedImage(IDalamudTextureWrap texture,PortraitCaptureSample sample,
 PortraitCaptureRetirement? retirement=null,IDisposable? lifetime=null,PortraitCaptureSurface? surface=null):IDisposable
{
 public IDalamudTextureWrap Texture{get;}=texture;
 public PortraitCaptureSample Sample{get;}=sample;
 public PortraitCaptureSurface? Surface{get;}=surface;
 public int Width=>Texture.Width;
 public int Height=>Texture.Height;
 private int disposed;
 public void Dispose()
 {
  if(Interlocked.Exchange(ref disposed,1)!=0)return;
  try{if(retirement is null)PortraitCaptureCleanupException.DisposeAll(Texture);else retirement.Release(Texture);}
  finally{lifetime?.Dispose();}
 }
}
internal interface IPortraitLiveImageApi
{
 bool IsFrameworkThread{get;}
 // Called only on Framework. Return immutable data after re-resolving native owners.
 bool TrySample(PortraitImageScope scope,PortraitCaptureViewport viewport,out PortraitCaptureSample? sample);
 // Initial source acquisition runs synchronously on Framework; after await only owned textures may be touched.
 Task<PortraitCaptureLayers?> CaptureAsync(PortraitCaptureSample sample);
 // Called only inside UiBuilder.Draw. Never read native editor state here.
 IDalamudTextureWrap Compose(PortraitCaptureLayers layers);
}
