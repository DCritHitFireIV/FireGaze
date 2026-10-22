using System.Numerics;
using Dalamud.Interface.Textures;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitCaptureRect(float X,float Y,float Width,float Height)
{
 public bool IsValid=>float.IsFinite(X)&&float.IsFinite(Y)&&float.IsFinite(Width)&&float.IsFinite(Height)
  &&Width>0&&Height>0&&float.IsFinite(X+Width)&&float.IsFinite(Y+Height);
}
internal readonly record struct PortraitCaptureSize(int Width,int Height);
internal readonly record struct PortraitCaptureViewport(uint ID,Vector2 Position,Vector2 Size)
{
 public bool IsValid=>ID!=0&&float.IsFinite(Position.X)&&float.IsFinite(Position.Y)
  &&float.IsFinite(Size.X)&&float.IsFinite(Size.Y)&&Size.X>0&&Size.Y>0;
}

internal static class PortraitCaptureGeometry
{
 public const int MaximumPixels=1_048_576;
 public static bool TryViewportCrop(PortraitCaptureRect rect,PortraitCaptureViewport viewport,out ImGuiViewportTextureArgs args)
 {
  args=default;
  if(!rect.IsValid||!viewport.IsValid)return false;
  var left=rect.X-viewport.Position.X;var top=rect.Y-viewport.Position.Y;
  if(left<0||top<0||left+rect.Width>viewport.Size.X||top+rect.Height>viewport.Size.Y)return false;
  args=new(){ViewportId=viewport.ID,AutoUpdate=false,TakeBeforeImGuiRender=true,KeepTransparency=false,
   Uv0=new(left/viewport.Size.X,top/viewport.Size.Y),Uv1=new((left+rect.Width)/viewport.Size.X,(top+rect.Height)/viewport.Size.Y)};
  return true;
 }
 public static bool TryFit(int width,int height,int maximumPixels,out PortraitCaptureSize result)
 {
  result=default;
  if(width is <=0 or >16384||height is <=0 or >16384||maximumPixels is <=0 or >MaximumPixels)return false;
  var scale=Math.Min(1d,Math.Sqrt(maximumPixels/((double)width*height)));
  var w=Math.Max(1,(int)Math.Floor(width*scale));var h=Math.Max(1,(int)Math.Floor(height*scale));
  if((long)w*h>maximumPixels)return false;
  result=new(w,h);return true;
 }
 public static bool TryTextureCopy(uint allocatedWidth,uint allocatedHeight,uint actualWidth,uint actualHeight,
  PortraitCaptureSize output,out TextureModificationArgs args)
 {
  args=default;
  if(allocatedWidth is 0 or >16384||allocatedHeight is 0 or >16384||actualWidth==0||actualHeight==0
   ||actualWidth>allocatedWidth||actualHeight>allocatedHeight||output.Width<=0||output.Height<=0
   ||output.Width>actualWidth||output.Height>actualHeight||(long)output.Width*output.Height>MaximumPixels)return false;
  args=new(){NewWidth=output.Width,NewHeight=output.Height,DxgiFormat=28,
   Uv0=Vector2.Zero,Uv1=new(actualWidth/(float)allocatedWidth,actualHeight/(float)allocatedHeight)};
  return true;
 }
 public static bool TryPlacement(PortraitCaptureRect character,PortraitCaptureRect layer,out PortraitCaptureRect result)
 {
  result=default;if(!character.IsValid||!layer.IsValid)return false;
  result=new((layer.X-character.X)/character.Width,(layer.Y-character.Y)/character.Height,
   layer.Width/character.Width,layer.Height/character.Height);
  return result.IsValid&&Math.Abs(result.X)<=4&&Math.Abs(result.Y)<=4&&result.Width<=8&&result.Height<=8;
 }
}
