namespace FireGaze.PortraitPreview;

/// <summary>
/// One explicit development attempt. The caller supplies the same joint Host, environment,
/// phase source and RT reader. This never changes the normal production admission policy.
/// Stop keeps request identity alive for the same installation's retirement-only returns.
/// </summary>
internal sealed class PortraitOwnedSceneExperimentBroker(
    Func<bool> framework, PortraitOwnedSceneJointHost host, PortraitOwnedSceneJointPlan plan,
    PortraitIdentity source, long sessionEpoch, long requestEpoch, PortraitNativeThreadStamp thread,
    TimeSpan duration, Func<(bool Accepted, string Reason)> validate,
    Action<IPortraitOwnedSceneLease> afterAdvance, Action<string> sourceRevoked,
    Action<string>? stopWork = null, Func<bool>? allowAdvance = null) : IPortraitOwnedSceneSessionBroker
{
    private readonly TaskCompletionSource installationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool attempted, entered, stopping, ended, invalidated, disposed, stopNotified;
    private int depth;
    private Task? drain;
    public string? LastError { get; private set; }
    public bool ProductionAdmission => false;
    public bool IsFrameworkThread { get { try { return framework(); } catch { return false; } } }
    public bool StartAdmission
    {
        get
        {
            if (attempted || disposed || invalidated || !IsFrameworkThread || duration <= TimeSpan.Zero
                || duration > TimeSpan.FromSeconds(2) || !source.IsValid || source.Generation == 0
                || sessionEpoch <= 0 || requestEpoch <= 0 || thread.NativeID == 0 || thread.ManagedID <= 0) return false;
            try { var validation=validate(); if (validation.Accepted) return true; LastError = validation.Reason; }
            catch(Exception error){LastError=error.Message;}
            return false;
        }
    }
    // RT reads can use the shared native dispatch, including a retirement-only phase.
    // A new provider copy needs this narrower active-work capability as well.
    internal bool CanCopy => attempted && !stopping && !invalidated && !disposed
        && host.IsActiveAdvanceAdmitted;
    internal PortraitOwnedSceneJointHostSnapshot Snapshot => host.Snapshot();
    internal bool RequestIdentityCurrent => !invalidated;

    public bool TryRequest(PortraitIdentity requestedSource, uint slot,
        Action<IPortraitOwnedSceneLease> advance, Func<IPortraitOwnedSceneLease,bool> retire)
    {
        if (!StartAdmission || requestedSource != source || slot >= 8 || advance is null || retire is null) return false;
        attempted = true; drain = RetainUntilDrained();
        try
        {
            if (!host.TryStartActive(plan, source, sessionEpoch, requestEpoch, thread, duration, out var reason))
            { LastError = reason; ended = stopping = true; host.TryStop(); return false; }
            if (!host.TryRequest(source, slot, lease =>
            {
                // An owned Render mutates its Counter and invalidates the previous RT token.
                // Keep one pending/current copied frame until explicit camera input revokes it.
                // The host's retirement callback bypasses this new-work gate.
                if (stopping || invalidated || ended || allowAdvance?.Invoke() == false) return;
                depth++; entered = true;
                try
                {
                    advance(lease);
                    if (CanCopy && !host.NativeReleaseConfirmed) afterAdvance(lease);
                }
                finally
                {
                    if (!ended && host.NativeReleaseConfirmed)
                    { stopping = true; ended = retire(lease); host.RequestStop(); }
                    depth--;
                }
            }, lease =>
            {
                if (ended) return false;
                depth++; stopping = true;
                try { return ended = retire(lease); }
                finally { depth--; }
            }))
            { LastError="The joint installation rejected its scene request."; ended=stopping=true;host.TryStop();return false; }
            return true;
        }
        catch(Exception error){LastError=error.Message;stopping=true;host.RequestStop();return false;}
        finally { installationFinished.TrySetResult(); }
    }
    public bool TryCancelUnstartedRequest(string reason)
    {
        if (!IsFrameworkThread || !attempted || entered || ended || depth != 0 || !host.TryCancelUnstartedRequest()) return false;
        ended = stopping = true; host.RequestStop(); return true;
    }
    public void Cancel(string reason) { stopping = true; host.RequestStop(); }
    internal void Revoke(string reason)
    {
        if (invalidated) return;
        invalidated = true; stopping = true; LastError = reason;
        sourceRevoked(reason); host.RevokeEvidence(); host.RequestStop();
    }
    public void Update()
    {
        if (!IsFrameworkThread || !attempted || depth != 0) return;
        if (!stopNotified && (ended || !stopping && !host.Snapshot().Recording))
        {
            stopNotified = stopping = true;
            // Deadline ends new work; the exact source/root/slot may still retire natively.
            stopWork?.Invoke("The bounded independent portrait experiment ended.");
            host.RequestStop();
        }
        if (ended) host.TryStop();
    }
    public Task DrainPhysicalAsync() => attempted ? drain! : Task.CompletedTask;
    private async Task RetainUntilDrained()
    {
        await installationFinished.Task.ConfigureAwait(false);
        try { await host.DrainPhysicalAsync().ConfigureAwait(false); }
        finally { GC.KeepAlive(this); }
    }
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            if (!attempted) { host.Dispose(); return; }
            if (!TryCancelUnstartedRequest("Experiment disposed before creation.")) Cancel("Experiment disposed.");
        }
        Update();
    }
}
