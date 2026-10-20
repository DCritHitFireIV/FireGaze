namespace FireGaze.PortraitPreview;

/// <summary>
/// Production owner of the existing active hook dispatcher. Every request gets a fresh
/// environment and physical hook generation; Framework queues identity only. The dispatcher
/// alone issues a current lease after its genuine source Render and confirms native retirement.
/// The normal environment validator continues to refuse incomplete production evidence.
/// </summary>
internal sealed class PortraitOwnedSceneControllerBroker(
    Func<bool> framework, Func<IPortraitOwnedSceneHookEnvironment> createEnvironment,
    IPortraitOwnedSceneHookFactory factory, PortraitNativePhaseHostReservations? reservations = null,
    Action<IPortraitOwnedSceneLease>? afterAdvance = null,
    Action<string>? sourceRevoked = null) : IPortraitOwnedSceneControllerBroker, IDisposable
{
    private IPortraitOwnedSceneHookEnvironment? preparedEnvironment;
    private volatile Request? active;
    private Task lastDrain = Task.CompletedTask;
    private volatile bool disposed;
    internal string? LastError { get; private set; }
    public bool IsFrameworkThread { get { try { return framework(); } catch { return false; } } }
    public bool ProductionAdmission
    {
        get
        {
            if (disposed || active is not null || !IsFrameworkThread) return false;
            try
            {
                var environment = preparedEnvironment ??= createEnvironment()
                    ?? throw new InvalidOperationException("Owned scene environment factory returned null.");
                if (!environment.IsFrameworkThread) { LastError = "Owned environment is outside Framework."; return false; }
                if (environment.TryValidateInstallation(out var reason)) return true;
                LastError = reason;
            }
            catch (Exception error) { LastError = error.Message; }
            return false;
        }
    }

    public bool TryRequest(PortraitIdentity source, uint slot, Action<IPortraitOwnedSceneLease> onAdvance,
        Func<IPortraitOwnedSceneLease, bool> onRetire)
    {
        if (disposed || !IsFrameworkThread || !source.IsValid || source.Generation == 0 || slot >= 8
            || onAdvance is null || onRetire is null) return false;
        Update();
        if (!ProductionAdmission) return false;
        var environment = preparedEnvironment!; preparedEnvironment = null;
        var request = new Request(new PortraitOwnedSceneHooks(environment, reservations, sourceRevoked: sourceRevoked), onAdvance, onRetire);
        active = request; lastDrain = RetainUntilRetired(request); LastError = null;
        try
        {
            if (request.Hooks.TryInstall(factory)
                && request.Hooks.TryRequest(source, slot, lease => Advance(request, lease), lease => Retire(request, lease)))
                return true;
            LastError = request.Hooks.LastError ?? "Owned native request was rejected.";
        }
        catch (Exception error) { LastError = error.Message; }
        finally { request.InstallationFinished.TrySetResult(); }
        // No active callback can run before this concrete dispatcher's TryRequest returns.
        // Its hook batch still remains owned if construction/disposal cannot be confirmed.
        request.Ended = true; request.Cancelled = true; request.Hooks.RequestStop(); Update();
        return false;
    }

    private void Advance(Request request, IPortraitOwnedSceneLease lease)
    {
        if (!ReferenceEquals(active, request) || request.Cancelled || request.Ended) return;
        request.Depth++; request.CallbackEntered = true;
        try
        {
            request.Advance(lease);
            // This synchronous continuation can initiate the official RT copy while the
            // same current lease and own ready-view scope are still available.
            if (!request.Cancelled && !request.Ended && !request.Hooks.NativeReleaseConfirmed) afterAdvance?.Invoke(lease);
        }
        finally
        {
            // Runtime operations can fail after freeing a raw allocation or completing
            // Release/Dtor themselves. Consume that exact lease's completion proof now;
            // a later phase must never inspect or retire the freed view again.
            if (!request.Ended && request.Hooks.NativeReleaseConfirmed)
            {
                request.Cancelled = true;
                request.Ended = request.Retire(lease);
                request.Hooks.RequestStop();
            }
            request.Depth--;
        }
    }
    private bool Retire(Request request, IPortraitOwnedSceneLease lease)
    {
        if (!ReferenceEquals(active, request) || request.Ended) return false;
        request.Depth++; request.Cancelled = true;
        try { return request.Ended = request.Retire(lease); }
        finally { request.Depth--; }
    }

    public bool TryCancelUnstartedRequest(string reason)
    {
        var request = active;
        if (!IsFrameworkThread || request is null || request.Depth != 0 || request.CallbackEntered || request.Ended
            || !request.Hooks.TryCancelUnstartedRequest()) return false;
        request.Cancelled = request.Ended = true; request.Hooks.RequestStop();
        return true;
    }
    public void Cancel(string reason)
    {
        if (active is not { } request) return;
        request.Cancelled = true;
        request.Hooks.RequestStop(); // Managed latch only. No guessed native cleanup.
    }
    internal void Update()
    {
        if (!IsFrameworkThread || active is not { } request || request.Depth != 0) return;
        LastError ??= request.Hooks.LastError;
        // Never dispose a request just because Stop was requested. A real retirement
        // callback, or a proof that it never entered, must finish the scene first.
        if (!request.Ended) return;
        request.Hooks.TryDispose();
        if (request.Hooks.DrainPhysicalAsync().IsCompletedSuccessfully) active = null;
    }
    internal Task DrainPhysicalAsync() => lastDrain;
    private static async Task RetainUntilRetired(Request request)
    {
        // Hook installation replaces its initially-complete drain. Wait only after it
        // has been attempted; otherwise an empty pre-install task would certify nothing.
        await request.InstallationFinished.Task.ConfigureAwait(false);
        try { await request.Hooks.DrainPhysicalAsync().ConfigureAwait(false); }
        finally { GC.KeepAlive(request); }
    }
    public void Dispose()
    {
        if (disposed) { Update(); return; }
        disposed = true; preparedEnvironment = null;
        if (!TryCancelUnstartedRequest("Owned broker disposed.")) Cancel("Owned broker disposed.");
        Update();
    }
    private sealed class Request(PortraitOwnedSceneHooks hooks, Action<IPortraitOwnedSceneLease> advance,
        Func<IPortraitOwnedSceneLease, bool> retire)
    {
        internal readonly PortraitOwnedSceneHooks Hooks = hooks;
        internal readonly Action<IPortraitOwnedSceneLease> Advance = advance;
        internal readonly Func<IPortraitOwnedSceneLease, bool> Retire = retire;
        internal readonly TaskCompletionSource InstallationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CallbackEntered;
        internal volatile bool Cancelled, Ended;
        internal int Depth;
    }
}
