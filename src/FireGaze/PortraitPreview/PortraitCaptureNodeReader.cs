using System.Numerics;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitCaptureNativeImage(IDalamudTextureWrap Texture,PortraitCaptureRect Bounds,
 Vector2 UV0,Vector2 UV1,uint Tint,uint AllocatedWidth,uint AllocatedHeight,uint ActualWidth,uint ActualHeight);

/// <summary>Retains a public SRV lease, with no game texture method or game refcount change.</summary>
internal static unsafe class PortraitCaptureNodeReader
{
 public static bool TryAcquire(nint componentAddress,nint imageAddress,bool character,Func<bool> ownerCurrent,
  Func<nint,PortraitCaptureRect?> readBounds,Func<nint,int,int,IDalamudTextureWrap> retain,
  out PortraitCaptureNativeImage? result)
 {
  result=null;
  if(!ownerCurrent()||!TryDescribe(componentAddress,imageAddress,character,out var before))return false;
  var bounds=readBounds(imageAddress);
  // GetBounds may enter another plugin. Only freshly validated pointers are read afterwards.
  if(!ownerCurrent()||bounds is not{IsValid:true} rectangle
   ||!TryDescribe(componentAddress,imageAddress,character,out var after)||before!=after)return false;
  IDalamudTextureWrap? owned=null;
  try
  {
   owned=retain(after.Srv,(int)after.AllocatedWidth,(int)after.AllocatedHeight);
   if(!ownerCurrent()||!TryDescribe(componentAddress,imageAddress,character,out var final)||final!=after)return false;
   result=new(owned,rectangle,after.UV0,after.UV1,after.Tint,
    after.AllocatedWidth,after.AllocatedHeight,after.ActualWidth,after.ActualHeight);
   owned=null;return true;
  }
  finally{owned?.Dispose();}
 }
 private static bool TryDescribe(nint componentAddress,nint imageAddress,bool character,out Description value)
 {
  value=default;if(componentAddress==0||imageAddress==0)return false;
  var portrait=(AtkComponentPortrait*)componentAddress;
  var manager=&portrait->UldManager;
  if(manager->NodeList==null||manager->NodeListCount is 0 or >4096)return false;
  var owns=false;
  for(var index=0;index<manager->NodeListCount;index++)if((nint)manager->NodeList[index]==imageAddress){owns=true;break;}
  if(!owns)return false;
  var image=(AtkImageNode*)imageAddress;
  if(image->Type!=NodeType.Image||(image->NodeFlags&NodeFlags.Visible)==0||image->Rotation!=0
   ||!float.IsFinite(image->ScaleX)||!float.IsFinite(image->ScaleY)||image->ScaleX<=0||image->ScaleY<=0)return false;
  var parts=image->PartsList;
  if(parts==null||parts->Parts==null||parts->PartCount is 0 or >4096||image->PartId>=parts->PartCount)return false;
  var part=parts->Parts[image->PartId];var asset=part.UldAsset;
  if(asset==null)return false;
  var kernel=asset->AtkTexture.TextureType==TextureType.KernelTexture?asset->AtkTexture.KernelTexture:
   asset->AtkTexture.TextureType==TextureType.Resource&&asset->AtkTexture.Resource!=null?asset->AtkTexture.Resource->KernelTextureObject:null;
  if(kernel==null||kernel->D3D11ShaderResourceView==null||kernel->AllocatedWidth is 0 or >16384
   ||kernel->AllocatedHeight is 0 or >16384||kernel->ActualWidth==0||kernel->ActualHeight==0
   ||kernel->ActualWidth>kernel->AllocatedWidth||kernel->ActualHeight>kernel->AllocatedHeight)return false;
  var width=character?kernel->ActualWidth:part.Width;var height=character?kernel->ActualHeight:part.Height;
  var u=character?0u:part.U;var v=character?0u:part.V;
  if(width==0||height==0||u+width>kernel->AllocatedWidth||v+height>kernel->AllocatedHeight)return false;
  var tint=(uint)image->Color.R|((uint)image->Color.G<<8)|((uint)image->Color.B<<16)|((uint)image->Color.A<<24);
  value=new((nint)parts,(nint)parts->Parts,image->PartId,(nint)asset,(nint)kernel,(nint)kernel->D3D11ShaderResourceView,
   kernel->AllocatedWidth,kernel->AllocatedHeight,kernel->ActualWidth,kernel->ActualHeight,
   new(u/(float)kernel->AllocatedWidth,v/(float)kernel->AllocatedHeight),
   new((u+width)/(float)kernel->AllocatedWidth,(v+height)/(float)kernel->AllocatedHeight),tint,
   image->ScreenX,image->ScreenY,image->ScaleX,image->ScaleY,image->Width,image->Height);
  return true;
 }
 private readonly record struct Description(nint Parts,nint PartArray,ushort PartID,nint Asset,nint Kernel,nint Srv,
  uint AllocatedWidth,uint AllocatedHeight,uint ActualWidth,uint ActualHeight,Vector2 UV0,Vector2 UV1,uint Tint,
  float ScreenX,float ScreenY,float ScaleX,float ScaleY,ushort Width,ushort Height);
}
