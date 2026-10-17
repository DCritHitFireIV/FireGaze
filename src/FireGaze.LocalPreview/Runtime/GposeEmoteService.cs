using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using NativePlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;
using Lumina.Excel.Sheets;
using Action = System.Action;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>
/// Supplies one local animation default to the player's native GPose copy. The real
/// character and its persistent emote history are never edited. The entry hook uses the
/// current ClientStructs UIModule virtual-table ABI, as reviewed against local Brio.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed unsafe class GposeEmoteService : IDisposable
{
    private const int ReadyFrames = 180;
    // The same currently reviewed client allocation identity used by NativePreviewActor.
    private const int GenerationOffset = 0x74;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly IDataManager data;
    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;
    private readonly Action prepareEntry;
    private readonly Func<bool> isEnabled;
    private readonly Func<nint, bool>? isPreviewActor;
    private readonly Func<(ushort EmoteId,byte Pose)?>? activePreviewEmote;
    private readonly GposeEmoteMemory memory = new();
    private Hook<UIModule.Delegates.EnterGPose>? enterGpose;
    private bool installFailed;
    private bool entering;
    private bool wasInGpose;
    private bool waitingForGpose;
    private bool disposed;
    private int remainingFrames;
    private ulong observedPostureCharacter;
    private nint observedPostureSource;
    private uint observedPostureGeneration;
    private bool observedNativeSitting;

    public bool Installed => !disposed && enterGpose is { IsEnabled: true };
    public ushort CurrentEmoteId => memory.EmoteId;
    private ulong CharacterId => NativePlayerState.Addresses.Instance.Value == 0 ? 0
        : ((NativePlayerState*)NativePlayerState.Addresses.Instance.Value)->ContentId;
    public int ReplayCount { get; private set; }
    public ushort LastAppliedEmoteId { get; private set; }
    public string Detail { get; private set; } = "等待 Framework 初始化拍照动作记忆。";

    public GposeEmoteService(IFramework framework, IClientState clientState, IObjectTable objects,
        IDataManager data, IGameInteropProvider interop, IPluginLog log, Action prepareEntry,
        Func<bool> isEnabled, Func<nint, bool>? isPreviewActor = null,
        Func<(ushort EmoteId,byte Pose)?>? activePreviewEmote = null)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.objects = objects;
        this.data = data;
        this.interop = interop;
        this.log = log;
        this.prepareEntry = prepareEntry;
        this.isEnabled = isEnabled;
        this.isPreviewActor = isPreviewActor;
        this.activePreviewEmote=activePreviewEmote;
        framework.Update += Update;
    }

    /// <summary>Wire to actual accepted local emotes, never a query or attempted command.</summary>
    public void RecordNative(ushort id) => Record(id, 0);

    /// <summary>Wire after the local display's timeline actually starts, including deferred starts.</summary>
    public void RecordPreview(ushort id, byte pose = 0) => Record(id, pose);

    private void Record(ushort id, byte pose)
    {
        if (disposed || !framework.IsInFrameworkUpdateThread || entering || clientState.IsGPosing
            || !clientState.IsLoggedIn || !isEnabled() || id == 0) return;
        if (data.GetExcelSheet<Emote>().GetRowOrDefault(id) is not { } row)return;
        // CN EmoteMode 1/2 are ground/chair sitting, including their hidden cpose
        // variants. Named actions such as sit-ups have other modes and remain eligible.
        bool sitting=row.EmoteMode.RowId is 1 or 2;
        if(!sitting && row.ActionTimeline[0].RowId==0)return;
        // Observe a newly entered native posture before recording a newer explicit
        // ordinary action; a delayed world frame must not erase that newer action.
        if(!sitting)ObserveNativeSitting();
        memory.Record(CharacterId, id, pose, true, sitting);
    }

    private void Update(IFramework _)
    {
        if (disposed || !framework.IsInFrameworkUpdateThread) return;
        memory.ChangeCharacter(clientState.IsLoggedIn ? CharacterId : 0);
        InstallEntryHook();
        bool gposing = clientState.IsGPosing;
        if (!isEnabled() || !clientState.IsLoggedIn)
        {
            memory.EndEntry(); waitingForGpose = false; wasInGpose = gposing;
            return;
        }
        if (!gposing)
        {
            if(!waitingForGpose && !entering)ObserveNativeSitting();
            if (wasInGpose || waitingForGpose && --remainingFrames <= 0)
            {
                memory.EndEntry(); waitingForGpose = false;
            }
            wasInGpose = false;
            return;
        }
        wasInGpose = true;
        waitingForGpose = false;
        if (!memory.HasPendingReplay) return;
        if (--remainingFrames <= 0)
        {
            memory.EndEntry();
            Detail = "拍照角色未在等待时间内就绪；保留游戏原有动作。";
            return;
        }
        try
        {
            var source = objects.LocalPlayer is { } local ? (NativeCharacter*)local.Address : null;
            var clone = ResolveOwnGposeClone(source);
            if (clone == null) return;
            ulong owner = CharacterId;
            ushort replayed=0;
            bool BindingCurrent()=>!disposed && framework.IsInFrameworkUpdateThread && isEnabled()
                    && clientState.IsGPosing && CharacterId == owner
                    && objects.LocalPlayer?.Address == (nint)source && ResolveOwnGposeClone(source)==clone;
            if (memory.TryReplay(owner, (id, pose) =>
            {
                replayed=id;
                if(id==0)return TryApplyIdleDefault(clone,source,BindingCurrent);
                if(data.GetExcelSheet<Emote>().GetRowOrDefault(id) is {} row && row.EmoteMode.RowId is 1 or 2)
                    return TryApplySittingDefault(clone,source,id,pose,(ushort)row.ActionTimeline[0].RowId,
                        (CharacterModes)row.EmoteMode.Value.ConditionMode,(byte)row.EmoteMode.RowId,BindingCurrent);
                return TryApplyEmoteDefault(clone,source,id,pose,ResolveLoopTimeline(clone,id),BindingCurrent);
            }))
            {
                ReplayCount++;
                LastAppliedEmoteId = replayed;
                Detail = replayed==0?"坐姿已结束；本人的拍照副本使用无动作默认。":"已将当前动作应用到本人的拍照副本；后续姿态编辑保留。";
            }
        }
        catch (Exception ex)
        {
            memory.EndEntry();
            Detail = "拍照动作记忆未能应用；保留游戏原有动作。";
            log.Error(ex, "[LocalPreview] GPose emote replay failed.");
        }
    }

    private bool IsNativeSitting(NativeCharacter* actor)
    {
        if(actor==null || actor->EmoteController.OwnerObject!=(BattleChara*)actor
            || actor->Mode is not (CharacterModes.EmoteLoop or CharacterModes.InPositionLoop))return false;
        return actor->EmoteController.CurrentPoseType is EmoteController.PoseType.Sit or EmoteController.PoseType.GroundSit
            || data.GetExcelSheet<Emote>().GetRowOrDefault(actor->EmoteController.EmoteId) is {} row && row.EmoteMode.RowId is 1 or 2;
    }

    private void ObserveNativeSitting()
    {
        var source=objects.LocalPlayer is {} local?(NativeCharacter*)local.Address:null;
        ulong owner=CharacterId;
        uint generation=source==null?0:Generation(source);
        bool sitting=IsNativeSitting(source);
        bool same=observedPostureCharacter==owner && observedPostureSource==(nint)source
            && observedPostureGeneration==generation;
        if(sitting && (!same || !observedNativeSitting))memory.ResetPosture(owner);
        observedPostureCharacter=owner;observedPostureSource=(nint)source;
        observedPostureGeneration=generation;observedNativeSitting=sitting;
    }

    private long BeginReplayEntry()
    {
        ulong owner=CharacterId;
        ObserveNativeSitting();
        if(activePreviewEmote?.Invoke() is {} active
            && data.GetExcelSheet<Emote>().GetRowOrDefault(active.EmoteId) is {} row && row.EmoteMode.RowId is 1 or 2)
        {
            memory.ResetPosture(owner);
            return memory.BeginPostureEntry(owner,active.EmoteId,active.Pose);
        }
        var source=objects.LocalPlayer is {} local?(NativeCharacter*)local.Address:null;
        return memory.EmoteId==0 && IsNativeSitting(source)?memory.BeginNativePostureEntry(owner):memory.BeginEntry(owner);
    }

    private void InstallEntryHook()
    {
        if (enterGpose != null || installFailed) return;
        try
        {
            var module = UIModule.Instance();
            if (module == null || module->VirtualTable == null) return;
            nint address = (nint)module->VirtualTable->EnterGPose;
            if (address == 0) return;
            enterGpose = interop.HookFromAddress<UIModule.Delegates.EnterGPose>(address, EnterGposeDetour);
            enterGpose.Enable();
            Detail = "拍照动作记忆已就绪；仅向本人的拍照副本应用。";
        }
        catch (Exception ex)
        {
            enterGpose?.Dispose(); enterGpose = null; installFailed = true;
            Detail = "当前客户端拍照入口不可用，已停用拍照动作记忆。";
            log.Error(ex, "[LocalPreview] GPose entry hook unavailable.");
        }
    }

    private bool EnterGposeDetour(UIModule* module)
    {
        // Nested calls, disabled features and non-framework callers retain the native path.
        if (disposed || entering || !framework.IsInFrameworkUpdateThread || !isEnabled()
            || !clientState.IsLoggedIn || clientState.IsGPosing) return enterGpose!.Original(module);
        entering = true;
        long entry = 0;
        bool prepared = true;
        try
        {
            entry=BeginReplayEntry();
            // Release the local client-actor slot before GPose allocates its own actors.
            try { prepareEntry(); }
            catch (Exception ex)
            {
                prepared = false;
                log.Error(ex, "[LocalPreview] Could not finish preview cleanup before GPose.");
            }
            bool entered = enterGpose!.Original(module);
            memory.CompleteEntry(entry, entered && prepared);
            waitingForGpose = entered && prepared;
            remainingFrames = ReadyFrames;
            return entered;
        }
        catch
        {
            memory.CompleteEntry(entry, false);
            throw;
        }
        finally { entering = false; }
    }

    private NativeCharacter* ResolveOwnGposeClone(NativeCharacter* source)
    {
        if (source == null || source->ObjectIndex >= 200 || source->ObjectKind != ObjectKind.Pc) return null;
        NativeCharacter* match = null;
        foreach (var obj in objects)
        {
            if (obj.ObjectIndex is < 201 or > 439 || obj.Address == (nint)source
                || isPreviewActor?.Invoke(obj.Address) == true) continue;
            var actor = (NativeCharacter*)obj.Address;
            if (!MatchesIdentity(actor, source)) continue;
            // Do not guess if another posing tool created a duplicate identity.
            if (match != null) return null;
            match = actor;
        }
        return match;
    }

    private static bool MatchesIdentity(NativeCharacter* clone, NativeCharacter* source)
        => clone != null && source != null && clone != source
            && clone->ObjectIndex is >= 201 and <= 439 && source->ObjectIndex < 200
            && clone->ObjectKind == ObjectKind.Pc && source->ObjectKind == ObjectKind.Pc
            && source->HomeWorld is > 0 and < ushort.MaxValue
            && (clone->HomeWorld == ushort.MaxValue || clone->HomeWorld == source->HomeWorld)
            && source->Name[0] != 0 && string.Equals(clone->NameString, source->NameString, StringComparison.Ordinal)
            && HasNativeSourceMapping(clone, source);

    private static bool HasNativeSourceMapping(NativeCharacter* clone, NativeCharacter* source)
    {
        // GPose copies do not preserve HomeWorld (the current CN client writes FFFF).
        // The controller's native primary-player pointer supplies ownership instead.
        // Creation at B09722/B09732 and AddCharacterToGPose at B12953/B12963
        // compare the original object id with Control.LocalPlayerEntityId and write
        // its new copy to +3508. The ordinary 64 source records can remain empty for
        // the main player, so neither those records nor a name lookup establish ownership.
        // Only raw reads are used here; no native virtual call or new hook is required.
        if (source->EntityId is 0 or 0xE0000000 || EventFramework.Addresses.Instance.Value == 0
            || Control.Addresses.Instance.Value == 0 || Control.Addresses.GetLocalPlayer.Value == 0) return false;
        var control = (Control*)Control.Addresses.Instance.Value;
        if (control->LocalPlayerEntityId != source->EntityId || control->LocalPlayer != (BattleChara*)source
            || *(BattleChara**)Control.Addresses.GetLocalPlayer.Value != (BattleChara*)source) return false;
        var events = *(EventFramework**)EventFramework.Addresses.Instance.Value;
        if (events == null) return false;
        return *(NativeCharacter**)((byte*)&events->EventSceneModule.EventGPoseController + 0x3508) == clone;
    }

    private static uint Generation(NativeCharacter* actor) => *(uint*)((byte*)actor + GenerationOffset);

    private ushort ResolveLoopTimeline(NativeCharacter* clone,ushort id)
    {
        if(clone==null || data.GetExcelSheet<Emote>().GetRowOrDefault(id) is not {} row)return 0;
        bool mounted=clone->Mount.MountId!=0;
        if(mounted && clone->Mount.OwnerObject!=clone)return 0;
        // Match the variant chosen by native PlayEmote on this photo copy. A
        // ground-sit reset may remove its mount while the real source stays mounted.
        var timeline=row.ActionTimeline[mounted?4:0];
        return timeline.RowId is >0 and <=ushort.MaxValue && timeline.IsValid?(ushort)timeline.RowId:(ushort)0;
    }

    private static bool NativePhotoReady()
    {
        if (EventFramework.Addresses.Instance.Value == 0) return false;
        var events = *(EventFramework**)EventFramework.Addresses.Instance.Value;
        // CN native entry advances through 1/2/3/4/6; state 7 is reached after
        // initialization and is the only phase running the GPose animation update.
        return events != null
            && *(int*)((byte*)&events->EventSceneModule.EventGPoseController + 0x3520) == 7;
    }

    private static bool TryApplyIdleDefault(NativeCharacter* clone,NativeCharacter* source,Func<bool> bindingCurrent)
    {
        if(NativeCharacter.Addresses.SetMode.Value==0 || ActionTimelineSequencer.Addresses.PlayTimeline.Value==0
            || !NativePhotoReady() || !MatchesIdentity(clone,source))return false;
        uint generation=Generation(clone),sourceGeneration=Generation(source);
        var draw=clone->DrawObject;
        bool Current()=>bindingCurrent() && NativePhotoReady() && Generation(clone)==generation
            && Generation(source)==sourceGeneration && clone->DrawObject==draw && draw!=null && draw->IsVisible
            && MatchesIdentity(clone,source) && clone->EmoteController.OwnerObject==(BattleChara*)clone
            && clone->Timeline.OwnerObject==clone && clone->Timeline.TimelineSequencer.Parent==clone;
        if(!Current())return false;
        // Normal allows the photo controller to resume its old native history on
        // a later frame. Pin idle once through the same local animation mode used
        // for ordinary defaults; later posing-tool edits still take precedence.
        clone->SetMode(CharacterModes.AnimLock,0);
        if(!Current())return false;
        clone->EmoteController.EmoteId=0;
        clone->EmoteController.CurrentPoseType=EmoteController.PoseType.Idle;
        clone->EmoteController.CPoseState=0;
        clone->Timeline.BaseOverride=3;
        clone->Timeline.TimelineSequencer.PlayTimeline(3);
        return Current();
    }

    private static bool TryApplySittingDefault(NativeCharacter* clone,NativeCharacter* source,ushort id,byte pose,
        ushort loopTimeline,CharacterModes mode,byte modeParam,Func<bool> bindingCurrent)
    {
        if(loopTimeline==0 || mode is not (CharacterModes.EmoteLoop or CharacterModes.InPositionLoop)
            || modeParam is not (1 or 2) || NativeCharacter.Addresses.SetMode.Value==0
            || ActionTimelineSequencer.Addresses.PlayTimeline.Value==0 || !NativePhotoReady() || !MatchesIdentity(clone,source))return false;
        uint generation=Generation(clone),sourceGeneration=Generation(source);
        var draw=clone->DrawObject;
        bool Current()=>bindingCurrent() && NativePhotoReady() && Generation(clone)==generation
            && Generation(source)==sourceGeneration && clone->DrawObject==draw && draw!=null && draw->IsVisible
            && MatchesIdentity(clone,source) && clone->EmoteController.OwnerObject==(BattleChara*)clone
            && clone->Timeline.OwnerObject==clone && clone->Timeline.TimelineSequencer.Parent==clone;
        if(!TryPlayOnGposeClone(clone,source,id,pose,Current) || !Current())return false;
        // A trial posture is absent from native photo history. Its ordinary
        // seated mode lets that history resume an older action, so pin this
        // posture's own seated loop once on the owned copy, like ordinary defaults.
        clone->SetMode(CharacterModes.AnimLock,0);
        if(!Current())return false;
        clone->EmoteController.CurrentPoseType=modeParam==1?EmoteController.PoseType.GroundSit:EmoteController.PoseType.Sit;
        clone->Timeline.BaseOverride=loopTimeline;
        clone->Timeline.TimelineSequencer.PlayTimeline(loopTimeline);
        return Current();
    }

    /// <summary>
    /// A one-shot PlayEmote does not enter native GPose's repeat history. Brio's local
    /// animation path uses AnimLock with BaseOverride instead: the native timeline
    /// updater selects the override when a clip finishes. This is installed once per
    /// entry on the owned photo copy. It does not rewrite timeline speed or reapply
    /// the override in later frames, leaving pause and posing-tool controls available.
    /// </summary>
    private static bool TryApplyEmoteDefault(NativeCharacter* clone, NativeCharacter* source,
        ushort id, byte pose, ushort loopTimeline, Func<bool>? bindingCurrent)
    {
        if (loopTimeline == 0 || NativeCharacter.Addresses.SetMode.Value == 0 || !NativePhotoReady()
            || !MatchesIdentity(clone, source)) return false;
        uint generation = Generation(clone), sourceGeneration = Generation(source);
        var draw = clone->DrawObject;
        bool Current() => bindingCurrent?.Invoke() != false && NativePhotoReady()
            && Generation(clone) == generation && Generation(source) == sourceGeneration
            && clone->DrawObject == draw && draw != null && draw->IsVisible
            && MatchesIdentity(clone, source) && clone->EmoteController.OwnerObject == (BattleChara*)clone
            && clone->Timeline.OwnerObject == clone && clone->Timeline.TimelineSequencer.Parent == clone;
        if (!TryPlayOnGposeClone(clone, source, id, pose, Current) || !Current()) return false;
        clone->SetMode(CharacterModes.AnimLock, 0);
        if (!Current()) return false;
        clone->Timeline.BaseOverride = loopTimeline;
        return true;
    }

    /// <summary>Framework only. Never accepts the live player or an unowned animation container.</summary>
    private static bool TryPlayOnGposeClone(NativeCharacter* clone, NativeCharacter* source,
        ushort id, byte pose, Func<bool>? bindingCurrent)
    {
        if (id == 0 || !MatchesIdentity(clone, source)
            || EmoteController.Addresses.PlayEmote.Value == 0
            || EmoteController.PlayEmoteOption.Addresses.StaticVirtualTable.Value == 0
            || clone->DrawObject == null || !clone->DrawObject->IsVisible
            || clone->EmoteController.OwnerObject != (BattleChara*)clone
            || clone->Timeline.OwnerObject != clone || clone->Timeline.TimelineSequencer.Parent != clone) return false;
        uint generation = Generation(clone);
        uint sourceGeneration = Generation(source);
        var draw = clone->DrawObject;
        if (bindingCurrent?.Invoke() == false || Generation(clone) != generation
            || Generation(source) != sourceGeneration || clone->DrawObject != draw
            || !MatchesIdentity(clone, source) || !draw->IsVisible
            || clone->EmoteController.OwnerObject != (BattleChara*)clone
            || clone->Timeline.OwnerObject != clone || clone->Timeline.TimelineSequencer.Parent != clone) return false;
        ushort oldOverride = clone->Timeline.BaseOverride;
        var option = new EmoteController.PlayEmoteOption
        {
            VirtualTable = EmoteController.PlayEmoteOption.StaticVirtualTablePointer,
            TargetId = clone->EmoteController.Target,
            DisableLogMessage = true,
        };
        clone->Timeline.BaseOverride = 0;
        bool accepted = clone->EmoteController.PlayEmote(id, &option);
        bool current = bindingCurrent?.Invoke() != false
            && Generation(clone) == generation && Generation(source) == sourceGeneration
            && clone->DrawObject == draw && MatchesIdentity(clone, source);
        if (!current) return false;
        if (!accepted) { clone->Timeline.BaseOverride = oldOverride; return false; }
        if (pose != 0) clone->EmoteController.CPoseState = pose;
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        framework.Update -= Update;
        enterGpose?.Dispose(); enterGpose = null;
        memory.ChangeCharacter(0);
    }
}
