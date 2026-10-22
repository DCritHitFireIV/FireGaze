namespace FireGaze.PortraitPreview;

/// <summary>
/// Cooperative native-entry exclusion shared by all participating generations and hook owners.
/// The process root contains only BCL objects, never a plugin type, delegate, hook or lease.
/// Explicitly supplying this registry does not prove unrelated creation sites participate;
/// production wiring must pass it to every overlapping same-plugin hook consumer first.
/// </summary>
internal sealed class PortraitNativePhaseHostReservations
{
    private const string RegistryKey = "FireGaze.NativeHookReservations.v1";
    private const string AnchorKey = "FireGaze.NativeHookReservations.ProcessAnchor.v1";
    private readonly Tuple<object, Dictionary<nint, object>>? shared;

    public PortraitNativePhaseHostReservations()
    {
        lock (AppDomain.CurrentDomain)
        {
            var current = AppDomain.CurrentDomain.GetData(RegistryKey);
            var anchor = AppContext.GetData(AnchorKey);
            if (current is null && anchor is null)
            {
                shared = Tuple.Create(new object(), new Dictionary<nint, object>());
                AppDomain.CurrentDomain.SetData(RegistryKey, shared);
                AppContext.SetData(AnchorKey, shared);
            }
            else if (ReferenceEquals(current, anchor)) shared = current as Tuple<object, Dictionary<nint, object>>;
        }
    }

    public bool IsReserved(nint address)
    {
        if (shared is null) return true;
        lock (shared.Item1)
            return !HasCurrentRoot() || shared.Item2.ContainsKey(0) || shared.Item2.ContainsKey(address);
    }

    public bool TryAcquireBatch(IReadOnlyList<nint> addresses, out PortraitNativePhaseHostReservation? lease, out string reason)
    {
        lease = null;
        reason = "The shared native-entry reservation registry or requested address batch is unavailable.";
        if (shared is null || addresses is null) return false;
        // Detach the caller's collection before the atomic operation. Duplicated/invalid
        // addresses indicate an installation contract error rather than a smaller batch.
        var copy = addresses.ToArray();
        if (copy.Length == 0 || copy.Any(address => address <= 0) || copy.Distinct().Count() != copy.Length) return false;
        lock (shared.Item1)
        {
            if (!HasCurrentRoot() || shared.Item2.ContainsKey(0)) return false;
            if (copy.Any(shared.Item2.ContainsKey))
            { reason = "A native entry is already reserved by a participating plugin hook owner."; return false; }
            var token = new object();
            // Prepare both allocations before publishing even the first address. Capacity
            // covers a poison sentinel as well, should an exceptional rollback be unprovable.
            var prepared = new PortraitNativePhaseHostReservation(this, copy, token);
            try
            {
                shared.Item2.EnsureCapacity(checked(shared.Item2.Count + copy.Length + 1));
                foreach (var address in copy) shared.Item2.Add(address, token);
            }
            catch (Exception)
            {
                try
                {
                    foreach (var address in copy)
                        if (shared.Item2.TryGetValue(address, out var actual) && ReferenceEquals(actual, token)) shared.Item2.Remove(address);
                }
                catch (Exception)
                {
                    // Key zero can never be a valid native entry. Its BCL token poisons
                    // this table for every participating generation without rooting one.
                    shared.Item2[0] = token;
                    reason = "Native-entry batch acquisition/rollback is unproven; the shared table remains isolated.";
                    return false;
                }
                reason = "Native-entry batch allocation failed; its unpublished ownership was rolled back.";
                return false;
            }
            lease = prepared;
            reason = "The complete native-entry batch is exclusively reserved until explicit successful retirement.";
            return true;
        }
    }

    internal bool TryRelease(nint[] addresses, object token, out string reason)
    {
        reason = "The shared native-entry reservation root or lease ownership changed; isolation remains held.";
        if (shared is null) return false;
        lock (shared.Item1)
        {
            if (!HasCurrentRoot() || shared.Item2.ContainsKey(0) || addresses.Any(address => !shared.Item2.TryGetValue(address, out var actual)
                    || !ReferenceEquals(actual, token))) return false;
            foreach (var address in addresses) shared.Item2.Remove(address);
            reason = "The retired native-entry batch reservation was released."; return true;
        }
    }

    private bool HasCurrentRoot() => ReferenceEquals(AppDomain.CurrentDomain.GetData(RegistryKey), shared)
        && ReferenceEquals(AppContext.GetData(AnchorKey), shared);
}

/// <summary>
/// Deliberately not IDisposable: leaving a scope, cancellation, garbage collection or unload
/// cannot certify that native hooks stopped. The owner calls TryRelease only after all native
/// resources and gateway ownership have successfully retired; failure retains the full batch.
/// </summary>
internal sealed class PortraitNativePhaseHostReservation
{
    private readonly object gate = new();
    private readonly PortraitNativePhaseHostReservations registry;
    private readonly nint[] addresses;
    private readonly object token;
    private bool released;

    internal PortraitNativePhaseHostReservation(PortraitNativePhaseHostReservations registry, nint[] addresses, object token)
    { this.registry = registry; this.addresses = addresses; this.token = token; }

    public bool TryRelease(out string reason)
    {
        lock (gate)
        {
            if (released) { reason = "This retired lease was already released."; return true; }
            if (!registry.TryRelease(addresses, token, out reason)) return false;
            released = true; return true;
        }
    }
}
