using System.Runtime.InteropServices;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

// Real source-editor layout. The seam replaces native process calls only; native field copies,
// slot normalization, allocation identities and capture consumer all execute production code.
unsafe sealed class SourceFixture : IPortraitNativeApi,IPortraitEditorNativeApi,IDisposable
{
    public readonly AgentBannerEditor* Editor=(AgentBannerEditor*)NativeMemory.AllocZeroed((nuint)sizeof(AgentBannerEditor));
    public readonly AgentBannerEditorState* State=(AgentBannerEditorState*)NativeMemory.AllocZeroed((nuint)sizeof(AgentBannerEditorState));
    public readonly CharaViewPortrait* View=(CharaViewPortrait*)NativeMemory.AllocZeroed((nuint)sizeof(CharaViewPortrait));
    public readonly Character* Actor=(Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
    public readonly Character* Real=(Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
    public Action? AfterExport;
    public SourceFixture()
    {
        Editor->EditorState=State;State->AgentBannerEditor=Editor;State->CharaView=View;
        State->OpenType=AgentBannerEditorState.EditorOpenType.Gearset;View->State=6;View->CharaViewPortraitCharacterLoaded=true;
        Actor->DrawData.OwnerObject=Actor;*(uint*)((byte*)Actor+0x74)=17;
        View->ModelData.CustomizeData.Race=1;View->ModelData.CustomizeData.Sex=1;View->ModelData.CustomizeData.Tribe=2;
        View->PortraitCharacterData.CustomizeData=View->ModelData.CustomizeData;
        for(int i=0;i<View->Items.Length;i++)View->Items[i].SlotId=14;
        View->CameraPosition=new(0,0,1.2f,0);View->CameraDistance=1.2f;View->CameraZoomNormalized=.5f;
    }
    public bool IsFrameworkThread=>true;
    public AgentBannerEditor* GetEditor()=>Editor;
    public bool IsEditorActive(AgentBannerEditor* editor)=>true;
    public bool TryGetBounds(AgentBannerEditor* editor,out PortraitEditorBounds bounds){bounds=default;return true;}
    public Character* GetLocalPlayer()=>Real;
    public Character* GetCharacter(CharaViewPortrait* view)=>view==View?Actor:null;
    public void Export(CharaViewPortrait* view,ExportedPortraitData* data){*data=default;data->BannerTimeline=12;data->AnimationProgress=.25f;AfterExport?.Invoke();}
    public bool IsPaused(CharaViewPortrait* view)=>true;
    public void SetItem(CharaViewPortrait* view,byte slot,PortraitPiece piece,bool companyCrest)=>throw new Exception("Source capture cannot set gear");
    public void SetGlasses(Character* character,ushort glasses)=>throw new Exception("Source capture cannot set glasses");
    public void SetVisibility(CharaViewPortrait* view,bool headVisible,bool weaponVisible,bool visor)=>throw new Exception("Source capture cannot set visibility");
    public void HideEditor(AgentBannerEditor* editor)=>throw new Exception("Source capture cannot hide editor");
    public bool TryHideOriginalEditor(AgentBannerEditor* editor,AgentBannerEditorState* state)=>throw new Exception("Source capture cannot hide editor");
    public void SetPoseTimed(CharaViewPortrait* view,ushort timeline,float time)=>throw new Exception("Source capture cannot set pose");
    public void SetExpression(CharaViewPortrait* view,byte expression)=>throw new Exception("Source capture cannot set expression");
    public void SetHeadDirection(CharaViewPortrait* view,float x,float y)=>throw new Exception("Source capture cannot set gaze");
    public void SetEyeDirection(CharaViewPortrait* view,float x,float y)=>throw new Exception("Source capture cannot set gaze");
    public void SetCameraZoom(CharaViewPortrait* view,byte zoom)=>throw new Exception("Source capture cannot set camera");
    public void SetBackground(CharaViewPortrait* view,ushort background)=>throw new Exception("Source capture cannot set background");
    public void SetDirectionalColor(CharaViewPortrait* view,byte red,byte green,byte blue)=>throw new Exception("Source capture cannot set light");
    public void SetDirectionalBrightness(CharaViewPortrait* view,byte value)=>throw new Exception("Source capture cannot set light");
    public void SetDirectionalAngle(CharaViewPortrait* view,short vertical,short horizontal)=>throw new Exception("Source capture cannot set light");
    public void SetAmbientColor(CharaViewPortrait* view,byte red,byte green,byte blue)=>throw new Exception("Source capture cannot set light");
    public void SetAmbientBrightness(CharaViewPortrait* view,byte value)=>throw new Exception("Source capture cannot set light");
    public void SetPaused(CharaViewPortrait* view,bool paused)=>throw new Exception("Source capture cannot set pause");
    public void SetCharacterVisible(CharaViewPortrait* view,bool visible)=>throw new Exception("Source capture cannot set visibility");
    public void ApplyCamera(CharaViewPortrait* view)=>throw new Exception("Source capture cannot set camera");
    public void SetFrame(AgentBannerEditorState* state,ushort frame)=>throw new Exception("Source capture cannot set frame");
    public void SetAccent(AgentBannerEditorState* state,ushort accent)=>throw new Exception("Source capture cannot set accent");
    public bool UpdateCustomization(CharaViewPortrait* view,Character* character,byte[] customize)=>throw new Exception("Source capture cannot set beauty");
    public void SetEars(Character* character,bool hidden)=>throw new Exception("Source capture cannot set beauty");
    public void Dispose(){NativeMemory.Free(Editor);NativeMemory.Free(State);NativeMemory.Free(View);NativeMemory.Free(Actor);NativeMemory.Free(Real);}
}
