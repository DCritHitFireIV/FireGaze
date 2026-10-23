using System.Runtime.InteropServices;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

[StructLayout(LayoutKind.Explicit, Size = 44)]
internal readonly record struct PortraitPixelTextureDesc(
    [field: FieldOffset(0)] uint Width, [field: FieldOffset(4)] uint Height,
    [field: FieldOffset(8)] uint MipLevels, [field: FieldOffset(12)] uint ArraySize,
    [field: FieldOffset(16)] uint Format, [field: FieldOffset(20)] uint SampleCount,
    [field: FieldOffset(24)] uint SampleQuality, [field: FieldOffset(28)] uint Usage,
    [field: FieldOffset(32)] uint BindFlags, [field: FieldOffset(36)] uint CPUAccessFlags,
    [field: FieldOffset(40)] uint MiscFlags);

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal readonly record struct PortraitPixelSrvDesc(
    [field: FieldOffset(0)] uint Format, [field: FieldOffset(4)] uint Dimension,
    [field: FieldOffset(8)] uint MostDetailedMip, [field: FieldOffset(12)] uint MipLevels,
    [field: FieldOffset(16)] uint Union2, [field: FieldOffset(20)] uint Union3);

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal readonly record struct PortraitPixelMapped(
    [field: FieldOffset(0)] nint Data, [field: FieldOffset(8)] uint RowPitch,
    [field: FieldOffset(12)] uint DepthPitch);

[StructLayout(LayoutKind.Explicit, Size = 8)]
internal readonly record struct PortraitPixelQueryDesc(
    [field: FieldOffset(0)] uint Query, [field: FieldOffset(4)] uint MiscFlags);

// The native boundary is deliberately separate from the frame/ownership state machine. Tests never
// load a device; the real implementation is exact installed CN TerraFX ABI with no new dependency.
internal interface IPortraitPixelReadbackD3D11Native
{
    int QueryInterface(nint value, Guid iid, out nint result);
    nint GetResource(nint value);
    nint GetDevice(nint value);
    PortraitPixelTextureDesc GetTextureDesc(nint value);
    PortraitPixelSrvDesc GetViewDesc(nint value);
    nint GetImmediateContext(nint value);
    uint GetContextType(nint value);
    nint CreateStaging(nint device, PortraitPixelTextureDesc descriptor);
    nint CreateEventQuery(nint device);
    void CopyTopMip(nint context, nint source, nint destination);
    void End(nint context, nint query);
    int GetData(nint context, nint query);
    int Map(nint context, nint texture, uint mapType, uint flags, out PortraitPixelMapped mapped);
    void Unmap(nint context, nint texture);
    void CopyBytes(nint address, Span<byte> destination);
    void Release(nint value);
}

/// <summary>
/// Native calls are admitted only inside EnterDraw, which the owner calls from public UiBuilder.Draw.
/// Source ownership is retained synchronously; all source inspection and immediate-context work is
/// frame-pumped. A submitted operation keeps its original COM refs until its event query completes.
/// Hidden Draw, device changes and cancellation never authorize guessing GPU retirement.
/// </summary>
internal sealed class PortraitPixelReadbackD3D11 : IPortraitPixelReadbackDrawBackend
{
    internal static readonly Guid Unknown = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid Srv = new("b0e06fe0-8192-4e1a-b1ca-36d7414710b2");
    private static readonly Guid Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private readonly object sync = new();
    private readonly Func<nint> readDevice;
    private readonly IPortraitPixelReadbackD3D11Native native;
    private nint lastDevice;
    private long generation = 1;
    private int drawThread;

    public PortraitPixelReadbackD3D11(IUiBuilder ui) : this(() => ui.DeviceHandle) { }
    internal PortraitPixelReadbackD3D11(Func<nint> readDevice, IPortraitPixelReadbackD3D11Native? native = null)
    {
        this.readDevice = readDevice;
        this.native = native ?? new NativeApi(Admitted);
    }

    public PortraitPixelReadbackIdentity Identity
    {
        get
        {
            // Borrowed pointer is an equality token only. QI and descriptor validation happen in Draw.
            var current = readDevice();
            lock (sync)
            {
                if (lastDevice != current) { lastDevice = current; generation = checked(generation + 1); }
                return new(current, generation);
            }
        }
    }

    public void Invalidate() { lock (sync) generation = checked(generation + 1); }
    public IPortraitPixelReadbackSource CreateSource(IDalamudTextureWrap texture)
        => new Source(this, texture ?? throw new ArgumentNullException(nameof(texture)));

    public IDisposable EnterDraw()
    {
        lock (sync)
        {
            if (drawThread != 0 || !LayoutMatches()) throw new InvalidOperationException("Readback Draw admission unavailable.");
            drawThread = Environment.CurrentManagedThreadId;
            return new DrawScope(this);
        }
    }
    private bool Admitted() { lock (sync) return drawThread == Environment.CurrentManagedThreadId; }
    private void RequireDraw()
    {
        if (!Admitted()) throw new InvalidOperationException("D3D11 readback requires the public Draw scope.");
    }
    public IPortraitPixelReadbackTransfer CreateTransfer(IPortraitPixelReadbackLease source)
    {
        RequireDraw();
        if (source is not Lease lease || !ReferenceEquals(lease.Owner, this) || lease.IsDisposed)
            throw new InvalidOperationException("Readback source ownership is unavailable.");
        // Return ownership even on partial initialization failure. The core can retire all acquired
        // refs, or preserve an uncertain result instead of losing it through a constructor exception.
        var transfer = new Transfer(this, lease);
        transfer.Initialize();
        return transfer;
    }

    internal static bool LayoutMatches() => Environment.Is64BitProcess
        && Marshal.SizeOf<PortraitPixelTextureDesc>() == 44 && Marshal.SizeOf<PortraitPixelSrvDesc>() == 24
        && Marshal.SizeOf<PortraitPixelMapped>() == 16 && Marshal.SizeOf<PortraitPixelQueryDesc>() == 8
        && Offsets<PortraitPixelTextureDesc>(["Width","Height","MipLevels","ArraySize","Format","SampleCount","SampleQuality","Usage","BindFlags","CPUAccessFlags","MiscFlags"],4)
        && Offsets<PortraitPixelSrvDesc>(["Format","Dimension","MostDetailedMip","MipLevels","Union2","Union3"],4)
        && Marshal.OffsetOf<PortraitPixelMapped>("<Data>k__BackingField") == 0
        && Marshal.OffsetOf<PortraitPixelMapped>("<RowPitch>k__BackingField") == 8
        && Marshal.OffsetOf<PortraitPixelMapped>("<DepthPitch>k__BackingField") == 12;
    private static bool Offsets<T>(string[] fields,int size) where T:struct
    {
        for(var index=0;index<fields.Length;index++)
            if(Marshal.OffsetOf<T>("<"+fields[index]+">k__BackingField") != index*size)return false;
        return true;
    }

    private sealed class DrawScope(PortraitPixelReadbackD3D11 owner) : IDisposable
    {
        private PortraitPixelReadbackD3D11? current = owner;
        public void Dispose()
        {
            var held = Interlocked.Exchange(ref current, null);
            if (held is null) return;
            held.RequireDraw();
            lock (held.sync) held.drawThread = 0;
        }
    }
    private sealed class Source(PortraitPixelReadbackD3D11 owner,IDalamudTextureWrap texture) : IPortraitPixelReadbackSource
    {
        public IPortraitPixelReadbackLease Retain()
        {
            var identity=owner.Identity;
            var retained=texture.CreateWrapSharingLowLevelResource();
            if (ReferenceEquals(retained,texture)) throw new InvalidOperationException("Texture share has no independent ownership.");
            // Transfer ownership before reading fallible wrapper metadata. The state machine then
            // retains the lease even if Width/Height throws and its release must be quarantined.
            return new Lease(owner,retained,identity);
        }
    }
    private sealed class Lease(PortraitPixelReadbackD3D11 owner, IDalamudTextureWrap texture,
        PortraitPixelReadbackIdentity identity) : IPortraitPixelReadbackLease
    {
        private int disposed;
        public PortraitPixelReadbackD3D11 Owner=>owner;
        public PortraitPixelReadbackIdentity Identity=>identity;
        public int Width=>texture.Width;
        public int Height=>texture.Height;
        public int DxgiFormat{get;set;}
        public bool IsDisposed=>Volatile.Read(ref disposed)!=0;
        public nint Handle=>!IsDisposed?checked((nint)texture.Handle.Handle):throw new InvalidOperationException("Readback source has been disposed.");
        public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)texture.Dispose();}
    }

    private sealed class Transfer(PortraitPixelReadbackD3D11 owner,Lease source) : IPortraitPixelReadbackTransfer
    {
        private readonly PortraitPixelReadbackD3D11 owner=owner;
        private readonly Lease source=source;
        private readonly List<nint> refs=[];
        private nint texture,context,staging,query;
        private nint uncertainReleaseReference;
        private Exception? failure;
        private bool uncertain,submitted,ended,complete,mapped,released;
        private PortraitPixelMapped mappedData;
        private long mappingGeneration;

        public void Initialize()
        {
            try
            {
                Current();
                var handle=source.Handle;
                var srv=Query(handle,Srv);
                var resource=Own(owner.native.GetResource(srv));
                texture=Query(resource,Texture2D);
                var desc=owner.native.GetTextureDesc(texture);
                var view=owner.native.GetViewDesc(srv);
                if(desc.Width!=source.Width || desc.Height!=source.Height || desc.MipLevels==0
                    || desc.ArraySize!=1 || desc.SampleCount!=1 || desc.SampleQuality!=0
                    || view.Dimension!=4 || view.MostDetailedMip!=0 || view.MipLevels==0
                    || view.MipLevels!=uint.MaxValue && view.MipLevels>desc.MipLevels
                    || !Compatible(desc.Format,view.Format))throw new InvalidOperationException("Unsupported readback texture descriptor.");
                var textureDevice=Own(owner.native.GetDevice(texture));
                var publicDevice=Query(source.Identity.Device,Device);
                if(Query(textureDevice,Unknown)!=Query(publicDevice,Unknown))
                    throw new InvalidOperationException("Readback texture belongs to another device.");
                context=Own(owner.native.GetImmediateContext(publicDevice));
                if(owner.native.GetContextType(context)!=0)throw new InvalidOperationException("Readback requires an immediate context.");
                staging=Own(owner.native.CreateStaging(publicDevice,desc with{MipLevels=1,ArraySize=1,SampleCount=1,SampleQuality=0,Usage=3,BindFlags=0,CPUAccessFlags=0x20000,MiscFlags=0}));
                query=Own(owner.native.CreateEventQuery(publicDevice));
                Current();
                if(source.Handle!=handle || owner.native.GetTextureDesc(texture)!=desc || owner.native.GetViewDesc(srv)!=view)
                    throw new InvalidOperationException("Readback source changed during acquisition.");
                source.DxgiFormat=checked((int)view.Format);
            }
            catch(Exception error){failure=error;uncertain|=error is AmbiguousOwnership;}
        }
        private static bool Compatible(uint resource,uint view)=>view is 28 or 29 or 87 or 91
            && (resource==view || resource==27&&view is 28 or 29 || resource==90&&view is 87 or 91);
        private void Current()
        {
            owner.RequireDraw();
            if(source.IsDisposed || source.Identity!=owner.Identity)throw new InvalidOperationException("Readback device generation changed.");
        }
        private nint Own(nint value)
        {
            if(value==0)throw new InvalidOperationException("Readback native operation returned no resource.");
            refs.Add(value);return value;
        }
        private nint Query(nint value,Guid iid)
        {
            Current();
            var status=owner.native.QueryInterface(value,iid,out var result);
            if(status<0 && result!=0){uncertain=true;throw new AmbiguousOwnership();}
            if(status<0)throw new InvalidOperationException("Readback native interface unavailable.");
            return Own(result);
        }
        public void Submit()
        {
            Current();
            if(failure is not null)throw new InvalidOperationException("Readback transfer initialization failed.",failure);
            if(submitted||released)throw new InvalidOperationException("Readback transfer was already submitted or retired.");
            submitted=true;
            // The flag precedes the void native call: a throwing/partial submission cannot be
            // mistaken for no GPU work. Without an ended query it remains physically unretired.
            owner.native.CopyTopMip(context,texture,staging);
            owner.native.End(context,query);
            ended=true;
        }
        private bool Probe()
        {
            owner.RequireDraw();
            if(complete)return true;
            if(!ended)return false;
            var result=owner.native.GetData(context,query);
            if(result==0){complete=true;return true;}
            if(result==1)return false;
            throw new InvalidOperationException("Readback GPU completion could not be established.");
        }
        public PortraitPixelReadbackMapStatus TryMap(out IPortraitPixelReadbackMapping? mapping)
        {
            mapping=null;Current();
            if(failure is not null || !submitted || released || mapped)return PortraitPixelReadbackMapStatus.Failed;
            if(!Probe())return PortraitPixelReadbackMapStatus.StillDrawing;
            var status=owner.native.Map(context,staging,1,0x100000,out var data);
            if(status<0)
            {
                if(data.Data!=0){uncertain=true;throw new InvalidOperationException("Failed Map returned uncertain mapping ownership.");}
                return status==unchecked((int)0x887A000A)?PortraitPixelReadbackMapStatus.StillDrawing:PortraitPixelReadbackMapStatus.Failed;
            }
            // Any successful Map must be paired with Unmap, including invalid pData/pitch. The
            // core validates rows and executes Unmap in its cleanup path before attempting retire.
            mapped=true;mappedData=data;mappingGeneration=checked(mappingGeneration+1);
            mapping=new Mapping(this,mappingGeneration);return PortraitPixelReadbackMapStatus.Ready;
        }
        public void Unmap()
        {
            owner.RequireDraw();
            if(!mapped||uncertain)throw new InvalidOperationException("Readback mapping cannot be safely unmapped.");
            mapped=false;mappingGeneration=checked(mappingGeneration+1);
            try{owner.native.Unmap(context,staging);mappedData=default;}
            catch{uncertain=true;throw;}
        }
        public bool TryRetire()
        {
            owner.RequireDraw();
            if(uncertain||uncertainReleaseReference!=0)throw new InvalidOperationException("Readback native ownership is quarantined.");
            if(released)return true;
            if(mapped)return false;
            if(submitted&&!Probe())return false;
            while(refs.Count>0)
            {
                var index=refs.Count-1;var value=refs[index];refs.RemoveAt(index);
                // Consume the release permit before external COM can reenter, while retaining
                // the uncertain pointer separately until a successful release has been proved.
                uncertainReleaseReference=value;
                try{owner.native.Release(value);uncertainReleaseReference=0;}catch{uncertain=true;throw;}
            }
            released=true;return true;
        }
        private sealed class Mapping(Transfer transfer,long generation) : IPortraitPixelReadbackMapping
        {
            private PortraitPixelMapped Data
            {
                get
                {
                    transfer.owner.RequireDraw();
                    if(!transfer.mapped||transfer.mappingGeneration!=generation||transfer.uncertain)
                        throw new InvalidOperationException("Readback mapping is no longer active.");
                    return transfer.mappedData;
                }
            }
            public int RowPitch=>checked((int)Data.RowPitch);
            public int DepthPitch=>checked((int)Data.DepthPitch);
            public void CopyBytes(int sourceByteOffset,Span<byte> destination)
            {
                var data=Data;
                var extent=checked((long)(transfer.source.Height-1)*data.RowPitch+(long)transfer.source.Width*4);
                if(data.Data==0 || sourceByteOffset<0 || (long)sourceByteOffset+destination.Length>extent
                    || extent>67_108_864 || (ulong)data.Data>long.MaxValue-(ulong)sourceByteOffset)
                    throw new InvalidOperationException("Readback mapped row is outside its bounded texture.");
                transfer.owner.native.CopyBytes(data.Data+sourceByteOffset,destination);
            }
        }
    }
    private sealed class AmbiguousOwnership : InvalidOperationException { }

    /// <summary>Exact CN TerraFX x64 ABI. No COM/context call is admitted outside public Draw.</summary>
    private sealed unsafe class NativeApi(Func<bool> admitted) : IPortraitPixelReadbackD3D11Native
    {
        private enum Interface { Device,Child,Srv,Texture,Context }
        private void Active(){if(!OperatingSystem.IsWindows()||!Environment.Is64BitProcess||!admitted())throw new InvalidOperationException("Native readback admission unavailable.");}
        private nint Method(nint value,Interface kind,int slot)
        {
            Active();
            var allowed=kind switch{Interface.Device=>slot is 5 or 24 or 40,Interface.Child=>slot==3,
                Interface.Srv=>slot is 7 or 8,Interface.Texture=>slot==10,Interface.Context=>slot is 14 or 15 or 28 or 29 or 46 or 112,_=>false};
            if(!allowed||!Read(value,out nint table)||table==0||!Read(checked(table+slot*sizeof(nint)),out nint method)||method==0)
                throw new InvalidOperationException("Readback COM method could not be established.");
            Active();return method;
        }
        public int QueryInterface(nint value,Guid iid,out nint result)
        {
            Active();result=0;
            if(!Read(value,out nint table)||table==0||!Read(table,out nint method)||method==0)throw new InvalidOperationException("Readback QI target unavailable.");
            return Marshal.QueryInterface(value,in iid,out result);
        }
        public nint GetResource(nint value){nint output=0;((delegate* unmanaged[MemberFunction]<nint,nint*,void>)Method(value,Interface.Srv,7))(value,&output);return output;}
        public nint GetDevice(nint value){nint output=0;((delegate* unmanaged[MemberFunction]<nint,nint*,void>)Method(value,Interface.Child,3))(value,&output);return output;}
        public PortraitPixelTextureDesc GetTextureDesc(nint value){PortraitPixelTextureDesc output=default;((delegate* unmanaged[MemberFunction]<nint,PortraitPixelTextureDesc*,void>)Method(value,Interface.Texture,10))(value,&output);return output;}
        public PortraitPixelSrvDesc GetViewDesc(nint value){PortraitPixelSrvDesc output=default;((delegate* unmanaged[MemberFunction]<nint,PortraitPixelSrvDesc*,void>)Method(value,Interface.Srv,8))(value,&output);return output;}
        public nint GetImmediateContext(nint value){nint output=0;((delegate* unmanaged[MemberFunction]<nint,nint*,void>)Method(value,Interface.Device,40))(value,&output);return output;}
        public uint GetContextType(nint value)=>((delegate* unmanaged[MemberFunction]<nint,uint>)Method(value,Interface.Context,112))(value);
        public nint CreateStaging(nint device,PortraitPixelTextureDesc descriptor)
        {
            nint output=0;var result=((delegate* unmanaged[MemberFunction]<nint,PortraitPixelTextureDesc*,nint,nint*,int>)Method(device,Interface.Device,5))(device,&descriptor,0,&output);
            if(result<0){if(output!=0)throw new AmbiguousOwnership();throw new InvalidOperationException("Readback staging creation failed.");}return output;
        }
        public nint CreateEventQuery(nint device)
        {
            PortraitPixelQueryDesc descriptor=default;nint output=0;
            var result=((delegate* unmanaged[MemberFunction]<nint,PortraitPixelQueryDesc*,nint*,int>)Method(device,Interface.Device,24))(device,&descriptor,&output);
            if(result<0){if(output!=0)throw new AmbiguousOwnership();throw new InvalidOperationException("Readback event creation failed.");}return output;
        }
        public void CopyTopMip(nint context,nint source,nint destination)
            =>((delegate* unmanaged[MemberFunction]<nint,nint,uint,uint,uint,uint,nint,uint,nint,void>)Method(context,Interface.Context,46))(context,destination,0,0,0,0,source,0,0);
        public void End(nint context,nint query)=>((delegate* unmanaged[MemberFunction]<nint,nint,void>)Method(context,Interface.Context,28))(context,query);
        public int GetData(nint context,nint query)=>((delegate* unmanaged[MemberFunction]<nint,nint,nint,uint,uint,int>)Method(context,Interface.Context,29))(context,query,0,0,1);
        public int Map(nint context,nint texture,uint mapType,uint flags,out PortraitPixelMapped mapped)
        {
            if(mapType!=1||flags!=0x100000)throw new InvalidOperationException("Only nonblocking READ mapping is permitted.");
            PortraitPixelMapped output=default;var result=((delegate* unmanaged[MemberFunction]<nint,nint,uint,uint,uint,PortraitPixelMapped*,int>)Method(context,Interface.Context,14))(context,texture,0,mapType,flags,&output);mapped=output;return result;
        }
        public void Unmap(nint context,nint texture)=>((delegate* unmanaged[MemberFunction]<nint,nint,uint,void>)Method(context,Interface.Context,15))(context,texture,0);
        public void Release(nint value){Active();Marshal.Release(value);}
        // Same fallible ReadProcessMemory primitive as PortraitOwnedSceneSlotNativeApi. No direct
        // dereference of a borrowed vtable or mapped pointer, and exact byte-count verification.
        private bool Read<T>(nint address,out T value) where T:unmanaged
        {
            value=default;if(address==0)return false;
            fixed(T* output=&value)return ReadProcessMemory((nint)(-1),address,output,(nuint)sizeof(T),out var count)&&count==(nuint)sizeof(T);
        }
        public void CopyBytes(nint address,Span<byte> destination)
        {
            Active();if(address==0)throw new InvalidOperationException("Mapped readback data unavailable.");
            fixed(byte* output=destination)
                if(!ReadProcessMemory((nint)(-1),address,output,(nuint)destination.Length,out var count)||count!=(nuint)destination.Length)
                    throw new InvalidOperationException("Mapped readback row could not be copied.");
        }
        [DllImport("kernel32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(nint process,nint address,void* buffer,nuint size,out nuint copied);
    }
}
