using System.Reflection;
using Lumina.Excel.Sheets;
var failures = 0; var checks = 0;
void Check(bool condition, string name) { checks++; if (!condition) { failures++; Console.WriteLine("FAIL: " + name); } }
var assembly = Assembly.Load("FireGaze.LocalPreview");
NativeInitializationTests.Run(Check);
NativeLifecycleTests.Run(Check);
NativeLeaseTests.Run(assembly,Check);
var catalogType = assembly.GetType("FireGaze.LocalPreview.Runtime.TrialSpecialHotbarCatalog");
Check(catalogType is not null, "the trial special bar resolves actions from the current client tables");
if(catalogType is not null) {
    using var game = new Lumina.GameData(args[0], new Lumina.LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified });
    FlightPoseBarTests.Run(game,Check);
    dynamic catalog = Activator.CreateInstance(catalogType,game.Excel)!;
    dynamic tank = catalog.GetBar(true,(ushort)223,1L);
    Check((uint)tank.Actions[0].Id==20122,"dwarf tank shows its native cannon action");
    Check((ushort)tank.Actions[0].Timeline==7863,"dwarf tank uses the mount cannon animation");
    Check((bool)tank.Actions[0].UsesMountActor,"mount cannon targets the mount clone");
    Check((int)tank.Actions.Count==6 && tank.Actions[2] is null,"missing slots after the local flight control remain empty");
    dynamic fork=catalog.GetBar(false,(ushort)56,2L);
    Check((uint)fork.Actions[0].Id==50731 && (ushort)fork.Actions[0].Timeline==13384,"pitchfork resolves its tilling action");
    Check(!(bool)fork.Actions[0].UsesMountActor,"ornament action targets the display player");
    dynamic lantern=catalog.GetBar(false,(ushort)58,3L);
    Check((uint)lantern.Actions[0].Id==51026 && (ushort)lantern.Actions[0].Timeline==13385,"antique lantern resolves its raise action");
    dynamic umbrella=catalog.GetBar(false,(ushort)1,4L);
    Check((uint)umbrella.Actions[0].Id==30869 && (uint)umbrella.Actions[1].Id==30868,"parasol native action order preserved");
    Check(catalog.GetBar(false,(ushort)0,1L) is null && catalog.GetBar(true,ushort.MaxValue,1L) is null,"invalid objects never produce a bar");
    dynamic wings=catalog.GetBar(false,(ushort)9,1L);
    Check(wings is not null && wings.Actions[0] is null,"no-action accessory still has an exit bar");
    var modelType = assembly.GetType("FireGaze.LocalPreview.Runtime.TrialSpecialHotbarModel");
    Check(modelType is not null,"trial bar dispatches by current immutable identity");
    if(modelType is not null) {
        dynamic model=Activator.CreateInstance(modelType,catalog)!;
        model.SetPreview(false,(ushort)56,10L);
        Check((ushort)model.Current.Id==56,"trial overrides any real accessory on the bar");
        int mapped=-1;
        bool mappedResult=model.TryMapNativeAction(30868u,(ushort)0,(ushort)1,out mapped);
        Check(mappedResult && mapped==1,"real accessory action maps by native slot, not by old identity");
        mapped=-1;Check(!(bool)model.TryMapNativeAction(1u,(ushort)0,(ushort)1,out mapped),"ordinary actions are not redirected");
        model.BeginFrame(100L);
        var played = new List<uint>(); var exited=0;
        Func<uint,bool> play=id=>{played.Add(id);return true;};
        System.Action exit=()=>exited++;
        Check((bool)model.ActivateSlot(0,10L,play,exit) && played.SequenceEqual(new[]{50731u}),"trial action runs with trial identity");
        Check((bool)model.ActivateSlot(0,10L,play,exit) && played.Count==1,"native and polled dispatch cannot play twice in one frame");
        Check((bool)model.ActivateSlot(1,10L,play,exit) && played.Count==1,"empty replacement slot consumes the old native action");
        Check(!(bool)model.ActivateSlot(0,9L,play,exit) && played.Count==1,"stale queued click never runs a replacement trial");
        model.BeginFrame(101L);Check((bool)model.ActivateSlot(0,10L,play,exit) && played.Count==2,"next input frame can replay the action");
        Check((bool)model.ActivateSlot(6,10L,play,exit) && exited==1,"last slot exits through the native cleanup callback");
        model.SetPreview(false,(ushort)58,11L);
        Check(!(bool)model.ActivateSlot(6,10L,play,exit) && exited==1,"stale exit cannot cancel a new trial");
        model.Clear();Check(model.Current is null && !(bool)model.ActivateSlot(0,11L,play,exit),"cleared bar relinquishes inputs");
        var inputType=assembly.GetType("FireGaze.LocalPreview.Runtime.TrialSpecialKeyboardRouting");
        Check(inputType is not null,"special bar respects hidden UI and text focus before reading native EX bindings");
        if(inputType is not null) {
            var canHandle=inputType.GetMethod("CanHandle")!;
            bool Can(bool active,bool hidden,bool text,bool typing)=>(bool)canHandle.Invoke(null,new object[]{active,hidden,text,typing})!;
            Check(Can(true,false,false,false),"trial EX bindings activate in normal gameplay");
            Check(!Can(false,false,false,false),"normal gameplay without trial keeps all keys");
            Check(!Can(true,true,false,false),"hidden UI keeps all keys");
            Check(!Can(true,false,true,false),"game text entry keeps all keys");
            Check(!Can(true,false,false,true),"plugin text entry keeps all keys");
        }
        var leaseType=assembly.GetType("FireGaze.LocalPreview.Runtime.TrialSpecialNativeBarLease");
        Check(leaseType is not null,"native special bar visibility is owned by a reversible identity lease");
        if(leaseType is not null) {
            dynamic lease=Activator.CreateInstance(leaseType)!;
            int Observe(bool active,nint address,ushort addonId,bool visible,nint owner,uint generation,ushort mount,ushort accessory)
                =>Convert.ToInt32(lease.Observe(active,address,addonId,visible,owner,generation,mount,accessory));
            Check(Observe(true,100,4,true,500,1,0,1)==1,"genuine accessory bar is hidden while a trial replaces it");
            Check(Observe(true,100,4,true,500,1,0,1)==1,"native refresh cannot reveal old special actions during trial");
            Check(Observe(false,100,4,false,500,1,0,1)==2,"same native bar and genuine source restore after exit");
            Check(Observe(false,100,4,false,500,1,0,1)==0,"visibility restoration is owned once");
            Check(Observe(true,100,4,false,500,1,0,1)==0,"preexisting hidden addon is not claimed");
            Check(Observe(false,100,4,false,500,1,0,1)==0,"preexisting hidden addon stays hidden");
            Check(Observe(true,100,4,true,500,1,0,1)==1,"new trial acquires visible native addon");
            Check(Observe(false,100,5,false,500,1,0,1)==0,"reused addon pointer with new id never receives old restore");
            Check(Observe(true,100,4,true,500,1,0,1)==1,"trial can reacquire another visible native addon");
            Check(Observe(false,100,4,false,500,2,0,1)==0,"reused player generation never receives old restore");
            Check(Observe(true,100,4,true,500,1,223,0)==1,"real mount native bar is replaced");
            Check(Observe(false,100,4,false,500,1,224,0)==0,"new native collection state defeats stale restore");
            Check(Observe(true,100,4,true,500,1,0,0)==0,"ordinary pet or player bars are never hidden without a genuine collection");
            Check(Observe(true,100,4,true,500,1,0,1)==1,"lease before addon destruction acquired");
            Check(Observe(false,0,0,false,500,1,0,1)==0,"destroyed addon is never touched");
            Check(Observe(false,100,4,false,500,1,0,1)==0,"destroyed addon lease cannot restore a later rebuild");
        }
        var total=0;
        foreach(var mount in game.GetExcelSheet<Mount>()!) { if(mount.RowId==0 || mount.RowId>short.MaxValue || mount.Singular.ExtractText().Length==0)continue;dynamic bar=catalog.GetBar(true,(ushort)mount.RowId,20L);Check(bar is not null,"valid mount keeps an exit bar #"+mount.RowId);total++; }
        foreach(var ornament in game.GetExcelSheet<Ornament>()!) { if(ornament.RowId==0||ornament.Singular.ExtractText().Length==0)continue;dynamic bar=catalog.GetBar(false,(ushort)ornament.RowId,20L);Check(bar is not null,"valid ornament keeps an exit bar #"+ornament.RowId);total++; }
        Console.WriteLine("Current client catalog checked: "+total);
    }
}
Console.WriteLine($"TrialSpecialHotbarTests: {checks-failures}/{checks} passed.");return failures==0?0:1;
