#if HAS_OWNED_HOOKS
using System.Runtime.InteropServices;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

// Both production components run together. Only the native game entry points and
// their eligibility observations are replaced; no fake lease is supplied.
internal static unsafe class OwnedSceneIntegrationCases
{
    private static readonly PortraitIdentity Source = new(0x100, 0x200, 0x300, 7, 0x400);
    private const nint Foreign = 0x600;
    public static int Run()
    {
        var failed = 0; var total = 0;
        foreach (var (name, test) in new (string, Action)[] {
            ("strict slot ownership admits creation before the slot is occupied", StrictCreation),
            ("Setup idle success, idle failure and foreign takeover never release unproved slot", UnprovedSetup),
            ("throwing Setup may retire only a proven exact owned slot", ThrowingOwnedSetup),
            ("expired constructor admission frees only the unconstructed allocation", UnconstructedCleanup),
            ("throwing or foreign-return constructor has no guessed native cleanup", UncertainConstructor),
            ("real dispatcher and scene release cleared slot, then Dtor, then detached confirmation", NativeTakeover),
            ("source close retires scene before original source Release", SourceClose),
            ("new native owner during Release blocks Dtor and any late cleanup", TakeoverDuringRelease),
            ("Dtor completion does not read freed view when confirmation loses slot", TakeoverAfterDtor),
            ("foreign takeover during own Release never recursively releases or destroys", ReentrantTakeoverDuringRelease),
        })
        {
            total++;
            try { test(); Console.WriteLine("PASS integration " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL integration " + name + ": " + ex.Message); }
        }
        Console.WriteLine($"{total-failed} integration passed; {failed} failed"); return failed == 0 ? 0 : 1;
    }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static void StrictCreation()
    {
        using var f = new Fixture(); f.Prepare(); f.SourceRender(100);
        Check(f.Api.ConstructCalls == 1, "strict idle slot prevented the constructor from being called");
        Check(f.Scene.IsReady, f.Scene.LastError ?? "strict creation did not reach ready");
    }
    static void UnprovedSetup()
    {
        foreach(var mode in new[]{"return-idle","throw-idle","return-foreign","throw-foreign","reenter-foreign"})
        {
            using var f=new Fixture();f.Api.SetupMode=mode;f.Prepare();f.SourceRender(100);
            Check(f.Api.ConstructCalls==1&&f.Api.SetupStarted,"setup failure fixture never entered native construction/setup");
            Check(f.Scene.IsQuarantined&&!f.Api.Freed,"unproved setup was not quarantined");
            Check(!f.Api.Events.Contains("release")&&!f.Api.Events.Any(x=>x.StartsWith("dtor:")),"unproved setup released or destroyed a view");
            Check(f.Api.SlotOwner==(mode.EndsWith("foreign")?Foreign:0),"foreign slot owner was lost");
            f.Api.Events.Clear();f.SourceRender(101);
            Check(f.Api.Events.SequenceEqual(new[]{"source-render"}),"late cleanup ran after unproved setup");
        }
    }
    static void ThrowingOwnedSetup()
    {
        using var f=new Fixture();f.Api.SetupMode="throw-owned";f.Prepare();f.SourceRender(100);
        Check(f.Scene.IsRetired&&!f.Scene.IsQuarantined&&f.Api.Freed,"exact owned throwing Setup did not retire safely");
        Check(f.Api.Events.SequenceEqual(new[]{"source-render","release","dtor:1"}),"owned Setup cleanup ordering changed");
        Check(f.Api.ReadsAfterFree==0&&f.Api.SlotOwner==0,"owned Setup cleanup inspected freed view or left a slot");
        Check(f.Hooks.TryDispose(),"retired throwing Setup blocked hook disposal");
    }
    static void UnconstructedCleanup()
    {
        using var f=new Fixture();f.Api.RejectConstructAdmission=true;f.Prepare();f.SourceRender(100);
        Check(f.Api.ConstructCalls==0&&f.Api.FreeUnconstructedCalls==1,"unconstructed allocation was not freed exactly once before Ctor");
        Check(f.Scene.IsRetired&&!f.Scene.IsQuarantined&&f.Api.ReadsAfterFree==0,"raw allocation cleanup used native view lifetime");
        Check(f.Api.Events.SequenceEqual(new[]{"source-render"}),"raw allocation cleanup called Release/Dtor");
        Check(f.Hooks.TryDispose(),"confirmed raw allocation cleanup blocked hook disposal");
    }
    static void UncertainConstructor()
    {
        foreach(var mode in new[]{"throw","foreign-return"})
        {
            using var f=new Fixture();f.Api.ConstructMode=mode;f.Prepare();f.SourceRender(100);
            Check(f.Api.ConstructCalls==1&&f.Scene.IsQuarantined&&!f.Api.Freed,"uncertain constructor was not quarantined");
            Check(f.Api.FreeUnconstructedCalls==0&&!f.Api.SetupStarted,"uncertain constructor was freed or entered Setup");
            Check(f.Api.Events.SequenceEqual(new[]{"source-render"}),"uncertain constructor used guessed cleanup");
            f.Api.Events.Clear();f.SourceRender(101);
            Check(f.Api.Events.SequenceEqual(new[]{"source-render"}),"uncertain constructor retried native cleanup");
        }
    }
    static void NativeTakeover()
    {
        using var f = new Fixture(); f.Start(); f.Api.Events.Clear(); f.Takeover();
        Check(string.Join(",",f.Api.Events) == "release,dtor:1,native-takeover", "retirement call order did not precede native original");
        Check(f.Scene.IsRetired && !f.Scene.IsQuarantined, "successful Release/Dtor rejected its own cleared slot");
        Check(f.Api.Freed && f.Api.ReadsAfterFree == 0 && f.Api.SlotOwner == Foreign, "freed view read or new native owner was lost");
        f.Hooks.RequestStop(); Check(f.Hooks.TryDispose(), "completed native retirement did not allow hook disposal");
    }
    static void SourceClose()
    {
        using var f = new Fixture(); f.Start(); f.Api.Events.Clear(); f.Factory.Get<CharaView.Delegates.Release>(PortraitOwnedSceneNativeKind.Release)((CharaView*)Source.Model);
        Check(string.Join(",",f.Api.Events)=="release,dtor:1,source-release", "source original ran before independent scene retired");
        Check(f.Scene.IsRetired && f.Api.ReadsAfterFree==0, "source close left a scene or inspected freed view");
    }
    static void TakeoverDuringRelease()
    {
        using var f=new Fixture();f.Start();f.Api.TakeoverDuringRelease=true;f.Api.Events.Clear();f.Takeover();
        Check(string.Join(",",f.Api.Events)=="release,native-takeover", "Dtor was called after a native owner replaced the released slot");
        Check(f.Scene.IsQuarantined&&!f.Api.Freed&&f.Api.SlotOwner==Foreign,"unqualified retirement was not quarantined");
        f.Api.Events.Clear();f.SourceRender(101);Check(f.Api.Events.SequenceEqual(new[]{"source-render"}),"late native cleanup ran after loss");
    }
    static void TakeoverAfterDtor()
    {
        using var f=new Fixture();f.Start();f.Api.TakeoverAfterDtor=true;f.Api.Events.Clear();f.Takeover();
        Check(f.Api.Freed&&f.Api.ReadsAfterFree==0,"retirement confirmation touched freed view");
        Check(f.Api.SlotOwner==Foreign,"confirmation removed native owner's replacement");
        Check(f.Scene.LastError?.Contains("could not confirm release")==true,"unconfirmed retirement was silently accepted");
    }
    static void ReentrantTakeoverDuringRelease()
    {
        using var f=new Fixture();f.Start();f.Api.ReenterTakeoverDuringRelease=true;f.Api.Events.Clear();f.Takeover();
        Check(string.Join(",",f.Api.Events)=="release,native-takeover,native-takeover","retirement reentered itself from a native original");
        Check(!f.Api.Freed&&f.Scene.IsQuarantined&&f.Api.SlotOwner==Foreign,"reentrant teardown destroyed the active native frame or native owner");
    }

    sealed class Fixture : IDisposable
    {
        public readonly Api Api=new();
        public readonly PortraitOwnedSceneHooks Hooks;
        public readonly Factory Factory;
        public readonly PortraitOwnedCharaView Scene;
        public Fixture(){Hooks=new(new EnvironmentProof(Api));Factory=new(Api);Api.Factory=Factory;Scene=new(Api);}
        public void Start()
        {
            Prepare(); SourceRender(100);Check(Scene.IsReady,Scene.LastError??"scene did not reach ready");
        }
        public void Prepare()
        {
            Check(Hooks.TryInstall(Factory),Hooks.LastError??"hooks install failed");
            var snapshot=new PortraitOwnedCharaViewSnapshot(Source,default,default,new CharaViewItem[14],0,new PortraitComposition{Paused=true,CameraDistance=3});
            Check(Hooks.TryRequest(Source,6,lease=>{
                if(lease.CanCreate)Check(Scene.TryCreate(snapshot,lease),Scene.LastError??"scene create failed");
                Check(Scene.Advance(lease,lease.NativeFrame),Scene.LastError??"scene advance failed");
            },lease=>Scene.TryRetire(lease)),"request rejected");
        }
        public void Takeover()=>Factory.Get<CharaView.Delegates.Initialize>(PortraitOwnedSceneNativeKind.Initialize)((CharaView*)Foreign,null,6,0);
        public void SourceRender(uint frame)=>Factory.Get<CharaView.Delegates.Render>(PortraitOwnedSceneNativeKind.Render)((CharaView*)Source.Model,frame);
        public void Dispose(){Hooks.RequestStop();if(!Api.Freed&&Api.SlotOwner==Api.View)SourceRender(999);Hooks.TryDispose();Api.Dispose();}
    }
    sealed class EnvironmentProof(Api api) : IPortraitOwnedSceneHookEnvironment
    {
        private static long nextModule = 0x240000000;
        private readonly nint module = (nint)Interlocked.Add(ref nextModule, 0x1000000);
        public bool IsFrameworkThread=>true;
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets=>PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool TryValidateInstallation(out string reason){reason="";return true;}
        public bool IsSourceCurrent(PortraitIdentity source)=>source==Source;
        public bool IsSlotIdle(uint slot)
        {
            if(slot!=6||api.SlotOwner!=0)return false;
            return !api.RejectConstructAdmission||api.View==0||++api.PostAllocateIdleReads==1;
        }
        public bool IsSlotOwnedBy(uint slot,nint view)
        {
            if(api.Freed){api.ReadsAfterFree++;throw new InvalidOperationException("accessed freed native view");}
            if(slot!=6||view!=api.View)return false;
            // Native Release clears these fields; accepting idle in this observer
            // would conceal a missing retirement permit in the production lease.
            var native=(CharaViewPortrait*)view;
            return api.SlotOwner==view&&native->ClientObjectIndex==slot&&native->ClientObjectId==slot+0xF0;
        }
        public bool TryReadViewSlot(nint view,out uint slot){slot=view==Source.Model?4u:6u;return view==Source.Model||view==api.View||view==Foreign;}
    }
    sealed class Factory(Api api) : IPortraitOwnedSceneHookFactory
    {
        readonly Dictionary<PortraitOwnedSceneNativeKind,Delegate> detours=[];
        public T Get<T>(PortraitOwnedSceneNativeKind kind) where T:Delegate=>(T)detours[kind];
        public IPortraitOwnedSceneNativeHook<T> Create<T>(PortraitOwnedSceneNativeTarget target,T detour) where T:Delegate
        {
            detours[target.Kind]=detour;
            Delegate original=target.Kind switch {
                PortraitOwnedSceneNativeKind.Render=>(CharaView.Delegates.Render)((v,f)=>{if((nint)v==Source.Model)api.Events.Add("source-render");else api.NativeRender((nint)v);}),
                PortraitOwnedSceneNativeKind.Setup=>(CharaViewPortrait.Delegates.Setup)((v,s,d,a,i,k)=>api.NativeSetup((nint)v,s)),
                PortraitOwnedSceneNativeKind.Release=>(CharaView.Delegates.Release)(v=>{if((nint)v==Source.Model)api.Events.Add("source-release");else api.NativeRelease((nint)v);}),
                PortraitOwnedSceneNativeKind.Dtor=>(CharaView.Delegates.Dtor)((v,f)=>{api.NativeDestroy((nint)v,f);return v;}),
                PortraitOwnedSceneNativeKind.Initialize=>(CharaView.Delegates.Initialize)((v,a,s,c)=>{api.SlotOwner=(nint)v;api.Events.Add("native-takeover");}),
                _=>detour,
            };
            return new Hook<T>((T)original);
        }
        sealed class Hook<T>(T original):IPortraitOwnedSceneNativeHook<T> where T:Delegate
        {public T Original=>original;public bool IsEnabled{get;private set;}public bool IsDisposed{get;private set;}public void Enable()=>IsEnabled=true;public void Disable()=>IsEnabled=false;public void Dispose(){IsEnabled=false;IsDisposed=true;}}
    }
    sealed class Api : IPortraitOwnedCharaViewNativeApi,IDisposable
    {
        public readonly List<string> Events=[];
        public Factory Factory=null!;
        public nint View,SlotOwner;
        readonly Character* character=(Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
        readonly nint camera=(nint)NativeMemory.AllocZeroed(0x200);
        public bool SetupStarted,Freed,TakeoverDuringRelease,TakeoverAfterDtor,ReenterTakeoverDuringRelease;
        public int ReadsAfterFree,ConstructCalls,FreeUnconstructedCalls,PostAllocateIdleReads;
        public bool RejectConstructAdmission;
        public string? SetupMode,ConstructMode;
        public nint Allocate(){View=(nint)NativeMemory.AllocZeroed((nuint)sizeof(CharaViewPortrait));return View;}
        public void FreeUnconstructed(nint view){FreeUnconstructedCalls++;NativeMemory.Free((void*)view);Freed=true;}
        public nint Construct(nint view){ConstructCalls++;if(ConstructMode=="throw")throw new InvalidOperationException("ctor failed");return ConstructMode=="foreign-return"?Foreign:view;}
        public void Setup(nint view,uint slot,CharaViewCharacterData data)=>Factory.Get<CharaViewPortrait.Delegates.Setup>(PortraitOwnedSceneNativeKind.Setup)((CharaViewPortrait*)view,slot,&data,null,0,0);
        public void NativeSetup(nint view,uint slot)
        {
            SetupStarted=true;
            if(SetupMode=="throw-idle")throw new InvalidOperationException("setup failed without slot");
            if(SetupMode=="return-idle")return;
            if(SetupMode=="reenter-foreign")
            {Factory.Get<CharaView.Delegates.Initialize>(PortraitOwnedSceneNativeKind.Initialize)((CharaView*)Foreign,null,6,0);return;}
            if(SetupMode is "return-foreign" or "throw-foreign")
            {SlotOwner=Foreign;if(SetupMode=="throw-foreign")throw new InvalidOperationException("setup lost slot");return;}
            SlotOwner=view;var v=(CharaViewPortrait*)view;v->ClientObjectId=slot+0xF0;v->ClientObjectIndex=slot;
            if(SetupMode=="throw-owned")throw new InvalidOperationException("setup failed with owned slot");
        }
        public void Render(nint view,uint frame)=>Factory.Get<CharaView.Delegates.Render>(PortraitOwnedSceneNativeKind.Render)((CharaView*)view,frame);
        public void NativeRender(nint view){var v=(CharaViewPortrait*)view;v->State=6;v->CharaViewPortraitCharacterLoaded=true;v->Camera=(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera*)camera;v->CameraManager=0x777;
            character->DrawData.OwnerObject=character;*(uint*)((byte*)character+0x74)=19;}
        public nint GetCharacter(nint view)=>(nint)character;
        public nint WorldCharacter=>0x999;
        public void Release(nint view)=>Factory.Get<CharaView.Delegates.Release>(PortraitOwnedSceneNativeKind.Release)((CharaView*)view);
        public void NativeRelease(nint view){Events.Add("release");var v=(CharaViewPortrait*)view;v->ClientObjectId=uint.MaxValue;v->ClientObjectIndex=0;v->Camera=null;v->CameraManager=0;v->State=0;SlotOwner=TakeoverDuringRelease?Foreign:0;
            if(ReenterTakeoverDuringRelease){ReenterTakeoverDuringRelease=false;Factory.Get<CharaView.Delegates.Initialize>(PortraitOwnedSceneNativeKind.Initialize)((CharaView*)Foreign,null,6,0);}}
        public void Destroy(nint view,byte flags)=>Factory.Get<CharaView.Delegates.Dtor>(PortraitOwnedSceneNativeKind.Dtor)((CharaView*)view,flags);
        public void NativeDestroy(nint view,byte flags){Events.Add("dtor:"+flags);NativeMemory.Free((void*)view);Freed=true;if(TakeoverAfterDtor)SlotOwner=Foreign;}
        public void SetPoseTimed(nint v,ushort t,float f){}public void SetExpression(nint v,byte e){}public void SetHeadDirection(nint v,float x,float y){}public void SetEyeDirection(nint v,float x,float y){}
        public void SetDirectionalColor(nint v,byte r,byte g,byte b){}public void SetDirectionalBrightness(nint v,byte b){}public void SetDirectionalAngle(nint v,short a,short b){}
        public void SetAmbientColor(nint v,byte r,byte g,byte b){}public void SetAmbientBrightness(nint v,byte b){}public void SetBackground(nint v,ushort b){}public void SetCameraZoom(nint v,byte z){}
        public void ApplyCamera(nint v){}public void SetPaused(nint v,bool p){}public bool IsPaused(nint v)=>true;public void SetCharacterVisible(nint v,bool visible){}
        public void Dispose(){if(View!=0&&!Freed)NativeMemory.Free((void*)View);NativeMemory.Free(character);NativeMemory.Free((void*)camera);}
    }
}
#endif
