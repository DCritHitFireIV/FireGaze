using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;
using PlayerState=FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

internal static unsafe class GposeSittingServiceCases
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public static void Run(Lumina.GameData game,Action<bool,string> check)
    {
        Console.WriteLine("CN idle timeline #3: "+game.GetExcelSheet<ActionTimeline>()!.GetRow(3).Key.ExtractText());
        foreach(ushort id in new ushort[]{50,52})
        {
            var sitting=game.GetExcelSheet<Emote>()!.GetRow(id);
            Console.WriteLine($"CN sitting #{id}: mode={sitting.EmoteMode.Value.ConditionMode}/{sitting.EmoteMode.RowId}, loop={sitting.ActionTimeline[0].RowId}:{sitting.ActionTimeline[0].Value.Key.ExtractText()}");
        }
        foreach(ushort sit in new ushort[]{50,52})
            foreach(bool previousTrial in new[]{false,true})
                foreach(bool sittingTrial in new[]{false,true})
                    foreach(bool stillSitting in new[]{false,true})
                    {
                        using var f=new ServiceFixture(game);
                        if(previousTrial)f.Service.RecordPreview(21);else f.Service.RecordNative(21);
                        var row=game.GetExcelSheet<Emote>()!.GetRow(sit);
                        if(sittingTrial)
                        {
                            f.Service.RecordPreview(sit,2);
                            if(stillSitting)f.ActivePreview=(sit,2);
                        }
                        else
                        {
                            f.Service.RecordNative(sit);
                            if(stillSitting)f.SetNativeSitting(sit,row.EmoteMode.Value.ConditionMode);
                        }
                        f.Update();
                        if(sittingTrial || !stillSitting)f.SetInheritedOldAction();
                        var authoritative=new ReadOnlySpan<byte>(f.Native.Source,sizeof(FFXIVClientStructs.FFXIV.Client.Game.Character.Character)).ToArray();
                        f.EnterAndUpdate();
                        var label=$"{(previousTrial?"trial":"native")} ordinary → {(sittingTrial?"trial":"native")} sit#{sit} → {(stillSitting?"seated":"standing")} GPose";
                        if(sittingTrial && stillSitting)
                            check(f.Service.CurrentEmoteId==0 && f.Service.ReplayCount==1 && f.Service.LastAppliedEmoteId==sit
                                && f.Native.EmoteId==sit && f.Native.Clone->Timeline.BaseOverride==row.ActionTimeline[0].RowId
                                && f.Native.Clone->Mode==CharacterModes.AnimLock
                                && f.Native.Clone->ModeParam==0 && f.Native.Clone->EmoteController.CPoseState==2
                                && f.Native.Clone->EmoteController.CurrentPoseType==(sit==52?EmoteController.PoseType.GroundSit:EmoteController.PoseType.Sit),
                                label+" pins active trial sitting as the persistent photo default after preview cleanup");
                        else if(stillSitting)
                            check(f.Service.CurrentEmoteId==0 && f.Service.ReplayCount==0 && f.Native.Calls==0
                                && f.Native.Clone->EmoteController.EmoteId==sit
                                && f.Native.Clone->Mode==(CharacterModes)row.EmoteMode.Value.ConditionMode,
                                label+" leaves the native inherited seated pose untouched");
                        else
                            check(f.Service.CurrentEmoteId==0 && f.Service.ReplayCount==1 && f.Native.Calls==0
                                && f.Native.Clone->Timeline.BaseOverride==3 && f.Native.Clone->EmoteController.EmoteId==0
                                && f.Native.Clone->Mode==CharacterModes.AnimLock && f.Native.Clone->Timeline.TimelineSequencer.TimelineIds[0]==3,
                                label+" pins idle so a prior native photo action cannot resume after the reset");
                        check(new ReadOnlySpan<byte>(f.Native.Source,sizeof(FFXIVClientStructs.FFXIV.Client.Game.Character.Character)).SequenceEqual(authoritative),
                            label+" leaves every authoritative source byte unchanged");
                    }
        foreach(bool firstTrial in new[]{false,true})foreach(bool lastTrial in new[]{false,true})
        {
            using var f=new ServiceFixture(game);
            if(firstTrial)f.Service.RecordPreview(21);else f.Service.RecordNative(21);
            if(lastTrial)f.Service.RecordPreview(1);else f.Service.RecordNative(1);
            f.Update();f.EnterAndUpdate();
            check(f.Service.CurrentEmoteId==1 && f.Service.ReplayCount==1 && f.Native.EmoteId==1
                && f.Service.LastAppliedEmoteId==1,
                $"{(firstTrial?"trial":"native")} ordinary → {(lastTrial?"trial":"native")} ordinary uses the latest ordinary action");
        }
        foreach(ushort sitting in new ushort[]{50,52})foreach(bool latestTrial in new[]{false,true})
        {
            using var f=new ServiceFixture(game);
            f.Service.RecordNative(21);f.Service.RecordNative(sitting);
            f.SetNativeSitting(sitting,game.GetExcelSheet<Emote>()!.GetRow(sitting).EmoteMode.Value.ConditionMode);
            if(sitting==50)f.Native.Source->Mode=CharacterModes.EmoteLoop;
            f.Update();
            if(latestTrial){f.Service.RecordPreview(1);f.ActivePreview=(1,0);}else f.Service.RecordNative(1);
            f.Native.Source->EmoteController.EmoteId=1;
            f.Update();
            check(f.Service.CurrentEmoteId==1,
                $"sitting #{sitting} observation cannot erase later {(latestTrial?"trial":"native")} ordinary action on following world frames");
            f.EnterAndUpdate();
            check(f.Service.ReplayCount==1 && f.Service.LastAppliedEmoteId==1 && f.Native.EmoteId==1,
                $"sitting #{sitting} entry preserves later {(latestTrial?"trial":"native")} ordinary action instead of choosing old posture");
        }
        using(var f=new ServiceFixture(game))
        {
            f.Service.RecordPreview(21);
            var row=game.GetExcelSheet<Emote>()!.GetRow(52);
            f.SetNativeSitting(52,row.EmoteMode.Value.ConditionMode);
            // No RecordNative callback: covers direct builtin posture transitions.
            f.Update();f.Native.Source->Mode=CharacterModes.Normal;f.Native.Source->EmoteController.CurrentPoseType=EmoteController.PoseType.Idle;
            f.Native.Source->EmoteController.EmoteId=0;f.SetInheritedOldAction();
            f.EnterAndUpdate();
            check(f.Service.CurrentEmoteId==0 && f.Service.ReplayCount==1 && f.Native.Calls==0
                && f.Native.Clone->EmoteController.EmoteId==0 && f.Native.Clone->Mode==CharacterModes.AnimLock
                && f.Native.Clone->Timeline.BaseOverride==3 && f.Native.Clone->Timeline.TimelineSequencer.TimelineIds[0]==3,
                "observed builtin sitting without an emote callback clears old memory before standing GPose");
        }
        using(var f=new ServiceFixture(game))
        {
            f.Service.RecordNative(21);f.Service.RecordPreview(52);f.SetInheritedOldAction();
            f.Native.AfterMode=()=>{(*(uint*)((byte*)f.Native.Clone+0x74))++;};
            f.EnterAndUpdate();
            check(f.Service.ReplayCount==0 && f.Native.Clone->EmoteController.EmoteId==21
                && f.Native.Clone->Timeline.BaseOverride==712 && f.Native.Clone->Timeline.TimelineSequencer.TimelineIds[0]==712,
                "idle default cannot clear scalar or timeline state after allocation reuse in native mode callback");
        }
        using(var f=new ServiceFixture(game))
        {
            f.Service.RecordNative(21);f.Service.RecordPreview(52);f.ActivePreview=(52,0);f.SetInheritedOldAction();f.Native.Accept=false;
            f.EnterAndUpdate();
            check(f.Service.ReplayCount==0 && f.Native.ModeCalls==0 && f.Native.Clone->Timeline.BaseOverride==712
                && f.Native.Clone->Mode==CharacterModes.EmoteLoop,
                "native rejection of an active trial sit cannot write its loop or seated mode");
        }
        using(var f=new ServiceFixture(game))
        {
            f.Service.RecordNative(21);f.Service.RecordPreview(52);f.SetInheritedOldAction();f.EnterAndUpdate();
            f.Native.Clone->Mode=CharacterModes.AnimLock;f.Native.Clone->EmoteController.EmoteId=1;
            f.Native.Clone->Timeline.BaseOverride=999;f.Native.Clone->Timeline.TimelineSequencer.TimelineIds[0]=999;f.Update();
            check(f.Service.ReplayCount==1 && f.Native.Clone->Timeline.BaseOverride==999 && f.Native.Clone->EmoteController.EmoteId==1
                && f.Native.Clone->Mode==CharacterModes.AnimLock,
                "a successful idle default runs once and preserves later posing-tool changes");
        }
        foreach(ushort sit in new ushort[]{50,52})
        {
            using var f=new ServiceFixture(game);
            f.Service.RecordNative(21);f.Service.RecordPreview(sit,2);f.ActivePreview=(sit,2);
            f.SetInheritedOldAction();f.EnterAndUpdate();
            int nativeCalls=f.Native.Calls,modeCalls=f.Native.ModeCalls,timelineCalls=f.Native.TimelineCalls;
            f.Native.Clone->Mode=CharacterModes.Normal;f.Native.Clone->EmoteController.EmoteId=1;
            f.Native.Clone->Timeline.BaseOverride=999;f.Native.Clone->Timeline.TimelineSequencer.TimelineIds[0]=999;f.Update();
            check(f.Service.ReplayCount==1 && f.Native.Calls==nativeCalls && f.Native.ModeCalls==modeCalls
                && f.Native.TimelineCalls==timelineCalls && f.Native.Clone->Mode==CharacterModes.Normal
                && f.Native.Clone->Timeline.BaseOverride==999 && f.Native.Clone->EmoteController.EmoteId==1,
                $"trial sitting #{sit} applies once and preserves subsequent posing-tool mode and timeline edits");
        }
    }

    private sealed class ServiceFixture:IDisposable
    {
        public readonly NativeGposeHarness.Fixture Native=new();
        private readonly PlayerState* player=(PlayerState*)NativeMemory.AllocZeroed((nuint)sizeof(PlayerState));
        private readonly nint previousPlayer=PlayerState.Addresses.Instance.Value;
        public (ushort EmoteId,byte Pose)? ActivePreview;
        private bool posing;
        private readonly IFramework framework;
        public readonly GposeEmoteService Service;
        public ServiceFixture(Lumina.GameData game)
        {
            PlayerState.Addresses.Instance.Value=(nint)player;player->ContentId=100;
            Native.Source->EmoteController.OwnerObject=(FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)Native.Source;
            framework=FixtureProxy.Make<IFramework>((m,_)=>m.Name=="get_IsInFrameworkUpdateThread"?true:null);
            var client=FixtureProxy.Make<IClientState>((m,_)=>m.Name switch{"get_IsLoggedIn"=>true,"get_IsGPosing"=>posing,_=>null});
            var source=FixtureProxy.Make<IPlayerCharacter>((m,_)=>m.Name=="get_Address"?(nint)Native.Source:null);
            var clone=FixtureProxy.Make<IGameObject>((m,_)=>m.Name switch{"get_Address"=>(nint)Native.Clone,"get_ObjectIndex"=>Native.Clone->ObjectIndex,_=>null});
            var objects=FixtureProxy.Make<IObjectTable>((m,_)=>m.Name switch
            {
                "get_LocalPlayer"=>source,
                "GetEnumerator"=>((IEnumerable<IGameObject>)new[]{clone}).GetEnumerator(),
                _=>null,
            });
            Service=new(framework,client,objects,new GameDataFixture(game),null!,FixtureProxy.Make<IPluginLog>((_,_)=>null),()=>{},()=>true);
            typeof(GposeEmoteService).GetField("installFailed",Private)!.SetValue(Service,true);
            typeof(GposeEmoteService).GetField("activePreviewEmote",Private)?.SetValue(Service,
                (Func<(ushort EmoteId,byte Pose)?>)(()=>ActivePreview));
        }
        public void SetNativeSitting(ushort id,byte mode)
        {
            Native.Source->Mode=(CharacterModes)mode;Native.Source->EmoteController.EmoteId=id;
            Native.Source->EmoteController.CurrentPoseType=id==52?EmoteController.PoseType.GroundSit:EmoteController.PoseType.Sit;
            Native.Clone->Mode=(CharacterModes)mode;Native.Clone->EmoteController.EmoteId=id;
        }
        public void SetInheritedOldAction()
        {
            Native.Clone->Mode=CharacterModes.EmoteLoop;Native.Clone->EmoteController.EmoteId=21;
            Native.Clone->Timeline.BaseOverride=712;Native.Clone->Timeline.TimelineSequencer.TimelineIds[0]=712;
        }
        public void Update()=>typeof(GposeEmoteService).GetMethod("Update",Private)!.Invoke(Service,[framework]);
        public void EnterAndUpdate()
        {
            var memory=(GposeEmoteMemory)typeof(GposeEmoteService).GetField("memory",Private)!.GetValue(Service)!;
            var capture=typeof(GposeEmoteService).GetMethod("BeginReplayEntry",Private);
            long entry=capture is null?memory.BeginEntry(100):(long)capture.Invoke(Service,null)!;
            ActivePreview=null; // Actual prepareEntry removes the display after capture.
            memory.CompleteEntry(entry,true);
            typeof(GposeEmoteService).GetField("remainingFrames",Private)!.SetValue(Service,180);
            posing=true;Update();
        }
        public void Dispose()
        {
            Service.Dispose();PlayerState.Addresses.Instance.Value=previousPlayer;NativeMemory.Free(player);Native.Dispose();
        }
    }
}
