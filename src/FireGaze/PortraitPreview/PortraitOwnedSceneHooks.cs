using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal enum PortraitOwnedSceneNativeKind { Dtor, Release, Initialize, Update, Setup, Render, CreateClientObject, DeleteClientObject }
internal readonly record struct PortraitOwnedSceneNativeTarget(PortraitOwnedSceneNativeKind Kind, nint Address);
internal interface IPortraitOwnedSceneNativeHook : IDisposable { bool IsEnabled { get; } bool IsDisposed { get; } void Enable(); void Disable(); }
internal interface IPortraitOwnedSceneNativeHook<out T> : IPortraitOwnedSceneNativeHook where T : Delegate { T Original { get; } }
internal interface IPortraitOwnedSceneHookFactory
{
    IPortraitOwnedSceneNativeHook<T> Create<T>(PortraitOwnedSceneNativeTarget target, T detour) where T : Delegate;
}
internal interface IPortraitOwnedSceneHookEnvironment
{
    bool IsFrameworkThread { get; }
    bool TryValidateInstallation(out string reason);
    IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets { get; }
    bool IsSourceCurrent(PortraitIdentity source);
    bool IsSlotIdle(uint slot);
    bool IsSlotOwnedBy(uint slot, nint view);
    bool TryReadViewSlot(nint view, out uint slot);
}

/// <summary>
/// Native-first adapter for the audited CharaView call chain. Construction never
/// installs hooks. Installation is all-or-nothing and requires an environment that
/// has closed the native consumer/context/retirement coverage contract.
/// </summary>
internal sealed unsafe partial class PortraitOwnedSceneHooks(IPortraitOwnedSceneHookEnvironment environment,
    PortraitNativePhaseHostReservations? reservations = null) : IDisposable
{
    private readonly List<IPortraitOwnedSceneNativeHook> hooks = [];
    private readonly PortraitNativePhaseHostReservations reservations = reservations ?? new();
    private PortraitNativePhaseHostReservation? reservation;
    private PortraitOwnedSceneLease? lease;
    private Action<IPortraitOwnedSceneLease>? advance;
    private Func<IPortraitOwnedSceneLease, bool>? retire;
    private PortraitIdentity source;
    private uint requestedSlot, sourceNativeSlot, lastFrame;
    private bool installed, installing, closing, stopped, hasFrame, retirementInProgress, unknownCreation;
    private long epoch;
    private int phaseDepth, phaseThread, originalDepth;
    public string? LastError { get; private set; }
    public bool IsInstalled => installed && hooks.All(hook => hook.IsEnabled && !hook.IsDisposed);

    public bool TryInstall(IPortraitOwnedSceneHookFactory factory)
    {
        if (installed || installing || stopped || closing || reservation is not null || hooks.Count != 0 || !environment.IsFrameworkThread) return false;
        installing = true;
        try
        {
            if (!environment.TryValidateInstallation(out var reason)) { LastError = reason; return false; }
            if (!NativeAbiMatchesSDK(out reason)) { LastError = reason; return false; }
            var targets = environment.Targets;
            if (!ValidateTargetInventory(targets)) { LastError = "Native target inventory is incomplete or duplicated."; return false; }
            // The passive observer and every owned generation share this atomic process
            // reservation. A query before Create is insufficient: another owner can win
            // the overlapping Render entry between that query and native construction.
            if (!reservations.TryAcquireBatch(targets.Select(target => target.Address).ToArray(), out reservation, out reason))
            { LastError = reason; return false; }
            // Exit hooks are first in the immutable inventory. Create all disabled,
            // then enable all; no lease can be requested during partial enabling.
            foreach (var target in targets) CreateHook(factory, target);
            if (closing) throw new InvalidOperationException("Native installation was closed before enabling.");
            foreach (var hook in hooks) hook.Enable();
            if (hooks.Any(hook => !hook.IsEnabled || hook.IsDisposed)) throw new InvalidOperationException("Native hook did not enable.");
            installed = true; return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message; stopped = true; DisposeHooks(); return false;
        }
        finally { installing = false; }
    }

    public bool TryRequest(PortraitIdentity requestedSource, uint slot, Action<IPortraitOwnedSceneLease> onAdvance,
        Func<IPortraitOwnedSceneLease, bool> onRetire)
    {
        if (!IsInstalled || closing || stopped || advance is not null || !requestedSource.IsValid
            || requestedSource.Generation == 0 || slot >= 8 || !environment.IsFrameworkThread
            || !environment.IsSourceCurrent(requestedSource) || !environment.IsSlotIdle(slot)
            || !environment.TryReadViewSlot(requestedSource.Model, out var sourceSlot) || sourceSlot >= 8 || sourceSlot == slot)
            return false;
        source = requestedSource; sourceNativeSlot = sourceSlot; requestedSlot = slot; advance = onAdvance; retire = onRetire;
        hasFrame = false; epoch++; return true;
    }

    private void CreateHook(IPortraitOwnedSceneHookFactory factory, PortraitOwnedSceneNativeTarget target)
    {
        switch (target.Kind)
        {
            case PortraitOwnedSceneNativeKind.Render:
                Add<CharaView.Delegates.Render>(factory, target, original => (view, frame) => Render(view, frame, original)); break;
            case PortraitOwnedSceneNativeKind.Initialize:
                Add<CharaView.Delegates.Initialize>(factory, target, original => (view, agent, slot, callback) => {
                    BeforeNative((nint)view, slot, false); Original(() => original()(view, agent, slot, callback)); }); break;
            case PortraitOwnedSceneNativeKind.Setup:
                Add<CharaViewPortrait.Delegates.Setup>(factory, target, original => (view, slot, data, agent, eventID, eventKind) => {
                    BeforeNative((nint)view, slot, false); Original(() => original()(view, slot, data, agent, eventID, eventKind)); }); break;
            case PortraitOwnedSceneNativeKind.Release:
                Add<CharaView.Delegates.Release>(factory, target, original => view => {
                    BeforeNative((nint)view, null, true); Original(() => original()(view)); }); break;
            case PortraitOwnedSceneNativeKind.Dtor:
                Add<CharaView.Delegates.Dtor>(factory, target, original => (view, flags) => {
                    BeforeNative((nint)view, null, true); CharaView* result = null;
                    Original(() => result = original()(view, flags)); return result; }); break;
            case PortraitOwnedSceneNativeKind.Update:
                Add<CharaView.Delegates.Update>(factory, target, original => (view, frame, character) => {
                    BeforeNative((nint)view, null, false); Original(() => original()(view, frame, character)); }); break;
            case PortraitOwnedSceneNativeKind.CreateClientObject:
                Add<ClientObjectManager.Delegates.CreateBattleCharacter>(factory, target, original => (manager, index, param) => {
                    BeforeClientObject(index); uint result = 0; Original(() => result = original()(manager, index, param)); return result; }); break;
            case PortraitOwnedSceneNativeKind.DeleteClientObject:
                Add<ClientObjectManager.Delegates.DeleteObjectByIndex>(factory, target, original => (manager, index, param) => {
                    BeforeClientObject(index); Original(() => original()(manager, index, param)); }); break;
        }
    }

    private void Add<T>(IPortraitOwnedSceneHookFactory factory, PortraitOwnedSceneNativeTarget target,
        Func<Func<T>, T> makeDetour) where T : Delegate
    {
        IPortraitOwnedSceneNativeHook<T>? hook = null;
        var detour = makeDetour(() => hook!.Original);
        // A factory which throws before returning a handle may already have created
        // native state. No later cleanup can certify that unreturned resource retired.
        unknownCreation = true;
        hook = factory.Create(target, detour) ?? throw new InvalidOperationException("Native Create returned no handle.");
        hooks.Add(hook); unknownCreation = false;
        if (hook.IsEnabled || hook.IsDisposed) throw new InvalidOperationException("Native hook did not start disabled and live.");
    }

    private void Original(Action original)
    {
        Interlocked.Increment(ref originalDepth);
        try { original(); }
        finally { Interlocked.Decrement(ref originalDepth); }
    }

    private void Render(CharaView* view, uint frame, Func<CharaView.Delegates.Render> original)
    {
        BeforeNative((nint)view, null, false);
        Original(() => original()(view, frame));
        try { AfterSourceRender((nint)view, frame); }
        catch (Exception ex) { StopUnsafe("Native source-phase observation failed: " + ex.Message); }
    }

    private void AfterSourceRender(nint view, uint frame)
    {
        // A Render recursively entered by another native original is not a new
        // scheduling phase. ImGui frame counts never enter this code path.
        if (view != source.Model || originalDepth != 0 || phaseDepth != 0
            || advance is null || stopped || !IsInstalled || !environment.IsFrameworkThread) return;
        if (!environment.IsSourceCurrent(source)) { StopUnsafe("Source allocation changed."); return; }
        if (hasFrame && lastFrame == frame) return;
        lastFrame = frame; hasFrame = true;
        InPhase(() => {
            if (lease is null)
            {
                if (closing) { ClearRequest(); return; }
                if (!environment.IsSlotIdle(requestedSlot)) { StopUnsafe("Native slot is no longer idle."); return; }
                lease = new(this, requestedSlot, source, epoch);
            }
            lease.SetNativeFrame(frame);
            if (!lease.IsCurrent) { StopUnsafe("Owned slot identity changed."); return; }
            if (closing) { RetireBeforeNative(); return; }
            try { advance(lease); }
            catch (Exception ex) { LastError = ex.Message; RetireBeforeNative(); stopped = true; }
        });
    }

    private void BeforeClientObject(uint index)
    {
        try
        {
            if (lease is null || (index != uint.MaxValue && index != 0xF0 + lease.Slot && index != 0xF0 + sourceNativeSlot)) return;
            if (index == 0xF0 + lease.Slot && lease.IsInOwnedCall && originalDepth > 0) return;
            RetireBeforeNative();
        }
        catch (Exception ex) { StopUnsafe("Native client-object observation failed: " + ex.Message); }
    }

    private void BeforeNative(nint view, uint? explicitSlot, bool teardown)
    {
        try
        {
            if (lease is null || stopped) return;
            if (lease.IsSelfCall(view)) return;
            var affects = view == source.Model && (teardown || explicitSlot.HasValue) || view == lease.BoundView;
            if (!affects)
                affects = explicitSlot == lease.Slot || environment.TryReadViewSlot(view, out var slot) && slot == lease.Slot;
            if (affects) RetireBeforeNative();
        }
        catch (Exception ex) { StopUnsafe("Native view observation failed: " + ex.Message); }
    }

    private void RetireBeforeNative()
    {
        var retiring = lease;
        if (retiring is null || stopped) return;
        if (retirementInProgress)
        { StopUnsafe("Native takeover reentered an active owned retirement; recursive Release is forbidden."); return; }
        if (!environment.IsFrameworkThread || !environment.IsSourceCurrent(source))
        { StopUnsafe("Cannot retire an owned scene on an unqualified native thread/source."); return; }
        InPhase(() => {
            if (!retiring.CanReleaseView(retiring.BoundView))
            { StopUnsafe("Owned slot was lost; stale Release is forbidden."); return; }
            bool released;
            retirementInProgress = true;
            try { released = retire?.Invoke(retiring) == true && retiring.WasReleased; }
            catch (Exception ex) { LastError = ex.Message; released = false; }
            finally { retirementInProgress = false; }
            retiring.Invalidate();
            if (!released) { StopUnsafe("Owned scene retirement failed; native still has priority."); return; }
            ClearRequest();
        });
    }

    private void InPhase(Action action)
    {
        var outer = phaseDepth == 0;
        if (outer) phaseThread = Environment.CurrentManagedThreadId;
        phaseDepth++;
        try { action(); }
        finally
        {
            phaseDepth--;
            if (outer)
            {
                phaseThread = 0;
                if (lease is { HasOpenOwnedCall: true })
                    StopUnsafe("An owned native-call scope escaped its source phase.");
            }
        }
    }

    internal bool IsLeaseCurrent(PortraitOwnedSceneLease current)
        => IsLeasePhaseCurrent(current)
            && current.CreationStage switch {
                PortraitOwnedSceneCreationStage.Unbound or PortraitOwnedSceneCreationStage.Allocated
                    or PortraitOwnedSceneCreationStage.Constructing or PortraitOwnedSceneCreationStage.Constructed
                    => environment.IsSlotIdle(current.Slot),
                PortraitOwnedSceneCreationStage.SetupPending => current.IsCreationCall(PortraitOwnedSceneCreationCall.Setup)
                    && (environment.IsSlotIdle(current.Slot) || environment.IsSlotOwnedBy(current.Slot, current.BoundView)),
                PortraitOwnedSceneCreationStage.Owned => current.RetirementStarted && environment.IsSlotIdle(current.Slot)
                    || environment.IsSlotOwnedBy(current.Slot, current.BoundView),
                _ => false,
            };

    internal bool IsCreationSlotIdle(PortraitOwnedSceneLease current)
        => IsLeasePhaseCurrent(current) && environment.IsSlotIdle(current.Slot);
    internal bool IsCreationSlotOwned(PortraitOwnedSceneLease current)
        => IsLeasePhaseCurrent(current) && environment.IsSlotOwnedBy(current.Slot, current.BoundView);

    private bool IsLeasePhaseCurrent(PortraitOwnedSceneLease current)
        => ReferenceEquals(lease, current) && !stopped && IsInstalled && phaseDepth > 0
            && phaseThread == Environment.CurrentManagedThreadId && environment.IsFrameworkThread
            && environment.IsSourceCurrent(current.Source);

    // Called after Dtor(1): never inspect the now-freed view, Character or camera.
    internal bool CanConfirmRetirement(PortraitOwnedSceneLease current)
        => current.RetirementStarted && IsLeasePhaseCurrent(current) && environment.IsSlotIdle(current.Slot);

    private void StopUnsafe(string reason)
    {
        LastError = reason; stopped = true; epoch++; lease?.Invalidate();
        // No catch-up Release: after an unknown takeover that would delete the
        // native consumer's new character. Production installation stays blocked
        // until exceptional retirement has a verified safe ownership protocol.
        advance = null; retire = null;
    }

    private void ClearRequest()
    {
        lease?.Invalidate(); lease = null; advance = null; retire = null; source = default; epoch++;
    }

    public void RequestStop() => closing = true;
    public bool TryDispose()
    {
        closing = true;
        if (installing || Volatile.Read(ref originalDepth) != 0 || phaseDepth != 0)
        { LastError = "Native installation or an active original/phase must return before hook retirement."; return false; }
        if (lease is { WasReleased: false, BoundView: not 0 })
        { LastError = "Owned native scene must retire in its native phase before hook disposal."; return false; }
        ClearRequest(); return DisposeHooks();
    }
    public void Dispose() => TryDispose();
    private bool DisposeHooks()
    {
        installed = false;
        if (Volatile.Read(ref originalDepth) != 0 || phaseDepth != 0)
        { LastError = "An active native original/phase still owns the hook batch."; return false; }
        var complete = true;
        for (var i = hooks.Count - 1; i >= 0; i--)
        {
            try
            {
                var hook = hooks[i];
                if (hook.IsDisposed) continue;
                if (hook.IsEnabled) hook.Disable();
                if (hook.IsEnabled) { complete = false; LastError = "A native hook did not disable."; continue; }
                hook.Dispose();
                if (!hook.IsDisposed) { complete = false; LastError = "Native hook disposal was not confirmed."; }
            }
            catch (Exception ex) { complete = false; LastError = ex.Message; }
        }
        if (unknownCreation)
        { LastError = "Native Create returned no handle; the complete address reservation remains isolated."; return false; }
        if (!complete) return false;
        if (reservation is not null)
        {
            if (!reservation.TryRelease(out var reason)) { LastError = reason; return false; }
            reservation = null;
        }
        hooks.Clear();
        return true;
    }
}
