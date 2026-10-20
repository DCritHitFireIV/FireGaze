#if HAS_OWNED_JOINT_HOST
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;
using RTManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;

internal static unsafe class JointPhysicalHostCases
{
    private static int checks;
    private static long nextBase = 0x9A0000000L;
    private static Fixture? current;
    private static readonly PortraitIdentity Source = new(11, 22, 33, 7, 44);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException("FAIL joint " + message); checks++; }
    internal static int Run()
    {
        try
        {
            ActiveStopUnbound(); ActiveOwnedChain("none"); ActiveOwnedChain("none", true); ActiveOwnedChain("wrong-operation"); ActiveOwnedChain("wrong-manager"); ActiveOwnedChain("foreign-slot"); ActiveOwnedChain("resource"); ActiveDispatch(); SharedCallbackAndGuards(); EpochAndNestedSource(); PartialInstall();
            PhysicalDrain("original"); PhysicalDrain("completion");
            QualificationAndFailure(); OriginalFailureAndAuthority(); FinalDeadline(); Expiration(); AutomaticRevocationCancellation(); FailureRevocationCancellation();
            Console.WriteLine($"PASS {checks} joint physical host checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void ActiveStopUnbound()
    {
        using var f = new Fixture(true);
        Check(f.Host.TryStartActive(f.Plan, Source, 7, 9, Fixture.Expected, TimeSpan.FromSeconds(2), out var reason), reason);
        var retired = false;
        Check(f.Host.TryRequest(Source, 6, _ => throw new InvalidOperationException("stopped experiment advanced"), lease => {
            retired = true;
            Check(!lease.CanCreate && !lease.TryBindCreatedView(777) && !f.Host.IsActiveAdvanceAdmitted,
                "retirement-only unbound lease cannot create or admit images");
            return true;
        }), "unbound experiment queues");
        f.Host.RequestStop();
        f.Call<CharaView.Delegates.Render>(PortraitNativeObservedPhase.SourceCharaViewRender)((CharaView*)Source.Model, 100);
        Check(retired && f.Host.TryStop() && f.Host.DrainPhysicalAsync().IsCompletedSuccessfully, "Stop before first source retires the detached request without creation");
    }
    private static void ActiveOwnedChain(string injection, bool stopDuringRender = false)
    {
        using var f = new Fixture(true);
        Check(f.Host.TryStartActive(f.Plan, Source, 7, 9, Fixture.Expected, TimeSpan.FromSeconds(2), out var reason), reason);
        IPortraitOwnedSceneLease? currentLease = null;
        const int ownView = 777;
        var setup = f.CallNative<CharaViewPortrait.Delegates.Setup>(PortraitOwnedSceneNativeKind.Setup);
        var render = f.Call<CharaView.Delegates.Render>(PortraitNativeObservedPhase.SourceCharaViewRender);
        var add = f.Call<PortraitNativeSlotCounter>(PortraitNativeObservedPhase.CounterAdd);
        var sub = f.Call<PortraitNativeSlotCounter>(PortraitNativeObservedPhase.CounterSub);
        var release = f.CallNative<CharaView.Delegates.Release>(PortraitOwnedSceneNativeKind.Release);
        var dtor = f.CallNative<CharaView.Delegates.Dtor>(PortraitOwnedSceneNativeKind.Dtor);
        var inSetup = false; var inRender = false; var nested = false; var advances = 0; var retires = 0;
        f.DuringOriginal = address => {
            if (address == f.NativeAddress(PortraitOwnedSceneNativeKind.Setup) && inSetup)
            {
                f.Env.OwnedView = ownView;
                if (injection == "wrong-operation") release((CharaView*)ownView);
                if (injection == "resource") f.Call<PortraitNativeResourceCleanup>(PortraitNativeObservedPhase.ResourceCleanup)((RTManager*)456);
                add((RenderManager*)(injection == "wrong-manager" ? 124 : 123), injection == "foreign-slot" ? 5u : 6u);
            }
            else if (address == f.Address(PortraitNativeObservedPhase.CounterAdd) || address == f.Address(PortraitNativeObservedPhase.CounterSub))
            {
                Check(currentLease is not null && !currentLease.IsCurrent, "counter pre-Original denies all old lease reads " + injection);
                if (injection == "none")
                {
                    Check(f.Env.Blocked > 0, "precise own counter blocks native reads throughout Original");
                    if (!nested) { nested = true; sub((RenderManager*)123, 6); nested = false; }
                    Check(f.Env.Blocked > 0 && !currentLease!.IsCurrent, "inner return cannot reopen the outer native write");
                }
            }
            else if (address == f.Address(PortraitNativeObservedPhase.SourceCharaViewRender) && inRender)
            { if (stopDuringRender) f.Host.RequestStop(); sub((RenderManager*)123, 6); }
            else if (address == f.NativeAddress(PortraitOwnedSceneNativeKind.Release) && retires > 0)
            { f.Env.OwnedView = 0; sub((RenderManager*)123, 6); }
        };
        Check(f.Host.TryRequest(Source, 6, lease => {
            currentLease = lease; advances++;
            if (advances == 1)
            {
                Check(lease.TryBindCreatedView(ownView), "exact active allocation binds");
                using (var construct = lease.EnterCreationCall(ownView, PortraitOwnedSceneCreationCall.Construct))
                    Check(construct is not null && lease.ConfirmConstructed(ownView), "exact active construction confirms");
                using (var setupCall = lease.EnterCreationCall(ownView, PortraitOwnedSceneCreationCall.Setup))
                {
                    Check(setupCall is not null, "exact Setup capability enters");
                    inSetup = true; setup((CharaViewPortrait*)ownView, 6, null, null, 0, 0); inSetup = false;
                    Check(lease.ConfirmSetup(ownView) == (injection == "none"), "Setup returns with pinned ownership or conservative denial " + injection);
                }
            }
            else
            {
                using var own = lease.EnterNativeCall(ownView, PortraitOwnedSceneNativeCall.Render);
                Check(own is not null, "exact Render capability enters");
                inRender = true; render((CharaView*)ownView, lease.NativeFrame); inRender = false;
                Check(lease.CanAccess(ownView), "own Render counter resumes a fresh source/slot checked phase");
            }
        }, lease => {
            currentLease = lease; retires++;
            using var own = lease.EnterOwnedCall(ownView);
            Check(own is not null && lease.TryBeginRetirement(ownView), "retirement-only phase pins exact live owner");
            using (var call = lease.EnterNativeCall(ownView, PortraitOwnedSceneNativeCall.Release))
            { Check(call is not null, "typed Release capability enters"); release((CharaView*)ownView); }
            using (var call = lease.EnterNativeCall(ownView, PortraitOwnedSceneNativeCall.Dtor))
            { Check(call is not null, "typed Dtor capability enters"); _ = dtor((CharaView*)ownView, 1); }
            return lease.MarkReleased(ownView);
        }), "active owned chain queues " + injection);
        render((CharaView*)Source.Model, 100);
        if (injection != "none")
        {
            Check(f.Env.Resumes == 0 && retires == 0 && !currentLease!.IsCurrent, "foreign mutation cannot resume or recursively release " + injection);
            Check(!f.Host.TryStop() && !f.Host.DrainPhysicalAsync().IsCompleted, "uncertain native owner remains quarantined " + injection);
            return;
        }
        Check(f.Env.Resumes == 1 && advances == 1 && retires == 0, "first callback creates and outer counter alone resumes");
        render((CharaView*)Source.Model, 101);
        Check(advances == 2 && f.Host.Snapshot().SourceReturns.Count == 2, "owned recursive Render does not poison the real source tree");
        if (!stopDuringRender)
        {
            f.Now = 2001; // Recorder/advance deadline passes before the next real source callback.
            render((CharaView*)Source.Model, 102);
        }
        Check(advances == 2 && retires == 1 && !currentLease!.IsCurrent, "deadline permits only actual source-return retirement without another advance");
        Check(f.Host.TryStop() && f.Host.DrainPhysicalAsync().IsCompletedSuccessfully, "native retirement then physically drains the exact joint batch: " + f.Host.Snapshot() + " / " + ((PortraitOwnedSceneHooks)typeof(PortraitOwnedSceneJointHost).GetField("hooks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Host)!).LastError);
    }
    private static void ActiveDispatch()
    {
        using var f = new Fixture(true);
        Check(f.Host.TryStartActive(f.Plan, Source, 7, 9, Fixture.Expected, TimeSpan.FromSeconds(2), out var reason), reason);
        IPortraitOwnedSceneLease? saved = null;
        var advances = 0;
        Check(f.Host.TryRequest(Source, 6, lease => { saved = lease; advances++; Check(lease.CanCreate, "active callback grants an idle phase lease"); }, _ => true), "active detached request queues");
        Check(advances == 0, "Framework queue cannot issue an active lease");
        f.Call<CharaView.Delegates.Render>(PortraitNativeObservedPhase.SourceCharaViewRender)((CharaView*)Source.Model, 101);
        Check(advances == 1 && saved is not null && !saved.IsCurrent, "same-stack source return dispatches once and cannot escape phase");
    }
    private static void SharedCallbackAndGuards()
    {
        using var f = new Fixture();
        Check(f.Start(), f.Reason);
        var s = f.Host.Snapshot();
        Check(f.Provider.Created.Count == 46 && f.Provider.Created.Select(h => h.Address).Distinct().Count() == 46
            && f.Plan.Targets.All(t => f.Reservations.IsReserved(t.Address)), "one complete 46-address batch precedes every native Create");
        Check(s.Installation != Guid.Empty && s.SessionEpoch == 7 && s.RequestEpoch == 9 && !s.ProductionAdmission,
            "one physical installation carries the exact detached session/request without scene admission");
        var render = f.Call<CharaView.Delegates.Render>(PortraitNativeObservedPhase.SourceCharaViewRender);
        f.DuringOriginal = address => Check(address != f.Address(PortraitNativeObservedPhase.SourceCharaViewRender)
            || f.Host.Snapshot().SourceReturns.Count == 0, "source evidence cannot publish before its sole actual Original returns");
        render((CharaView*)Source.Model, 0);
        s = f.Host.Snapshot();
        Check(f.Originals(f.Address(PortraitNativeObservedPhase.SourceCharaViewRender)) == 1
            && f.Provider.Created.Count(h => h.Address == f.Address(PortraitNativeObservedPhase.SourceCharaViewRender)) == 1,
            "shared Render creates one physical hook and calls Original exactly once");
        Check(s.SourceReturns.Count == 1 && s.SourceReturns[0].Installation == s.Installation
            && s.SourceReturns[0].Source == Source && s.SourceReturns[0].NativeFrame == 0
            && s.SourceReturns[0].SessionEpoch == 7 && s.SourceReturns[0].RequestEpoch == 9,
            "actual callback authority emits current source-return evidence including native frame zero");
        f.DuringOriginal = _ => Check(f.GuardDepth == 1 && f.Notices.Count > 0
            && f.Notices[^1].Installation == f.Host.Snapshot().Installation && f.Notices[^1].SessionEpoch == 7 && f.Notices[^1].RequestEpoch == 9,
            "same-installation detached subscriber and guard both revoke before the actual Original");
        f.Call<PortraitNativeSlotCounter>(PortraitNativeObservedPhase.CounterAdd)((RenderManager*)123, 7);
        f.Call<PortraitNativeSlotCounter>(PortraitNativeObservedPhase.CounterSub)((RenderManager*)123, 2);
        f.Call<PortraitNativeResourceCleanup>(PortraitNativeObservedPhase.ResourceCleanup)((RTManager*)456);
        var recreate = f.Call<PortraitNativeResourceRecreate>(PortraitNativeObservedPhase.ResourceRecreate);
        var native = (delegate* unmanaged[Cdecl]<RTManager*, byte>)Marshal.GetFunctionPointerForDelegate(recreate);
        Check(native((RTManager*)456) == 0xE7, "real native resource detour preserves every AL bit of its byte result");
        s = f.Host.Snapshot();
        Check(f.GuardDepth == 0 && f.GuardEntries == 4 && s.CounterDepth == 0 && s.ResourceDepth == 0
            && s.MutationEpoch >= 8 && s.Resource.NativeReturn == 0xE7 && s.Resource.Receiver == 456,
            "counter/resource guards close after Original and preserve detached resource result and epochs");
        f.DuringOriginal = null;
        var entries = f.GuardEntries;
        f.Call<PortraitNativeSlotCounter>(PortraitNativeObservedPhase.CounterAdd)((RenderManager*)999, 8);
        Check(f.GuardEntries == entries && f.Originals(f.Address(PortraitNativeObservedPhase.CounterAdd)) == 2,
            "out-of-domain native slot preserves exact Original without inventing a mutation guard");
        f.Call<RenderManager.Delegates.Render>(PortraitNativeObservedPhase.ManagerRender)((RenderManager*)1001);
        f.Call<PortraitNativeManagerUpdateCandidate>(PortraitNativeObservedPhase.ManagerUpdateCandidate)((RenderManager*)1002, -3.25f);
        f.Call<PortraitNativeSceneCameraUpdate>(PortraitNativeObservedPhase.SceneCameraUpdate)((SceneCameraManager*)1003);
        s = f.Host.Snapshot();
        Check(s.Observation.Records.Count(r => r.Phase == PortraitNativeObservedPhase.SourceCharaViewRender) == 2
            && s.Observation.Records.Any(r => r.Phase == PortraitNativeObservedPhase.ManagerUpdateCandidate && r.FloatArgument == -3.25f),
            "same recorder observes shared source and all seven unique typed phase entries");
        Check(f.Host.TryStop() && f.Host.DrainPhysicalAsync().IsCompletedSuccessfully
            && f.Plan.Targets.All(t => !f.Reservations.IsReserved(t.Address)), "ordinary stop physically retires and releases the whole joint batch");
        render((CharaView*)Source.Model, 1);
        Check(f.Originals(f.Address(PortraitNativeObservedPhase.SourceCharaViewRender)) == 1 && f.Directs > 0
            && f.Host.Snapshot().SourceReturns.Count == 1, "late stable shared entry calls only direct target and cannot issue source evidence");
        GC.KeepAlive(recreate);
    }
    private static void EpochAndNestedSource()
    {
        using var f = new Fixture(); Check(f.Start(), f.Reason);
        var render = f.Call<CharaView.Delegates.Render>(PortraitNativeObservedPhase.SourceCharaViewRender);
        var add = f.Call<PortraitNativeSlotCounter>(PortraitNativeObservedPhase.CounterAdd);
        f.DuringOriginal = address => { if (address == f.Address(PortraitNativeObservedPhase.SourceCharaViewRender)) add((RenderManager*)123, 0); };
        render((CharaView*)Source.Model, 10);
        Check(f.Host.Snapshot().SourceReturns.Count == 0 && f.GuardDepth == 0,
            "mutation inside source Original invalidates that return rather than borrowing historical health");
        f.DuringOriginal = null; render((CharaView*)Source.Model, 11);
        Check(f.Host.Snapshot().SourceReturns is { Count: 1 } returns && returns[0].NativeFrame == 11
            && returns[0].MutationEpoch > 0, "a newer actual source invocation can observe the new idle mutation epoch");
        var nested = false;
        f.DuringOriginal = address => { if (!nested && address == f.Address(PortraitNativeObservedPhase.SourceCharaViewRender))
            { nested = true; render((CharaView*)(Source.Model + 1), 13); } };
        render((CharaView*)Source.Model, 12);
        Check(f.Host.Snapshot().SourceReturns.Count == 1, "nested foreign Render poisons the enclosing source-return tree");
        f.DuringOriginal = null; f.Stamp = new(4321, 99); render((CharaView*)Source.Model, 14);
        Check(f.Host.Snapshot().SourceReturns.Count == 1, "off-thread source callback cannot open a qualified return marker");
        f.Stamp = Fixture.Expected; f.DuringOriginal = _ => f.RequestCurrent = false;
        render((CharaView*)Source.Model, 15);
        Check(f.Host.Snapshot().SourceReturns.Count == 1 && !f.Host.Snapshot().Recording,
            "a request epoch revoked during Original cannot publish after the callback returns");
    }
    private static void PartialInstall()
    {
        using (var f = new Fixture())
        {
            f.Provider.FailEnableAt = 41;
            Check(!f.Start(), "partial joint Enable returns failure");
            Check(f.Host.DrainPhysicalAsync().IsCompletedSuccessfully && f.Provider.Created.All(h => h.IsDisposed)
                && f.Plan.Targets.All(t => !f.Reservations.IsReserved(t.Address)),
                "partial Enable retires all returned consumer and phase hooks before releasing the reservation");
        }
        using (var f = new Fixture())
        {
            f.Provider.FailCreateAt = 42;
            Check(!f.Start() && !f.Host.DrainPhysicalAsync().IsCompleted,
                "unknown Create in extra ingress cannot masquerade as physical retirement");
            Check(f.Plan.Targets.All(t => f.Reservations.IsReserved(t.Address)) && f.Provider.Created.All(h => h.IsDisposed),
                "unknown partial Create retains all 46 addresses while compensating every returned handle");
        }
    }
    private static void PhysicalDrain(string pause)
    {
        using var f = new Fixture(); Check(f.Start(), f.Reason);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Action hold = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("joint held callback"); };
        if (pause == "original") f.DuringOriginal = _ => hold(); else f.OnGuardDispose = hold;
        var add = f.Call<PortraitNativeSlotCounter>(PortraitNativeObservedPhase.CounterAdd);
        var running = Task.Run(() => add((RenderManager*)123, 0));
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "native callback entered its " + pause);
        Check(!f.Host.TryStop() && !f.Host.DrainPhysicalAsync().IsCompleted && f.Host.Snapshot().InFlight == 1
            && f.Plan.Targets.All(t => f.Reservations.IsReserved(t.Address)),
            "whole batch remains reserved until complete physical callback exits " + pause);
        release.Set(); Check(running.Wait(TimeSpan.FromSeconds(5)) && f.Host.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)),
            "asynchronous physical retirement completes after " + pause);
        Check(f.GuardDepth == 0 && f.Plan.Targets.All(t => !f.Reservations.IsReserved(t.Address)),
            "mutation finally and full reservation release complete after " + pause);
        var before = f.Originals(f.Address(PortraitNativeObservedPhase.CounterAdd));
        add((RenderManager*)123, 0);
        Check(f.Originals(f.Address(PortraitNativeObservedPhase.CounterAdd)) == before && f.Directs == 1,
            "late extra ingress contains neither old observer nor trampoline after " + pause);
    }
    private static void QualificationAndFailure()
    {
        using (var f = new Fixture())
        {
            f.BadResolution = true;
            Check(!f.Start() && f.Provider.Created.Count == 0 && f.Plan.Targets.All(t => !f.Reservations.IsReserved(t.Address)),
                "different SDK/module evidence rejects joint qualification before any Create or reservation");
        }
        using (var f = new Fixture())
        {
            Check(f.Start(), f.Reason); f.FailGuard = true;
            f.Call<PortraitNativeSlotCounter>(PortraitNativeObservedPhase.CounterAdd)((RenderManager*)123, 1);
            Check(f.Originals(f.Address(PortraitNativeObservedPhase.CounterAdd)) == 1 && f.Host.Snapshot().Failed,
                "guard failure still executes native Original exactly once and latches failed qualification");
            f.Call<CharaView.Delegates.Render>(PortraitNativeObservedPhase.SourceCharaViewRender)((CharaView*)Source.Model, 1);
            Check(f.Host.Snapshot().SourceReturns.Count == 0, "failed mutation guard forbids later source authority");
        }
        using (var f = new Fixture())
        {
            Check(f.Start(), f.Reason);
            f.OnNotice = _ => throw new InvalidOperationException("test failing managed subscriber");
            f.Call<PortraitNativeResourceCleanup>(PortraitNativeObservedPhase.ResourceCleanup)((RTManager*)123);
            Check(f.Originals(f.Address(PortraitNativeObservedPhase.ResourceCleanup)) == 1 && f.GuardDepth == 0
                && f.Host.Snapshot().Failed, "subscriber failure latches denial while guard finally and native Original both complete");
        }
        using (var f = new Fixture())
        {
            var pendingCopy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.OnNotice = notice => { if (notice.Kind == PortraitOwnedSceneJointMutationKind.Resource) pendingCopy.TrySetCanceled(); };
            Check(f.Start(), f.Reason);
            f.DuringOriginal = _ => Check(pendingCopy.Task.IsCanceled,
                "same-installation resource subscriber cancels pending managed copy before the actual native Original");
            f.Call<PortraitNativeResourceCleanup>(PortraitNativeObservedPhase.ResourceCleanup)((RTManager*)123);
        }
    }
    private static void OriginalFailureAndAuthority()
    {
        using var f = new Fixture(); Check(f.Start(), f.Reason);
        var failure = new InvalidOperationException("test original failure identity");
        f.DuringOriginal = _ => throw failure;
        Exception? actual = null;
        try { f.Call<CharaView.Delegates.Render>(PortraitNativeObservedPhase.SourceCharaViewRender)((CharaView*)Source.Model, 10); }
        catch (Exception error) { actual = error; }
        Check(ReferenceEquals(actual, failure) && f.Originals(f.Address(PortraitNativeObservedPhase.SourceCharaViewRender)) == 1
            && f.Host.Snapshot().SourceReturns.Count == 0 && f.Host.Snapshot().InFlight == 0,
            "source Original exception preserves identity, exact-once behavior and closes the private return marker");
        actual = null;
        try { _ = f.Call<PortraitNativeResourceRecreate>(PortraitNativeObservedPhase.ResourceRecreate)((RTManager*)123); }
        catch (Exception error) { actual = error; }
        Check(ReferenceEquals(actual, failure) && f.GuardDepth == 0 && f.Host.Snapshot().Resource.Transition == PortraitNativeResourceTransition.OriginalThrew
            && f.Originals(f.Address(PortraitNativeObservedPhase.ResourceRecreate)) == 1,
            "resource Original throw unwinds the mutation guard without retrying or inventing an AL result");
        f.DuringOriginal = null;
        var owner = (PortraitOwnedSceneHooks)typeof(PortraitOwnedSceneJointHost).GetField("hooks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Host)!;
        var key = new object();
        var forged = new PortraitOwnedSceneHooks.JointSourceInvocation(owner, key, f.Host.Snapshot().Installation,
            new(default, Source.Model, 10, 0, null, null));
        forged.Open(key, true);
        Check(owner.IsInstalled && !forged.IsReturningFor(owner),
            "a manual marker for the actual installed owner cannot open without its private physical callback authority");
    }
    private static void FinalDeadline()
    {
        foreach (var finalTime in new long[] { 2001, -1 })
        {
            using var f = new Fixture(); Check(f.Start(), f.Reason);
            var pendingCopy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.OnNotice = notice => { if (notice.Kind == PortraitOwnedSceneJointMutationKind.Revoked) pendingCopy.TrySetCanceled(); };
            var afterOriginal = false; var reads = 0;
            f.DuringOriginal = _ => afterOriginal = true;
            f.OnThreadRead = () => { if (afterOriginal && ++reads == 3) f.Now = finalTime; };
            f.Call<CharaView.Delegates.Render>(PortraitNativeObservedPhase.SourceCharaViewRender)((CharaView*)Source.Model, 10);
            Check(reads >= 3 && pendingCopy.Task.IsCanceled
                && f.Notices.Count(n => n.Kind == PortraitOwnedSceneJointMutationKind.Revoked) == 1,
                "the final publication time sample revokes pending managed work on expiry or clock reversal");
            Check(f.Host.Snapshot().SourceReturns.Count == 0,
                "final deadline sample follows recorder and thread checks so delayed return cannot publish stale bounded evidence");
        }
    }
    private static void Expiration()
    {
        using var f = new Fixture(); Check(f.Start(), f.Reason);
        f.Now = 2001;
        f.Call<CharaView.Delegates.Render>(PortraitNativeObservedPhase.SourceCharaViewRender)((CharaView*)Source.Model, 1);
        Check(f.Host.Snapshot().SourceReturns.Count == 0 && !f.Host.Snapshot().Recording,
            "callback deadline denies evidence independently of Framework polling");
        f.Expiry.TrySetResult();
        Check(f.Host.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5))
            && f.Plan.Targets.All(t => !f.Reservations.IsReserved(t.Address)),
            "bounded owner timer physically retires all 46 hooks without a Framework poll");
    }
    private static void AutomaticRevocationCancellation()
    {
        foreach (var cause in new[] { "request loss", "expiry" })
        {
            using var f = new Fixture();
            var pendingCopy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.OnNotice = notice => { if (notice.Kind == PortraitOwnedSceneJointMutationKind.Revoked) pendingCopy.TrySetCanceled(); };
            Check(f.Start(), f.Reason);
            if (cause == "request loss") f.RequestCurrent = false; else f.Now = 2001;
            f.DuringOriginal = _ => Check(pendingCopy.Task.IsCanceled,
                "automatic " + cause + " cancels pending managed copy before the actual native Original");
            f.Call<CharaView.Delegates.Render>(PortraitNativeObservedPhase.SourceCharaViewRender)((CharaView*)Source.Model, 1);
            Check(!f.Host.Snapshot().Recording && f.Notices.Count(n => n.Kind == PortraitOwnedSceneJointMutationKind.Revoked) == 1,
                "automatic " + cause + " emits one revocation notice");
            Check(f.Host.TryStop() && f.Notices.Count(n => n.Kind == PortraitOwnedSceneJointMutationKind.Revoked) == 1,
                "explicit stop after " + cause + " preserves exactly-once cancellation notification");
        }
    }
    private static void FailureRevocationCancellation()
    {
        foreach (var cause in new[] { "clock exception", "request exception", "null guard", "recorder clock exception", "snapshot expiry", "snapshot recorder expiry" })
        {
            using var f = new Fixture();
            var pendingCopy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.OnNotice = notice => { if (notice.Kind == PortraitOwnedSceneJointMutationKind.Revoked) pendingCopy.TrySetCanceled(); };
            Check(f.Start(), f.Reason);
            var reads = 0;
            if (cause == "clock exception") f.OnClockRead = () => throw new InvalidOperationException("clock failure");
            if (cause == "request exception") f.ThrowRequest = true;
            if (cause == "null guard") f.NullGuard = true;
            if (cause == "recorder clock exception") f.OnClockRead = () => { if (++reads == 2) throw new InvalidOperationException("recorder clock failure"); };
            if (cause == "snapshot expiry") f.Now = 2001;
            if (cause == "snapshot recorder expiry") f.OnClockRead = () => { if (++reads == 2) f.Now = 2001; };
            if (cause.StartsWith("snapshot", StringComparison.Ordinal)) _ = f.Host.Snapshot();
            else
            {
                f.DuringOriginal = _ => Check(pendingCopy.Task.IsCanceled,
                    cause + " cancels pending managed copy before the actual native Original");
                if (cause == "null guard") f.Call<PortraitNativeResourceCleanup>(PortraitNativeObservedPhase.ResourceCleanup)((RTManager*)123);
                else f.Call<RenderManager.Delegates.Render>(PortraitNativeObservedPhase.ManagerRender)((RenderManager*)123);
            }
            f.OnClockRead = null; f.ThrowRequest = false;
            Check(pendingCopy.Task.IsCanceled && !f.Host.Snapshot().Recording
                && f.Notices.Count(n => n.Kind == PortraitOwnedSceneJointMutationKind.Revoked) == 1,
                cause + " emits exactly one terminal cancellation notice");
            Check(f.Host.TryStop() && f.Notices.Count(n => n.Kind == PortraitOwnedSceneJointMutationKind.Revoked) == 1,
                "explicit stop after " + cause + " cannot duplicate the terminal notice");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly PortraitNativeThreadStamp Expected = new(1234, 7);
        internal readonly PortraitNativePhaseHostReservations Reservations = new();
        internal readonly PortraitOwnedSceneJointPlan Plan;
        internal readonly Provider Provider;
        internal readonly PortraitOwnedSceneJointHost Host;
        internal readonly Environment Env;
        internal readonly TaskCompletionSource Expiry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool RequestCurrent = true, BadResolution, FailGuard, ThrowRequest, NullGuard;
        internal long Now;
        internal PortraitNativeThreadStamp Stamp = Expected;
        internal int GuardDepth, GuardEntries, Directs;
        internal Action<nint>? DuringOriginal;
        internal Action? OnGuardDispose;
        internal Action? OnThreadRead;
        internal Action? OnClockRead;
        internal Action<PortraitOwnedSceneJointMutationNotice>? OnNotice;
        internal readonly List<PortraitOwnedSceneJointMutationNotice> Notices = [];
        internal string Reason = "";
        private readonly Dictionary<nint, int> originalCalls = [];
        internal Fixture(bool active = false)
        {
            current = this;
            Check(PortraitOwnedSceneJointPlan.TryCreate((nint)Interlocked.Add(ref nextBase, 0x10000000), out var plan, out _), "fixture frozen plan"); Plan = plan!;
            Provider = new(this);
            var factory = new DalamudPortraitOwnedSceneJointHookFactory(Provider, new OwnedPhysicalCases.Proof(), Provider, Resolve);
            Env = new(Plan.ModuleBase, active);
            Host = new(Env, factory, Reservations, Guard, Guard,
                () => ThrowRequest ? throw new InvalidOperationException("request failure") : RequestCurrent,
                () => { OnClockRead?.Invoke(); return Interlocked.Read(ref Now); }, 1000, () => { OnThreadRead?.Invoke(); return Stamp; }, (_, _) => Expiry.Task,
                notice => { Notices.Add(notice); OnNotice?.Invoke(notice); });
        }
        internal bool Start() => Host.TryStart(Plan, Source, 7, 9, Expected, TimeSpan.FromSeconds(2), out Reason);
        private IDisposable? Guard()
        {
            if (FailGuard) throw new InvalidOperationException("test failed detached guard");
            if (NullGuard) return null;
            Interlocked.Increment(ref GuardDepth); Interlocked.Increment(ref GuardEntries);
            return new GuardScope(() => { OnGuardDispose?.Invoke(); Interlocked.Decrement(ref GuardDepth); });
        }
        internal nint Address(PortraitNativeObservedPhase phase) => Plan.Targets.Single(t => t.Roles.Contains(new(null, phase))).Address;
        internal T Call<T>(PortraitNativeObservedPhase phase) where T : Delegate
        {
            var detour = Provider.Created.Single(h => h.Address == Address(phase)).Detour;
            return (T)Delegate.CreateDelegate(typeof(T), detour.Target, detour.Method);
        }
        internal nint NativeAddress(PortraitOwnedSceneNativeKind kind) => Plan.Targets.First(t => t.Roles.Contains(new(kind, null))).Address;
        internal T CallNative<T>(PortraitOwnedSceneNativeKind kind) where T : Delegate
        {
            var detour = Provider.Created.Single(h => h.Address == NativeAddress(kind)).Detour;
            return (T)Delegate.CreateDelegate(typeof(T), detour.Target, detour.Method);
        }
        internal int Originals(nint address) { lock (originalCalls) return originalCalls.GetValueOrDefault(address); }
        internal void Signal(nint address, bool direct)
        {
            if (direct) { Interlocked.Increment(ref Directs); return; }
            lock (originalCalls) originalCalls[address] = originalCalls.GetValueOrDefault(address) + 1;
            DuringOriginal?.Invoke(address);
        }
        private bool Resolve(PortraitNativePhaseTargetSet set, Func<nint, bool> reserved, out PortraitNativePhaseResolvedPlan? plan, out string reason)
        {
            var inventory = PortraitNativeCounterIngressContract.For(set);
            plan = new(Plan.ModuleBase, 0x3811000, PortraitNativePhaseObservationBinding.EXE,
                BadResolution ? "wrong SDK" : PortraitNativePhaseObservationBinding.SDK, Guid.Parse("c72f17a8-6ed1-4a30-a097-74137dc08f9e"),
                inventory.Select(t => new PortraitNativePhaseBoundTarget(t.Phase, Plan.ModuleBase + (nint)t.RVA, t.Delegate)).ToArray());
            reason = "test detached frozen resolution";
            return plan.Targets.All(t => !reserved(t.Address));
        }
        public void Dispose() => Host.TryStop();
    }
    private sealed class GuardScope(Action close) : IDisposable { public void Dispose() => close(); }
    private sealed class Environment(nint module, bool active) : IPortraitOwnedSceneHookEnvironment, IPortraitOwnedSceneActiveMutationEnvironment
    {
        internal nint OwnedView;
        internal int Blocked, Resumes;
        public bool TryQualifyActiveSource(PortraitIdentity source, uint frame, long request, uint slot, out nint renderer)
        { renderer = 123; return active && source == Source && slot == 6; }
        public IDisposable? BeginOwnedCounterMutation() { Blocked++; return new GuardScope(() => Blocked--); }
        public IDisposable? ResumeOwnedCounterPhase(PortraitIdentity source, uint frame, long request, nint renderer) { Resumes++; return Blocked == 0 ? new GuardScope(() => { }) : null; }
        public bool IsFrameworkThread => true;
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool TryValidateInstallation(out string reason) { reason = "scene admission must remain disabled"; return active; }
        public bool IsSourceCurrent(PortraitIdentity source) => active && Blocked == 0 && source == Source;
        public bool IsSlotIdle(uint slot) => active && Blocked == 0 && slot == 6 && OwnedView == 0;
        public bool IsSlotOwnedBy(uint slot, nint view) => active && Blocked == 0 && slot == 6 && view == OwnedView && view != 0;
        public bool TryReadViewSlot(nint view, out uint slot) { slot = view == Source.Model ? 1u : uint.MaxValue; return active && slot < 8; }
    }
    private static void Signal(nint address, bool direct) => current!.Signal(address, direct);
    private sealed class Provider(Fixture fixture) : IPortraitQualifiedNativeHookProvider, IPortraitNativePhaseHostFactory
    {
        internal readonly List<NativeHook> Created = [];
        internal int FailCreateAt = -1, FailEnableAt = -1;
        public string Identity => "test external qualified provider";
        public bool TryQualifyStartDisabled(out string reason) { reason = Identity; return true; }
        public bool QualificationPreserved() => true;
        public bool TryVerifyUnmodifiedTargets(PortraitNativePhaseResolvedPlan plan, out string reason)
        {
            reason = "test all verified targets must still be disabled/unmodified";
            return plan.Targets.All(target => Created.All(hook => hook.Address != target.Address || !hook.IsEnabled));
        }
        public Delegate DirectTarget(nint address, Type type) => Original(address, type, true);
        public Delegate DirectTarget(PortraitNativePhaseBoundTarget target, Type type) => DirectTarget(target.Address, type);
        public IPortraitNativePhaseHostHook Create(PortraitNativePhaseBoundTarget target, Type type, Delegate detour)
            => CreateQualifiedHook(target.Address, type, detour, () => true);
        public IPortraitNativePhaseHostHook CreateQualifiedHook(nint address, Type type, Delegate detour, Func<bool> verify)
        {
            Check(fixture.Plan.Targets.All(t => fixture.Reservations.IsReserved(t.Address)), "atomic 46-entry reservation exists at every native Create");
            if (Created.Count == FailCreateAt) throw new InvalidOperationException("test unknown native Create");
            var ordinal = Created.Count;
            var hook = new NativeHook(address, detour, Original(address, type, false), verify,
                () => { if (ordinal == FailEnableAt) throw new InvalidOperationException("test partial Enable"); });
            Created.Add(hook); return hook;
        }
        private static Delegate Original(nint address, Type type, bool direct)
        {
            var invoke = type.GetMethod("Invoke")!;
            var method = new DynamicMethod("JointOriginal", invoke.ReturnType, invoke.GetParameters().Select(p => p.ParameterType).ToArray(), typeof(JointPhysicalHostCases).Module, true);
            var il = method.GetILGenerator(); il.Emit(OpCodes.Ldc_I8, (long)address); il.Emit(OpCodes.Conv_I); il.Emit(direct ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Call, typeof(JointPhysicalHostCases).GetMethod(nameof(Signal), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!);
            if (invoke.ReturnType != typeof(void))
            { il.Emit(OpCodes.Ldc_I4, 0xE7); if (invoke.ReturnType.IsPointer || invoke.ReturnType == typeof(nint)) il.Emit(OpCodes.Conv_I); }
            il.Emit(OpCodes.Ret); return method.CreateDelegate(type);
        }
    }
    private sealed class NativeHook(nint address, Delegate detour, Delegate original, Func<bool> verify, Action enabling) : IPortraitNativePhaseHostHook
    {
        internal Delegate Detour => detour;
        public nint Address => address;
        public string BackendName => "SafetyHook";
        public bool IsEnabled { get; private set; }
        public bool IsDisposed { get; private set; }
        public Delegate Original => original;
        public void Enable() { Check(verify(), "per-enable target verification"); enabling(); IsEnabled = true; }
        public void Disable() => IsEnabled = false;
        public void Dispose() { Check(!IsEnabled, "no enabled native hook disposal"); IsDisposed = true; }
    }
}
#endif
