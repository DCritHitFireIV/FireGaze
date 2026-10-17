using System.Reflection;
using FireGaze.LocalPreview.Interaction;
using FireGaze.LocalPreview.Runtime;
using Lumina;
using Lumina.Excel.Sheets;

internal static class FlightPoseBarTests
{
    public static void Run(GameData game,System.Action<bool,string> check)
    {
        var jump=game.GetExcelSheet<GeneralAction>()!.GetRow(2);
        Console.WriteLine($"GeneralAction #2: {jump.Name.ExtractText()}, icon={jump.Icon}");
        var catalog=new TrialSpecialHotbarCatalog(game.Excel);
        var tank=catalog.GetBar(true,223,10)!;
        var normal=catalog.GetBar(true,335,11)!;
        bool Flight(TrialSpecialAction? action)=>action?.GetType().GetProperty("Kind")?.GetValue(action)?.ToString()=="FlightPose";
        check(tank.Actions[0]?.Id==20122 && Flight(tank.Actions[1]),"mount flight-pose button follows its dedicated action");
        check(Flight(normal.Actions[0]),"mount without dedicated actions puts flight-pose toggle in first slot");
        var button=normal.Actions[0];
        check(button is not null && button.Name=="飞行姿态" && button.Icon==jump.Icon && button.Timeline==0,
            "flight-pose button uses current client's genuine Jump icon without an action timeline");
        check(!catalog.GetBar(false,56,12)!.Actions.Any(Flight),"accessory bar has no mount flight-pose button");
        var maximum=0;
        foreach(var mount in game.GetExcelSheet<Mount>()!)
        {
            if(mount.RowId==0 || mount.RowId>short.MaxValue || mount.Singular.ExtractText().Length==0)continue;
            var bar=catalog.GetBar(true,(ushort)mount.RowId,20)!;
            var native=bar.Actions.Select((action,slot)=>(action,slot)).Where(x=>x.action is not null && !Flight(x.action)).ToArray();
            maximum=Math.Max(maximum,native.Length);
            var flight=bar.Actions.Select((action,slot)=>(action,slot)).Where(x=>Flight(x.action)).ToArray();
            check(flight.Length==1 && flight[0].slot==(native.Length==0?0:native.Max(x=>x.slot)+1),
                "mount flight-pose follows all dedicated actions #"+mount.RowId);
        }
        Console.WriteLine("Maximum current native mount special actions: "+maximum);
        var commandFor=typeof(TrialNativeSpecialHotbar).GetMethod("CommandFor",BindingFlags.Static|BindingFlags.NonPublic);
        check(commandFor is not null,"native bar resolves local-only controls to safe displayed command metadata");
        if(commandFor is not null && button is not null)
        {
            var command=(TrialNativeHotbarCommand)commandFor.Invoke(null,[button])!;
            check(command==new TrialNativeHotbarCommand(10,2),"native flight slot displays GeneralAction Jump but remains leased to local routing");
            command=(TrialNativeHotbarCommand)commandFor.Invoke(null,[tank.Actions[0]])!;
            check(command==new TrialNativeHotbarCommand(1,20122),"dedicated mount action command type and identity remain unchanged");
        }
        var set=typeof(TrialSpecialHotbarModel).GetMethod("SetPreview",[typeof(bool),typeof(ushort),typeof(long),typeof(bool)]);
        var activate=typeof(TrialSpecialHotbarModel).GetMethod("ActivateSlot",[typeof(int),typeof(long),typeof(Func<uint,bool>),typeof(System.Action),typeof(Func<bool>)]);
        check(set is not null && activate is not null,"flight-pose routing has separate visual-state and activation callbacks");
        if(set is null || activate is null)return;
        var model=new TrialSpecialHotbarModel(catalog);var plays=0;var toggles=0;var exits=0;
        Func<uint,bool> play=_=>{plays++;return true;};Func<bool> toggle=()=>{toggles++;return true;};System.Action exit=()=>exits++;
        set.Invoke(model,[true,(ushort)335,30L,false]);model.BeginFrame(100);
        bool Activate(int slot,long revision)=>(bool)activate.Invoke(model,[slot,revision,play,exit,toggle])!;
        check(Activate(0,30) && toggles==1 && plays==0 && exits==0,"flight slot only invokes local visual toggle");
        set.Invoke(model,[true,(ushort)335,30L,true]);
        check(model.Current!.Actions[0]!.Name=="地面姿态","active flight pose exposes switch-back label");
        check(Activate(0,30) && toggles==1,"publishing toggled pose cannot bypass same-frame input deduplication");
        model.BeginFrame(101);check(Activate(0,30) && toggles==2,"later frame can switch back to ground pose");
        check(!Activate(0,29) && toggles==2,"stale flight click cannot toggle replacement preview");
        check(!model.TryMapNativeAction(2,335,0,out _),"real native Jump/action id never routes to flight toggle");
        check(Activate(6,30) && exits==1,"flight control preserves final exit slot");
        set.Invoke(model,[true,(ushort)223,31L,false]);model.BeginFrame(102);
        check(Activate(0,31) && plays==1 && toggles==2,"native mount dedicated action still plays through action callback");
        check(Activate(1,31) && toggles==3 && plays==1,"dedicated mount flight slot uses visual callback after its action");
        model.Clear();check(!Activate(1,31) && toggles==3,"cleared preview releases local flight control");
    }
}
