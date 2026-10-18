using FireGaze.PortraitPreview;
#if HAS_CAPTURE_NODE
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
#endif

internal static class CaptureNativeNodeTests
{
 public static unsafe int Run()
 {
#if !HAS_CAPTURE_NODE
  throw new InvalidOperationException("Native image reader does not yet revalidate ULD ownership after bounds callbacks.");
#else
  var checks=0;
  void Check(bool value,string why){checks++;if(!value)throw new InvalidOperationException(why);}
  AtkComponentPortrait portrait=default;AtkImageNode image=default;AtkUldPartsList parts=default;AtkUldPart part=default;AtkUldAsset asset=default;Texture kernel=default;
  var nodes=stackalloc AtkResNode*[1];nodes[0]=(AtkResNode*)&image;
  portrait.UldManager.NodeList=nodes;portrait.UldManager.NodeListCount=1;
  image.Type=NodeType.Image;image.NodeFlags=NodeFlags.Visible;image.ScaleX=image.ScaleY=1;image.PartsList=&parts;
  image.Color.R=image.Color.G=image.Color.B=image.Color.A=255;
  parts.PartCount=1;parts.Parts=&part;part.UldAsset=&asset;part.U=20;part.V=30;part.Width=100;part.Height=200;
  asset.AtkTexture.TextureType=TextureType.KernelTexture;asset.AtkTexture.KernelTexture=&kernel;
  kernel.AllocatedWidth=512;kernel.AllocatedHeight=512;kernel.ActualWidth=400;kernel.ActualHeight=500;
  kernel.D3D11ShaderResourceView=(void*)123;
  var live=true;var retained=new List<CountingCaptureTexture>();
  var address=(nint)(&portrait);var imageAddress=(nint)(&image);
  PortraitCaptureRect? Bounds(nint node)=>new(10,20,160,256);
  Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap Retain(nint srv,int width,int height)
  {Check(srv==123&&width==512&&height==512,"retains independently owned SRV rather than native texture pointer");var texture=new CountingCaptureTexture(width,height);retained.Add(texture);return texture;}
  Check(PortraitCaptureNodeReader.TryAcquire(address,imageAddress,false,()=>live,Bounds,Retain,out var value)&&value is not null,"valid ULD image can retain a public texture reference");
  Check(value!.UV0==new System.Numerics.Vector2(20f/512,30f/512)&&value.UV1==new System.Numerics.Vector2(120f/512,230f/512)&&value.Bounds==new PortraitCaptureRect(10,20,160,256),"overlay retains exact atlas rectangle and screen layout");value.Texture.Dispose();
  Check(PortraitCaptureNodeReader.TryAcquire(address,imageAddress,true,()=>live,Bounds,Retain,out value)&&value is not null&&value.UV1==new System.Numerics.Vector2(400f/512,500f/512),"character uses actual texture dimensions, not logical ULD part dimensions");value!.Texture.Dispose();
  var before=retained.Count;
  PortraitCaptureRect? Changed(nint node){live=false;return new(0,0,10,10);}
  Check(!PortraitCaptureNodeReader.TryAcquire(address,imageAddress,false,()=>live,Changed,Retain,out _)&&retained.Count==before,"owner replaced during native bounds lookup is rejected before retaining saved resources");
  live=true;parts.PartCount=0;
  Check(!PortraitCaptureNodeReader.TryAcquire(address,imageAddress,false,()=>live,Bounds,Retain,out _),"invalid atlas count rejected");parts.PartCount=1;
  image.PartId=1;Check(!PortraitCaptureNodeReader.TryAcquire(address,imageAddress,false,()=>live,Bounds,Retain,out _),"out of range part rejected");image.PartId=0;
  image.Rotation=.5f;Check(!PortraitCaptureNodeReader.TryAcquire(address,imageAddress,false,()=>live,Bounds,Retain,out _),"rotated image is rejected instead of claiming an accurate axis-aligned composite");image.Rotation=0;
  image.NodeFlags=0;Check(!PortraitCaptureNodeReader.TryAcquire(address,imageAddress,false,()=>live,Bounds,Retain,out _),"invisible image is not captured");image.NodeFlags=NodeFlags.Visible;
  Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap ReentrantRetain(nint srv,int width,int height){var t=new CountingCaptureTexture();retained.Add(t);live=false;return t;}
  Check(!PortraitCaptureNodeReader.TryAcquire(address,imageAddress,false,()=>live,Bounds,ReentrantRetain,out _)&&retained[^1].Disposes==1,"reentrant owner change while acquiring SRV releases the returned public lease");
  return checks;
#endif
 }
}
