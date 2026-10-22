using System.Runtime.InteropServices;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
unsafe sealed class EditorFixture : IPortraitNativeApi,IPortraitEditorNativeApi,IDisposable {
 public AgentBannerEditor* Editor=(AgentBannerEditor*)NativeMemory.AllocZeroed((nuint)sizeof(AgentBannerEditor));
 public AgentBannerEditorState* State=(AgentBannerEditorState*)NativeMemory.AllocZeroed((nuint)sizeof(AgentBannerEditorState));
 public CharaViewPortrait* View=(CharaViewPortrait*)NativeMemory.AllocZeroed((nuint)sizeof(CharaViewPortrait));
 public Character* Actor=(Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
 public Character* Real=(Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
 public ExportedPortraitData Data;public bool Paused,Framework=true,RejectCustomize,ThrowPose,ReplaceOnPose,ThrowCustomize,ThrowItem,RestartPoseOnSeek,UseNativeTimelineBias=true,DeferGazeExport;public int Calls,FrameCalls,AccentCalls,VisibilityCalls,PoseCalls,PauseCalls;
 public EditorFixture(){Editor->EditorState=State;State->AgentBannerEditor=Editor;State->CharaView=View;State->OpenType=AgentBannerEditorState.EditorOpenType.Gearset;View->State=6;View->CharaViewPortraitCharacterLoaded=true;Actor->DrawData.OwnerObject=Actor;*(uint*)((byte*)Actor+0x74)=17;View->ModelData.CustomizeData.Race=1;View->ModelData.CustomizeData.Sex=1;View->ModelData.CustomizeData.Tribe=2;View->ModelData.CustomizeData.Hairstyle=4;View->PortraitCharacterData.CustomizeData=View->ModelData.CustomizeData;for(int i=0;i<View->Items.Length;i++)View->Items[i].SlotId=14;View->CharacterVisible=true;View->CameraPosition=new(1.234567f,2,3,4);View->CameraTarget=new(5,6,7,8);View->CameraYaw=2.5f;Data.BannerTimeline=12;Data.AnimationProgress=.25f;Data.Expression=7;Data.BannerBg=30;State->BannerEntry.BannerFrame=40;State->BannerEntry.BannerDecoration=50;}
 public bool IsFrameworkThread=>Framework;public AgentBannerEditor* GetEditor()=>Editor;public bool IsEditorActive(AgentBannerEditor* e)=>true;public bool TryGetBounds(AgentBannerEditor* e,out PortraitEditorBounds b){b=default;return true;}public Character* GetLocalPlayer()=>Real;public Character* GetCharacter(CharaViewPortrait* v)=>Actor;
 public void SetItem(CharaViewPortrait* v,byte slot,PortraitPiece p,bool crest){Calls++;if(ThrowItem)throw new InvalidOperationException("wardrobe diagnostic sentinel");v->Items[0]=new(){SlotId=slot,ItemId=p.ItemID,Stain0Id=p.Stain0,Stain1Id=p.Stain1};}
 public void SetGlasses(Character* c,ushort x){Calls++;c->DrawData.GlassesIds[0]=x;}public void SetVisibility(CharaViewPortrait* v,bool h,bool w,bool vis){v->PortraitCharacterData.HeadgearHidden=!h;v->PortraitCharacterData.WeaponHidden=!w;v->PortraitCharacterData.VisorClosed=vis;}public void HideEditor(AgentBannerEditor* e)=>e->EditorState=null;public bool TryHideOriginalEditor(AgentBannerEditor* e,AgentBannerEditorState* s)=>false;
 public void SetCharacterVisible(CharaViewPortrait* v,bool visible){VisibilityCalls++;v->CharacterVisible=visible;}
 public void Export(CharaViewPortrait* v,ExportedPortraitData* d)=>*d=Data;
 public void SetPoseTimed(CharaViewPortrait* v,ushort timeline,float time){Calls++;PoseCalls++;if(ThrowPose){ThrowPose=false;throw new InvalidOperationException("pose failure");}Data.BannerTimeline=timeline;Data.AnimationProgress=UseNativeTimelineBias?MathF.Max(0,time-.001f):time;if(RestartPoseOnSeek)Data.AnimationProgress=0;if(ReplaceOnPose)Editor->EditorState=null;}
 public void SetExpression(CharaViewPortrait* v,byte value){Calls++;Data.Expression=value;}
 public void SetHeadDirection(CharaViewPortrait* v,float x,float y){Calls++;Actor->LookAt.BannerCameraFollowFlag&=~LookAtContainer.BannerCameraFollowFlags.Head;Actor->LookAt.BannerHeadDirection=new(x,y);if(!DeferGazeExport)Data.HeadDirection=new((Half)x,(Half)y);}
 public void SetEyeDirection(CharaViewPortrait* v,float x,float y){Calls++;Actor->LookAt.BannerCameraFollowFlag&=~LookAtContainer.BannerCameraFollowFlags.Eyes;Actor->LookAt.BannerEyeDirection=new(x,y);if(!DeferGazeExport)Data.EyeDirection=new((Half)x,(Half)y);}
 public void SetCameraZoom(CharaViewPortrait* v,byte value){Calls++;v->CameraZoom=value;Data.CameraZoom=value;}
 public void SetBackground(CharaViewPortrait* v,ushort value){Calls++;Data.BannerBg=value;}
 public void SetDirectionalColor(CharaViewPortrait* v,byte red,byte green,byte blue){Calls++;Data.DirectionalLightingColorRed=red;Data.DirectionalLightingColorGreen=green;Data.DirectionalLightingColorBlue=blue;}
 public void SetDirectionalBrightness(CharaViewPortrait* v,byte value){Calls++;Data.DirectionalLightingBrightness=value;}
 public void SetDirectionalAngle(CharaViewPortrait* v,short vertical,short horizontal){Calls++;Data.DirectionalLightingVerticalAngle=vertical;Data.DirectionalLightingHorizontalAngle=horizontal;}
 public void SetAmbientColor(CharaViewPortrait* v,byte red,byte green,byte blue){Calls++;Data.AmbientLightingColorRed=red;Data.AmbientLightingColorGreen=green;Data.AmbientLightingColorBlue=blue;}
 public void SetAmbientBrightness(CharaViewPortrait* v,byte value){Calls++;Data.AmbientLightingBrightness=value;}
 public bool IsPaused(CharaViewPortrait* v)=>Paused;public void SetPaused(CharaViewPortrait* v,bool p){Calls++;PauseCalls++;Paused=p;}public void ApplyCamera(CharaViewPortrait* v){Calls++;Data.ImageRotation=v->ImageRotation;Data.CameraZoom=v->CameraZoom;}public void SetFrame(AgentBannerEditorState* s,ushort x){Calls++;FrameCalls++;s->BannerEntry.BannerFrame=x;}public void SetAccent(AgentBannerEditorState* s,ushort x){Calls++;AccentCalls++;s->BannerEntry.BannerDecoration=x;}
 public void SetEars(Character* c,bool ears) { }
 public bool UpdateCustomization(CharaViewPortrait* v,Character* c,byte[] x){Calls++;if(ThrowCustomize){ThrowCustomize=false;throw new InvalidOperationException("customize failure");}if(RejectCustomize)return false;x.AsSpan().CopyTo(c->DrawData.CustomizeData.Data);return true;}
 public void Dispose(){NativeMemory.Free(Editor);NativeMemory.Free(State);NativeMemory.Free(View);NativeMemory.Free(Actor);NativeMemory.Free(Real);}
}
