using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitLiveCaptureInput(IDalamudTextureWrap Texture,TextureModificationArgs CopyArgs);
internal sealed record PortraitCaptureSurface(string GenerationKey,int Width,int Height)
{
 public bool IsValid=>!string.IsNullOrWhiteSpace(GenerationKey)&&Width>0&&Height>0
  &&(long)Width*Height<=PortraitCaptureGeometry.MaximumPixels;
}

/// <summary>Owns independent SRV references; native objects are never carried through asynchronous work.</summary>
internal sealed class PortraitLiveCaptureInputs(PortraitLiveCaptureInput character,
 IReadOnlyList<PortraitCaptureLayer> overlays,PortraitCaptureSize size,PortraitCaptureSurface? surface=null):IDisposable
{
 public PortraitLiveCaptureInput Character{get;}=character;
 public PortraitCaptureSize Size{get;}=size;
 public PortraitCaptureSurface? Surface{get;}=surface;
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
 bool TryDescribePortrait(PortraitCaptureSample sample,out PortraitCaptureSurface? surface){surface=null;return false;}
}

internal interface IPortraitLightingFitCaptureApi:IPortraitLiveImageApi
{
 bool TryDescribePortrait(PortraitCaptureSample sample,out PortraitCaptureSurface? surface);
 Task<PortraitCaptureLayers?> CaptureAsync(PortraitCaptureSample sample,PortraitCaptureSurface expectedSurface);
}

internal interface IPortraitCaptureTextures
{
 Task<IDalamudTextureWrap> CopyAsync(IDalamudTextureWrap source,TextureModificationArgs args);
 Task<IDalamudTextureWrap> CaptureViewportAsync(ImGuiViewportTextureArgs args);
 Task<IDalamudTextureWrap> ComposeAsync(PortraitCaptureLayers layers);
}

/// <summary>Framework source acquisition and independently owned asynchronous image preparation.</summary>
internal sealed class PortraitLiveCaptureApi(IPortraitLiveCaptureSource source,IPortraitCaptureTextures textures)
 :IPortraitLightingFitCaptureApi,IDisposable
{
 public bool IsFrameworkThread=>source.IsFrameworkThread;
 public bool TrySample(PortraitImageScope scope,PortraitCaptureViewport viewport,out PortraitCaptureSample? sample)
 {
  sample=null;
  return IsFrameworkThread&&source.TrySample(scope,viewport,out sample);
 }
 public Task<PortraitCaptureLayers?> CaptureAsync(PortraitCaptureSample sample)
  =>Capture(sample,null);
 public Task<PortraitCaptureLayers?> CaptureAsync(PortraitCaptureSample sample,PortraitCaptureSurface expectedSurface)
  =>Capture(sample,expectedSurface);
 public bool TryDescribePortrait(PortraitCaptureSample sample,out PortraitCaptureSurface? surface)
 {
  surface=null;
  if(!IsFrameworkThread||sample.Scope!=PortraitImageScope.Portrait||!sample.IsValid
   ||!source.TrySample(sample.Scope,sample.Viewport,out var before)||before!=sample
   ||!source.TryDescribePortrait(sample,out var described)||described is not{IsValid:true}
   ||!source.TrySample(sample.Scope,sample.Viewport,out var after)||after!=sample)return false;
  surface=described;return true;
 }
 private Task<PortraitCaptureLayers?> Capture(PortraitCaptureSample sample,PortraitCaptureSurface? expectedSurface)
 {
  if(!IsFrameworkThread||!sample.IsValid||!source.TrySample(sample.Scope,sample.Viewport,out var fresh)||fresh!=sample)
   return Task.FromResult<PortraitCaptureLayers?>(null);
  if(sample.Scope==PortraitImageScope.VisibleAdventurerPlate)
  {
   if(expectedSurface is not null)return Task.FromResult<PortraitCaptureLayers?>(null);
   if(sample.Plate is not {} plate||!PortraitCaptureGeometry.TryViewportCrop(plate.Rect,sample.Viewport,out var args))
    return Task.FromResult<PortraitCaptureLayers?>(null);
   // No cancellation token is passed: in the supported public viewport implementation canceling
   // its scheduled Framework tick can leave FirstUpdateTask permanently unfinished.
   return CapturePlate(textures.CaptureViewportAsync(args));
  }
  if(!source.TryAcquirePortrait(sample,out var inputs)||inputs is null)return Task.FromResult<PortraitCaptureLayers?>(null);
  try
  {
   if(expectedSurface is not null&&inputs.Surface!=expectedSurface
    ||!source.TrySample(sample.Scope,sample.Viewport,out fresh)||fresh!=sample)
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
    using var sources=new PortraitCaptureLayers(inputs.Size.Width,inputs.Size.Height,layers);
    pixels=null;overlays=null;
    var flattened=await textures.ComposeAsync(sources).ConfigureAwait(false);
    if(flattened.Width!=inputs.Size.Width||flattened.Height!=inputs.Size.Height)
    {flattened.Dispose();throw new InvalidOperationException("Unexpected composed dimensions.");}
    return new(inputs.Size.Width,inputs.Size.Height,[new(flattened)],false);
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
 public IDalamudTextureWrap Compose(PortraitCaptureLayers layers)
 {
  if(layers.RequiresComposition||layers.Layers.Count!=1)
   throw new InvalidOperationException("Capture has not completed image preparation.");
  return layers.Layers[0].Texture.CreateWrapSharingLowLevelResource();
 }
 public void Dispose()=>source.Dispose();
}

/// <summary>Only public Dalamud texture services; source references remain open under their owner.</summary>
internal sealed class PortraitCaptureTextures(ITextureProvider textures,ITextureReadbackProvider readback):IPortraitCaptureTextures
{
 public Task<IDalamudTextureWrap> CopyAsync(IDalamudTextureWrap source,TextureModificationArgs args)
  =>textures.CreateFromExistingTextureAsync(source,args,leaveWrapOpen:true,
   debugName:"FireGaze analysis source",cancellationToken:CancellationToken.None);
 public Task<IDalamudTextureWrap> CaptureViewportAsync(ImGuiViewportTextureArgs args)
  =>textures.CreateFromImGuiViewportAsync(args,debugName:"FireGaze visible adventurer plate",cancellationToken:CancellationToken.None);
 public Task<IDalamudTextureWrap> ComposeAsync(PortraitCaptureLayers layers)
  =>new PortraitCaptureSoftwareCompositor(textures,readback).ComposeAsync(layers.Width,layers.Height,layers.Layers);
}
