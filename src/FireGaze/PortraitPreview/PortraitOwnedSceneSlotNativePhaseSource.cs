namespace FireGaze.PortraitPreview;

/// <summary>
/// A synchronous source-Render/retirement edge owner. Detached observations and
/// Framework frames cannot create this scope. It installs no hooks and performs
/// no constructor reads. Production defaults deny every native entry.
/// </summary>
internal sealed class PortraitOwnedSceneSlotNativePhaseSource(
    IPortraitOwnedSceneSlotNativeApi native, Func<bool>? framework = null,
    Func<bool>? buildValidated = null, Func<long>? sessionEpoch = null) : IPortraitOwnedSceneSlotPhaseSource
{
    private readonly object gate = new();
    private PortraitOwnedSceneSlotPhase current;
    private PortraitOwnedSceneSlotRoots previousRoots;
    private int thread;
    private int nativeMutationDepth;
    private long token, serial, request, session, managerGeneration;
    private bool bootstrapping;

    // Used only to enable the fallible native-copy boundary, including root
    // bootstrap. This property never grants a phase/lease and never reads native
    // memory, so NativeApi may query it without recursive root reads.
    public bool IsReadScopeActive
    {
        get { lock (gate) return Permitted(); }
    }

    public IDisposable? EnterSource(PortraitIdentity source, uint nativeFrame, long requestEpoch)
    {
        lock (gate)
        {
            if (token != 0) return null;
            return Enter(source, nativeFrame, requestEpoch);
        }
    }

    public IDisposable? EnterRetirement(PortraitIdentity source, uint nativeFrame, long requestEpoch)
    {
        lock (gate)
        {
            if (token == 0) return Enter(source, nativeFrame, requestEpoch);
            if (request != requestEpoch || current.Source != source || current.NativeFrame != nativeFrame
                || !TryCapture(out _)) return null;
            return new Scope(this, token, false);
        }
    }

    private IDisposable? Enter(PortraitIdentity source, uint nativeFrame, long requestEpoch)
    {
        if (nativeMutationDepth != 0 || !source.IsValid || source.Generation == 0 || requestEpoch <= 0) return null;
        try
        {
            if (framework?.Invoke() != true || buildValidated?.Invoke() != true
                || sessionEpoch?.Invoke() is not > 0) return null;
            session = sessionEpoch();
            if (session <= 0) return null;
            request = requestEpoch;
            thread = Environment.CurrentManagedThreadId;
            token = checked(++serial);
            var enteringToken = token;
            bootstrapping = true;
            if (!Permitted() || !native.TryReadRoots(out var roots) || !roots.IsValid
                || token != enteringToken || !Permitted()) { Revoke(); return null; }
            if (managerGeneration == 0 || previousRoots != roots)
            { managerGeneration = checked(managerGeneration + 1); previousRoots = roots; }
            current = new(source, roots, session, managerGeneration, nativeFrame,
                token, thread, true, true);
            bootstrapping = false;
            if (!TryCapture(out _)) { Revoke(); return null; }
            return new Scope(this, token, true);
        }
        catch (Exception) { Revoke(); return null; }
        finally { bootstrapping = false; }
    }

    public bool TryCapture(out PortraitOwnedSceneSlotPhase phase)
    {
        lock (gate)
        {
            phase = default;
            if (bootstrapping || current.PhaseToken == 0 || !Permitted())
            { if (token != 0 && thread == Environment.CurrentManagedThreadId) Revoke(); return false; }
            try
            {
                var candidate = current;
                if (!native.TryReadRoots(out var roots) || roots != candidate.Roots
                    || current != candidate || !Permitted()) { Revoke(); return false; }
                phase = candidate; return true;
            }
            catch (Exception) { Revoke(); return false; }
        }
    }

    // A real reset ingress must call this even if all manager addresses survive.
    // Until that typed ingress is qualified, installation still remains denied.
    public void InvalidateManagers()
    {
        lock (gate)
        {
            Revoke();
            managerGeneration = checked(managerGeneration + 1);
            if (managerGeneration == 0) managerGeneration = 1;
        }
    }

    // Detached pre-write guard. It spans the entire actual native Original, so
    // reentrant or concurrent source Render cannot reopen a phase while the
    // counter write is pending. No native read/release occurs in either edge.
    public IDisposable BeginCounterMutation() => BeginDetachedMutation();

    // Resource and counter writes share one depth. Finishing an inner callback
    // cannot reopen source/retirement reads while another Original still runs.
    public IDisposable BeginResourceMutation() => BeginDetachedMutation();

    private IDisposable BeginDetachedMutation()
    {
        lock (gate)
        {
            var nextDepth = checked(nativeMutationDepth + 1);
            var nextGeneration = checked(managerGeneration + 1);
            nativeMutationDepth = nextDepth;
            Revoke();
            managerGeneration = nextGeneration;
            return new DetachedMutation(this);
        }
    }

    public void EndRequest(long requestEpoch)
    {
        lock (gate) { if (request == requestEpoch) Revoke(); }
    }

    private bool Permitted()
    {
        if (nativeMutationDepth != 0 || token == 0 || thread != Environment.CurrentManagedThreadId || request <= 0 || session <= 0) return false;
        try { return framework?.Invoke() == true && buildValidated?.Invoke() == true && sessionEpoch?.Invoke() == session; }
        catch (Exception) { return false; }
    }

    private void Revoke()
    { current = default; token = request = session = 0; thread = 0; bootstrapping = false; }

    private void Exit(long expected, bool owns)
    {
        lock (gate)
        {
            if (token != expected) return;
            if (thread != Environment.CurrentManagedThreadId || owns) Revoke();
        }
    }

    private sealed class Scope(PortraitOwnedSceneSlotNativePhaseSource owner, long token, bool owns) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Exit(token, owns); }
    }

    private sealed class DetachedMutation(PortraitOwnedSceneSlotNativePhaseSource owner) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.gate) { owner.Revoke(); owner.nativeMutationDepth--; }
        }
    }
}
