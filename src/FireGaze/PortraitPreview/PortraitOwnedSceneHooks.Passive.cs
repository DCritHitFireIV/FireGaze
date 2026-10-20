using System.Runtime.InteropServices;

namespace FireGaze.PortraitPreview;

internal enum PortraitOwnedScenePassiveBaselineState { Waiting, Observed, Revoked }
internal sealed record PortraitOwnedScenePassiveBaseline(PortraitOwnedScenePassiveBaselineState State, Guid Installation,
    PortraitIdentity Source, long SessionEpoch, PortraitNativeThreadStamp ExpectedThread, PortraitNativeThreadStamp? Thread,
    uint? NativeFrame, long ArmedAt, long Deadline, string Reason)
{
    public bool ProductionAdmission => false;
}

internal sealed partial class PortraitOwnedSceneHooks
{
    private Guid installation;
    private PortraitOwnedScenePassiveBaseline? passiveBaseline;
    private Task? baselineExpiryTask;
    private readonly Func<PortraitNativeThreadStamp> nativeThread = nativeThread ?? ReadNativeThread;
    public PortraitOwnedScenePassiveBaseline? PassiveSourceBaseline => Volatile.Read(ref passiveBaseline);
    internal int PhysicalHookCount => Volatile.Read(ref publishedHooks).Count(hook => !hook.IsDisposed);
    internal int PhysicalInFlight => Volatile.Read(ref publishedHooks).OfType<IPortraitOwnedScenePhysicalHook>().Sum(hook => hook.InFlight);
    internal bool ReservationHeld => Volatile.Read(ref reservation) is not null;

    public bool TryArmPassiveSourceBaseline(PortraitIdentity source, long sessionEpoch, PortraitNativeThreadStamp expectedThread, TimeSpan duration)
    {
        if (!passiveOnly || !IsInstalled || closing || stopped || !environment.IsFrameworkThread || installation == Guid.Empty
            || !source.IsValid || source.Generation == 0 || sessionEpoch <= 0 || expectedThread.NativeID == 0 || expectedThread.ManagedID <= 0
            || duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(1) || PassiveSourceBaseline is not null) return false;
        try
        {
            var now = timestamp();
            if (now < passiveLastTimestamp || now >= passiveDeadline || closing || stopped) return false;
            var snapshot = new PortraitOwnedScenePassiveBaseline(PortraitOwnedScenePassiveBaselineState.Waiting, installation, source,
                sessionEpoch, expectedThread, null, null, now, Math.Min(passiveDeadline, checked(now + (long)(duration.TotalSeconds * timestampFrequency))),
                "Waiting for a future actual source invocation of the completely installed owned generation.");
            if (Interlocked.CompareExchange(ref passiveBaseline, snapshot, null) is not null) return false;
            baselineExpiryTask = ExpirePassiveBaselineAsync(snapshot, duration); return true;
        }
        catch (Exception) { RevokePassiveBaseline("A bounded source Return baseline could not arm."); return false; }
    }

    private PortraitOwnedScenePassiveBaseline? BeginPassiveReturn(nint view)
    {
        var snapshot = PassiveSourceBaseline;
        if (!passiveOnly || !installed || closing || stopped || snapshot is not { State: PortraitOwnedScenePassiveBaselineState.Waiting }
            || snapshot.Source.Model != view || snapshot.Installation != installation) return null;
        try
        {
            var now = timestamp();
            if (now < snapshot.ArmedAt || now >= snapshot.Deadline || nativeThread() != snapshot.ExpectedThread)
            { RevokePassiveBaseline("The exact source entered outside the bounded independently observed thread."); return null; }
            return snapshot;
        }
        catch (Exception) { RevokePassiveBaseline("The source invocation thread or clock was unavailable."); return null; }
    }
    private void CompletePassiveReturn(PortraitOwnedScenePassiveBaseline? invocation, nint view, uint frame, bool returned)
    {
        if (invocation is null || !ReferenceEquals(PassiveSourceBaseline, invocation)) return;
        try
        {
            var now = timestamp(); var observed = nativeThread();
            if (!returned || closing || stopped || !installed || invocation.Installation != installation || view != invocation.Source.Model
                || now < invocation.ArmedAt || now >= invocation.Deadline || observed != invocation.ExpectedThread)
            { RevokePassiveBaseline("The future native source Original did not return under the same installed owner/thread/deadline."); return; }
            var complete = invocation with { State = PortraitOwnedScenePassiveBaselineState.Observed, Thread = observed, NativeFrame = frame,
                Reason = "One actual successful future source Return was observed; source/slot ownership and scene admission are not granted." };
            Interlocked.CompareExchange(ref passiveBaseline, complete, invocation);
        }
        catch (Exception) { RevokePassiveBaseline("The source Return evidence was unavailable."); }
    }
    private void RevokePassiveBaseline(string reason)
    {
        while (PassiveSourceBaseline is { } current && current.State != PortraitOwnedScenePassiveBaselineState.Revoked)
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref passiveBaseline,
                current with { State = PortraitOwnedScenePassiveBaselineState.Revoked, Reason = reason }, current), current)) return;
        }
    }
    private static PortraitNativeThreadStamp ReadNativeThread() => new(GetCurrentThreadID(), Environment.CurrentManagedThreadId);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)] private static extern uint GetCurrentThreadID();
}
