using FireGaze.PortraitPreview;
#if HAS_CAPTURE_NATIVE
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using Dalamud.Interface.Textures.TextureWraps;
#endif

internal static class CaptureNativeOwnerTests
{
 public static unsafe int Run()
 {
#if !HAS_CAPTURE_NATIVE
  throw new InvalidOperationException("Native sample source does not yet verify editor/local-owner/visible-plate identities.");
#else
  var checks=0;void Check(bool value,string why){checks++;if(!value)throw new InvalidOperationException(why);}
  AgentBannerEditor editor=default;AgentBannerEditorState state=default;CharaViewPortrait view=default;AtkUnitBase addon=default;
  AgentCharaCard plateAgent=default;AgentCharaCard.Storage storage=default;AtkUnitBase card=default;
  editor.EditorState=&state;editor.AddonId=11;state.AgentBannerEditor=&editor;state.CharaView=&view;
  view.State=6;view.CharaViewPortraitCharacterLoaded=true;addon.Id=11;addon.IsVisible=true;addon.Flags1A1|=1;
  plateAgent.Data=&storage;plateAgent.AddonId=21;storage.AccountId=30;storage.ContentId=40;card.Id=21;card.IsVisible=true;card.Flags1A1|=1;
  var game=new FakeNativeCaptureGame{EditorAddon=(nint)(&addon),PlateAddon=(nint)(&card),PlateAgent=(nint)(&plateAgent),Owner=new(30,40),
   Frame=new(new((nint)(&editor),(nint)(&state),(nint)(&view),7,8),"frame")};
  using var source=new PortraitLiveCaptureSource(game);
  var viewport=new PortraitCaptureViewport(1,System.Numerics.Vector2.Zero,new(1920,1080));
  game.Framework=false;Check(!source.TrySample(PortraitImageScope.Portrait,viewport,out _)&&game.Reads==0,"non-Framework sampling never calls game APIs");game.Framework=true;
  Check(source.TrySample(PortraitImageScope.Portrait,viewport,out var sample)&&sample is{Owner.AccountID:30,Owner.ContentID:40},"portrait binds the fresh local account/content identity");
  var first=sample;
  source.AdvanceGeneration();Check(source.TrySample(PortraitImageScope.Portrait,viewport,out sample)&&sample!=first,"same native address after lifecycle finalization gets a new source generation");
  addon.IsVisible=false;Check(!source.TrySample(PortraitImageScope.Portrait,viewport,out _),"hidden portrait editor cannot supply a sample");addon.IsVisible=true;
  view.CharaViewPortraitCharacterLoaded=false;Check(!source.TrySample(PortraitImageScope.Portrait,viewport,out _),"unloaded character cannot supply pixels");view.CharaViewPortraitCharacterLoaded=true;
  Check(source.TrySample(PortraitImageScope.VisibleAdventurerPlate,viewport,out sample)&&sample?.Plate?.Rect==new PortraitCaptureRect(100,80,800,500),"plate sample includes actual whole-window screen bounds");
  first=sample;storage.BannerBg=17;
  Check(source.TrySample(PortraitImageScope.VisibleAdventurerPlate,viewport,out sample)&&sample!=first,"plate design change invalidates prior image even when source addresses are stable");
  storage.ContentId=99;Check(!source.TrySample(PortraitImageScope.VisibleAdventurerPlate,viewport,out _),"another player's card is never sampled");storage.ContentId=40;
  game.Bounds=new(-10,80,800,500);Check(!source.TrySample(PortraitImageScope.VisibleAdventurerPlate,viewport,out _),"partially offscreen plate is rejected instead of silently cropped");game.Bounds=new(100,80,800,500);
  var cardAddress=game.PlateAddon;game.OnWindowBounds=()=>((AtkUnitBase*)cardAddress)->IsVisible=false;
  Check(!source.TrySample(PortraitImageScope.VisibleAdventurerPlate,viewport,out _),"native bounds callback hiding the card invalidates the sample before storage is reused");card.IsVisible=true;game.OnWindowBounds=null;
  AtkComponentPortrait component=default;AtkComponentNode componentNode=default;AtkImageNode image=default;
  AtkUldPartsList parts=default;AtkUldPart part=default;AtkUldAsset asset=default;Texture kernel=default;
  var addonNodes=stackalloc AtkResNode*[1];addonNodes[0]=(AtkResNode*)&componentNode;
  addon.UldManager.NodeList=addonNodes;addon.UldManager.NodeListCount=1;
  componentNode.Type=(NodeType)1000;componentNode.Component=(AtkComponentBase*)&component;component.OwnerNode=&componentNode;
  var componentNodes=stackalloc AtkResNode*[1];componentNodes[0]=(AtkResNode*)&image;
  component.UldManager.NodeList=componentNodes;component.UldManager.NodeListCount=1;component.CharacterImageNode=&image;
  image.Type=NodeType.Image;image.NodeFlags=NodeFlags.Visible;image.ScaleX=image.ScaleY=1;image.PartsList=&parts;
  image.Color.R=image.Color.G=image.Color.B=image.Color.A=255;parts.Parts=&part;parts.PartCount=1;
  part.UldAsset=&asset;part.Width=160;part.Height=256;asset.AtkTexture.TextureType=TextureType.KernelTexture;asset.AtkTexture.KernelTexture=&kernel;
  kernel.AllocatedWidth=512;kernel.AllocatedHeight=512;kernel.ActualWidth=400;kernel.ActualHeight=500;kernel.D3D11ShaderResourceView=(void*)123;
  PortraitLiveCaptureInputs? inputs=null;Check(source.TrySample(PortraitImageScope.Portrait,viewport,out sample)&&source.TryAcquirePortrait(sample!,out inputs)&&inputs is not null,"native portrait source retains actual character render texture");
  Check(inputs!.Surface is{IsValid:true,Width:400,Height:500},"native capture surface records actual output dimensions");inputs.Dispose();
  Check(source.TryDescribePortrait(sample!,out var surface)&&surface is{IsValid:true},"native surface description retains real source without GPU pixel copy");
  game.Frame=game.Frame! with{StateKey="different-light-RGB"};
  Check(source.TrySample(PortraitImageScope.Portrait,viewport,out sample)&&source.TryDescribePortrait(sample!,out var colorSurface)&&colorSurface==surface,"native surface generation excludes color-only full-state changes");
  kernel.D3D11ShaderResourceView=(void*)321;
  Check(source.TryDescribePortrait(sample!,out var srvSurface)&&srvSurface!=surface,"a replaced real SRV changes capture generation");
  kernel.ActualWidth=390;
  Check(source.TryDescribePortrait(sample!,out var sized)&&sized is{Width:390}&&sized!=srvSurface,"native render-target size changes capture generation");
  source.AdvanceGeneration();
  Check(source.TrySample(PortraitImageScope.Portrait,viewport,out sample)&&source.TryDescribePortrait(sample!,out var generationSurface)&&generationSurface!=sized,"same SRV reused after lifecycle changes capture generation");
  Check(game.Textures.All(x=>x.Disposes==1),"every independent native surface-description reference is released exactly once");
  // Render-target dimensions are validated at TryAcquire and copied dimensions are validated by
  // the async pipeline. The callback's immutable editor key intentionally does not dereference
  // GPU resources, so a reallocated target cannot be carried across await.
  var retainedBefore=game.Retains;var addonAddress=(nint)(&addon);game.OnNodeBounds=()=>((AtkUnitBase*)addonAddress)->UldManager.NodeListCount=0;
  Check(!source.TryAcquirePortrait(sample!,out _)&&game.Retains==retainedBefore,"component removed from still-live addon during GetBounds is rejected before retaining its old resource");
  source.Dispose();Check(!source.TrySample(PortraitImageScope.Portrait,viewport,out _),"disposed native source refuses future reads");
  return checks;
#endif
 }
#if HAS_CAPTURE_NATIVE
 private sealed class FakeNativeCaptureGame:IPortraitCaptureGameApi
 {
  public bool Framework=true;public int Reads;public nint EditorAddon,PlateAddon,PlateAgent;public PortraitCaptureOwner Owner;public PortraitCaptureEditorFrame? Frame;
  public PortraitCaptureRect Bounds=new(100,80,800,500);public Action? OnWindowBounds,OnNodeBounds;public int Retains;
  public bool IsFrameworkThread=>Framework;
  public PortraitCaptureEditorFrame? ReadEditor(){Reads++;return Frame;}
  public PortraitCaptureOwner ReadOwner(){Reads++;return Owner;}
  public nint ReadEditorAddon(){Reads++;return EditorAddon;}
  public nint ReadPlateAddon(){Reads++;return PlateAddon;}
  public nint ReadPlateAgent(){Reads++;return PlateAgent;}
  public ComponentType ReadComponentType(nint component)=>ComponentType.Portrait;
  public PortraitCaptureRect? ReadNodeBounds(nint node){OnNodeBounds?.Invoke();return new(0,0,160,256);}
  public PortraitCaptureRect? ReadWindowBounds(nint addon){OnWindowBounds?.Invoke();return Bounds;}
  public List<CountingCaptureTexture> Textures=[];
  public IDalamudTextureWrap Retain(nint handle,int width,int height){Retains++;var texture=new CountingCaptureTexture(width,height,handle);Textures.Add(texture);return texture;}
 }
#endif
}
