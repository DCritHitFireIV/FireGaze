using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

unsafe static class NativeGposeHarness
{
    static readonly MethodInfo? writer = typeof(Program).Assembly.GetType("FireGaze.LocalPreview.Runtime.GposeEmoteService")
        ?.GetMethod("TryPlayOnGposeClone", BindingFlags.NonPublic | BindingFlags.Static);
    static readonly MethodInfo? defaultWriter = typeof(Program).Assembly.GetType("FireGaze.LocalPreview.Runtime.GposeEmoteService")
        ?.GetMethod("TryApplyEmoteDefault", BindingFlags.NonPublic | BindingFlags.Static);
    static Fixture? current;
    public static void RunMountedServiceCases(Lumina.GameData game,Action<bool,string> check)
    {
        var row=game.GetExcelSheet<Lumina.Excel.Sheets.Emote>()!.GetRow(21);
        check(row.ActionTimeline[0].RowId==712 && row.ActionTimeline[4].RowId==686,
            "current CN laughter supplies distinct foot 712 and mounted 686 loop variants");
        using var f=new Fixture();
        var player=(FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState*)NativeMemory.AllocZeroed((nuint)sizeof(FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState));
        var oldPlayer=FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Addresses.Instance.Value;
        var update=typeof(GposeEmoteService).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var fields=BindingFlags.Instance|BindingFlags.NonPublic;
        bool posing=false;
        var framework=FixtureProxy.Make<IFramework>((m,_)=>m.Name=="get_IsInFrameworkUpdateThread"?true:null);
        var client=FixtureProxy.Make<IClientState>((m,_)=>m.Name switch{"get_IsLoggedIn"=>true,"get_IsGPosing"=>posing,_=>null});
        var source=FixtureProxy.Make<IPlayerCharacter>((m,_)=>m.Name=="get_Address"?(nint)f.Source:null);
        var clone=FixtureProxy.Make<IGameObject>((m,_)=>m.Name switch{"get_Address"=>(nint)f.Clone,"get_ObjectIndex"=>f.Clone->ObjectIndex,_=>null});
        var objects=FixtureProxy.Make<IObjectTable>((m,_)=>m.Name switch
        {
            "get_LocalPlayer"=>source,
            "GetEnumerator"=>((IEnumerable<IGameObject>)new[]{clone}).GetEnumerator(),
            _=>null,
        });
        try
        {
            FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Addresses.Instance.Value=(nint)player;player->ContentId=100;
            GposeEmoteService MakeService(ushort emote=21)
            {
                posing=false;
                var service=new GposeEmoteService(framework,client,objects,new GameDataFixture(game),null!,
                    FixtureProxy.Make<IPluginLog>((_,_)=>null),()=>{},()=>true);
                typeof(GposeEmoteService).GetField("installFailed",fields)!.SetValue(service,true);
                typeof(GposeEmoteService).GetField("remainingFrames",fields)!.SetValue(service,180);
                service.RecordPreview(emote);
                var memory=(GposeEmoteMemory)typeof(GposeEmoteService).GetField("memory",fields)!.GetValue(service)!;
                var entry=memory.BeginEntry(100);memory.CompleteEntry(entry,true);posing=true;
                return service;
            }
            f.Clone->Mount.OwnerObject=f.Clone;f.Clone->Mount.MountId=335;
            f.Source->Mount.OwnerObject=f.Source;f.Source->Mount.MountId=335;
            var authoritative=new ReadOnlySpan<byte>(f.Source,sizeof(Character)).ToArray();
            using(var service=MakeService())
            {
                update.Invoke(service,[framework]);
                check(service.ReplayCount==1 && f.Clone->Timeline.BaseOverride==686,
                    "real GPose service pins mounted laughter variant on its own mounted clone");
                check(new ReadOnlySpan<byte>(f.Source,sizeof(Character)).SequenceEqual(authoritative),
                    "mounted GPose service replay leaves authoritative mounted source unchanged");
            }
            // A sit/ground-sit reset removed the clone's mount. The real source still
            // has a mount here, proving selection uses the photo clone's actual state.
            f.Clone->Mount.MountId=0;f.Clone->Mode=CharacterModes.Normal;
            using(var service=MakeService())
            {
                update.Invoke(service,[framework]);
                check(service.ReplayCount==1 && f.Clone->Timeline.BaseOverride==712,
                    "ground-sit reset with no clone mount keeps foot laughter despite a mounted source");
            }
            f.Clone->Mount.MountId=335;f.Accept=false;f.Clone->Mode=CharacterModes.Normal;f.Clone->Timeline.BaseOverride=333;
            var beforeModes=f.ModeCalls;
            using(var service=MakeService())
            {
                update.Invoke(service,[framework]);
                check(service.ReplayCount==0 && f.ModeCalls==beforeModes && f.Clone->Timeline.BaseOverride==333
                    && f.Clone->Mode==CharacterModes.Normal,
                    "real mounted GPose service preserves override and mode when native PlayEmote rejects");
            }
            f.Accept=true;f.Clone->Mount.OwnerObject=f.Source;f.Clone->Timeline.BaseOverride=333;
            var beforeCalls=f.Calls;
            using(var service=MakeService())
            {
                update.Invoke(service,[framework]);
                check(service.ReplayCount==0 && f.Calls==beforeCalls && f.Clone->Timeline.BaseOverride==333,
                    "a nonzero GPose clone mount with transplanted owner fails before native replay");
            }
            f.Clone->Mount.OwnerObject=f.Clone;
            var footOnly=game.GetExcelSheet<Lumina.Excel.Sheets.Emote>()!.First(x=>x.RowId is >0 and <=ushort.MaxValue
                && x.ActionTimeline[0].RowId!=0 && x.ActionTimeline[4].RowId==0 && x.EmoteMode.RowId is not (1 or 2));
            beforeCalls=f.Calls;
            using(var service=MakeService((ushort)footOnly.RowId))
            {
                update.Invoke(service,[framework]);
                check(service.ReplayCount==0 && f.Calls==beforeCalls && f.Clone->Timeline.BaseOverride==333,
                    "a mounted replay with no mounted timeline cannot pin an unrelated foot variant");
            }
        }
        finally
        {
            FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Addresses.Instance.Value=oldPlayer;
            NativeMemory.Free(player);
        }
    }
    public static void Run(Action<bool, string> check)
    {
        using var f = new Fixture();
        bool Play(ushort id = 21, byte pose = 0, Func<bool>? valid = null)
            => (bool?)writer?.Invoke(null, [Pointer.Box(f.Clone, typeof(Character*)), Pointer.Box(f.Source, typeof(Character*)), id, pose, valid]) ?? false;

        byte[] original = new ReadOnlySpan<byte>(f.Source, sizeof(Character)).ToArray();
        check(Play(21, 3) && f.Calls == 1 && f.EmoteId == 21 && f.Clone->EmoteController.CPoseState == 3,
            "a ready own GPose clone receives the requested emote and explicit pose");
        check(f.Calls == 1 && f.Quiet && f.Target == 700 && f.OptionVtable,
            "native clone replay preserves its target and suppresses a public chat log");
        check(new ReadOnlySpan<byte>(f.Source, sizeof(Character)).SequenceEqual(original),
            "GPose replay leaves every byte of the authoritative player unchanged");
        f.Clone->HomeWorld = ushort.MaxValue;
        check(Play() && f.Calls == 2,
            "the native primary-player mapping identifies the real GPose clone with an unset home world");
        f.Clone->HomeWorld = 1;
        int calls = f.Calls;
        f.OwnGposeClone = 0;
        check(!Play() && f.Calls == calls,
            "a same-name same-world posing-tool actor outside the native primary mapping cannot receive replay");
        calls = f.Calls;
        f.OwnGposeClone = (nint)f.Clone; f.LocalPlayerId = 0x12345679;
        check(!Play() && f.Calls == calls,
            "a source with a different native local-player identity cannot receive replay");
        calls = f.Calls;
        f.LocalPlayerId = 0x12345678; f.OwnGposeClone = (nint)f.Source;
        check(!Play() && f.Calls == calls,
            "native primary mapping to a different copy rejects a lookalike clone");
        calls = f.Calls;
        f.OwnGposeClone = (nint)f.Clone; f.LocalPlayer = (nint)f.Clone;
        check(!Play() && f.Calls == calls,
            "a supplied source that is not the native local player cannot authorize replay");
        calls = f.Calls;
        f.LocalPlayer = (nint)f.Source; f.EventsAvailable = false;
        check(!Play() && f.Calls == calls,
            "an unavailable native GPose controller fails closed");
        calls = f.Calls;
        f.EventsAvailable = true;
        f.Source->EntityId = 0xE0000000;
        check(!Play() && f.Calls == calls,
            "a source without a valid native object identity never replays");
        calls = f.Calls;
        f.Source->EntityId = 0x12345678;
        f.Clone->ObjectIndex = 199;
        check(!Play() && f.Calls == calls, "a live-world actor is never a GPose playback target");
        f.Clone->ObjectIndex = 201; f.Clone->HomeWorld = 2;
        check(!Play() && f.Calls == calls, "a same-name player on another world cannot receive the replay");
        f.Clone->HomeWorld = 1; f.Clone->NameString = "Other Player";
        check(!Play() && f.Calls == calls, "another person's GPose clone cannot receive the replay");
        f.Clone->NameString = "Local Player"; f.Clone->ObjectKind = ObjectKind.BattleNpc;
        check(!Play() && f.Calls == calls, "non-player native allocations are rejected");
        f.Clone->ObjectKind = ObjectKind.Pc; f.Clone->DrawObject->IsVisible = false;
        check(!Play() && f.Calls == calls, "an undrawn GPose clone waits without consuming playback");
        f.Clone->DrawObject->IsVisible = true; f.Clone->Timeline.OwnerObject = f.Source;
        check(!Play() && f.Calls == calls, "a transplanted timeline container cannot write through the real player");
        f.Clone->Timeline.OwnerObject = f.Clone;
        check(!Play(valid: () => false) && f.Calls == calls, "a stale final GPose binding is rejected before the native call");
        check(!Play(valid: () => { (*(uint*)((byte*)f.Clone + 0x74))++; return true; }) && f.Calls == calls,
            "allocation reuse during final validation cannot receive an old replay");
        f.Accept = false; f.Clone->Timeline.BaseOverride = 333;
        check(!Play() && f.Calls == calls + 1 && f.Clone->Timeline.BaseOverride == 333,
            "a native playback rejection restores only the clone's previous override");
        f.Accept = true;
        check(Play() && f.Calls == calls + 2 && f.Clone->Timeline.BaseOverride == 0,
            "successful GPose playback clears the stale native base override");

        bool Apply(ushort timeline = 511, Func<bool>? valid = null)
            => (bool?)defaultWriter?.Invoke(null, [Pointer.Box(f.Clone, typeof(Character*)), Pointer.Box(f.Source, typeof(Character*)), (ushort)21, (byte)0, timeline, valid]) ?? false;
        calls = f.Calls;
        f.PhotoPhase = 6;
        check(!Apply() && f.Calls == calls && f.ModeCalls == 0,
            "a drawn photo clone still in native entry initialization waits before applying the remembered action");
        f.PhotoPhase = 7;
        check(Apply() && f.Calls == calls + 1 && f.ModeCalls == 1
            && f.Clone->Mode == CharacterModes.AnimLock && f.Clone->Timeline.BaseOverride == 511,
            "a GPose default pins the selected loop through the native animation mode after one emote playback");
        check(new ReadOnlySpan<byte>(f.Source, sizeof(Character)).SequenceEqual(original),
            "the native GPose loop pin leaves every real-player byte unchanged");
        calls = f.Calls;
        check(!Apply(0) && f.Calls == calls && f.ModeCalls == 1,
            "an absent loop timeline cannot consume a replay or enter animation lock");
        Character.Addresses.SetMode.Value = 0;
        check(!Apply() && f.Calls == calls && f.ModeCalls == 1,
            "an unavailable native mode API fails before starting a partial GPose default");
        f.InstallMode();
        f.Accept = false; f.Clone->Timeline.BaseOverride = 333; f.Clone->Mode = CharacterModes.Normal;
        check(!Apply() && f.Calls == calls + 1 && f.ModeCalls == 1
            && f.Clone->Mode == CharacterModes.Normal && f.Clone->Timeline.BaseOverride == 333,
            "a rejected emote never pins a loop or changes the GPose clone mode");
        f.Accept = true;
        f.AfterPlay = () => f.OwnGposeClone = 0;
        check(!Apply() && f.ModeCalls == 1 && f.Clone->Mode == CharacterModes.Normal,
            "losing native GPose ownership during emote playback prevents a later mode write");
        f.AfterPlay = null; f.OwnGposeClone = (nint)f.Clone;
        f.AfterMode = () => (*(uint*)((byte*)f.Clone + 0x74))++;
        check(!Apply() && f.Clone->Timeline.BaseOverride == 0,
            "allocation reuse during the native mode call cannot receive an old loop pin");
        f.AfterMode = null;
        int validations = 0;
        check(!Play(valid: () =>
        {
            if (++validations == 2) (*(uint*)((byte*)f.Clone + 0x74))++;
            return true;
        }), "a final external binding validation cannot hide allocation reuse occurring inside that validation");
    }

    internal sealed class Fixture : IDisposable
    {
        public Character* Source = (Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
        public Character* Clone = (Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
        readonly DrawObject* draw = (DrawObject*)NativeMemory.AllocZeroed((nuint)sizeof(DrawObject));
        readonly nint* optionVtable = (nint*)NativeMemory.AllocZeroed(32);
        readonly nint previousPlay = EmoteController.Addresses.PlayEmote.Value;
        readonly nint previousOptions = EmoteController.PlayEmoteOption.Addresses.StaticVirtualTable.Value;
        readonly nint previousMode = Character.Addresses.SetMode.Value;
        readonly nint previousTimeline=ActionTimelineSequencer.Addresses.PlayTimeline.Value;
        readonly EventFramework* events = (EventFramework*)NativeMemory.AllocZeroed((nuint)sizeof(EventFramework));
        readonly nint* eventsPointer = (nint*)NativeMemory.AllocZeroed((nuint)sizeof(nint));
        readonly nint previousEvents = EventFramework.Addresses.Instance.Value;
        readonly Control* control = (Control*)NativeMemory.AllocZeroed((nuint)sizeof(Control));
        readonly nint* localPlayerPointer = (nint*)NativeMemory.AllocZeroed((nuint)sizeof(nint));
        readonly nint previousControl = Control.Addresses.Instance.Value;
        readonly nint previousLocalPlayer = Control.Addresses.GetLocalPlayer.Value;
        public nint OwnGposeClone { set => *(nint*)((byte*)&events->EventSceneModule.EventGPoseController + 0x3508) = value; }
        public int PhotoPhase { set => *(int*)((byte*)&events->EventSceneModule.EventGPoseController + 0x3520) = value; }
        public uint LocalPlayerId { set => control->LocalPlayerEntityId = value; }
        public nint LocalPlayer { set { *localPlayerPointer = value; control->LocalPlayer = (BattleChara*)value; } }
        public bool EventsAvailable { set => *eventsPointer = value ? (nint)events : 0; }
        public int Calls, ModeCalls, TimelineCalls;
        public Action? AfterPlay, AfterMode;
        public uint EmoteId;
        public bool Accept = true, Quiet, OptionVtable;
        public ulong Target;
        public Fixture()
        {
            current = this;
            Source->NameString = "Local Player"; Source->HomeWorld = 1; Source->ObjectIndex = 0; Source->ObjectKind = ObjectKind.Pc;
            Source->EntityId = 0x12345678;
            Clone->NameString = "Local Player"; Clone->HomeWorld = 1; Clone->ObjectIndex = 201; Clone->ObjectKind = ObjectKind.Pc;
            *eventsPointer = (nint)events;
            EventFramework.Addresses.Instance.Value = (nint)eventsPointer;
            Control.Addresses.Instance.Value = (nint)control;
            Control.Addresses.GetLocalPlayer.Value = (nint)localPlayerPointer;
            LocalPlayer = (nint)Source; LocalPlayerId = Source->EntityId; OwnGposeClone = (nint)Clone;
            PhotoPhase = 7;
            Clone->EmoteController.OwnerObject = (BattleChara*)Clone;
            Clone->EmoteController.Target = 700UL;
            Clone->Timeline.OwnerObject = Clone;
            Clone->Timeline.TimelineSequencer.Parent = Clone;
            Clone->DrawObject = draw; draw->IsVisible = true;
            *(uint*)((byte*)Clone + 0x74) = 1;
            EmoteController.Addresses.PlayEmote.Value = (nint)(delegate* unmanaged<EmoteController*, uint, EmoteController.PlayEmoteOption*, byte>)&Play;
            EmoteController.PlayEmoteOption.Addresses.StaticVirtualTable.Value = (nint)optionVtable;
            InstallMode();
            ActionTimelineSequencer.Addresses.PlayTimeline.Value=(nint)(delegate* unmanaged<ActionTimelineSequencer*,ushort,void*,void>)&PlayTimeline;
        }
        public void InstallMode() => Character.Addresses.SetMode.Value = (nint)(delegate* unmanaged<Character*, CharacterModes, byte, void>)&SetMode;
        [UnmanagedCallersOnly]
        static byte Play(EmoteController* controller, uint id, EmoteController.PlayEmoteOption* options)
        {
            var f = current!;
            f.Calls++; f.EmoteId = id;
            f.Quiet = options != null && options->DisableLogMessage;
            f.Target = options == null ? 0 : options->TargetId.Id;
            f.OptionVtable = options != null && (nint)options->VirtualTable == (nint)f.optionVtable;
            if (f.Accept) controller->EmoteId = (ushort)id;
            f.AfterPlay?.Invoke();
            return f.Accept ? (byte)1 : (byte)0;
        }
        [UnmanagedCallersOnly]
        static void SetMode(Character* actor, CharacterModes mode, byte parameter)
        {
            var f = current!;
            f.ModeCalls++;
            actor->Mode = mode; actor->ModeParam = parameter;
            f.AfterMode?.Invoke();
        }
        [UnmanagedCallersOnly]
        static void PlayTimeline(ActionTimelineSequencer* sequencer,ushort id,void* extra)
        {
            current!.TimelineCalls++;sequencer->TimelineIds[0]=id;
        }
        public void Dispose()
        {
            EmoteController.Addresses.PlayEmote.Value = previousPlay;
            EmoteController.PlayEmoteOption.Addresses.StaticVirtualTable.Value = previousOptions;
            Character.Addresses.SetMode.Value = previousMode;
            ActionTimelineSequencer.Addresses.PlayTimeline.Value=previousTimeline;
            EventFramework.Addresses.Instance.Value = previousEvents;
            Control.Addresses.Instance.Value = previousControl;
            Control.Addresses.GetLocalPlayer.Value = previousLocalPlayer;
            NativeMemory.Free(Source); NativeMemory.Free(Clone); NativeMemory.Free(draw); NativeMemory.Free(optionVtable);
            NativeMemory.Free(events); NativeMemory.Free(eventsPointer);
            NativeMemory.Free(control); NativeMemory.Free(localPlayerPointer);
            current = null;
        }
    }
}
