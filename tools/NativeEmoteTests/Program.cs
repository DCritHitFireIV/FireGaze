using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Runtime;
using Lumina.Excel.Sheets;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

// Calls the production native-timeline predicate against private character memory
// and the actual CN sheet. A base-lane-only regression loses mounted overlays.
unsafe class Program
{
    static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Supply the current CN sqpack path");
        using var game = new Lumina.GameData(args[0],new Lumina.LuminaOptions { DefaultExcelLanguage=Lumina.Data.Language.ChineseSimplified });
        var sheet = game.GetExcelSheet<Emote>()!;
        var data = new GameDataFixture(game);
        var runtime=(ActorPreviewRuntime)RuntimeHelpers.GetUninitializedObject(typeof(ActorPreviewRuntime));
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(ActorPreviewRuntime).GetField("data",flags)!.SetValue(runtime,data);
        var predicate=typeof(ActorPreviewRuntime).GetMethod("IsEmoteTimeline",flags)!;
        Character actor=default;
        bool Matches(ushort emote) => (bool)predicate.Invoke(runtime,[Pointer.Box(Unsafe.AsPointer(ref actor),typeof(Character*)),emote])!;
        int passed=0,failed=0;
        void Check(bool value,string why) { if(value) { passed++; Console.WriteLine("PASS "+why); } else { failed++;Console.WriteLine("FAIL "+why); } }
        Check(sheet.GetRow(21).ActionTimeline[4].RowId==686,"CN mounted emote fixture remains the sampled 686 overlay");
        for(var slot=0;slot<14;slot++)
        {
            actor=default;actor.Mount.MountId=446;actor.Timeline.TimelineSequencer.TimelineIds[0]=166;
            actor.Timeline.TimelineSequencer.TimelineIds[slot]=686;
            Check(Matches(21),"recognizes sampled mounted action in native timeline slot "+slot);
        }
        actor=default;actor.Timeline.TimelineSequencer.TimelineIds[0]=166;
        Check(!Matches(21),"mount idle alone does not prolong the completed action");
        actor.Timeline.TimelineSequencer.TimelineIds[1]=999;
        Check(!Matches(21),"another action's overlay cannot claim the requested action");
        actor=default;
        Check(!Matches(21),"zero animation lanes are not emote matches");
        Check(!Matches(ushort.MaxValue),"missing emote row is rejected");
        using(var playback=new PlaybackFixture(data))
        {
            playback.Request(21);
            Check(playback.PlayCalls==0,"new display defers action until native draw is ready");
            playback.Ready=true;playback.Flush();
            Check(playback.PlayCalls==1,"ready display plays the queued action exactly once");
            playback.Flush();
            Check(playback.PlayCalls==1,"subsequent ready frame does not restart the action");
        }
        using(var playback=new PlaybackFixture(data))
        {
            playback.Request(21);playback.Effects.Clear();playback.Ready=true;playback.Flush();
            Check(playback.PlayCalls==0,"removed pending action cannot start on a later frame");
        }
        using(var playback=new PlaybackFixture(data))
        {
            playback.Request(21);playback.Request(1);playback.Ready=true;playback.Flush();
            Check(playback.PlayCalls==1 && playback.LastTimeline==sheet.GetRow(1).ActionTimeline[0].RowId,"latest pending action replaces the earlier request");
        }
        using(var playback=new PlaybackFixture(data))
        {
            playback.PrepareMotion();playback.Motion();
            Check(playback.PlayCalls==1 && playback.LastTimeline==13 && playback.BaseOverride==13,"stable runtime motion reaches the owned rider");
        }
        using(var playback=new PlaybackFixture(data))
        {
            playback.PrepareMotion();
            playback.OnFinalMotionLookup=()=>playback.Request(21);
            playback.Motion();
            Check(playback.PlayCalls==1 && playback.LastTimeline==686 && playback.BaseOverride==0,"new active overlay during final lookup blocks the stale motion plan without a revision change");
        }
        using(var playback=new PlaybackFixture(data))
        {
            playback.PrepareMotion();
            playback.OnFinalMotionLookup=()=>{playback.Request(21);playback.AdvanceRevision();};
            playback.Motion();
            Check(playback.PlayCalls==1 && playback.LastTimeline==686 && playback.BaseOverride==0,"new preview revision during final lookup blocks the stale motion plan");
        }
        using(var playback=new PlaybackFixture(data))
        {
            playback.PrepareMotion();
            playback.OnFinalMotionLookup=playback.AdvanceRevision;
            playback.Motion();
            Check(playback.PlayCalls==0 && playback.BaseOverride==0,"changed ownership revision discards a motion plan before writing");
            playback.Motion();
            Check(playback.PlayCalls==1 && playback.LastTimeline==13,"discarded motion plan does not consume the next valid playback edge");
        }
        Console.WriteLine($"{passed}/{passed+failed} production native emote cases passed");
        return failed==0?0:1;
    }
}
unsafe sealed class PlaybackFixture : IDisposable
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static PlaybackFixture current=null!;
    readonly Character* source=(Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
    readonly Character* clone=(Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
    readonly ClientObjectManager* manager=(ClientObjectManager*)NativeMemory.AllocZeroed((nuint)sizeof(ClientObjectManager));
    readonly nint* table=(nint*)NativeMemory.AllocZeroed(64);
    readonly Character** local=(Character**)NativeMemory.AllocZeroed((nuint)sizeof(nint));
    readonly ActorPreviewRuntime runtime;
    readonly NativePreviewActor display;
    public Dictionary<PreviewSlot,PreviewEffect> Effects=new();
    public int PlayCalls;public ushort LastTimeline;
    int lookupCount;
    public System.Action? OnFinalMotionLookup;
    public PlaybackFixture(IDataManager data)
    {
        current=this;*(nint**)source=table;*(nint**)clone=table;
        table[1]=(nint)(delegate* unmanaged<GameObject*,ulong>)&Identity;
        source->EntityId=500;clone->EntityId=700;clone->ObjectIndex=207;
        source->Mount.OwnerObject=source;clone->Mount.OwnerObject=clone;
        source->Timeline.OwnerObject=source;clone->Timeline.OwnerObject=clone;
        source->Timeline.TimelineSequencer.Parent=source;clone->Timeline.TimelineSequencer.Parent=clone;
        *local=source;Control.Addresses.GetLocalPlayer.Value=(nint)local;
        manager->BattleCharas[207].BattleChara=(BattleChara*)clone;
        ClientObjectManager.Addresses.Instance.Value=(nint)manager;
        ClientObjectManager.Addresses.GetObjectByIndex.Value=(nint)(delegate* unmanaged<ClientObjectManager*,ushort,Character*>)&Lookup;
        ActionTimelineSequencer.Addresses.PlayTimeline.Value=(nint)(delegate* unmanaged<ActionTimelineSequencer*,ushort,void*,void>)&Play;
        display=(NativePreviewActor)typeof(NativePreviewActor).GetConstructors(All).Single().Invoke([Pointer.Box(manager,typeof(ClientObjectManager*)),(ushort)207,Pointer.Box(clone,typeof(Character*)),Pointer.Box(source,typeof(Character*)),null]);
        runtime=(ActorPreviewRuntime)RuntimeHelpers.GetUninitializedObject(typeof(ActorPreviewRuntime));
        Set("data",data);Set("effects",Effects);Set("visualActor",display);
    }
    void Set(string name,object value)=>typeof(ActorPreviewRuntime).GetField(name,All)!.SetValue(runtime,value);
    public bool Ready { set=>typeof(NativePreviewActor).GetProperty("IsReady")!.SetValue(display,value); }
    public void Request(ushort id)
    {
        var effect=new PreviewEffect(PreviewSlot.Emote,id);
        typeof(ActorPreviewRuntime).GetMethod("Apply",All)!.Invoke(runtime,[Pointer.Box(clone,typeof(Character*)),effect]);
        Effects[PreviewSlot.Emote]=effect;
    }
    public void Flush()
    {
        var method=typeof(ActorPreviewRuntime).GetMethod("ApplyPendingEmote",All);
        if(method is not null)method.Invoke(runtime,null);
    }
    public ushort BaseOverride=>clone->Timeline.BaseOverride;
    public void PrepareMotion()
    {
        Ready=true;source->Timeline.TimelineSequencer.TimelineIds[0]=13;
        clone->Mount.MountId=446;clone->Timeline.BaseOverride=0;
        Effects[PreviewSlot.Mount]=new(PreviewSlot.Mount,446);
        lookupCount=0;
    }
    public void AdvanceRevision()
    {
        var field=typeof(ActorPreviewRuntime).GetField("revision",All)!;
        field.SetValue(runtime,(long)field.GetValue(runtime)!+1);
    }
    public void Motion()
    {
        lookupCount=0;
        var method=typeof(ActorPreviewRuntime).GetMethod("ApplyGroundedMotion",All);
        if(method is not null)method.Invoke(runtime,[Pointer.Box(clone,typeof(Character*))]);
        else if(display.Actor==clone)
        {
            // Exercise the current production writer through the pre-fix tail boundary.
            var emote=Effects.TryGetValue(PreviewSlot.Emote,out var effect)
                && (bool)typeof(ActorPreviewRuntime).GetMethod("IsEmoteTimeline",All)!.Invoke(runtime,[Pointer.Box(clone,typeof(Character*)),effect.Id])!;
            var writer=typeof(NativePreviewActor).GetMethod("UpdateGroundedMotion",All)!;
            writer.Invoke(display,writer.GetParameters().Length==1?[emote]:[emote,null]);
        }
    }
    [UnmanagedCallersOnly] static ulong Identity(GameObject* obj)=>obj->EntityId;
    [UnmanagedCallersOnly] static Character* Lookup(ClientObjectManager* manager,ushort slot)
    {
        if(++current.lookupCount==4 && current.OnFinalMotionLookup is {} callback)
        { current.OnFinalMotionLookup=null;callback(); }
        return (Character*)manager->BattleCharas[slot].BattleChara;
    }
    [UnmanagedCallersOnly] static void Play(ActionTimelineSequencer* sequencer,ushort id,void* unk)
    {
        current.PlayCalls++;current.LastTimeline=id;
        sequencer->TimelineIds[id==686?1:0]=id;
    }
    public void Dispose() {NativeMemory.Free(source);NativeMemory.Free(clone);NativeMemory.Free(manager);NativeMemory.Free(table);NativeMemory.Free(local);}
}
sealed class GameDataFixture(Lumina.GameData game) : IDataManager
{
    public Dalamud.Game.ClientLanguage Language=>Dalamud.Game.ClientLanguage.ChineseSimplified;
    public Lumina.GameData GameData=>game;
    public Lumina.Excel.ExcelModule Excel=>game.Excel;
    public bool HasModifiedGameDataFiles=>false;
    public Lumina.Excel.ExcelSheet<T> GetExcelSheet<T>(Dalamud.Game.ClientLanguage? language=null,string? name=null) where T:struct,Lumina.Excel.IExcelRow<T> => game.GetExcelSheet<T>(name:name)!;
    public Lumina.Excel.SubrowExcelSheet<T> GetSubrowExcelSheet<T>(Dalamud.Game.ClientLanguage? language=null,string? name=null) where T:struct,Lumina.Excel.IExcelSubrow<T> => throw new NotSupportedException();
    public Lumina.Data.FileResource? GetFile(string path)=>throw new NotSupportedException();
    public T? GetFile<T>(string path) where T:Lumina.Data.FileResource=>throw new NotSupportedException();
    public Task<T> GetFileAsync<T>(string path,CancellationToken token) where T:Lumina.Data.FileResource=>throw new NotSupportedException();
    public bool FileExists(string path)=>throw new NotSupportedException();
}
