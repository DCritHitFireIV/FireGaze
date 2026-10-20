using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal enum PortraitOwnedSceneNativeKind { Dtor, Release, Initialize, Update, Setup, Render, CreateClientObject, DeleteClientObject, ConfigureContext, ConfigureDefaultContext, CleanupContext, ConstructContext, DestroyContext }
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
    PortraitNativePhaseHostReservations? reservations = null, Func<long>? timestamp = null, long timestampFrequency = 0,
    Func<PortraitNativeThreadStamp>? nativeThread = null,
    Func<TimeSpan, CancellationToken, Task>? passiveDelay = null) : IDisposable
{
    private readonly List<IPortraitOwnedSceneNativeHook> hooks = [];
    private IPortraitOwnedSceneNativeHook[] publishedHooks = [];
    private bool allHooksPhysical = true;
    private readonly PortraitNativePhaseHostReservations reservations = reservations ?? new();
    private PortraitNativePhaseHostReservation? reservation;
    private IPortraitOwnedSceneHookFactory? owningFactory;
    private TaskCompletionSource physicalRetired = CompletedRetirement();
    private Task? retirementTask;
    private Task? passiveExpiryTask;
    private readonly CancellationTokenSource expiryCancellation = new();
    private int retirementDriving;
    private bool passiveOnly;
    private long passiveDeadline, passiveLastTimestamp;
    private readonly Func<long> timestamp = timestamp ?? System.Diagnostics.Stopwatch.GetTimestamp;
    private readonly Func<TimeSpan, CancellationToken, Task> passiveDelay = passiveDelay
        ?? ((duration, cancellation) => Task.Delay(duration, cancellation));
    private readonly long timestampFrequency = timestampFrequency == 0 ? System.Diagnostics.Stopwatch.Frequency : timestampFrequency;
    private PortraitOwnedSceneLease? lease;
    private Action<IPortraitOwnedSceneLease>? advance;
    private Func<IPortraitOwnedSceneLease, bool>? retire;
    private PortraitIdentity source;
    private uint requestedSlot, sourceNativeSlot, lastFrame;
    private volatile bool installed, installing, closing, stopped;
    private bool hasFrame, retirementInProgress, unknownCreation;
    private long epoch, requestEpoch;
    private int phaseDepth, phaseThread, originalDepth;
    public string? LastError { get; private set; }
    public bool IsInstalled => installed && Volatile.Read(ref publishedHooks).All(hook => hook.IsEnabled && !hook.IsDisposed);

    public bool TryInstall(IPortraitOwnedSceneHookFactory factory) => TryInstallCore(factory, false, TimeSpan.Zero);
    public bool TryInstallPassive(IPortraitOwnedSceneHookFactory factory, TimeSpan duration)
        => duration > TimeSpan.Zero && duration <= TimeSpan.FromSeconds(5) && TryInstallCore(factory, true, duration);
    private bool TryInstallCore(IPortraitOwnedSceneHookFactory factory, bool passive, TimeSpan duration)
    {
        if (installed || installing || stopped || closing || reservation is not null || hooks.Count != 0 || !environment.IsFrameworkThread) return false;
        installing = true;
        try
        {
            var reason = "Passive owned transport requires a qualified official factory.";
            if (!passive && !environment.TryValidateInstallation(out reason)) { LastError = reason; return false; }
            if (!NativeAbiMatchesSDK(out reason)) { LastError = reason; return false; }
            var targets = environment.Targets.ToArray();
            if (!ValidateTargetInventory(targets)) { LastError = "Native target inventory is incomplete or duplicated."; return false; }
            var qualified = factory as IPortraitOwnedSceneQualifiedHookFactory;
            if (passive && qualified is null) { LastError = reason; return false; }
            if (qualified is not null && !qualified.TryQualifyTransport(targets, out reason)) { LastError = reason; return false; }
            if (passive && (timestampFrequency <= 0 || timestampFrequency > long.MaxValue / 300))
            { LastError = "A bounded passive clock is unavailable."; return false; }
            // The passive observer and every owned generation share this atomic process
            // reservation. A query before Create is insufficient: another owner can win
            // the overlapping Render entry between that query and native construction.
            if (!reservations.TryAcquireBatch(targets.Select(target => target.Address).ToArray(), out reservation, out reason))
            { LastError = reason; return false; }
            physicalRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);
            owningFactory = factory; passiveOnly = passive;
            if (passive)
            {
                passiveLastTimestamp = timestamp(); passiveDeadline = checked(passiveLastTimestamp + (long)(duration.TotalSeconds * timestampFrequency));
                passiveExpiryTask = ExpirePassiveAsync(duration);
            }
            if (qualified is not null && !qualified.TryVerifyUnmodifiedTargets(targets, out reason))
                throw new InvalidOperationException(reason);
            // Exit hooks are first in the immutable inventory. Create all disabled,
            // then enable all; no lease can be requested during partial enabling.
            foreach (var target in targets)
            {
                if (closing) throw new InvalidOperationException("Native installation was closed before the next Create.");
                CreateHook(factory, target);
            }
            if (closing) throw new InvalidOperationException("Native installation was closed before enabling.");
            if (qualified is not null && !qualified.TryVerifyUnmodifiedTargets(targets, out reason))
                throw new InvalidOperationException(reason);
            foreach (var hook in hooks)
            {
                if (closing) throw new InvalidOperationException("Native installation was closed during partial enabling.");
                hook.Enable();
                if (closing) throw new InvalidOperationException("Native installation was closed during partial enabling.");
            }
            if (hooks.Any(hook => !hook.IsEnabled || hook.IsDisposed)) throw new InvalidOperationException("Native hook did not enable.");
            installed = true;
            installation = Guid.NewGuid();
            if (passiveOnly)
            {
                Volatile.Write(ref passiveContextCollector, new PassiveContextCollector(installation));
                Volatile.Write(ref passiveNativeCollector, new PassiveNativeCollector(installation));
            }
            (environment as IPortraitOwnedSceneContextLifetimeEnvironment)?.BeginContextLifetimeBatch();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message; stopped = true;
            if (!DisposeHooks()) retirementTask ??= DriveRetirementAsync();
            return false;
        }
        finally
        {
            installing = false;
            // Sub-millisecond timers may complete synchronously during install.
            // Their close latch must still retire after the installation guard exits.
            if (passive && closing) TryDispose();
        }
    }

    public bool TryRequest(PortraitIdentity requestedSource, uint slot, Action<IPortraitOwnedSceneLease> onAdvance,
        Func<IPortraitOwnedSceneLease, bool> onRetire)
    {
        if (passiveOnly || !IsInstalled || closing || stopped || advance is not null || !requestedSource.IsValid
            || requestedSource.Generation == 0 || slot >= 8 || !environment.IsFrameworkThread) return false;
        var sourceSlot = uint.MaxValue;
        if (environment is IPortraitOwnedSceneHookPhaseEnvironment scoped)
        {
            // A Framework request carries detached identity only. Reading native slot
            // state here would require manufacturing a phase before source Render.
            if (!scoped.QueueRequest(requestedSource, slot, epoch + 1)) return false;
        }
        else if (!environment.IsSourceCurrent(requestedSource) || !environment.IsSlotIdle(slot)
            || !environment.TryReadViewSlot(requestedSource.Model, out sourceSlot) || sourceSlot >= 8 || sourceSlot == slot)
            return false;
        source = requestedSource; sourceNativeSlot = sourceSlot; requestedSlot = slot; advance = onAdvance; retire = onRetire;
        hasFrame = false; requestEpoch = ++epoch; return true;
    }

    private void CreateHook(IPortraitOwnedSceneHookFactory factory, PortraitOwnedSceneNativeTarget target)
    {
        switch (target.Kind)
        {
            case PortraitOwnedSceneNativeKind.ConstructContext:
                Add<ConstructContext>(factory, target, original => context => ObservePassiveNative(target, context, 0,
                    0, 0, 0, 0, 0, () => {
                    var invocation = BeginPassiveContextEvent(); nint result = 0; var returned = false;
                    try
                    {
                        Original(() => result = original()(context)); returned = true;
                        ObserveLifetime(() => (environment as IPortraitOwnedSceneContextLifetimeEnvironment)?.ObserveContextConstructed(context, result));
                        return result;
                    }
                    finally { CompletePassiveContextEvent(invocation, PortraitOwnedScenePassiveContextEventKind.Construct, context, 0, result, returned); }
                }, result => (long)result)); break;
            case PortraitOwnedSceneNativeKind.DestroyContext:
                Add<DestroyContext>(factory, target, original => (context, flags) => ObservePassiveNative(target, context, 1,
                    flags, 0, 0, 0, 0, () => {
                    var invocation = BeginPassiveContextEvent(); nint result = 0; var returned = false;
                    try
                    {
                        ObserveLifetime(() => (environment as IPortraitOwnedSceneContextLifetimeEnvironment)?.ObserveContextDestroying(context));
                        Original(() => result = original()(context, flags)); returned = true; return result;
                    }
                    finally { CompletePassiveContextEvent(invocation, PortraitOwnedScenePassiveContextEventKind.Destroy, context, flags, result, returned); }
                }, result => (long)result)); break;
            case PortraitOwnedSceneNativeKind.ConfigureContext:
                Add<ConfigureContext>(factory, target, original => (context, slot, type) => ObservePassiveNative(target, context, 2,
                    slot, type, 0, 0, 0, () => {
                    BeforeContext(context, ContextMutationKind.Configure, slot, type);
                    byte result = 0; Original(() => result = original()(context, slot, type)); return result;
                }, result => result)); break;
            case PortraitOwnedSceneNativeKind.ConfigureDefaultContext:
                Add<ConfigureDefaultContext>(factory, target, original => (context, slot) => ObservePassiveNative(target, context, 1,
                    slot, 0, 0, 0, 0, () => {
                    BeforeContext(context, ContextMutationKind.ConfigureDefault, slot, 0);
                    byte result = 0; Original(() => result = original()(context, slot)); return result;
                }, result => result)); break;
            case PortraitOwnedSceneNativeKind.CleanupContext:
                Add<CleanupContext>(factory, target, original => context => ObservePassiveNative(target, context, 0,
                    0, 0, 0, 0, 0, () => {
                    BeforeContext(context, ContextMutationKind.Cleanup, null, null);
                    Original(() => original()(context)); return 0L;
                }, result => result)); break;
            case PortraitOwnedSceneNativeKind.Render:
                Add<CharaView.Delegates.Render>(factory, target, original => (view, frame) => ObservePassiveNative(target, (nint)view, 1,
                    frame, 0, 0, 0, 0, () => { Render(view, frame, original); return 0L; }, result => result)); break;
            case PortraitOwnedSceneNativeKind.Initialize:
                Add<CharaView.Delegates.Initialize>(factory, target, original => (view, agent, slot, callback) => ObservePassiveNative(target, (nint)view, 3,
                    (long)agent, slot, (long)callback, 0, 0, () => {
                    BeforeNative((nint)view, slot, false); Original(() => original()(view, agent, slot, callback)); return 0L;
                }, result => result)); break;
            case PortraitOwnedSceneNativeKind.Setup:
                Add<CharaViewPortrait.Delegates.Setup>(factory, target, original => (view, slot, data, agent, eventID, eventKind) => ObservePassiveNative(target, (nint)view, 5,
                    slot, (long)data, (long)agent, eventID, unchecked((long)eventKind), () => {
                    BeforeNative((nint)view, slot, false); Original(() => original()(view, slot, data, agent, eventID, eventKind)); return 0L;
                }, result => result)); break;
            case PortraitOwnedSceneNativeKind.Release:
                Add<CharaView.Delegates.Release>(factory, target, original => view => ObservePassiveNative(target, (nint)view, 0,
                    0, 0, 0, 0, 0, () => { BeforeNative((nint)view, null, true); Original(() => original()(view)); return 0L; }, result => result)); break;
            case PortraitOwnedSceneNativeKind.Dtor:
                Add<CharaView.Delegates.Dtor>(factory, target, original => (view, flags) => (CharaView*)ObservePassiveNative(target, (nint)view, 1,
                    flags, 0, 0, 0, 0, () => {
                    BeforeNative((nint)view, null, true); CharaView* result = null;
                    Original(() => result = original()(view, flags)); return (nint)result;
                }, result => (long)result)); break;
            case PortraitOwnedSceneNativeKind.Update:
                Add<CharaView.Delegates.Update>(factory, target, original => (view, frame, character) => ObservePassiveNative(target, (nint)view, 2,
                    frame, (long)character, 0, 0, 0, () => { BeforeNative((nint)view, null, false); Original(() => original()(view, frame, character)); return 0L; }, result => result)); break;
            case PortraitOwnedSceneNativeKind.CreateClientObject:
                Add<ClientObjectManager.Delegates.CreateBattleCharacter>(factory, target, original => (manager, index, param) => ObservePassiveNative(target, (nint)manager, 2,
                    index, param, 0, 0, 0, () => { BeforeClientObject(index); uint result = 0; Original(() => result = original()(manager, index, param)); return result; }, result => result)); break;
            case PortraitOwnedSceneNativeKind.DeleteClientObject:
                Add<ClientObjectManager.Delegates.DeleteObjectByIndex>(factory, target, original => (manager, index, param) => ObservePassiveNative(target, (nint)manager, 2,
                    index, param, 0, 0, 0, () => { BeforeClientObject(index); Original(() => original()(manager, index, param)); return 0L; }, result => result)); break;
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
        allHooksPhysical &= hook is IPortraitOwnedScenePhysicalHook;
        Volatile.Write(ref publishedHooks, hooks.ToArray());
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
        var baseline = BeginPassiveReturn((nint)view);
        BeforeNative((nint)view, null, false);
        var returned = false;
        try { Original(() => original()(view, frame)); returned = true; }
        finally { CompletePassiveReturn(baseline, (nint)view, frame, returned); }
        try { AfterSourceRender((nint)view, frame); }
        catch (Exception ex) { StopUnsafe("Native source-phase observation failed: " + ex.Message); }
    }

    private void AfterSourceRender(nint view, uint frame)
    {
        // A Render recursively entered by another native original is not a new
        // scheduling phase. ImGui frame counts never enter this code path.
        if (view != source.Model || originalDepth != 0 || phaseDepth != 0
            || advance is null || stopped || !IsInstalled || !environment.IsFrameworkThread) return;
        if (hasFrame && lastFrame == frame) return;
        using var nativePhase = (environment as IPortraitOwnedSceneHookPhaseEnvironment)?.EnterSourcePhase(source, frame, requestEpoch);
        if (environment is IPortraitOwnedSceneHookPhaseEnvironment && nativePhase is null)
        { StopUnsafe("The native source phase could not be qualified."); return; }
        if (!environment.IsSourceCurrent(source)
            || !environment.TryReadViewSlot(source.Model, out var currentSourceSlot)
            || currentSourceSlot >= 8 || currentSourceSlot == requestedSlot
            || sourceNativeSlot != uint.MaxValue && currentSourceSlot != sourceNativeSlot)
        { StopUnsafe("Source allocation or slot changed."); return; }
        sourceNativeSlot = currentSourceSlot;
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

    private void ObserveLifetime(Action observe)
    {
        try { observe(); }
        catch (Exception error) { StopUnsafe("Native context lifetime observation failed: " + error.Message); }
    }

    private void BeforeContext(nint context, ContextMutationKind kind, uint? slot, uint? type)
    {
        try
        {
            var current = lease;
            if (current is null || stopped) return;
            using var nativePhase = EnterRetirementNativePhase();
            if (environment is not IPortraitOwnedSceneContextEnvironment contexts
                || environment is IPortraitOwnedSceneHookPhaseEnvironment && nativePhase is null
                || !contexts.TryReadContext(context, current.BoundView, current.IsInOwnedCall, out var evidence)
                || evidence.RequestEpoch != requestEpoch || evidence.ManagerGeneration <= 0
                || evidence.PhaseToken <= 0 || evidence.CharacterGeneration == 0
                || !contexts.IsContextEvidenceCurrent(context, evidence))
            { StopUnsafe("Unknown native context identity; owned Release is forbidden."); return; }
            var exactSelf = evidence.Role == PortraitOwnedSceneContextRole.Owned
                && evidence.View == current.BoundView && evidence.AllocationEpoch > 0 && current.IsInOwnedCall;
            if (exactSelf) return;
            var mutation = new ContextMutation(context, kind, evidence.State, slot, type);
            if (!mutation.Touches(current.Slot) && !mutation.Touches(sourceNativeSlot)) return;
            // Release can tear down the receiver of the imminent native Original.
            // An active own Original can likewise still be using its view/context.
            // Neither is safe to destroy from a reentrant takeover callback.
            if (evidence.Role == PortraitOwnedSceneContextRole.Owned || current.HasOpenOwnedCall)
            { StopUnsafe("Native context receiver or active owned call must not be freed before its Original."); return; }
            RetireBeforeNative();
        }
        catch (Exception error) { StopUnsafe("Native context observation failed: " + error.Message); }
    }

    private void BeforeClientObject(uint index)
    {
        try
        {
            if (lease is null || (index != uint.MaxValue && index != 0xF0 + lease.Slot && index != 0xF0 + sourceNativeSlot)) return;
            using var nativePhase = EnterRetirementNativePhase();
            if (environment is IPortraitOwnedSceneHookPhaseEnvironment && nativePhase is null)
            { StopUnsafe("The client-object retirement phase could not be qualified."); return; }
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
            using var nativePhase = EnterRetirementNativePhase();
            if (environment is IPortraitOwnedSceneHookPhaseEnvironment && nativePhase is null)
            { StopUnsafe("The native retirement observation phase could not be qualified."); return; }
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
        using var nativePhase = EnterRetirementNativePhase();
        if (environment is IPortraitOwnedSceneHookPhaseEnvironment && nativePhase is null)
        { StopUnsafe("The native retirement phase could not be qualified."); return; }
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

    private IDisposable? EnterRetirementNativePhase()
        => (environment as IPortraitOwnedSceneHookPhaseEnvironment)?.EnterRetirementPhase(source, lastFrame, requestEpoch);

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

    internal bool IsNativeCallPhaseCurrent(PortraitOwnedSceneLease current) => IsLeasePhaseCurrent(current);

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
        (environment as IPortraitOwnedSceneHookPhaseEnvironment)?.EndRequest(requestEpoch);
        requestEpoch = 0;
        lease?.Invalidate(); lease = null; advance = null; retire = null; source = default; epoch++;
    }

    public void RequestStop() => closing = true;
    public void UpdatePassive()
    {
        if (!passiveOnly) return;
        if (closing) { TryDispose(); return; }
        if (!installed) return;
        try
        {
            var now = timestamp();
            if (now < passiveLastTimestamp || now >= passiveDeadline) { TryDispose(); return; }
            passiveLastTimestamp = now;
        }
        catch (Exception error) { LastError = error.Message; TryDispose(); }
    }
    public Task DrainPhysicalAsync() => physicalRetired.Task;
    public bool TryDispose()
    {
        closing = true;
        expiryCancellation.Cancel();
        RevokePassiveBaseline("The owned native generation is closing.");
        if (installing || phaseDepth != 0 || Volatile.Read(ref originalDepth) != 0 && !allHooksPhysical)
        { LastError = "Native installation or an active original/phase must return before hook retirement."; return false; }
        if (lease is { WasReleased: false, BoundView: not 0 })
        { LastError = "Owned native scene must retire in its native phase before hook disposal."; return false; }
        ClearRequest(); var complete = DisposeHooks();
        if (!complete) retirementTask ??= DriveRetirementAsync();
        return complete;
    }
    public void Dispose() => TryDispose();
    private bool DisposeHooks()
    {
        if (Interlocked.CompareExchange(ref retirementDriving, 1, 0) != 0) return physicalRetired.Task.IsCompletedSuccessfully;
        try { return DisposeHooksCore(); }
        finally { Volatile.Write(ref retirementDriving, 0); }
    }
    private bool DisposeHooksCore()
    {
        installed = false;
        if (phaseDepth != 0 || Volatile.Read(ref originalDepth) != 0 && !allHooksPhysical)
        { LastError = "An active native original/phase still owns the hook batch."; return false; }
        var complete = true;
        // Clear every forward before disabling any entry. A copied dispatcher
        // remains tracked through prefix, Original and completion by its gateway.
        foreach (var physical in hooks.OfType<IPortraitOwnedScenePhysicalHook>()) physical.DetachForward();
        foreach (var physical in hooks.OfType<IPortraitOwnedScenePhysicalHook>())
            if (!physical.BeginRetirement(out var reason)) { complete = false; LastError = reason; }
        for (var i = hooks.Count - 1; i >= 0; i--)
        {
            try
            {
                var hook = hooks[i];
                if (hook.IsDisposed) continue;
                if (hook is IPortraitOwnedScenePhysicalHook physical)
                {
                    if (!physical.TryCompleteRetirement(out var reason)) { complete = false; LastError = reason; }
                    continue;
                }
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
        Volatile.Write(ref publishedHooks, []);
        ObserveLifetime(() => (environment as IPortraitOwnedSceneContextLifetimeEnvironment)?.EndContextLifetimeBatch());
        owningFactory = null;
        physicalRetired.TrySetResult();
        return true;
    }
    private static TaskCompletionSource CompletedRetirement()
    { var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); completion.SetResult(); return completion; }
}
