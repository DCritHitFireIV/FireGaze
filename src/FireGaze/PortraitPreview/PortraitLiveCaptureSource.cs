using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using NativeBounds=FFXIVClientStructs.FFXIV.Common.Math.Bounds;

namespace FireGaze.PortraitPreview;

/// <summary>The caller supplies a fresh ready, paused editor snapshot key, never an observed cached state.</summary>
internal sealed record PortraitCaptureEditorFrame(PortraitIdentity Identity,string StateKey);

internal interface IPortraitCaptureGameApi
{
 bool IsFrameworkThread{get;}
 PortraitCaptureEditorFrame? ReadEditor();
 PortraitCaptureOwner ReadOwner();
 nint ReadEditorAddon();
 nint ReadPlateAddon();
 nint ReadPlateAgent();
 ComponentType ReadComponentType(nint component);
 PortraitCaptureRect? ReadNodeBounds(nint node);
 PortraitCaptureRect? ReadWindowBounds(nint addon);
 IDalamudTextureWrap Retain(nint handle,int width,int height);
}

/// <summary>All native reads occur synchronously on Framework, before any public texture task.</summary>
internal sealed unsafe class PortraitLiveCaptureSource:IPortraitLiveCaptureSource
{
 private readonly IPortraitCaptureGameApi api;
 private readonly IAddonLifecycle? lifecycle;
 private long generation=1;
 private bool disposed;
 public PortraitLiveCaptureSource(IFramework framework,IGameGui gameGUI,IObjectTable objects,
  IAddonLifecycle lifecycle,Func<PortraitCaptureEditorFrame?> readReadyEditor)
  :this(new LiveGameApi(framework,gameGUI,objects,readReadyEditor))
 {
  this.lifecycle=lifecycle;
  lifecycle.RegisterListener(AddonEvent.PreFinalize,"BannerEditor",BeforeFinalize);
  lifecycle.RegisterListener(AddonEvent.PreFinalize,"CharaCard",BeforeFinalize);
 }
 internal PortraitLiveCaptureSource(IPortraitCaptureGameApi api)=>this.api=api;
 public bool IsFrameworkThread=>!disposed&&api.IsFrameworkThread;
 private void BeforeFinalize(AddonEvent type,AddonArgs args)=>AdvanceGeneration();
 internal void AdvanceGeneration()=>generation++;

 public bool TrySample(PortraitImageScope scope,PortraitCaptureViewport viewport,out PortraitCaptureSample? sample)
 {
  sample=null;
  if(!IsFrameworkThread||scope is not(PortraitImageScope.Portrait or PortraitImageScope.VisibleAdventurerPlate))return false;
  var frame=api.ReadEditor();var owner=api.ReadOwner();var currentGeneration=generation;
  if(frame is null||!frame.Identity.IsValid||frame.Identity.Generation==0||string.IsNullOrWhiteSpace(frame.StateKey)
   ||!owner.IsValid||!EditorCurrent(frame,owner))return false;
  PortraitPlateCaptureBinding? plate=null;
  if(scope==PortraitImageScope.VisibleAdventurerPlate)
  {
   if(!TryPlate(owner,out var addon,out var agent,out var storage))return false;
   var rect=api.ReadWindowBounds((nint)addon);
   if(!EditorCurrent(frame,owner)||currentGeneration!=generation
    ||!TryPlate(owner,out var freshAddon,out var freshAgent,out var freshStorage)
    ||freshAddon!=addon||freshAgent!=agent||freshStorage!=storage||rect is not{IsValid:true} bounds
    ||!PortraitCaptureGeometry.TryViewportCrop(bounds,viewport,out _))return false;
   var visual=PlateVisualKey(storage);
   if(visual is null)return false;
   plate=new((nint)addon,addon->Id,(nint)agent,(nint)storage,owner.AccountID,owner.ContentID,
    currentGeneration,bounds,visual);
  }
  if(!EditorCurrent(frame,owner)||currentGeneration!=generation)return false;
  sample=new(owner,frame.Identity,$"{currentGeneration}:{frame.StateKey}",scope,viewport,plate);
  return true;
 }

 private bool EditorCurrent(PortraitCaptureEditorFrame expected,PortraitCaptureOwner owner)
 {
  if(!IsFrameworkThread||api.ReadEditor()!=expected||api.ReadOwner()!=owner)return false;
  var addon=(AtkUnitBase*)api.ReadEditorAddon();
  if(addon==null||!addon->IsVisible||!addon->IsReady)return false;
  var editor=(AgentBannerEditor*)expected.Identity.Editor;
  if(editor==null||addon->Id!=editor->AddonId||editor->EditorState==null
   ||(nint)editor->EditorState!=expected.Identity.State)return false;
  var state=editor->EditorState;var view=state->CharaView;
  return state->AgentBannerEditor==editor&&view!=null&&(nint)view==expected.Identity.Model
   &&view->State==6&&view->CharaViewPortraitCharacterLoaded;
 }

 private bool TryPlate(PortraitCaptureOwner owner,out AtkUnitBase* addon,out AgentCharaCard* agent,out AgentCharaCard.Storage* storage)
 {
  addon=null;agent=null;storage=null;
  if(!IsFrameworkThread||api.ReadOwner()!=owner)return false;
  agent=(AgentCharaCard*)api.ReadPlateAgent();addon=(AtkUnitBase*)api.ReadPlateAddon();
  if(agent==null||addon==null||!addon->IsVisible||!addon->IsReady||addon->Id!=agent->AddonId)return false;
  storage=agent->Data;
  return storage!=null&&storage->AccountId==owner.AccountID&&storage->ContentId==owner.ContentID;
 }

 private bool Current(PortraitCaptureSample expected)
  =>TrySample(expected.Scope,expected.Viewport,out var current)&&current==expected;

 public bool TryDescribePortrait(PortraitCaptureSample sample,out PortraitCaptureSurface? surface)
 {
  surface=null;
  if(!TryAcquirePortrait(sample,out var inputs)||inputs is null)return false;
  using(inputs)
  {
   if(!Current(sample)||inputs.Surface is not{IsValid:true} described)return false;
   surface=described;return true;
  }
 }

 public bool TryAcquirePortrait(PortraitCaptureSample sample,out PortraitLiveCaptureInputs? inputs)
 {
  inputs=null;
  if(sample.Scope!=PortraitImageScope.Portrait||!IsFrameworkThread||!Current(sample))return false;
  var addon=api.ReadEditorAddon();
  var component=PortraitThumbnailSource.FindPortrait(addon,()=>Current(sample)&&api.ReadEditorAddon()==addon,api.ReadComponentType);
  if(component==0||!Current(sample)||api.ReadEditorAddon()!=addon)return false;
  bool ComponentCurrent()=>Current(sample)&&api.ReadEditorAddon()==addon
   &&PortraitThumbnailSource.FindPortrait(addon,()=>Current(sample)&&api.ReadEditorAddon()==addon,api.ReadComponentType)==component;
  var portrait=(AtkComponentPortrait*)component;
  PortraitCaptureNativeImage? character=null;var acquired=new List<PortraitCaptureNativeImage>();
  try
  {
   if(!PortraitCaptureNodeReader.TryAcquire(component,(nint)portrait->CharacterImageNode,true,
    ComponentCurrent,api.ReadNodeBounds,api.Retain,out character)||character is null)return false;
   acquired.Add(character);
   foreach(var kind in new[]{0,1})
   {
    if(!ComponentCurrent())return false;
    var image=kind==0?portrait->FrameImageNode:portrait->AccentImageNode;
    if(image==null||(image->NodeFlags&NodeFlags.Visible)==0)continue;
    if(!PortraitCaptureNodeReader.TryAcquire(component,(nint)image,false,
     ComponentCurrent,api.ReadNodeBounds,api.Retain,out var overlay)||overlay is null)return false;
    acquired.Add(overlay);
   }
   if(!Current(sample)||!PortraitCaptureGeometry.TryFit((int)character.ActualWidth,(int)character.ActualHeight,
    PortraitCaptureGeometry.MaximumPixels,out var size)||!PortraitCaptureGeometry.TryTextureCopy(character.AllocatedWidth,
     character.AllocatedHeight,character.ActualWidth,character.ActualHeight,size,out var copy))return false;
   var layers=new List<PortraitCaptureLayer>();
   foreach(var overlay in acquired.Skip(1))
   {
    if(!PortraitCaptureGeometry.TryPlacement(character.Bounds,overlay.Bounds,out var placement))return false;
    layers.Add(new(overlay.Texture,placement,overlay.UV0,overlay.UV1,overlay.Tint));
   }
   var key=FormattableString.Invariant($"{generation}|{component:X}");
   foreach(var image in acquired)
    key+=FormattableString.Invariant($"|{(ulong)image.Texture.Handle.Handle:X16}:{image.AllocatedWidth}:{image.AllocatedHeight}:{image.ActualWidth}:{image.ActualHeight}:{image.Bounds.X:R}:{image.Bounds.Y:R}:{image.Bounds.Width:R}:{image.Bounds.Height:R}:{image.UV0.X:R}:{image.UV0.Y:R}:{image.UV1.X:R}:{image.UV1.Y:R}:{image.Tint}");
   var surface=new PortraitCaptureSurface(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))),size.Width,size.Height);
   inputs=new(new(character.Texture,copy),layers,size,surface);acquired.Clear();return true;
  }
  finally{PortraitCaptureCleanupException.DisposeAll(acquired.Select(layer=>(IDisposable)layer.Texture).ToArray());}
 }

 private static string? PlateVisualKey(AgentCharaCard.Storage* storage)
 {
  using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
  // Known pointer-free design/demographic blocks, not the animated CharaView or allocation padding.
  hash.AppendData(new ReadOnlySpan<byte>((byte*)storage+24,27));
  hash.AppendData(new ReadOnlySpan<byte>((byte*)storage+80,16));
  hash.AppendData(new ReadOnlySpan<byte>((byte*)storage+200,24));
  hash.AppendData(new ReadOnlySpan<byte>((byte*)storage+536,64));
  hash.AppendData(new ReadOnlySpan<byte>((byte*)storage+1272,25));
  hash.AppendData(new ReadOnlySpan<byte>(&storage->PortraitData,sizeof(ExportedPortraitData)));
  if(!AddText(hash,&storage->Name)||!AddText(hash,&storage->FreeCompany)||!AddText(hash,&storage->SearchComment))return null;
  foreach(ref var activity in storage->Activities)
  {
   var icon=activity.IconId;hash.AppendData(new ReadOnlySpan<byte>(&icon,sizeof(uint)));
   fixed(Utf8String* name=&activity.Name)if(!AddText(hash,name))return null;
  }
  return Convert.ToHexString(hash.GetHashAndReset());
 }
 private static bool AddText(IncrementalHash hash,Utf8String* text)
 {
  if(text->StringLength>4096||text->StringLength!=0&&(byte*)text->StringPtr==null)return false;
  var count=(uint)text->StringLength;hash.AppendData(new ReadOnlySpan<byte>(&count,sizeof(uint)));
  if(count!=0)hash.AppendData(new ReadOnlySpan<byte>(text->StringPtr,(int)count));return true;
 }

 public void Dispose()
 {
  if(disposed)return;disposed=true;generation++;
  lifecycle?.UnregisterListener(AddonEvent.PreFinalize,"BannerEditor",BeforeFinalize);
  lifecycle?.UnregisterListener(AddonEvent.PreFinalize,"CharaCard",BeforeFinalize);
 }

 private sealed class LiveGameApi(IFramework framework,IGameGui gameGUI,IObjectTable objects,
  Func<PortraitCaptureEditorFrame?> editor):IPortraitCaptureGameApi
 {
  public bool IsFrameworkThread=>framework.IsInFrameworkUpdateThread;
  public PortraitCaptureEditorFrame? ReadEditor()=>IsFrameworkThread?editor():null;
  public PortraitCaptureOwner ReadOwner()
  {
   var local=IsFrameworkThread?(Character*)(objects.LocalPlayer?.Address??0):null;
   return local==null?default:new(local->AccountId,local->ContentId);
  }
  public nint ReadEditorAddon()=>IsFrameworkThread?(nint)gameGUI.GetAddonByName<AtkUnitBase>("BannerEditor"):0;
  public nint ReadPlateAddon()=>IsFrameworkThread?(nint)gameGUI.GetAddonByName<AtkUnitBase>("CharaCard"):0;
  public nint ReadPlateAgent()=>IsFrameworkThread?(nint)AgentCharaCard.Instance():0;
  public ComponentType ReadComponentType(nint component)=>((AtkComponentBase*)component)->GetComponentType();
  public PortraitCaptureRect? ReadNodeBounds(nint node)
  {
   NativeBounds bounds=default;((AtkResNode*)node)->GetBounds(&bounds);
   return new(bounds.Pos1.X,bounds.Pos1.Y,bounds.Width,bounds.Height);
  }
  public PortraitCaptureRect? ReadWindowBounds(nint addon)
  {
   NativeBounds bounds=default;((AtkUnitBase*)addon)->GetWindowBounds(&bounds);
   return new(bounds.Pos1.X,bounds.Pos1.Y,bounds.Width,bounds.Height);
  }
  public IDalamudTextureWrap Retain(nint handle,int width,int height)
   =>((IDalamudTextureWrap)new BorrowedTexture(handle,width,height)).CreateWrapSharingLowLevelResource();
 }
 private sealed class BorrowedTexture(nint handle,int width,int height):IDalamudTextureWrap
 {
  public ImTextureID Handle=>new(handle);
  public int Width=>width;
  public int Height=>height;
  public void Dispose(){}
 }
}
