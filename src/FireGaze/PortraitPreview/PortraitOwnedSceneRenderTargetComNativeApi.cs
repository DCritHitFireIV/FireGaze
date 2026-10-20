using System.Runtime.InteropServices;
using Dalamud.Interface;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Exact installed TerraFX/Windows COM ABI, without a new assembly or package reference.
/// IUnknown QI/Release use the .NET implementations; descriptor methods use the audited
/// ID3D11 inherited slots. Default construction denies every acquisition. No call occurs
/// during construction, layout inspection, tests, or current production wiring.
/// </summary>
internal sealed unsafe class PortraitOwnedSceneRenderTargetComNativeApi(
    IPortraitOwnedSceneSlotNativeApi memory, Func<bool>? admission = null) : IPortraitOwnedSceneRenderTargetComApi
{
    private readonly object sync = new();
    private readonly Dictionary<nint,int> owned = [];
    private const int DeviceSlot=3, ResourceSlot=7, SrvDescSlot=8, TextureDescSlot=10;
    private const int Failure=unchecked((int)0x80004005);

    // The getter is borrowed. Only the QI result obtained by ComReader is released.
    internal static PortraitOwnedSceneRenderTargetComReader ForUI(IPortraitOwnedSceneSlotNativeApi memory,
        IUiBuilder ui, Func<bool>? admission = null)
        => new(new PortraitOwnedSceneRenderTargetComNativeApi(memory, admission), () => ui.DeviceHandle, admission);

    public int QueryInterface(nint source, Guid iid, out nint result)
    {
        result=0;
        if(source==0 || !Admitted())return Failure;
        var status=Marshal.QueryInterface(source,in iid,out result);
        if(status>=0 && result!=0)Track(result);
        return status;
    }
    public bool GetResource(nint srv,out nint resource)
    {
        resource=0;
        if(!TryMethod(srv,ResourceSlot,out var method))return false;
        nint result=0;
        ((delegate* unmanaged[MemberFunction]<nint,nint*,void>)method)(srv,&result);
        resource=result;if(result!=0)Track(result);return true;
    }
    public bool GetDevice(nint child,out nint device)
    {
        device=0;
        if(!TryMethod(child,DeviceSlot,out var method))return false;
        nint result=0;
        ((delegate* unmanaged[MemberFunction]<nint,nint*,void>)method)(child,&result);
        device=result;if(result!=0)Track(result);return true;
    }
    public bool GetTextureDesc(nint texture,out PortraitOwnedD3D11Texture2DDesc descriptor)
    {
        descriptor=default;
        if(!LayoutMatches() || !TryMethod(texture,TextureDescSlot,out var method))return false;
        PortraitOwnedD3D11Texture2DDesc result=default;
        ((delegate* unmanaged[MemberFunction]<nint,PortraitOwnedD3D11Texture2DDesc*,void>)method)(texture,&result);
        descriptor=result;return true;
    }
    public bool GetViewDesc(nint srv,out PortraitOwnedD3D11SrvDesc descriptor)
    {
        descriptor=default;
        if(!LayoutMatches() || !TryMethod(srv,SrvDescSlot,out var method))return false;
        PortraitOwnedD3D11SrvDesc result=default;
        ((delegate* unmanaged[MemberFunction]<nint,PortraitOwnedD3D11SrvDesc*,void>)method)(srv,&result);
        descriptor=result;return true;
    }
    public bool Release(nint value)
    {
        // Current native-phase admission may already have been revoked. These are independent
        // COM refs returned by successful calls, never borrowed Kernel/UI pointer values.
        lock(sync)
        {
            if(!owned.TryGetValue(value,out var count)||count<=0)return false;
            if(count==1)owned.Remove(value);else owned[value]=count-1;
        }
        // Consume the one release permit BEFORE calling external COM. A throw is uncertain,
        // so the higher physical scope quarantines it and never retries.
        Marshal.Release(value);return true;
    }
    private void Track(nint value){lock(sync)owned[value]=owned.TryGetValue(value,out var count)?checked(count+1):1;}
    private bool TryMethod(nint value,int index,out nint method)
    {
        method=0;
        return value!=0 && index is >=0 and <=10 && Admitted()
            && memory.TryRead(value,out nint table)&&table!=0
            && memory.TryRead(table+index*sizeof(nint),out method)&&method!=0 && Admitted();
    }
    private bool Admitted(){try{return Environment.Is64BitProcess && OperatingSystem.IsWindows() && admission?.Invoke()==true;}catch{return false;}}
    internal static bool LayoutMatches()=>sizeof(PortraitOwnedD3D11Texture2DDesc)==44
        && sizeof(PortraitOwnedD3D11SrvDesc)==24
        && Offsets<PortraitOwnedD3D11Texture2DDesc>(["Width","Height","MipLevels","ArraySize","Format",
            "SampleCount","SampleQuality","Usage","BindFlags","CPUAccessFlags","MiscFlags"])
        && Offsets<PortraitOwnedD3D11SrvDesc>(["Format","Dimension","MostDetailedMip","MipLevels","Union2","Union3"]);
    private static bool Offsets<T>(string[] fields) where T:struct
    {
        // Positional record fields are explicit-layout auto-property backing fields.
        // Validate their actual emitted offsets instead of relying on size alone.
        try {
            for(var i=0;i<fields.Length;i++)
                if(Marshal.OffsetOf<T>("<"+fields[i]+">k__BackingField")!=i*sizeof(uint))return false;
            return true;
        } catch { return false; }
    }
}
