#if !HAS_CAPTURE
Console.Error.WriteLine("FAIL: no bounded real-resolution portrait/visible-plate geometry, stable single-flight capture or late-resource cleanup exists");
return 1;
#else
using System.Numerics;
using FireGaze.PortraitPreview;

var checks=0;
void Check(bool pass,string message) {checks++;if(!pass)throw new InvalidOperationException(message);}
var view=new PortraitCaptureViewport(7,new(100,50),new(1920,1080));
Check(PortraitCaptureGeometry.TryViewportCrop(new(292,158,960,540),view,out var cropArgs),"valid screen rectangle has a bounded viewport crop");
Check(cropArgs.ViewportId==7&&cropArgs.Uv0==new Vector2(.1f,.1f)&&cropArgs.Uv1==new Vector2(.6f,.6f),"nonzero viewport origin is subtracted once");
Check(!cropArgs.AutoUpdate&&cropArgs.TakeBeforeImGuiRender&&!cropArgs.KeepTransparency,"capture takes one pre-ImGui opaque frame");
foreach(var rect in new[]{new PortraitCaptureRect(99,50,100,100),new(100,49,100,100),new(2000,50,21,20),new(100,1100,20,31),new(100,50,0,1),new(100,50,-1,1),new(float.NaN,0,2,2),new(100,50,float.PositiveInfinity,1)})
 Check(!PortraitCaptureGeometry.TryViewportCrop(rect,view,out _),"out-of-screen or invalid crop is rejected without clamping into a partial plate");
foreach(var invalid in new[]{new PortraitCaptureViewport(0,Vector2.Zero,new(100,100)),new(1,Vector2.Zero,Vector2.Zero),new(1,new(float.NaN,0),new(100,100))})
 Check(!PortraitCaptureGeometry.TryViewportCrop(new(0,0,10,10),invalid,out _),"invalid viewport is refused");
Check(PortraitCaptureGeometry.TryFit(576,960,1_048_576,out var size)&&size==new PortraitCaptureSize(576,960),"real 576x960 render target is not downscaled to history size");
Check(PortraitCaptureGeometry.TryFit(1920,1080,1_048_576,out size)&&(long)size.Width*size.Height<=1_048_576&&size.Width<1920,"large frame is bounded before GPU readback");
Check(PortraitCaptureGeometry.TryFit(12,20,1_048_576,out size)&&size==new PortraitCaptureSize(12,20),"small frame is never upscaled");
Check(!PortraitCaptureGeometry.TryFit(0,10,100,out _)&&!PortraitCaptureGeometry.TryFit(32768,1,100,out _)&&!PortraitCaptureGeometry.TryFit(10,10,0,out _),"unreasonable dimensions fail before arithmetic/allocation");
Check(PortraitCaptureGeometry.TryTextureCopy(1024,1024,576,960,new(576,960),out var copy)&&copy.Uv1==new Vector2(576f/1024,960f/1024)&&copy.DxgiFormat==28,"actual image excludes allocation padding and requests RGBA8");
Check(!PortraitCaptureGeometry.TryTextureCopy(512,512,576,960,new(160,256),out _),"actual image cannot exceed allocation");
Check(PortraitCaptureGeometry.TryPlacement(new(20,30,200,400),new(30,50,100,200),out var placement)&&placement==new PortraitCaptureRect(.05f,.05f,.5f,.5f),"layer position is relative to the character rectangle");
Check(!PortraitCaptureGeometry.TryPlacement(new(0,0,0,0),new(1,1,2,2),out _),"invalid parent placement fails");
checks+=await CaptureLifecycleTests.Run();
checks+=await CapturePipelineTests.Run();
checks+=CaptureNativeNodeTests.Run();
checks+=CaptureNativeOwnerTests.Run();
Console.WriteLine($"PortraitCaptureTests: {checks} passed, 0 failed");
return 0;
#endif
