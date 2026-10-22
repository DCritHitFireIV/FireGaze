using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

/// <summary>Only process-native calls are replaceable in offline lifecycle tests.</summary>
internal interface IPortraitOwnedCharaViewNativeApi
{
    nint Allocate();
    void FreeUnconstructed(nint view);
    nint Construct(nint view);
    void Setup(nint view,uint slot,CharaViewCharacterData data);
    void Render(nint view,uint nativeFrame);
    nint GetCharacter(nint view);
    nint WorldCharacter { get; }
    void SetPoseTimed(nint view,ushort timeline,float time);
    void SetExpression(nint view,byte expression);
    void SetHeadDirection(nint view,float x,float y);
    void SetEyeDirection(nint view,float x,float y);
    void SetDirectionalColor(nint view,byte red,byte green,byte blue);
    void SetDirectionalBrightness(nint view,byte value);
    void SetDirectionalAngle(nint view,short vertical,short horizontal);
    void SetAmbientColor(nint view,byte red,byte green,byte blue);
    void SetAmbientBrightness(nint view,byte value);
    void SetBackground(nint view,ushort background);
    void SetCameraZoom(nint view,byte zoom);
    void ApplyCamera(nint view);
    void SetPaused(nint view,bool paused);
    bool IsPaused(nint view);
    void SetCharacterVisible(nint view,bool visible);
    void Release(nint view);
    void Destroy(nint view,byte freeFlags);
}

/// <summary>
/// Real SDK adapter. No world-character import, managed patch, source editor state or callback
/// delegate is involved. Merely constructing this adapter performs no native operation.
/// </summary>
internal sealed unsafe class PortraitOwnedCharaViewNativeApi(Func<nint> worldCharacter) : IPortraitOwnedCharaViewNativeApi
{
    public nint Allocate()
    {
        var space=IMemorySpace.GetUISpace();
        return space==null ? 0 : (nint)space->Malloc((ulong)sizeof(CharaViewPortrait),0);
    }
    public void FreeUnconstructed(nint view)=>IMemorySpace.Free((void*)view,(ulong)sizeof(CharaViewPortrait));
    public nint Construct(nint view)=>(nint)((CharaViewPortrait*)view)->Ctor();
    public void Setup(nint view,uint slot,CharaViewCharacterData data)=>((CharaViewPortrait*)view)->Setup(slot,&data,null,0,0);
    public void Render(nint view,uint nativeFrame)=>((CharaViewPortrait*)view)->Render(nativeFrame);
    public nint GetCharacter(nint view)=>(nint)((CharaViewPortrait*)view)->GetCharacter();
    public nint WorldCharacter=>worldCharacter();
    public void SetPoseTimed(nint view,ushort timeline,float time)=>((CharaViewPortrait*)view)->SetPoseTimed(timeline,time);
    public void SetExpression(nint view,byte expression)=>((CharaViewPortrait*)view)->SetExpression(expression);
    public void SetHeadDirection(nint view,float x,float y)=>((CharaViewPortrait*)view)->SetHeadDirection(x,y);
    public void SetEyeDirection(nint view,float x,float y)=>((CharaViewPortrait*)view)->SetEyeDirection(x,y);
    public void SetDirectionalColor(nint view,byte red,byte green,byte blue)=>((CharaViewPortrait*)view)->SetDirectionalLightingColor(red,green,blue);
    public void SetDirectionalBrightness(nint view,byte value)=>((CharaViewPortrait*)view)->SetDirectionalLightingBrightness(value);
    public void SetDirectionalAngle(nint view,short vertical,short horizontal)=>((CharaViewPortrait*)view)->SetDirectionalLightingAngle(vertical,horizontal);
    public void SetAmbientColor(nint view,byte red,byte green,byte blue)=>((CharaViewPortrait*)view)->SetAmbientLightingColor(red,green,blue);
    public void SetAmbientBrightness(nint view,byte value)=>((CharaViewPortrait*)view)->SetAmbientLightingBrightness(value);
    public void SetBackground(nint view,ushort background)=>((CharaViewPortrait*)view)->SetBackground(background);
    public void SetCameraZoom(nint view,byte zoom)=>((CharaViewPortrait*)view)->SetCameraZoom(zoom);
    public void ApplyCamera(nint view)=>((CharaViewPortrait*)view)->ApplyCameraPositions();
    public void SetPaused(nint view,bool paused)=>((CharaViewPortrait*)view)->ToggleAnimationPlayback(paused);
    public bool IsPaused(nint view)=>((CharaViewPortrait*)view)->IsAnimationPaused();
    public void SetCharacterVisible(nint view,bool visible)=>((CharaViewPortrait*)view)->ToggleCharacterVisibility(visible);
    public void Release(nint view)=>((CharaViewPortrait*)view)->Release();
    public void Destroy(nint view,byte freeFlags)=>((CharaViewPortrait*)view)->Dtor(freeFlags);
}
