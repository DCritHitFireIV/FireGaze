using FireGaze.PortraitPreview;
#if HAS_CAPTURE_API
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
#endif

internal static class CapturePipelineTests
{
 public static async Task<int> Run()
 {
#if !HAS_CAPTURE_API
  await Task.CompletedTask;
  throw new InvalidOperationException("Public capture pipeline does not yet retain portrait layers or capture the actual visible plate.");
#else
  var checks=0;
  void Check(bool value,string why){checks++;if(!value)throw new InvalidOperationException(why);}
  var native=new FakeLiveSource();var gpu=new FakeCaptureTextures();
  using var api=new PortraitLiveCaptureApi(native,gpu);
  native.Framework=false;
  Check(!api.TrySample(PortraitImageScope.Portrait,native.Sample.Viewport,out _)&&native.Samples==0,"public boundary prevents off-Framework source reads");
  Check(await api.CaptureAsync(native.Sample) is null&&native.Acquires==0,"off-Framework capture never acquires native layers");
  native.Framework=true;
  var character=new CountingCaptureTexture(1024,1024);var frame=new CountingCaptureTexture(2048,2048);
  native.Inputs=new(new(character,new(){NewWidth=576,NewHeight=960,DxgiFormat=28,Uv1=new(.5625f,.9375f)}),[new(frame,new(.1f,.2f,.8f,.7f),new(.2f,.3f),new(.6f,.8f),0x7fffffff)],new(576,960));
  var task=api.CaptureAsync(native.Sample);
  Check(!task.IsCompleted&&character.Disposes==0&&frame.Disposes==0,"native references remain owned during the asynchronous public copy");
  Check(gpu.CopyCalls==1&&gpu.ViewportCalls==0&&gpu.LastCopy.NewWidth==576&&gpu.LastCopy.NewHeight==960,"portrait copies actual source resolution rather than a history thumbnail");
  native.Framework=false;
  var pixels=new CountingCaptureTexture();gpu.Copy.SetResult(pixels);
  using(var result=await task)
  {
   Check(result is{RequiresComposition:false,Layers.Count:1}&&result.Width==576&&result.Height==960,"portrait output is flattened before publication with its original dimensions");
   Check(gpu.PreparedSource!.Layers[1].Placement==new PortraitCaptureRect(.1f,.2f,.8f,.7f)&&gpu.PreparedSource.Layers[1].UV0==new System.Numerics.Vector2(.2f,.3f)&&gpu.PreparedSource.Layers[1].Tint==0x7fffffff,"frame placement, atlas UV and tint reach the safe compositor unchanged");
   Check(character.Disposes==1&&frame.Disposes==1&&native.Samples==2,"composition completion releases native references without another source lookup");
  }
  Check(pixels.Disposes==1&&frame.Disposes==1&&gpu.PreparedTexture!.Disposes==1,"final portrait owner releases its independent prepared image once");
  native.Framework=true;
  var plate=native.Sample with{Scope=PortraitImageScope.VisibleAdventurerPlate,Plate=new(1,2,3,4,5,6,7,new(200,100,800,500),"plate")};
  native.Sample=plate;
  task=api.CaptureAsync(plate);
  Check(gpu.ViewportCalls==1&&gpu.LastViewport.Uv0==new System.Numerics.Vector2(200f/1920,100f/1080)&&gpu.LastViewport.TakeBeforeImGuiRender,"plate captures the visible whole-card rectangle before ImGui, rather than reusing the character texture");
  var screenshot=new CountingCaptureTexture(800,500);gpu.Viewport.SetResult(screenshot);
  await Until(()=>gpu.CopyCalls==2);
  Check(gpu.LastCopy.NewWidth==800&&gpu.LastCopy.NewHeight==500&&gpu.LastCopy.MakeOpaque,"plate screenshot is normalized to bounded RGBA pixels");
  var platePixels=new CountingCaptureTexture(800,500);gpu.Copy.SetResult(platePixels);
  using(var result=await task)Check(result is{Width:800,Height:500,RequiresComposition:false},"single plate frame avoids an unnecessary layered composition");
  Check(screenshot.Disposes==1&&platePixels.Disposes==1,"viewport temporary and returned plate are both owned and released");
  var mismatch=new FakeLiveSource{MismatchAfterAcquire=true};var textures=new FakeCaptureTextures();using var boundary=new PortraitLiveCaptureApi(mismatch,textures);
  var stale=new CountingCaptureTexture();mismatch.Inputs=new(new(stale,new()),[],new(576,960));
  Check(await boundary.CaptureAsync(mismatch.Sample) is null&&stale.Disposes==1&&textures.CopyCalls==0,"source identity changing during acquisition releases native references before a public copy");
  var broken=new FakeLiveSource();var failedGpu=new FakeCaptureTextures();using var failure=new PortraitLiveCaptureApi(broken,failedGpu);
  var source=new CountingCaptureTexture();var overlay=new CountingCaptureTexture();broken.Inputs=new(new(source,new()),[new(overlay)],new(576,960));
  task=failure.CaptureAsync(broken.Sample);failedGpu.Copy.SetException(new InvalidOperationException("failed GPU copy"));
  try{await task;Check(false,"copy failure must propagate to the lifecycle error boundary");}catch(InvalidOperationException){}
  Check(source.Disposes==1&&overlay.Disposes==1,"GPU failure releases acquired portrait and overlay references");
  var fitSource=new FakeLiveSource();var fitTextures=new FakeCaptureTextures();using var fitApi=new PortraitLiveCaptureApi(fitSource,fitTextures);
  Check(fitApi.TryDescribePortrait(fitSource.Sample,out var described)&&described==fitSource.Surface,"fit surface description is detached and validated against full source sample");
  var rejected=new CountingCaptureTexture();fitSource.Inputs=new(new(rejected,new()),[],new(2,1),fitSource.Surface with{GenerationKey="different-resource"});
  Check(await fitApi.CaptureAsync(fitSource.Sample,fitSource.Surface) is null&&rejected.Disposes==1&&fitTextures.CopyCalls==0,"changed SRV/crop generation is rejected and released before physical copy");
  var accepted=new CountingCaptureTexture();fitSource.Inputs=new(new(accepted,new()),[],new(2,1),fitSource.Surface);
  var fitTask=fitApi.CaptureAsync(fitSource.Sample,fitSource.Surface);Check(fitTextures.CopyCalls==1,"matching expected surface starts exactly one physical copy");
  fitTextures.Copy.SetResult(new CountingCaptureTexture(2,1));using(var result=await fitTask)Check(result is{Width:2,Height:1},"matching fit surface preserves actual dimensions");
  return checks;
#endif
 }
#if HAS_CAPTURE_API
 private static async Task Until(Func<bool> value){var end=DateTime.UtcNow.AddSeconds(5);while(!value()){if(DateTime.UtcNow>end)throw new TimeoutException();await Task.Delay(1);}}
 private sealed class FakeLiveSource:IPortraitLiveCaptureSource
 {
  public bool Framework=true,MismatchAfterAcquire;public int Samples,Acquires;
  public PortraitCaptureSample Sample=new(new(1,2),new(3,4,5,6,7),"sample",PortraitImageScope.Portrait,new(7,System.Numerics.Vector2.Zero,new(1920,1080)));
  public PortraitLiveCaptureInputs? Inputs;
  public PortraitCaptureSurface Surface=new("fit-surface",2,1);
  public bool IsFrameworkThread=>Framework;
  public bool TrySample(PortraitImageScope scope,PortraitCaptureViewport viewport,out PortraitCaptureSample? sample){Samples++;sample=Sample with{Scope=scope};if(MismatchAfterAcquire&&Acquires>0)sample=sample with{StateKey="changed"};return true;}
  public bool TryAcquirePortrait(PortraitCaptureSample sample,out PortraitLiveCaptureInputs? inputs){Acquires++;inputs=Inputs;Inputs=null;return inputs is not null;}
  public bool TryDescribePortrait(PortraitCaptureSample sample,out PortraitCaptureSurface? surface){surface=Surface;return true;}
  public void Dispose()=>Inputs?.Dispose();
 }
 private sealed class FakeCaptureTextures:IPortraitCaptureTextures
 {
  public int CopyCalls,ViewportCalls;public TextureModificationArgs LastCopy;public ImGuiViewportTextureArgs LastViewport;
  public TaskCompletionSource<IDalamudTextureWrap> Copy=new(TaskCreationOptions.RunContinuationsAsynchronously),Viewport=new(TaskCreationOptions.RunContinuationsAsynchronously);
  public Task<IDalamudTextureWrap> CopyAsync(IDalamudTextureWrap source,TextureModificationArgs args){CopyCalls++;LastCopy=args;Copy=new(TaskCreationOptions.RunContinuationsAsynchronously);return Copy.Task;}
  public Task<IDalamudTextureWrap> CaptureViewportAsync(ImGuiViewportTextureArgs args){ViewportCalls++;LastViewport=args;Viewport=new(TaskCreationOptions.RunContinuationsAsynchronously);return Viewport.Task;}
  public PortraitCaptureLayers? PreparedSource;public CountingCaptureTexture? PreparedTexture;
  public Task<IDalamudTextureWrap> ComposeAsync(PortraitCaptureLayers layers)
  {PreparedSource=layers;PreparedTexture=new(layers.Width,layers.Height);return Task.FromResult<IDalamudTextureWrap>(PreparedTexture);}
 }
#endif
}
