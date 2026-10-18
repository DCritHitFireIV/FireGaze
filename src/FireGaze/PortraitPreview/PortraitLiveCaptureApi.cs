using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitLiveCaptureInput(IDalamudTextureWrap Texture,TextureModificationArgs CopyArgs);

/// <summary>Owns independent SRV references; native objects are never carried through asynchronous work.</summary>
internal sealed class PortraitLiveCaptureInputs(PortraitLiveCaptureInput character,
 IReadOnlyList<PortraitCaptureLayer> overlays,PortraitCaptureSize size):IDisposable
{
 public PortraitLiveCaptureInput Character{get;}=character;
 public PortraitCaptureSize Size{get;}=size;
 private IReadOnlyList<PortraitCaptureLayer>? overlays=overlays;
 private int disposed;
 public IReadOnlyList<PortraitCaptureLayer> TakeOverlays()=>Interlocked.Exchange(ref overlays,null)??[];
 public void Dispose()
 {
  if(Interlocked.Exchange(ref disposed,1)!=0)return;
  Character.Texture.Dispose();
  foreach(var texture in TakeOverlays().Select(x=>x.Texture).Distinct<IDalamudTextureWrap>(ReferenceEqualityComparer.Instance))texture.Dispose();
 }
}

internal interface IPortraitLiveCaptureSource:IDisposable
{
 bool IsFrameworkThread{get;}
 bool TrySample(PortraitImageScope scope,PortraitCaptureViewport viewport,out PortraitCaptureSample? sample);
 bool TryAcquirePortrait(PortraitCaptureSample sample,out PortraitLiveCaptureInputs? inputs);
}

internal interface IPortraitCaptureTextures
{
 Task<IDalamudTextureWrap> CopyAsync(IDalamudTextureWrap source,TextureModificationArgs args);
 Task<IDalamudTextureWrap> CaptureViewportAsync(ImGuiViewportTextureArgs args);
 IDalamudTextureWrap Compose(PortraitCaptureLayers layers);
}

/// <summary>Framework source acquisition, public GPU copies, and Draw-only composition.</summary>
internal sealed class PortraitLiveCaptureApi(IPortraitLiveCaptureSource source,IPortraitCaptureTextures textures)
 :IPortraitLiveImageApi,IDisposable
{
 public bool IsFrameworkThread=>source.IsFrameworkThread;
 public bool TrySample(PortraitImageScope scope,PortraitCaptureViewport viewport,out PortraitCaptureSample? sample)
 {
  sample=null;
  return IsFrameworkThread&&source.TrySample(scope,viewport,out sample);
 }
 public Task<PortraitCaptureLayers?> CaptureAsync(PortraitCaptureSample sample)
 {
  if(!IsFrameworkThread||!sample.IsValid||!source.TrySample(sample.Scope,sample.Viewport,out var fresh)||fresh!=sample)
   return Task.FromResult<PortraitCaptureLayers?>(null);
  if(sample.Scope==PortraitImageScope.VisibleAdventurerPlate)
  {
   if(sample.Plate is not {} plate||!PortraitCaptureGeometry.TryViewportCrop(plate.Rect,sample.Viewport,out var args))
    return Task.FromResult<PortraitCaptureLayers?>(null);
   // No cancellation token is passed: in the supported public viewport implementation canceling
   // its scheduled Framework tick can leave FirstUpdateTask permanently unfinished.
   return CapturePlate(textures.CaptureViewportAsync(args));
  }
  if(!source.TryAcquirePortrait(sample,out var inputs)||inputs is null)return Task.FromResult<PortraitCaptureLayers?>(null);
  try
  {
   if(!source.TrySample(sample.Scope,sample.Viewport,out fresh)||fresh!=sample)
   {inputs.Dispose();return Task.FromResult<PortraitCaptureLayers?>(null);}
   return CapturePortrait(inputs);
  }
  catch{inputs.Dispose();throw;}
 }
 private async Task<PortraitCaptureLayers?> CapturePortrait(PortraitLiveCaptureInputs inputs)
 {
  using(inputs)
  {
   IDalamudTextureWrap? pixels=null;IReadOnlyList<PortraitCaptureLayer>? overlays=null;
   try
   {
    pixels=await textures.CopyAsync(inputs.Character.Texture,inputs.Character.CopyArgs).ConfigureAwait(false);
    if(pixels.Width!=inputs.Size.Width||pixels.Height!=inputs.Size.Height)throw new InvalidOperationException("Unexpected copy dimensions.");
    overlays=inputs.TakeOverlays();
    var layers=new PortraitCaptureLayer[1+overlays.Count];layers[0]=new(pixels);
    for(var index=0;index<overlays.Count;index++)layers[index+1]=overlays[index];
    var result=new PortraitCaptureLayers(inputs.Size.Width,inputs.Size.Height,layers);
    pixels=null;overlays=null;return result;
   }
   finally
   {
    pixels?.Dispose();
    if(overlays is not null)foreach(var layer in overlays)layer.Texture.Dispose();
   }
  }
 }
 private async Task<PortraitCaptureLayers?> CapturePlate(Task<IDalamudTextureWrap> pending)
 {
  using var captured=await pending.ConfigureAwait(false);
  if(!PortraitCaptureGeometry.TryFit(captured.Width,captured.Height,PortraitCaptureGeometry.MaximumPixels,out var size))return null;
  var args=new TextureModificationArgs{NewWidth=size.Width,NewHeight=size.Height,DxgiFormat=28,MakeOpaque=true};
  var pixels=await textures.CopyAsync(captured,args).ConfigureAwait(false);
  if(pixels.Width!=size.Width||pixels.Height!=size.Height){pixels.Dispose();throw new InvalidOperationException("Unexpected viewport copy dimensions.");}
  return new(size.Width,size.Height,[new(pixels)],false);
 }
 public IDalamudTextureWrap Compose(PortraitCaptureLayers layers)=>textures.Compose(layers);
 public void Dispose()=>source.Dispose();
}

/// <summary>Only public Dalamud texture services; source references remain open under their owner.</summary>
internal sealed class PortraitCaptureTextures(ITextureProvider textures):IPortraitCaptureTextures
{
 public Task<IDalamudTextureWrap> CopyAsync(IDalamudTextureWrap source,TextureModificationArgs args)
  =>textures.CreateFromExistingTextureAsync(source,args,leaveWrapOpen:true,
   debugName:"FireGaze analysis source",cancellationToken:CancellationToken.None);
 public Task<IDalamudTextureWrap> CaptureViewportAsync(ImGuiViewportTextureArgs args)
  =>textures.CreateFromImGuiViewportAsync(args,debugName:"FireGaze visible adventurer plate",cancellationToken:CancellationToken.None);
 public IDalamudTextureWrap Compose(PortraitCaptureLayers layers)
 {
  if(!layers.RequiresComposition&&layers.Layers.Count==1)
   return layers.Layers[0].Texture.CreateWrapSharingLowLevelResource();
  using var surface=textures.CreateDrawListTexture("FireGaze live portrait composition");
  surface.Size=new(layers.Width,layers.Height);surface.ClearColor=Vector4.Zero;
  var draw=ImGui.ImDrawList(ImGui.GetDrawListSharedData());
  try
  {
   draw._ResetForNewFrame();draw.PushClipRect(Vector2.Zero,surface.Size,false);
   foreach(var layer in layers.Layers)
   {
    var rect=layer.Placement??new(0,0,1,1);
    var min=new Vector2(rect.X*layers.Width,rect.Y*layers.Height);
    var max=min+new Vector2(rect.Width*layers.Width,rect.Height*layers.Height);
    draw.AddImage(layer.Texture.Handle,min,max,layer.UV0??Vector2.Zero,layer.UV1??Vector2.One,layer.Tint);
   }
   draw.PopClipRect();surface.Draw(draw,Vector2.Zero,Vector2.One);
   return surface.CreateWrapSharingLowLevelResource();
  }
  finally{draw.Destroy();}
 }
}
