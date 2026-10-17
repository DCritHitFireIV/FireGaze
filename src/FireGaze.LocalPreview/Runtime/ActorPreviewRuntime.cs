// Local experimental adapter. Native model-setup techniques were studied in HMapSync
// (Enceladeum/HMapSync, AGPL-3.0, StateApplyService). This file is not offered as an MIT
// relicensing of that work. No relay, packet filtering, map, movement or unlock hooks.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Action = System.Action;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Runtime;

public enum PreviewSlot { Mount, Minion, Accessory, Emote }
public readonly record struct PreviewEffect(PreviewSlot Slot, ushort Id, byte Pose = 0);
public sealed record RuntimeCapabilities(bool Mount, bool Minion, bool Accessory, bool Emote,
    bool InputBridge, bool EmoteObservation, string Reason);

/// <summary>
/// Local model and timeline adapter. Effects own only their touched slots; accepted native input
/// relinquishes all ownership. Never invokes an action manager to implement a preview.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed unsafe class ActorPreviewRuntime : IDisposable, IHmsShadowSource
{
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly ICondition conditions;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Dictionary<PreviewSlot, PreviewEffect> effects = new();
    private readonly List<IDisposable> installedHooks = new();
    private readonly NativeInputHandoff<NativeIntent> handoff = new();
    private readonly ImplicitMinionOwnership implicitMinion = new();
    private readonly Dictionary<PreviewSlot, int> spawnWait = new();
    // Audited CN companion executor (RVA 0x8E2810 in the current executable).
    // UseAction can return true after merely queuing; this leaf runs only at execution.
    // The signature is unique in the reviewed client and resolves no packet sender.
    private const string UseCompanionSignature = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 3D ?? ?? ?? ?? 8B DA 48 8B F1 48 85 FF";
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool UseCompanionNative(ActionManager* manager, uint id);
    private Hook<UseCompanionNative>? useCompanion;
    private Hook<ActionManager.Delegates.UseAction>? useAction;
    private Hook<ActionManager.Delegates.UseActionLocation>? useLocation;
    private Hook<ActionManager.Delegates.GetActionStatus>? actionStatus;
    private Hook<EmoteManager.Delegates.CanExecuteEmote>? canEmote;
    private Hook<EmoteManager.Delegates.ExecuteEmote>? executeEmote;
    private Hook<AgentEmote.Delegates.CanUseEmote>? canAgentEmote;
    private Hook<AgentEmote.Delegates.ExecuteEmote>? executeAgentEmote;
    private Hook<EmoteController.Delegates.PlayEmote>? playEmote;
    private Hook<MountContainer.Delegates.CreateAndSetupMount>? mountSetup;
    private ActorSnapshot? baseline;
    private HmsPresentationBaseline? hmsBaseline;
    private NativePreviewActor? visualActor;
    private bool deferredEmote;
    private nint actorAddress;
    private ulong actorId;
    private uint territory;
    private bool ownCall;
    private bool disposed;
    private int nativeDepth;
    private bool replacingPreview;
    private long revision;
    private ExternalBinding? externalBinding;
    private NativeIntent externalIntent;
    private long nextHmsEvidence;
    private readonly Dictionary<long, (ExternalBinding Binding, ushort EmoteId)> hmsEvidence = new();

    public event Action<ulong, ushort>? EmoteObserved;
    public event Action<ulong, ushort>? NativeEmoteEntered;
    public event Action<ulong, ushort>? NativeEmoteExited;
    public event Action? NativeInputAccepted;
    public event Action<ushort>? ExternalEmoteAccepted;
    public event Action<string>? PreviewCancelled;
    public (ulong OwnerId, ushort EmoteId)? CurrentNativeEmote { get; private set; }
    public bool HasActivePreview => effects.Count != 0;
    public IReadOnlyList<PreviewEffect> ActiveEffects => effects.Values.ToArray();
    public bool OwnsDisplayActor(nint address) => address!=0 && visualActor is not null && (nint)visualActor.Actor==address;
    public RuntimeCapabilities Capabilities { get; private set; } = new(false, false, false, false, false, false, "未初始化");

    public CaptureProjection GetCaptureProjection()
    {
        if (HasActivePreview && !BindingCurrent()) BeforeHmsLifecycle();
        // Presentation lives on a client-owned actor. The real actor is already authoritative.
        if (visualActor is not null) return new(1, false, 0, clientState.TerritoryType, revision, null);
        return !disposed && HasActivePreview && BindingCurrent() && baseline is { } snapshot
            ? new(1, true, actorId, territory, revision, snapshot.Presentation)
            : new(1, false, 0, clientState.TerritoryType, revision, null);
    }

    public bool TryGetHmsCaptureFrame(out HmsCaptureFrame frame)
    {
        frame = default;
        if (disposed || objects.LocalPlayer is not { } player || !clientState.IsLoggedIn) return false;
        if (!HasActivePreview || visualActor is not null)
        {
            frame = new(player.Address, player.GameObjectId, clientState.TerritoryType, revision, false, default, false, false);
            return true;
        }
        if (!BindingCurrent() || hmsBaseline is not { } snapshot) return false;
        var actor = Bound();
        bool ownedEmoteTimeline = effects.TryGetValue(PreviewSlot.Emote, out var emote)
            && actor->EmoteController.EmoteId == emote.Id && IsEmoteTimeline(actor, emote.Id);
        frame = new(actorAddress, actorId, territory, revision, true, snapshot,
            effects.ContainsKey(PreviewSlot.Mount), ownedEmoteTimeline);
        return true;
    }
    public bool IsHmsCaptureFrameCurrent(in HmsCaptureFrame frame)
        => !disposed && clientState.IsLoggedIn && objects.LocalPlayer is { } player
            && player.Address == frame.Address && player.GameObjectId == frame.ActorId
            && clientState.TerritoryType == frame.Territory && revision == frame.Revision
            && (HasActivePreview && visualActor is null) == frame.Project && (!frame.Project || BindingCurrent());

    /// <summary>Records actual HMS dispatch even when an outer native emote hook owns suspension.</summary>
    public long BeginHmsEmoteEvidence(ushort id)
    {
        if (disposed || ownCall || !HasActivePreview || !BindingCurrent() || id == 0
            || nextHmsEvidence == long.MaxValue || hmsEvidence.Count >= 64) return 0;
        long token = ++nextHmsEvidence;
        hmsEvidence.Add(token, (new(actorAddress, actorId, territory, revision), id));
        return token;
    }
    public void CompleteHmsEmoteEvidence(long token, bool accepted)
    {
        if (token == 0 || !hmsEvidence.Remove(token, out var evidence)) return;
        if (disposed || !BindingCurrent()
            || evidence.Binding != new ExternalBinding(actorAddress, actorId, territory, revision)) return;
        handoff.ReportDecision(new(PreviewSlot.Emote, evidence.EmoteId), accepted);
    }

    /// <summary>The external plugin invokes its operation exactly once between these two methods.</summary>
    public long BeginExternalPresentation(int kind, uint id)
    {
        if (disposed || ownCall || nativeDepth != 0 || !HasActivePreview || !BindingCurrent()
            || kind is < (int)PreviewSlot.Mount or > (int)PreviewSlot.Emote || id > ushort.MaxValue) return 0;
        var intent = new NativeIntent((PreviewSlot)kind, id);
        try
        {
            long token = handoff.BeginExternal(intent, () => new Suspension(this, projectMetadata: true));
            if (token == 0) return 0;
            externalBinding = new(actorAddress, actorId, territory, revision);
            externalIntent = intent;
            nativeDepth++;
            return token;
        }
        catch (Exception ex) { ReportHandoff(ex); return 0; }
    }

    public bool EndExternalPresentation(long token, bool accepted)
    {
        if (externalBinding is not { } binding) return false;
        bool current = !disposed && BindingCurrent() && binding == new ExternalBinding(actorAddress, actorId, territory, revision);
        var intent = externalIntent;
        ulong owner = binding.ActorId;
        try
        {
            bool completed = handoff.CompleteExternal(token, accepted, current);
            if (completed && accepted && intent.Slot == PreviewSlot.Emote && intent.Id is > 0 and <= ushort.MaxValue)
                Raise(EmoteObserved, owner, (ushort)intent.Id);
            return completed;
        }
        catch (Exception ex) { ReportHandoff(ex); return false; }
        finally
        {
            // An obsolete token must not release the protection of a newer transaction.
            if (!handoff.HasExternal) ReleaseExternalProtection();
        }
    }

    /// <summary>HMS owns the ensuing lifecycle cleanup; remove our visuals without replaying history.</summary>
    public void BeforeHmsLifecycle()
    {
        if (BindingCurrent()) ClearForReason(PreviewExitReason.Busy);
        else ClearAll(false);
    }

    private bool BindingCurrent() => clientState.IsLoggedIn && territory == clientState.TerritoryType && Bound() != null;
    private void ReleaseExternalProtection()
    {
        if (externalBinding == null) return;
        externalBinding = null;
        nativeDepth--;
    }
    private void InvalidateExternalPresentation()
    {
        handoff.InvalidateExternal();
        ReleaseExternalProtection();
    }
    private readonly record struct ExternalBinding(nint Address, ulong ActorId, uint Territory, long Revision);

    public ActorPreviewRuntime(IFramework framework, IClientState clientState, IObjectTable objects,
        ICondition conditions, IDataManager data, IGameInteropProvider interop, IPluginLog log)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.objects = objects;
        this.conditions = conditions;
        this.data = data;
        this.log = log;
        territory = clientState.TerritoryType;
        try
        {
            // Use generated pointers from the loaded CN ClientStructs, plus the audited
            // companion execution signature absent from its generated API.
            useAction = Install(interop.HookFromAddress<ActionManager.Delegates.UseAction>(ActionManager.Addresses.UseAction.Value, UseActionDetour));
            useCompanion = Install(interop.HookFromSignature<UseCompanionNative>(UseCompanionSignature, UseCompanionDetour));
            useLocation = Install(interop.HookFromAddress<ActionManager.Delegates.UseActionLocation>(ActionManager.Addresses.UseActionLocation.Value, UseLocationDetour));
            actionStatus = Install(interop.HookFromAddress<ActionManager.Delegates.GetActionStatus>(ActionManager.Addresses.GetActionStatus.Value, ActionStatusDetour));
            canEmote = Install(interop.HookFromAddress<EmoteManager.Delegates.CanExecuteEmote>(EmoteManager.Addresses.CanExecuteEmote.Value, CanEmoteDetour));
            executeEmote = Install(interop.HookFromAddress<EmoteManager.Delegates.ExecuteEmote>(EmoteManager.Addresses.ExecuteEmote.Value, ExecuteEmoteDetour));
            canAgentEmote = Install(interop.HookFromAddress<AgentEmote.Delegates.CanUseEmote>(AgentEmote.Addresses.CanUseEmote.Value, CanAgentEmoteDetour));
            executeAgentEmote = Install(interop.HookFromAddress<AgentEmote.Delegates.ExecuteEmote>(AgentEmote.Addresses.ExecuteEmote.Value, ExecuteAgentEmoteDetour));
            playEmote = Install(interop.HookFromAddress<EmoteController.Delegates.PlayEmote>(EmoteController.Addresses.PlayEmote.Value, PlayEmoteDetour));
            mountSetup = Install(interop.HookFromAddress<MountContainer.Delegates.CreateAndSetupMount>(MountContainer.Addresses.CreateAndSetupMount.Value, MountSetupDetour));
            bool mount = MountContainer.Addresses.CreateAndSetupMount.Value != 0;
            bool minion = CompanionContainer.Addresses.SetupCompanion.Value != 0;
            bool accessory = OrnamentContainer.Addresses.SetupOrnament.Value != 0;
            bool emote = ActionTimelineSequencer.Addresses.PlayTimeline.Value != 0;
            Capabilities = new(mount, minion, accessory, emote, true, true,
                "本地实验：模型显示与原生输入交接仍需当前客户端实机验收；不改变移动速度、跳跃、飞行或解锁状态。");
        }
        catch (Exception ex)
        {
            foreach (var hook in installedHooks.AsEnumerable().Reverse()) { try { hook.Dispose(); } catch { } }
            installedHooks.Clear();
            log.Error(ex, "[LocalPreview] Native hook setup failed; previews disabled.");
            Capabilities = new(false, false, false, false, false, false, "客户端接口不可用，已关闭本地试用。");
        }
        framework.Update += Update;
    }

    private T Install<T>(T hook) where T : class, IDisposable
    {
        installedHooks.Add(hook);
        // Hook<T> has no non-generic Enable interface; enable through overload resolution below.
        EnableHook(hook);
        return hook;
    }
    private static void EnableHook<T>(T hook) where T : class, IDisposable
    {
        switch (hook)
        {
            case Hook<UseCompanionNative> h: h.Enable(); break;
            case Hook<ActionManager.Delegates.UseAction> h: h.Enable(); break;
            case Hook<ActionManager.Delegates.UseActionLocation> h: h.Enable(); break;
            case Hook<ActionManager.Delegates.GetActionStatus> h: h.Enable(); break;
            case Hook<EmoteManager.Delegates.CanExecuteEmote> h: h.Enable(); break;
            case Hook<EmoteManager.Delegates.ExecuteEmote> h: h.Enable(); break;
            case Hook<AgentEmote.Delegates.CanUseEmote> h: h.Enable(); break;
            case Hook<AgentEmote.Delegates.ExecuteEmote> h: h.Enable(); break;
            case Hook<EmoteController.Delegates.PlayEmote> h: h.Enable(); break;
            case Hook<MountContainer.Delegates.CreateAndSetupMount> h: h.Enable(); break;
        }
    }

    public IReadOnlyList<PreviewSlot> GetConflicts(PreviewEffect effect)
    {
        var conflicts = new HashSet<PreviewSlot> { effect.Slot };
        if (effect.Slot == PreviewSlot.Mount) { conflicts.Add(PreviewSlot.Accessory); conflicts.Add(PreviewSlot.Minion); }
        if (effect.Slot == PreviewSlot.Mount && effects.TryGetValue(PreviewSlot.Emote, out var emote) && !HasEmoteVariant(emote.Id, 4)) conflicts.Add(PreviewSlot.Emote);
        if (effect.Slot == PreviewSlot.Accessory) conflicts.Add(PreviewSlot.Mount);
        if (effect.Slot == PreviewSlot.Minion) conflicts.Add(PreviewSlot.Mount);
        if (effect.Slot == PreviewSlot.Emote && effects.ContainsKey(PreviewSlot.Mount) && !HasEmoteVariant(effect.Id, 4)) conflicts.Add(PreviewSlot.Mount);
        return conflicts.Where(effects.ContainsKey).ToArray();
    }

    public bool TryApply(PreviewEffect effect, out string reason)
    {
        reason = "";
        if (disposed || !Capabilities.InputBridge) { reason = Capabilities.Reason; return false; }
        if (nativeDepth != 0) { reason = "原生操作正在执行，请稍后再试。"; return false; }
        if (!clientState.IsLoggedIn || IsTeleportCast()) { reason = "当前状态无法开始试用。"; return false; }
        if (conditions[ConditionFlag.Crafting] || conditions[ConditionFlag.PreparingToCraft]) { reason = "正在制作状态中，请稍后再试"; return false; }
        if (conditions[ConditionFlag.InCombat] || conditions[ConditionFlag.Unconscious]
            || conditions[ConditionFlag.LoggingOut] || conditions[ConditionFlag.WatchingCutscene]
            || conditions[ConditionFlag.OccupiedInCutSceneEvent]) { reason = "当前状态无法开始试用。"; return false; }
        var actor = Local();
        if (actor == null || conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51]) { reason = "当前无法试用。"; return false; }
        if (!Valid(effect)) { reason = "该对象没有可用的本地显示资源。"; return false; }
        if (!EnsureBaseline(actor)) { reason = "角色或地图刚发生变化，请重试。"; return false; }
        // Conflicts are removed only after the requested resource has passed validation.
        replacingPreview = true;
        try { foreach (var slot in GetConflicts(effect)) Clear(slot); }
        finally { replacingPreview = false; }
        if (!EnsureBaseline(actor)) { reason = "角色或地图刚发生变化，请重试。"; return false; }
        try
        {
            ownCall = true;
            if (visualActor is null)
            {
                visualActor = NativePreviewActor.Create(actor, log)
                    ?? throw new InvalidOperationException("无法创建本地展示角色");
                var display = visualActor.Actor;
                if (display == null) throw new InvalidOperationException("本地展示角色已失效");
                var snapshot = baseline!.Value;
                if (snapshot.MountId != 0) Apply(display, new(PreviewSlot.Mount, snapshot.MountId));
                if (snapshot.MinionId != 0) Apply(display, new(PreviewSlot.Minion, snapshot.MinionId));
                if (snapshot.AccessoryId != 0) Apply(display, new(PreviewSlot.Accessory, snapshot.AccessoryId));
                display->Mode = snapshot.Mode;
                display->ModeParam = snapshot.ModeParam;
                if (snapshot.Timeline != 0) display->Timeline.TimelineSequencer.PlayTimeline(snapshot.Timeline);
            }
            actor = visualActor.Actor;
            if (actor == null) throw new InvalidOperationException("本地展示角色已失效");
            Apply(actor, effect);
            effects[effect.Slot] = effect;
            spawnWait[effect.Slot] = 60;
            revision++;
            if (effect.Slot == PreviewSlot.Mount && effects.TryGetValue(PreviewSlot.Emote, out var heldEmote)) Apply(actor, heldEmote);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[LocalPreview] Applying local effect failed.");
            ClearAll(false);
            reason = "本地试用未能启动，已恢复原显示。";
            return false;
        }
        finally { ownCall = false; }
    }

    private bool Valid(PreviewEffect effect) => effect.Id is > 0 and <= (ushort)short.MaxValue && effect.Slot switch
    {
        PreviewSlot.Mount => Capabilities.Mount && data.GetExcelSheet<Mount>().HasRow(effect.Id),
        PreviewSlot.Minion => Capabilities.Minion && data.GetExcelSheet<Lumina.Excel.Sheets.Companion>().HasRow(effect.Id),
        PreviewSlot.Accessory => Capabilities.Accessory && data.GetExcelSheet<Lumina.Excel.Sheets.Ornament>().HasRow(effect.Id),
        PreviewSlot.Emote => Capabilities.Emote && data.GetExcelSheet<Emote>().GetRowOrDefault(effect.Id) is { } e && e.ActionTimeline[0].RowId != 0,
        _ => false,
    };

    private bool HasEmoteVariant(ushort id, int slot) => data.GetExcelSheet<Emote>().GetRowOrDefault(id) is { } row && row.ActionTimeline[slot].RowId != 0;
    private void Apply(NativeCharacter* actor, PreviewEffect effect)
    {
        switch (effect.Slot)
        {
            case PreviewSlot.Mount:
                actor->Mount.CreateAndSetupMount((short)effect.Id, 0, 0, 0, 0, 0, 0);
                actor->Mode = effect.Id == 0 ? CharacterModes.Normal : CharacterModes.Mounted;
                actor->ModeParam = 0;
                break;
            case PreviewSlot.Minion: actor->CompanionData.SetupCompanion((short)effect.Id, 0); break;
            case PreviewSlot.Accessory: actor->OrnamentData.SetupOrnament((short)effect.Id, 0); break;
            case PreviewSlot.Emote:
                // Initial native drawing can reset the animation lanes. Start the
                // selected action only after the display hierarchy is actually ready.
                if (visualActor is { IsReady: false }) { deferredEmote = true; break; }
                deferredEmote = false;
                var row = data.GetExcelSheet<Emote>().GetRow(effect.Id);
                bool displayMounted = effects.ContainsKey(PreviewSlot.Mount) || actor->Mount.MountId != 0;
                int postureSlot = displayMounted ? 4 : actor->Mode == CharacterModes.InPositionLoop ? actor->ModeParam == 1 ? 2 : 3 : 0;
                ushort timeline = (ushort)row.ActionTimeline[postureSlot].RowId;
                if (timeline == 0) timeline = (ushort)row.ActionTimeline[0].RowId;
                ushort intro = postureSlot == 0 ? (ushort)row.ActionTimeline[1].RowId : (ushort)0;
                actor->EmoteController.EmoteId = effect.Id;
                var mode = (CharacterModes)row.EmoteMode.Value.ConditionMode;
                if (mode != 0 && !displayMounted) actor->SetMode(mode, (byte)row.EmoteMode.RowId);
                actor->Timeline.BaseOverride = 0;
                actor->Timeline.TimelineSequencer.PlayTimeline(intro != 0 ? intro : timeline);
                // Pose is zero by default; callers must explicitly request a pose clone.
                if (effect.Pose != 0) actor->EmoteController.CPoseState = effect.Pose;
                break;
        }
    }

    public void Clear(PreviewSlot slot)
    {
        if (slot == PreviewSlot.Emote) deferredEmote = false;
        if (!effects.TryGetValue(slot, out var effect)) return;
        if (visualActor is not null)
        {
            InvalidateExternalPresentation();
            var display = visualActor.Actor;
            ownCall = true;
            try
            {
                if (display != null && baseline is { } snapshot)
                {
                    RestoreSlot(display, slot, snapshot, preserveCurrent: false);
                    if (slot == PreviewSlot.Mount && snapshot.MountId == 0)
                    { display->Mode = snapshot.Mode; display->ModeParam = snapshot.ModeParam; }
                }
            }
            finally
            {
                ownCall = false;
                effects.Remove(slot); spawnWait.Remove(slot); revision++;
                if (!HasActivePreview && !replacingPreview) Forget();
            }
            return;
        }
        InvalidateExternalPresentation();
        var actor = Bound();
        try
        {
            if (actor != null && baseline is { } snapshot)
            {
                ownCall = true;
                var decision = PreviewRestorePolicy.Decide(PreviewExitReason.Manual, AllowsPresentation(), OwnsMode(actor), BindingCurrent());
                if (decision.TouchActor)
                {
                    if (slot == PreviewSlot.Mount) implicitMinion.Observe(MinionId(actor), actor->CompanionData.CompanionId);
                    if (decision.RestoreBaseline) RestoreSlot(actor, slot, snapshot, preserveCurrent: true);
                    else RemoveOwnedSlot(actor, slot);
                }
            }
        }
        catch (Exception ex) { ReportHandoff(ex); }
        finally
        {
            ownCall = false;
            effects.Remove(slot);
            spawnWait.Remove(slot);
            revision++;
            if (!replacingPreview) RestoreImplicitMinion(actor);
            if (!HasActivePreview && !replacingPreview) Forget();
        }
    }

    public void ClearAll(bool restoreBaseline = true)
    {
        InvalidateExternalPresentation();
        if (visualActor is not null)
        {
            effects.Clear(); spawnWait.Clear(); Forget();
            return;
        }
        if (restoreBaseline) foreach (var slot in effects.Keys.ToArray()) Clear(slot);
        effects.Clear();
        spawnWait.Clear();
        Forget();
    }

    private void RestoreSlot(NativeCharacter* actor, PreviewSlot slot, ActorSnapshot snapshot, bool preserveCurrent)
    {
        switch (slot)
        {
            case PreviewSlot.Mount:
                if (!preserveCurrent || !effects.TryGetValue(slot, out var m) || actor->Mount.MountId == m.Id)
                    actor->Mount.CreateAndSetupMount((short)snapshot.MountId, 0, 0, 0, 0, 0, 0);
                break;
            case PreviewSlot.Minion:
                if (!preserveCurrent || !effects.TryGetValue(slot, out var c) || MinionId(actor) == c.Id || MinionId(actor) == 0)
                    actor->CompanionData.SetupCompanion((short)snapshot.MinionId, 0);
                break;
            case PreviewSlot.Accessory:
                if (!preserveCurrent || !effects.TryGetValue(slot, out var a) || actor->OrnamentData.OrnamentId == a.Id || actor->OrnamentData.OrnamentId == 0)
                    actor->OrnamentData.SetupOrnament((short)snapshot.AccessoryId, 0);
                break;
            case PreviewSlot.Emote:
                if (!preserveCurrent || !effects.TryGetValue(slot, out var e) || actor->EmoteController.EmoteId == e.Id)
                {
                    bool ownsMode = OwnsMode(actor);
                    actor->EmoteController.EmoteId = snapshot.EmoteId;
                    if (!preserveCurrent || ownsMode) actor->SetMode(snapshot.Mode, snapshot.ModeParam);
                    actor->Timeline.BaseOverride = snapshot.BaseOverride;
                    actor->EmoteController.CPoseState = snapshot.Pose;
                    actor->EmoteController.CurrentPoseType = snapshot.PoseType;
                    if (snapshot.Timeline != 0) actor->Timeline.TimelineSequencer.PlayTimeline(snapshot.Timeline);
                }
                break;
        }
    }

    private bool AllowsPresentation() => clientState.IsLoggedIn
        && !conditions[ConditionFlag.InCombat] && !conditions[ConditionFlag.Unconscious]
        && !conditions[ConditionFlag.Crafting] && !conditions[ConditionFlag.PreparingToCraft]
        && !conditions[ConditionFlag.LoggingOut] && !conditions[ConditionFlag.BetweenAreas]
        && !conditions[ConditionFlag.BetweenAreas51] && !conditions[ConditionFlag.WatchingCutscene]
        && !conditions[ConditionFlag.OccupiedInCutSceneEvent] && !IsTeleportCast();

    private bool OwnsMode(NativeCharacter* actor)
    {
        if (!effects.TryGetValue(PreviewSlot.Emote, out var effect) || actor->EmoteController.EmoteId != effect.Id) return false;
        if (data.GetExcelSheet<Emote>().GetRowOrDefault(effect.Id) is not { } row) return false;
        return actor->Mode == (CharacterModes)row.EmoteMode.Value.ConditionMode;
    }

    private void RemoveOwnedSlot(NativeCharacter* actor, PreviewSlot slot)
    {
        if (!effects.TryGetValue(slot, out var effect)) return;
        var currentMode = actor->Mode;
        var currentParam = actor->ModeParam;
        switch (slot)
        {
            case PreviewSlot.Mount when actor->Mount.MountId == effect.Id:
                actor->Mount.CreateAndSetupMount(0,0,0,0,0,0,0);
                break;
            case PreviewSlot.Minion when MinionId(actor) == effect.Id ||
                (actor->CompanionData.CompanionObject == null && spawnWait.ContainsKey(slot)
                    && (actor->CompanionData.CompanionId == 0 || actor->CompanionData.CompanionId == effect.Id)):
                actor->CompanionData.SetupCompanion(0,0);
                break;
            case PreviewSlot.Accessory when actor->OrnamentData.OrnamentId == effect.Id ||
                (actor->OrnamentData.OrnamentObject == null && spawnWait.ContainsKey(slot) && actor->OrnamentData.OrnamentId == 0):
                actor->OrnamentData.SetupOrnament(0,0);
                break;
            case PreviewSlot.Emote when actor->EmoteController.EmoteId == effect.Id:
                bool ownsMode = OwnsMode(actor);
                actor->EmoteController.EmoteId = 0;
                actor->Timeline.BaseOverride = 0;
                if (ownsMode && currentMode is CharacterModes.EmoteLoop or CharacterModes.InPositionLoop)
                {
                    actor->Mode = CharacterModes.Normal;
                    actor->ModeParam = 0;
                    return;
                }
                break;
        }
        // Container teardown must not turn a newly-entered crafting/death mode into idle.
        if (actor->Mode == CharacterModes.Normal && currentMode is not CharacterModes.Normal
            && currentMode is not CharacterModes.EmoteLoop && currentMode is not CharacterModes.InPositionLoop)
        {
            actor->Mode = currentMode;
            actor->ModeParam = currentParam;
        }
    }

    private void ClearForReason(PreviewExitReason reason)
    {
        if (visualActor is not null) { ClearAll(false); return; }
        var actor = Bound();
        var decision = PreviewRestorePolicy.Decide(reason, AllowsPresentation(), actor != null && OwnsMode(actor));
        if (decision.TouchActor && actor != null)
        {
            ownCall = true;
            try { foreach (var slot in effects.Keys.ToArray()) { try { RemoveOwnedSlot(actor,slot); } catch(Exception ex) { ReportHandoff(ex); } } }
            finally { ownCall = false; }
        }
        ClearAll(false);
    }

    private bool EnsureBaseline(NativeCharacter* actor)
    {
        if (baseline != null)
        {
            // A request can arrive before the next Framework.Update after an actor/map change.
            // Drop the old intent without touching either actor, and refuse this one request.
            if (Bound() != actor || territory != clientState.TerritoryType)
            {
                ClearAll(false);
                territory = clientState.TerritoryType;
                return false;
            }
            return true;
        }
        if (actor == null || Local() != actor) return false;
        territory = clientState.TerritoryType;
        actorAddress = (nint)actor;
        actorId = objects.LocalPlayer?.GameObjectId ?? 0;
        // Long-lived baselines contain values only. Child addresses are useful solely inside
        // a synchronous native suspension and are never retained across preview frames.
        baseline = ActorSnapshot.Read(actor, includeObjectAddresses: false);
        hmsBaseline = HmsPresentationBaseline.Read(actor);
        revision++;
        return true;
    }
    private void Forget()
    {
        deferredEmote = false;
        InvalidateExternalPresentation();
        var owned = visualActor; visualActor = null;
        try { owned?.Dispose(); } catch (Exception ex) { ReportHandoff(ex); }
        implicitMinion.Reset(); hmsEvidence.Clear(); baseline = null; hmsBaseline = null; actorAddress = 0; actorId = 0; revision++;
    }
    private NativeCharacter* Local() => objects.LocalPlayer is { } p ? (NativeCharacter*)p.Address : null;
    private NativeCharacter* Bound() => objects.LocalPlayer is { } p && p.Address == actorAddress && p.GameObjectId == actorId ? (NativeCharacter*)p.Address : null;
    private static ushort MinionId(NativeCharacter* actor) => actor->CompanionData.CompanionObject == null ? (ushort)0 : (ushort)actor->CompanionData.CompanionObject->BaseId;

    private void RestoreImplicitMinion(NativeCharacter* actor)
    {
        if (actor == null || !BindingCurrent() || actor != Bound()) return;
        ushort current = MinionId(actor);
        implicitMinion.Observe(current, actor->CompanionData.CompanionId);
        if (!implicitMinion.ShouldRestore(true, AllowsPresentation(), effects.ContainsKey(PreviewSlot.Mount),
                effects.ContainsKey(PreviewSlot.Minion), current, actor->CompanionData.CompanionId)) return;
        bool previousOwnCall = ownCall;
        ownCall = true;
        try
        {
            actor->CompanionData.SetupCompanion((short)implicitMinion.Id, 0);
            implicitMinion.Reset();
        }
        catch (Exception ex) { ReportHandoff(ex); }
        finally { ownCall = previousOwnCall; }
    }

    private void Update(IFramework _)
    {
        if (disposed) return;
        if (territory != clientState.TerritoryType)
        {
            territory = clientState.TerritoryType;
            ClearForReason(PreviewExitReason.ZoneChanged);
            Raise(PreviewCancelled,"地图已变化");
        }
        if (!HasActivePreview) return;
        if (Bound() == null) { ClearAll(false); Raise(PreviewCancelled, "角色已变化"); return; }
        if (conditions[ConditionFlag.InCombat] || conditions[ConditionFlag.Crafting] || conditions[ConditionFlag.PreparingToCraft]
            || conditions[ConditionFlag.Unconscious] || conditions[ConditionFlag.LoggingOut]
            || conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51]
            || conditions[ConditionFlag.WatchingCutscene] || conditions[ConditionFlag.OccupiedInCutSceneEvent]
            || IsTeleportCast()) { Cancel("当前状态已结束试用"); return; }
        if (handoff.HasExternal) return;
        var actor = visualActor?.Actor;
        if (actor == null || visualActor is null) { ClearAll(false); return; }
        if (!visualActor.Tick(Bound())) return;
        ApplyPendingEmote();
        if (visualActor is null || visualActor.Actor != actor || Bound() == null) return;
        foreach (var effect in effects.Values.ToArray())
        {
            if (spawnWait.TryGetValue(effect.Slot, out int frames) && frames > 0)
            {
                spawnWait[effect.Slot] = frames - 1;
                continue;
            }
            bool present = effect.Slot switch
            {
                PreviewSlot.Mount => actor->Mount.MountId == effect.Id && actor->Mount.MountObject != null,
                PreviewSlot.Minion => MinionId(actor) == effect.Id,
                PreviewSlot.Accessory => actor->OrnamentData.OrnamentId == effect.Id && actor->OrnamentData.OrnamentObject != null,
                _ => actor->EmoteController.EmoteId == effect.Id && IsEmoteTimeline(actor, effect.Id),
            };
            if (!present)
            {
                bool pendingChild = effect.Slot == PreviewSlot.Minion && actor->CompanionData.CompanionObject == null
                    || effect.Slot == PreviewSlot.Accessory && actor->OrnamentData.OrnamentObject == null
                    || effect.Slot == PreviewSlot.Mount && actor->Mount.MountObject == null;
                if (pendingChild || effect.Slot == PreviewSlot.Emote && actor->EmoteController.EmoteId == effect.Id) Clear(effect.Slot);
                else { effects.Remove(effect.Slot); spawnWait.Remove(effect.Slot); }
            }
        }
        if (!HasActivePreview) Forget();
        else ApplyGroundedMotion(actor);
    }
    private void ApplyGroundedMotion(NativeCharacter* actor)
    {
        if (visualActor is not { } display || display.Actor != actor) return;
        var expectedRevision = revision;
        bool emoteActive = PreviewEmoteActive(actor);
        // Native allocation lookups can synchronously start another action on the
        // same display actor. Recheck semantic ownership after the writer's last
        // native lookup; pointer/generation checks alone cannot detect that change.
        display.UpdateGroundedMotion(emoteActive, () => !disposed && revision == expectedRevision
            && visualActor == display && HasActivePreview && PreviewEmoteActive(actor) == emoteActive);
    }
    private bool PreviewEmoteActive(NativeCharacter* actor) => deferredEmote
        || effects.TryGetValue(PreviewSlot.Emote, out var effect) && IsEmoteTimeline(actor, effect.Id);
    private void ApplyPendingEmote()
    {
        if (!deferredEmote || visualActor is not { IsReady: true } display) return;
        deferredEmote = false;
        var actor = display.Actor;
        if (actor == null || display != visualActor || !effects.TryGetValue(PreviewSlot.Emote, out var effect)) return;
        var previousOwnCall = ownCall;
        ownCall = true;
        try { Apply(actor, effect); }
        finally { ownCall = previousOwnCall; }
    }
    private bool IsTeleportCast()
    {
        var mgr = ActionManager.Instance();
        if (mgr == null || mgr->CastTimeTotal <= 0 || mgr->CastActionType != ActionType.Action) return false;
        // 5 and 6 are the stable sheet identities of Teleport and Return, not packet opcodes.
        return mgr->CastActionId is 5 or 6;
    }
    private bool IsEmoteTimeline(NativeCharacter* actor, ushort emoteId)
    {
        if (data.GetExcelSheet<Emote>().GetRowOrDefault(emoteId) is not { } row) return false;
        // Mounted and facial actions occupy overlay lanes. The base lane can stay
        // at mounted idle for their whole lifetime; it cannot establish completion.
        foreach (var currentTimeline in actor->Timeline.TimelineSequencer.TimelineIds)
            foreach (var timeline in row.ActionTimeline)
                if (timeline.RowId != 0 && timeline.RowId == currentTimeline) return true;
        return false;
    }
    private void Cancel(string reason) { ClearForReason(PreviewExitReason.Busy); Raise(PreviewCancelled, reason); }

    private readonly record struct NativeIntent(PreviewSlot Slot, uint Id);
    private bool Relevant(ActionType type) => type is ActionType.Mount or ActionType.Companion or ActionType.Ornament;
    private static PreviewSlot Slot(ActionType type) => type switch { ActionType.Mount => PreviewSlot.Mount, ActionType.Companion => PreviewSlot.Minion, _ => PreviewSlot.Accessory };
    private bool Bypass => ownCall || !HasActivePreview || disposed || handoff.HasExternal;
    private IPreviewSuspension<NativeIntent> Suspend(NativeIntent _) => new Suspension(this);
    private void ReportHandoff(Exception exception)
    {
        try { log.Error(exception, "[LocalPreview] Native handoff boundary failed; original input is never retried."); } catch { }
    }
    private bool RunNative(NativeIntent intent, Func<bool> original, bool starts)
    {
        nativeDepth++;
        try { return handoff.RunGuarded(intent, original, starts, Suspend, ReportHandoff); }
        finally { nativeDepth--; }
    }
    private T InspectNative<T>(Func<T> original, NativeIntent intent)
    {
        // A query may re-enter the dispatcher. It is still inspection, not a new
        // user attempt, even though handoff tracks its own independent depth.
        nativeDepth++;
        try { return handoff.InspectGuarded(original, () => Suspend(intent), ReportHandoff); }
        finally { nativeDepth--; }
    }

    // CN GeneralAction row 23 is the explicit hotbar "Dismount" input. Its native
    // status and dispatcher reject MountId == 0 before the mount-container hook.
    // A display-only mount therefore ends here; no gameplay state is fabricated.
    private static bool IsExplicitDismount(ActionType type, uint id)
        => (type == ActionType.GeneralAction && id == 23) || (type == ActionType.Mount && id == 0);

    // Current CN GeneralAction 9/24 select a mount before recursively dispatching
    // UseAction(Mount, selectedId). Handle the outer request even without a candidate.
    private static bool IsActiveMountRequest(ActionType type, uint id, ActionManager.UseActionMode mode)
        => mode is ActionManager.UseActionMode.None or ActionManager.UseActionMode.Macro
            && ((type == ActionType.Mount && id != 0) || IsMountRoulette(type, id));

    private static bool IsMountRoulette(ActionType type, uint id)
        => type == ActionType.GeneralAction && id is 9 or 24;

    private bool CanCancelMountAttempt()
    {
        if (Bypass || !effects.ContainsKey(PreviewSlot.Mount) || !BindingCurrent()) return false;
        var currentRevision = revision;
        var owned = visualActor;
        var source = Bound();
        if (owned is null) return false;
        var display = owned.Actor;
        // The native lookup can re-enter. Never cancel a replacement preview or
        // touch a display slot whose ownership has already been lost.
        return display != null && !Bypass && effects.ContainsKey(PreviewSlot.Mount)
            && revision == currentRevision && owned == visualActor && BindingCurrent() && Bound() == source;
    }

    private bool TryExitDisplayMount()
    {
        if (nativeDepth != 0 || !BindingCurrent() || baseline is not { MountId: 0 }
            || !effects.TryGetValue(PreviewSlot.Mount, out var effect) || visualActor is null) return false;
        long currentRevision = revision;
        var ownedDisplay = visualActor;
        var actor = Bound();
        var display = ownedDisplay.Actor;
        // The owned-actor lookup is native and may re-enter lifecycle or preview code.
        if (revision != currentRevision || ownedDisplay != visualActor || !BindingCurrent() || Bound() != actor
            || actor == null || actor->Mount.MountId != 0 || display == null || display->Mount.MountId != effect.Id) return false;
        // Clear only the mount slot. The independent actor and all other preview slots
        // retain ownership; cancellation must never replay the rejected native action.
        try { Clear(PreviewSlot.Mount); }
        catch (Exception ex) { ReportHandoff(ex); }
        Raise(PreviewCancelled, "试骑乘已结束");
        return true;
    }

    private bool UseActionDetour(ActionManager* mgr, ActionType type, uint id, ulong target, uint extra, ActionManager.UseActionMode mode, uint combo, bool* area)
    {
        if (!Bypass && nativeDepth == 0 && IsActiveMountRequest(type, id, mode))
        {
            // User intent now ends the trial even if native validation rejects or
            // only queues this request. Protect cleanup and dispatch as one input;
            // preserve every argument/result and never replay the native call.
            nativeDepth++;
            try
            {
                // Ownership lookup is native too and belongs inside this protection.
                bool cancel = false;
                try { cancel = CanCancelMountAttempt(); }
                catch (Exception ex) { ReportHandoff(ex); }
                if (cancel)
                {
                    try { ClearForReason(PreviewExitReason.Busy); Raise(PreviewCancelled, "已尝试使用原生坐骑，全部试用已结束"); }
                    catch (Exception ex) { ReportHandoff(ex); }
                    return useAction!.Original(mgr,type,id,target,extra,mode,combo,area);
                }
            }
            finally { nativeDepth--; }
        }
        if (!Bypass && IsExplicitDismount(type, id))
        {
            if (TryExitDisplayMount()) return true;
            // A genuine mount keeps the complete native path. GeneralAction's outer
            // true can also mean queued/dispatched, so it cannot commit preview cleanup.
            return useAction!.Original(mgr,type,id,target,extra,mode,combo,area);
        }
        // A queued roulette can dispatch an inner Mount with its default mode.
        // Keep it within one transaction so that inner call is not a new attempt;
        // a real accepted leaf still uses the existing native handoff rules.
        if (!Bypass && IsMountRoulette(type, id))
            return RunNative(new(PreviewSlot.Mount,id), () => useAction!.Original(mgr,type,id,target,extra,mode,combo,area), false);
        if (Bypass || !Relevant(type)) return useAction!.Original(mgr,type,id,target,extra,mode,combo,area);
        return RunNative(new(Slot(type),id), () => useAction!.Original(mgr,type,id,target,extra,mode,combo,area), false);
    }
    private bool UseLocationDetour(ActionManager* mgr, ActionType type, uint id, ulong target, Vector3* pos, uint extra, byte a7)
    {
        if (Bypass || !Relevant(type)) return useLocation!.Original(mgr,type,id,target,pos,extra,a7);
        return RunNative(new(Slot(type),id), () => useLocation!.Original(mgr,type,id,target,pos,extra,a7), true);
    }
    private bool UseCompanionDetour(ActionManager* mgr, uint id)
    {
        if (Bypass) return useCompanion!.Original(mgr,id);
        var intent = new NativeIntent(PreviewSlot.Minion,id);
        var cooldownBefore = mgr != null ? mgr->CompanionActionCooldown : float.NaN;
        return RunNative(intent, () =>
        {
            bool result = useCompanion!.Original(mgr,id);
            // The ordinary field executor sets its cooldown before sending, and returns
            // true only after dispatch. Its special minion-battle branch can return true
            // even when its agent is absent/rejects, so that bool alone is insufficient.
            handoff.ReportDecision(intent, result && mgr != null && cooldownBefore <= 0
                && mgr->CompanionActionCooldown > 0);
            return result;
        }, false);
    }
    private uint ActionStatusDetour(ActionManager* mgr, ActionType type, uint id, ulong target, bool recast, bool casting, uint* info)
    {
        if (Bypass || !Relevant(type)) return actionStatus!.Original(mgr,type,id,target,recast,casting,info);
        return InspectNative(() => actionStatus!.Original(mgr,type,id,target,recast,casting,info),new(Slot(type),id));
    }
    private bool CanEmoteDetour(EmoteManager* mgr, ushort id) => Bypass ? canEmote!.Original(mgr,id) : InspectNative(() => canEmote!.Original(mgr,id),new(PreviewSlot.Emote,id));
    private bool CanAgentEmoteDetour(AgentEmote* agent, ushort id) => Bypass ? canAgentEmote!.Original(agent,id) : InspectNative(() => canAgentEmote!.Original(agent,id),new(PreviewSlot.Emote,id));
    private bool ExecuteEmoteDetour(EmoteManager* mgr, ushort id, EmoteController.PlayEmoteOption* option)
    {
        bool Original()
        {
            bool accepted = executeEmote!.Original(mgr,id,option);
            if (accepted && !ownCall && id != 0) Raise(ExternalEmoteAccepted, id);
            return accepted;
        }
        if (Bypass) return Original();
        return RunNative(new(PreviewSlot.Emote,id), Original, true);
    }
    private void ExecuteAgentEmoteDetour(AgentEmote* agent, ushort id, EmoteController.PlayEmoteOption* option, bool history, bool live)
    {
        if (Bypass) { executeAgentEmote!.Original(agent,id,option,history,live); return; }
        RunNative(new(PreviewSlot.Emote,id), () => { executeAgentEmote!.Original(agent,id,option,history,live); return true; }, false);
    }
    private bool PlayEmoteDetour(EmoteController* controller, uint id, EmoteController.PlayEmoteOption* option)
    {
        ulong owner = controller->OwnerObject == null ? 0 : controller->OwnerObject->GetGameObjectId();
        var previous = CurrentNativeEmote;
        CurrentNativeEmote = id <= ushort.MaxValue ? (owner,(ushort)id) : null;
        if (id <= ushort.MaxValue) Raise(NativeEmoteEntered,owner,(ushort)id);
        try
        {
            bool result;
            if (!Bypass && owner == actorId)
                result = RunNative(new(PreviewSlot.Emote,id), () => playEmote!.Original(controller,id,option), true);
            else
            {
                try { result = playEmote!.Original(controller,id,option); }
                catch(Exception ex) { ReportHandoff(ex); return false; }
            }
            if (result && !ownCall && !(handoff.HasExternal && owner == actorId) && id is > 0 and <= ushort.MaxValue)
                Raise(EmoteObserved,owner,(ushort)id);
            if (result && !ownCall && owner == objects.LocalPlayer?.GameObjectId && id is > 0 and <= ushort.MaxValue)
                Raise(ExternalEmoteAccepted, (ushort)id);
            return result;
        }
        finally
        {
            if (id <= ushort.MaxValue) Raise(NativeEmoteExited,owner,(ushort)id);
            CurrentNativeEmote = previous;
        }
    }
    private void MountSetupDetour(MountContainer* container, short id, uint top, uint body, uint legs, byte stain, byte unk6, byte unk7)
    {
        // A real container teardown (including the native HUD dismount) ends only the mount slot.
        // Native action handoffs already own their full accepted-input transaction.
        var actor = Bound();
        var ownedDisplay = visualActor;
        long currentRevision = revision;
        bool mountOnly = !Bypass && nativeDepth == 0 && id == 0 && actor != null && container != null
            && container->OwnerObject == actor && BindingCurrent()
            && effects.ContainsKey(PreviewSlot.Mount);
        mountSetup!.Original(container,id,top,body,legs,stain,unk6,unk7);
        if (!mountOnly || revision != currentRevision || visualActor != ownedDisplay || !BindingCurrent() || Bound() != actor) return;
        if (ownedDisplay is not null)
        {
            try { CompleteDisplayDismount(actor, ownedDisplay, currentRevision); }
            catch (Exception ex) { ReportHandoff(ex); }
            return;
        }
        effects.Remove(PreviewSlot.Mount);
        spawnWait.Remove(PreviewSlot.Mount);
        revision++;
        RestoreImplicitMinion(Bound());
        if (!HasActivePreview) Forget();
    }

    private void CompleteDisplayDismount(NativeCharacter* actor, NativePreviewActor ownedDisplay, long expectedRevision)
    {
        bool Current() => !disposed && revision == expectedRevision && visualActor == ownedDisplay
            && BindingCurrent() && Bound() == actor;
        var display = ownedDisplay.Actor;
        // Both actor lookup and native teardown can re-enter preview/lifecycle code.
        if (!Current() || actor == null || actor->Mount.MountId != 0 || display == null
            || !effects.TryGetValue(PreviewSlot.Mount, out var effect)
            || display->Mount.MountId != 0 && display->Mount.MountId != effect.Id) return;
        var mode = display->Mode;
        var modeParam = display->ModeParam;
        bool previousOwnCall = ownCall;
        ownCall = true;
        try
        {
            // This is the new native unmounted state, never a restoration of the old baseline.
            display->Mount.CreateAndSetupMount(0,0,0,0,0,0,0);
            var currentDisplay = ownedDisplay.Actor;
            if (!Current() || currentDisplay != display || actor->Mount.MountId != 0 || display->Mount.MountId != 0) return;
            if (mode == CharacterModes.Mounted)
            {
                display->Mode = actor->Mode == CharacterModes.Mounted ? CharacterModes.Normal : actor->Mode;
                display->ModeParam = actor->Mode == CharacterModes.Mounted ? (byte)0 : actor->ModeParam;
            }
            else { display->Mode = mode; display->ModeParam = modeParam; }
        }
        finally { ownCall = previousOwnCall; }
        // Isolated previews never modify the real actor. Future manual clears must
        // restore its newly accepted state, not the mount/posture from preview entry.
        var nativeBaseline = ActorSnapshot.Read(actor, includeObjectAddresses: false);
        var nativeHmsBaseline = HmsPresentationBaseline.Read(actor);
        if (nativeBaseline.Mode == CharacterModes.Mounted)
        {
            // The outer dismount can finish mode/timeline changes after this hook.
            // Normalize only our value snapshots; never write the real source.
            nativeBaseline = nativeBaseline with { Mode = CharacterModes.Normal, ModeParam = 0, Timeline = 0, BaseOverride = 0,
                Presentation = nativeBaseline.Presentation with { CharMode = (byte)CharacterModes.Normal, CharModeParam = 0, TimelineId = 0, BaseOverride = 0 } };
            nativeHmsBaseline = nativeHmsBaseline with { Root = nativeHmsBaseline.Root with { Mode = CharacterModes.Normal, ModeParam = 0,
                Timeline = nativeHmsBaseline.Root.Timeline with { T0 = 0, BaseOverride = 0 } } };
        }
        baseline = nativeBaseline;
        hmsBaseline = nativeHmsBaseline;
        InvalidateExternalPresentation();
        effects.Remove(PreviewSlot.Mount);
        spawnWait.Remove(PreviewSlot.Mount);
        revision++;
        if (!HasActivePreview) Forget();
    }

    private sealed class Suspension : IPreviewSuspension<NativeIntent>
    {
        private readonly ActorPreviewRuntime runtime;
        private readonly ActorSnapshot current;
        private readonly nint address;
        private readonly bool projectedMode;
        private readonly bool projectedEmote;
        private readonly bool projectedMount;
        private readonly bool projectedAccessory;
        private readonly bool projectedMinion;
        private readonly bool projectedMetadata;
        private readonly bool external;
        private readonly bool isolated;
        private readonly NativePreviewActor? displayOwner;
        private readonly long revision;
        public Suspension(ActorPreviewRuntime runtime, bool projectMetadata = false)
        {
            this.runtime = runtime;
            external = projectMetadata;
            var actor = runtime.Bound();
            if (actor == null || runtime.baseline is not { } baseline) return;
            address = (nint)actor;
            revision = runtime.revision;
            current = ActorSnapshot.Read(actor);
            displayOwner = runtime.visualActor;
            isolated = displayOwner is not null;
            if (isolated) return;
            // No containers are destroyed. Only public semantic state is temporarily exposed as
            // the real baseline while the engine checks/starts this one synchronous native input.
            projectedMode = runtime.OwnsMode(actor);
            projectedEmote = runtime.effects.TryGetValue(PreviewSlot.Emote,out var emote) && actor->EmoteController.EmoteId == emote.Id;
            projectedMount = runtime.effects.TryGetValue(PreviewSlot.Mount,out var mount) && actor->Mount.MountId == mount.Id;
            projectedAccessory = runtime.effects.TryGetValue(PreviewSlot.Accessory,out var accessory) && actor->OrnamentData.OrnamentId == accessory.Id;
            projectedMinion = runtime.effects.TryGetValue(PreviewSlot.Minion,out var minion) && MinionId(actor) == minion.Id;
            projectedMetadata = projectMetadata && projectedEmote;
            if (projectedMode) { actor->Mode = baseline.Mode; actor->ModeParam = baseline.ModeParam; }
            if (projectedEmote) actor->EmoteController.EmoteId = baseline.EmoteId;
            if (projectedMount) actor->Mount.MountId = baseline.MountId;
            if (projectedAccessory) actor->OrnamentData.OrnamentId = baseline.AccessoryId;
            if (projectedMinion && actor->CompanionData.CompanionObject != null)
                actor->CompanionData.CompanionObject->BaseId = baseline.MinionId;
            if (projectedMetadata) WriteMetadata(actor, baseline);
        }
        public void Resume()
            => ResumeCore(includeMount: true);
        private void ResumeCore(bool includeMount)
        {
            var actor = runtime.Bound();
            if (actor == null || (nint)actor != address || runtime.revision != revision || !runtime.BindingCurrent()) return;
            if (projectedMode) { actor->Mode = current.Mode; actor->ModeParam = current.ModeParam; }
            if (projectedEmote) actor->EmoteController.EmoteId = current.EmoteId;
            if (projectedMount && includeMount) actor->Mount.MountId = current.MountId;
            if (projectedAccessory) actor->OrnamentData.OrnamentId = current.AccessoryId;
            if (projectedMinion && (nint)actor->CompanionData.CompanionObject == current.MinionObject && actor->CompanionData.CompanionObject != null)
                actor->CompanionData.CompanionObject->BaseId = current.MinionId;
            if (projectedMetadata) WriteMetadata(actor, current);
        }
        private static void WriteMetadata(NativeCharacter* actor, ActorSnapshot snapshot)
        {
            actor->EmoteController.CPoseState = snapshot.Pose;
            actor->EmoteController.CurrentPoseType = snapshot.PoseType;
            actor->Timeline.BaseOverride = snapshot.BaseOverride;
            actor->Timeline.TimelineSequencer.TimelineIds[0] = snapshot.Timeline;
            actor->Timeline.IsWeaponDrawn = snapshot.Presentation.WeaponDrawn;
        }
        public void Accept(NativeIntent intent)
        {
            if (isolated)
            {
                if (runtime.revision != revision || !runtime.BindingCurrent()) return;
                if (intent.Slot == PreviewSlot.Mount && intent.Id == 0)
                    runtime.CompleteDisplayDismount((NativeCharacter*)address, displayOwner!, revision);
                else runtime.ClearAll(false);
                Raise(runtime.NativeInputAccepted);
                return;
            }
            var winner = intent.Slot;
            if (winner == PreviewSlot.Mount && intent.Id == 0)
            {
                // Explicit native dismount ends only the mount preview, preserving the other slots.
                ResumeCore(includeMount: false);
                runtime.effects.Remove(PreviewSlot.Mount);
                runtime.spawnWait.Remove(PreviewSlot.Mount);
                runtime.revision++;
                runtime.RestoreImplicitMinion(runtime.Bound());
                if (!runtime.HasActivePreview) runtime.Forget();
                return;
            }
            var actor = runtime.Bound();
            if (actor == null || (nint)actor != address || runtime.revision != revision || !runtime.BindingCurrent())
            {
                if (runtime.revision == revision) runtime.ClearAll(false);
                return;
            }
            ushort exposedTimeline = projectedMetadata ? runtime.baseline!.Value.Timeline : current.Timeline;
            var acceptedPresentation = external || winner == PreviewSlot.Emote || actor->Timeline.TimelineSequencer.TimelineIds[0] != exposedTimeline
                ? ActorSnapshot.Read(actor) : (ActorSnapshot?)null;
            runtime.ownCall = true;
            try
            {
                if (runtime.baseline is { } baseline)
                {
                    // Preserve new native state. Clear an old model only if the native input has
                    // not replaced its object yet; never restore the winner from an old snapshot.
                    if (runtime.effects.TryGetValue(PreviewSlot.Mount, out var mount) && (nint)actor->Mount.MountObject == current.MountObject
                        && (actor->Mount.MountId == mount.Id || actor->Mount.MountId == baseline.MountId))
                    {
                        // A mount cast starts before its new model appears. Remove the hidden old
                        // preview only while the id is still the exposed baseline, never a new id.
                        bool oldPendingMount = winner == PreviewSlot.Mount && NativeOwnershipPolicy.RemovePendingMount(actor->Mount.MountId,baseline.MountId,intent.Id,
                            (nint)actor->Mount.MountObject == current.MountObject);
                        if (winner != PreviewSlot.Mount || oldPendingMount)
                            actor->Mount.CreateAndSetupMount(winner == PreviewSlot.Mount ? (short)0 : (short)baseline.MountId,0,0,0,0,0,0);
                    }
                    if (runtime.effects.TryGetValue(PreviewSlot.Minion,out var minion)
                        && NativeOwnershipPolicy.RestoreSibling(winner == PreviewSlot.Minion,MinionId(actor),minion.Id,baseline.MinionId,
                            (nint)actor->CompanionData.CompanionObject == current.MinionObject))
                        actor->CompanionData.SetupCompanion((short)baseline.MinionId,0);
                    else if (winner == PreviewSlot.Minion && runtime.effects.ContainsKey(PreviewSlot.Minion)
                        && (actor->CompanionData.CompanionId == 0 || actor->CompanionData.CompanionId == baseline.MinionId)
                        && actor->CompanionData.CompanionId != intent.Id
                        && NativeOwnershipPolicy.RemovePendingChild(MinionId(actor),baseline.MinionId,intent.Id,
                            (nint)actor->CompanionData.CompanionObject == current.MinionObject))
                        actor->CompanionData.SetupCompanion(0,0);
                    if (runtime.effects.TryGetValue(PreviewSlot.Accessory,out var accessory)
                        && NativeOwnershipPolicy.RestoreSibling(winner == PreviewSlot.Accessory,actor->OrnamentData.OrnamentId,accessory.Id,baseline.AccessoryId,
                            (nint)actor->OrnamentData.OrnamentObject == current.AccessoryObject))
                        actor->OrnamentData.SetupOrnament((short)baseline.AccessoryId,0);
                    else if (winner == PreviewSlot.Accessory && runtime.effects.ContainsKey(PreviewSlot.Accessory)
                        && NativeOwnershipPolicy.RemovePendingChild(actor->OrnamentData.OrnamentId,baseline.AccessoryId,intent.Id,
                            (nint)actor->OrnamentData.OrnamentObject == current.AccessoryObject))
                        actor->OrnamentData.SetupOrnament(0,0);
                    if (NativeOwnershipPolicy.RestorePreviousTimeline(winner == PreviewSlot.Emote, runtime.effects.ContainsKey(PreviewSlot.Emote),
                        actor->EmoteController.EmoteId, baseline.EmoteId, actor->Timeline.TimelineSequencer.TimelineIds[0],
                        exposedTimeline, actor->Mode == baseline.Mode))
                    {
                        actor->Timeline.BaseOverride = baseline.BaseOverride;
                        if (baseline.Timeline != 0 && actor->Mode == baseline.Mode) actor->Timeline.TimelineSequencer.PlayTimeline(baseline.Timeline);
                    }
                }
            }
            finally
            {
                // A sibling container's teardown may reset the rider's scalar presentation.
                // Preserve the accepted external operation's new state, without executing it again.
                if (acceptedPresentation is { } accepted && runtime.Bound() == actor && runtime.BindingCurrent())
                {
                    actor->Mode = accepted.Mode;
                    actor->ModeParam = accepted.ModeParam;
                    actor->EmoteController.EmoteId = accepted.EmoteId;
                    WriteMetadata(actor, accepted);
                }
                runtime.effects.Clear();
                runtime.spawnWait.Clear();
                runtime.Forget();
                runtime.ownCall = false;
                Raise(runtime.NativeInputAccepted);
            }
        }
    }

    private readonly record struct ActorSnapshot(CharacterModes Mode, byte ModeParam, ushort EmoteId,
        byte Pose, EmoteController.PoseType PoseType, ushort BaseOverride, ushort Timeline,
        ushort MountId, ushort MinionId, ushort AccessoryId, nint MountObject, nint MinionObject, nint AccessoryObject,
        PresentationSnapshot Presentation)
    {
        public static ActorSnapshot Read(NativeCharacter* actor, bool includeObjectAddresses = true)
        {
            var companion = actor->CompanionData.CompanionObject;
            var minionGo = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)companion;
            var minionOffset = minionGo != null ? minionGo->Position - actor->GameObject.Position : default;
            var draw = actor->GameObject.DrawObject;
            var bodyOffset = draw != null ? draw->Object.Position - actor->GameObject.Position : default;
            var mount = actor->Mount.MountObject;
            ushort mountTimeline = mount != null ? mount->Timeline.TimelineSequencer.TimelineIds[0] : (ushort)0;
            float pitch = 0;
            if (actor->Mode == CharacterModes.Mounted && mountTimeline is >= 4040 and <= 4058
                && mount != null && mount->DrawObject != null)
            {
                var q = mount->DrawObject->Rotation;
                pitch = MathF.Asin(Math.Clamp(2f * (q.W * q.X - q.Y * q.Z), -1f, 1f));
            }
            var presentation = new PresentationSnapshot((byte)actor->Mode,actor->ModeParam,actor->EmoteController.EmoteId,
                actor->Timeline.TimelineSequencer.TimelineIds[0],actor->Timeline.BaseOverride,(byte)actor->EmoteController.CurrentPoseType,
                actor->EmoteController.CPoseState,actor->Timeline.IsWeaponDrawn,actor->Mount.MountId,mountTimeline,pitch,
                ActorPreviewRuntime.MinionId(actor),companion != null ? (byte)companion->Behavior : (byte)0,
                companion != null ? companion->Timeline.TimelineSequencer.TimelineIds[0] : (ushort)0,
                minionOffset.X,minionOffset.Y,minionOffset.Z,minionGo != null ? minionGo->Rotation : 0,
                actor->OrnamentData.OrnamentId,actor->OrnamentData.OrnamentId != 0 ? actor->Timeline.TimelineSequencer.TimelineIds[0] : (ushort)0,
                bodyOffset.X,bodyOffset.Y,bodyOffset.Z);
            return new(actor->Mode,actor->ModeParam,actor->EmoteController.EmoteId,
                actor->EmoteController.CPoseState,actor->EmoteController.CurrentPoseType,actor->Timeline.BaseOverride,
                actor->Timeline.TimelineSequencer.TimelineIds[0],actor->Mount.MountId,ActorPreviewRuntime.MinionId(actor),actor->OrnamentData.OrnamentId,
                includeObjectAddresses ? (nint)mount : 0,includeObjectAddresses ? (nint)companion : 0,
                includeObjectAddresses ? (nint)actor->OrnamentData.OrnamentObject : 0,presentation);
        }
    }

    private static void Raise(Action? handlers) { if (handlers == null) return; foreach(Action h in handlers.GetInvocationList()) { try { h(); } catch { } } }
    private static void Raise<T>(Action<T>? handlers,T value) { if (handlers == null) return; foreach(Action<T> h in handlers.GetInvocationList()) { try { h(value); } catch { } } }
    private static void Raise<T,U>(Action<T,U>? handlers,T a,U b) { if (handlers == null) return; foreach(Action<T,U> h in handlers.GetInvocationList()) { try { h(a,b); } catch { } } }
    public void Dispose()
    {
        if (disposed) return;
        try { framework.Update -= Update; } catch (Exception ex) { ReportHandoff(ex); }
        try { ClearAll(); }
        catch (Exception ex) { ReportHandoff(ex); }
        finally
        {
            effects.Clear(); spawnWait.Clear(); Forget(); disposed = true;
            foreach(var hook in installedHooks.AsEnumerable().Reverse())
                try { hook.Dispose(); } catch(Exception ex) { try { log.Warning(ex,"[LocalPreview] Hook disposal failed."); } catch { } }
            installedHooks.Clear();
        }
    }
}
