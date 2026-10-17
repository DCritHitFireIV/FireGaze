using System.Reflection;

internal static class NativeLeaseTests
{
    public static void Run(Assembly assembly, System.Action<bool,string> check)
    {
        var identityType=assembly.GetType("FireGaze.LocalPreview.Runtime.TrialNativeHotbarIdentity");
        var commandType=assembly.GetType("FireGaze.LocalPreview.Runtime.TrialNativeHotbarCommand");
        var leaseType=assembly.GetType("FireGaze.LocalPreview.Runtime.TrialNativeHotbarLease");
        var bridgeType=assembly.GetType("FireGaze.LocalPreview.Interaction.TrialNativeSpecialHotbar");
        var layout=bridgeType?.GetProperty("LayoutValid",BindingFlags.Public|BindingFlags.Static);
        check(layout is not null && (bool)layout.GetValue(null)!,"actual FFCS native hotbar layout validates without Marshal.OffsetOf marshaling");
        check(identityType is not null && commandType is not null && leaseType is not null,
            "native special hotbar has a source/module/addon/revision lease");
        if(identityType is null || commandType is null || leaseType is null)return;
        dynamic Identity(long revision=9,uint generation=7,ushort mount=0,ushort accessory=41,nint module=1000,nint addon=2000,ushort addonId=4)
            =>Activator.CreateInstance(identityType,module,addon,addonId,(nint)3000,generation,mount,accessory,revision)!;
        dynamic Command(byte type,uint id)=>Activator.CreateInstance(commandType,type,id)!;
        dynamic original=Array.CreateInstance(commandType,16);dynamic expected=Array.CreateInstance(commandType,16);
        for(var i=0;i<16;i++){original.SetValue(Command(1,(uint)(100+i)),i);expected.SetValue(Command(0,0),i);}
        expected.SetValue(Command(1,50731),0);expected.SetValue(Command(10,23),6);
        dynamic lease=Activator.CreateInstance(leaseType)!;
        check((bool)lease.Acquire(Identity(),original,expected,0u,false),"native lease captures ephemeral slots and hidden addon state");
        int slot=-1;
        check((bool)lease.TryRoute(Identity(),(nint)(1000+0x105a0),Command(1,50731),out slot) && slot==0,"native slot pointer maps to trial action");
        slot=-1;check((bool)lease.TryRoute(Identity(),(nint)(1000+0x105a0+6*0xe8),Command(10,23),out slot) && slot==6,"native last slot maps to local exit");
        slot=-1;check((bool)lease.TryRoute(Identity(),(nint)(1000+0x105a0+8*0xe8),Command(0,0),out slot) && slot==8,"leased empty slots are consumed");
        slot=-1;check(!(bool)lease.TryRoute(Identity(),(nint)(1000+0x105a0+1),Command(1,50731),out slot),"unaligned native slot pointer is never consumed");
        slot=-1;check(!(bool)lease.TryRoute(Identity(),(nint)(1000+0x11420),Command(1,50731),out slot),"cross hotbar is outside keyboard lease");
        slot=-1;check(!(bool)lease.TryRoute(Identity(10),(nint)(1000+0x105a0),Command(1,50731),out slot),"stale revision cannot execute new trial");
        slot=-1;check(!(bool)lease.TryRoute(Identity(generation:8),(nint)(1000+0x105a0),Command(1,50731),out slot),"reused source generation cannot execute trial");
        slot=-1;check(!(bool)lease.TryRoute(Identity(addonId:5),(nint)(1000+0x105a0),Command(1,50731),out slot),"rebuilt addon cannot execute old lease");
        slot=-1;check(!(bool)lease.TryRoute(Identity(),(nint)(1000+0x105a0),Command(1,51026),out slot),"native rewritten slot wins over trial lease");
        check((bool)lease.CanRestore(Identity()),"unchanged identity permits compare-and-swap restoration");
        check(!(bool)lease.CanRestore(Identity(accessory:58)),"new genuine accessory forbids restoring old slots");
        check((bool)lease.CanRestorePresentation(Identity(accessory:58)),"same UI allocation can CAS-remove its remaining trial commands after genuine collection changes");
        check(!(bool)lease.CanRestorePresentation(Identity(generation:8)),"new source generation defeats UI cleanup too");
        check(!(bool)lease.CanRestorePresentation(Identity(addonId:5)),"rebuilt addon defeats UI cleanup too");
        check((bool)lease.CanRestoreCommands(Identity(addon:0,addonId:0)),"destroyed addon still allows CAS cleanup of same module transient commands");
        check((bool)lease.CanRestoreModule(Identity(addon:0,addonId:0)),"same source mode lease can be restored after addon destruction without touching addon");
        check(!(bool)lease.CanRestoreModule(Identity(accessory:58)),"new genuine collection owns module mode and keyboard input");
        check(!(bool)lease.CanRestoreCommands(Identity(generation:8)),"new source allocation forbids module command restoration");
        check(!(bool)lease.CanRestore(Identity(module:1001)),"new native module forbids restoring old slots");
        check((uint)lease.OriginalMode==0 && !(bool)lease.OriginalVisible,"native mode and visibility snapshot remain distinct");
        check((uint)lease.Original(0).Id==100 && (byte)lease.Original(0).Type==1,"restore stores commands instead of native string pointers");
        check((bool)lease.IsExpected(0,Command(1,50731)) && !(bool)lease.IsExpected(0,Command(1,51026)),"restoration only owns unchanged expected commands");
        var invoke=leaseType.GetMethod("CanInvokeNativeSlot");
        check(invoke is not null,"development native PetSlot probe requires current lease guard");
        if(invoke is not null)
        {
            bool Can(object identity,int slot,object command)=>(bool)invoke.Invoke((object)lease,[identity,slot,command])!;
            check(Can(Identity(),0,Command(1,50731)) && Can(Identity(),6,Command(10,23)),"native probe admits only currently owned action and exit slot");
            check(!Can(Identity(),7,Command(0,0)) && !Can(Identity(),-1,Command(0,0)),"native probe cannot execute extra slots or negative index");
            check(!Can(Identity(10),0,Command(1,50731)),"queued native probe with stale revision never executes");
            check(!Can(Identity(),0,Command(1,51026)),"game overwritten command defeats native probe before original dispatcher");
        }
        lease.Clear();slot=-1;
        check(!(bool)lease.Held && !(bool)lease.TryRoute(Identity(),(nint)(1000+0x105a0),Command(1,50731),out slot),"cleared lease releases native input");
        check(!(bool)lease.Acquire(Identity(module:0),original,expected,0u,false),"invalid singleton cannot acquire native bar");
        lease.Suspend(9L);
        check(!(bool)lease.CanAcquire(9L) && (bool)lease.CanAcquire(10L),"native update suspends only the current trial revision");
        check(!(bool)lease.Acquire(Identity(),original,expected,0u,false),"same trial cannot recapture residual trial commands as genuine state");
        check((bool)lease.Acquire(Identity(10),original,expected,0u,false),"new explicit trial revision can acquire native UI again");
        lease.Clear();
        check(!(bool)lease.CanAcquire(9L),"clearing ownership does not erase revision suspension");
    }
}
