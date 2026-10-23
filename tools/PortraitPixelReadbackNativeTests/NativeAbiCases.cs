#if HAS_NATIVE_READBACK
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FireGaze.PortraitPreview;

// Real native call signatures against self-owned local memory. No SDK service, game process,
// graphics device, external DLL, hook, or desktop operation is involved in this fixture.
static unsafe class NativeAbiCases
{
    static nint objectAddress;
    static int seen,checks,readFlags,readType,queryFlags,querySize,copyValues,releaseCalls;
    static PortraitPixelTextureDesc staging;
    static PortraitPixelQueryDesc query;
    static void Check(bool condition,string reason){checks++;if(!condition)throw new Exception(reason);}
    internal static int Run()
    {
        var table=(nint*)NativeMemory.AllocZeroed(113,(nuint)sizeof(nint));
        var obj=(nint*)NativeMemory.AllocZeroed(1,(nuint)sizeof(nint));
        objectAddress=(nint)obj;*obj=(nint)table;
        try
        {
            table[0]=(nint)(delegate* unmanaged[MemberFunction]<nint,Guid*,nint*,int>)&Query;
            table[1]=(nint)(delegate* unmanaged[MemberFunction]<nint,uint>)&AddRef;
            table[2]=(nint)(delegate* unmanaged[MemberFunction]<nint,uint>)&Release;
            table[3]=(nint)(delegate* unmanaged[MemberFunction]<nint,nint*,void>)&GetDevice;
            table[5]=(nint)(delegate* unmanaged[MemberFunction]<nint,PortraitPixelTextureDesc*,nint,nint*,int>)&CreateTexture;
            table[7]=(nint)(delegate* unmanaged[MemberFunction]<nint,nint*,void>)&GetResource;
            table[8]=(nint)(delegate* unmanaged[MemberFunction]<nint,PortraitPixelSrvDesc*,void>)&GetView;
            table[10]=(nint)(delegate* unmanaged[MemberFunction]<nint,PortraitPixelTextureDesc*,void>)&GetTexture;
            table[14]=(nint)(delegate* unmanaged[MemberFunction]<nint,nint,uint,uint,uint,PortraitPixelMapped*,int>)&Map;
            table[15]=(nint)(delegate* unmanaged[MemberFunction]<nint,nint,uint,void>)&Unmap;
            table[24]=(nint)(delegate* unmanaged[MemberFunction]<nint,PortraitPixelQueryDesc*,nint*,int>)&CreateQuery;
            table[28]=(nint)(delegate* unmanaged[MemberFunction]<nint,nint,void>)&End;
            table[29]=(nint)(delegate* unmanaged[MemberFunction]<nint,nint,nint,uint,uint,int>)&GetData;
            table[40]=(nint)(delegate* unmanaged[MemberFunction]<nint,nint*,void>)&GetContext;
            table[46]=(nint)(delegate* unmanaged[MemberFunction]<nint,nint,uint,uint,uint,uint,nint,uint,nint,void>)&Copy;
            table[112]=(nint)(delegate* unmanaged[MemberFunction]<nint,uint>)&ContextType;
            var nativeType=typeof(PortraitPixelReadbackD3D11).GetNestedType("NativeApi",BindingFlags.NonPublic)!;
            var admitted=true;
            var native=(IPortraitPixelReadbackD3D11Native)Activator.CreateInstance(nativeType,
                BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[new Func<bool>(()=>admitted)],null)!;
            Check(native.QueryInterface(objectAddress,PortraitPixelReadbackD3D11.Unknown,out var result)==0&&result==objectAddress,"QI uses exact slot0 and owned result");
            Check(native.GetResource(objectAddress)==objectAddress&&seen==7,"GetResource slot7 exact out-pointer ABI");
            Check(native.GetDevice(objectAddress)==objectAddress&&seen==3,"GetDevice slot3 exact out-pointer ABI");
            var texture=native.GetTextureDesc(objectAddress);
            Check(seen==10&&texture.Width==3&&texture.CPUAccessFlags==77,"TextureDesc slot10 exact 44byte offsets");
            Check(native.GetViewDesc(objectAddress).Format==29&&seen==8,"SrvDesc slot8 exact 24byte offsets");
            Check(native.GetImmediateContext(objectAddress)==objectAddress&&seen==40,"GetImmediateContext slot40 exact owned result ABI");
            Check(native.GetContextType(objectAddress)==0&&seen==112,"GetType slot112 exact uint return ABI");
            var desired=new PortraitPixelTextureDesc(3,4,1,1,28,1,0,3,0,0x20000,0);
            Check(native.CreateStaging(objectAddress,desired)==objectAddress&&seen==5&&staging==desired,"CreateTexture2D slot5 argument and output ABI");
            Check(native.CreateEventQuery(objectAddress)==objectAddress&&seen==24&&query==default,"CreateQuery slot24 creates EVENT0 flags0");
            native.CopyTopMip(objectAddress,123,456);
            Check(seen==46&&copyValues==1,"CopySubresourceRegion slot46 preserves dst/src and null top-mip box");
            native.End(objectAddress,789);Check(seen==28,"End slot28 exact void ABI");
            Check(native.GetData(objectAddress,789)==1&&seen==29&&queryFlags==1&&querySize==0,"GetData slot29 DONOTFLUSH status-only ABI");
            Check(native.Map(objectAddress,456,1,0x100000,out var mapped)==0&&seen==14&&readFlags==0x100000&&readType==1
                &&mapped is {Data:1234,RowPitch:32,DepthPitch:0},"Map slot14 nonblocking flags and mapped16 layout");
            native.Unmap(objectAddress,456);Check(seen==15,"Unmap slot15 exact uint subresource ABI");
            native.Release(objectAddress);Check(releaseCalls==1,"owned Release consumes slot2 once");
            var mappedBytes=stackalloc byte[8];for(var i=0;i<8;i++)mappedBytes[i]=(byte)(10+i);
            var output=new byte[8];native.CopyBytes((nint)mappedBytes,output);
            Check(output.SequenceEqual(new byte[]{10,11,12,13,14,15,16,17}),"strict mapped memory copies exact requested bytes");
            admitted=false;var rejected=false;try{native.GetContextType(objectAddress);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"native entry checks callback admission before any vtable call");
        }
        finally{NativeMemory.Free(obj);NativeMemory.Free(table);objectAddress=0;}
        return checks;
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static int Query(nint self,Guid* iid,nint* result){*result=self;seen=0;return 0;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static uint AddRef(nint self)=>2;
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static uint Release(nint self){releaseCalls++;return 1;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static void GetDevice(nint self,nint* result){seen=3;*result=self;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static void GetResource(nint self,nint* result){seen=7;*result=self;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static void GetView(nint self,PortraitPixelSrvDesc* result){seen=8;*result=new(29,4,0,1,0,0);}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static void GetTexture(nint self,PortraitPixelTextureDesc* result){seen=10;*result=new(3,4,1,1,28,1,0,0,0,77,0);}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static void GetContext(nint self,nint* result){seen=40;*result=self;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static uint ContextType(nint self){seen=112;return 0;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static int CreateTexture(nint self,PortraitPixelTextureDesc* descriptor,nint initial,nint* result){seen=5;staging=*descriptor;*result=self;return initial==0?0:-1;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static int CreateQuery(nint self,PortraitPixelQueryDesc* descriptor,nint* result){seen=24;query=*descriptor;*result=self;return 0;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static void Copy(nint self,nint dst,uint dstSub,uint x,uint y,uint z,nint src,uint srcSub,nint box){seen=46;copyValues=dst==456&&src==123&&dstSub==0&&srcSub==0&&x==0&&y==0&&z==0&&box==0?1:0;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static void End(nint self,nint query){seen=28;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static int GetData(nint self,nint query,nint output,uint size,uint flags){seen=29;queryFlags=(int)flags;querySize=(int)size;return 1;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static int Map(nint self,nint texture,uint sub,uint type,uint flags,PortraitPixelMapped* output){seen=14;readFlags=(int)flags;readType=(int)type;*output=new(1234,32,0);return 0;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvMemberFunction)])]
    static void Unmap(nint self,nint texture,uint sub){seen=15;}
}
#endif
