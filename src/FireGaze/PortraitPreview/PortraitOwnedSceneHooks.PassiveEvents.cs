namespace FireGaze.PortraitPreview;

internal enum PortraitOwnedScenePassiveContextEventKind { Construct, Destroy }
internal readonly record struct PortraitOwnedScenePassiveContextEvent(long Sequence,
    PortraitOwnedScenePassiveContextEventKind Kind, long Context, byte Flags, long Result, bool OriginalReturned,
    PortraitNativeThreadStamp EntryThread, PortraitNativeThreadStamp ReturnThread, long EnteredAt, long ReturnedAt);
internal sealed record PortraitOwnedScenePassiveContextEvents(Guid Installation, int Capacity, long Dropped,
    IReadOnlyList<PortraitOwnedScenePassiveContextEvent> Events)
{
    public bool ProductionAdmission => false;
}

internal sealed partial class PortraitOwnedSceneHooks
{
    private PassiveContextCollector? passiveContextCollector;
    internal PortraitOwnedScenePassiveContextEvents? PassiveContextEvents => Volatile.Read(ref passiveContextCollector)?.Read();
    private readonly record struct PassiveContextInvocation(PassiveContextCollector Collector, PortraitNativeThreadStamp Thread, long EnteredAt);
    private PassiveContextInvocation? BeginPassiveContextEvent()
    {
        if (!passiveOnly || !installed || closing || stopped || Volatile.Read(ref passiveContextCollector) is not { } collector) return null;
        try
        {
            var now = timestamp(); var observed = nativeThread();
            return now >= passiveLastTimestamp && now < passiveDeadline && observed.NativeID > 0 && observed.ManagedID > 0
                && !closing && !stopped ? new(collector, observed, now) : null;
        }
        catch (Exception) { return null; }
    }
    private void CompletePassiveContextEvent(PassiveContextInvocation? invocation, PortraitOwnedScenePassiveContextEventKind kind,
        nint context, byte flags, nint result, bool returned)
    {
        if (invocation is not { } entry || !ReferenceEquals(Volatile.Read(ref passiveContextCollector), entry.Collector)
            || !installed || closing || stopped) return;
        try
        {
            var now = timestamp(); var observed = nativeThread();
            if (now < entry.EnteredAt || now >= passiveDeadline || observed.NativeID == 0 || observed.ManagedID <= 0 || closing || stopped) return;
            entry.Collector.Append(kind, context, flags, result, returned, entry.Thread, observed, entry.EnteredAt, now);
        }
        catch (Exception) { /* Scalar observation never changes native Original behavior. */ }
    }
    private sealed class PassiveContextCollector(Guid installation)
    {
        private const int Capacity = 64;
        private readonly Entry[] entries = new Entry[Capacity];
        private long claimed, dropped;
        internal void Append(PortraitOwnedScenePassiveContextEventKind kind, nint context, byte flags, nint result, bool returned,
            PortraitNativeThreadStamp entryThread, PortraitNativeThreadStamp returnThread, long enteredAt, long returnedAt)
        {
            var sequence = Interlocked.Increment(ref claimed);
            if (sequence <= 0 || sequence > Capacity) { Interlocked.Increment(ref dropped); return; }
            ref var entry = ref entries[(int)sequence - 1];
            entry.Value = new(sequence, kind, (long)context, flags, (long)result, returned, entryThread, returnThread, enteredAt, returnedAt);
            Volatile.Write(ref entry.Published, 1);
        }
        internal PortraitOwnedScenePassiveContextEvents Read()
        {
            var detached = new List<PortraitOwnedScenePassiveContextEvent>(Capacity);
            for (var index = 0; index < entries.Length; index++)
                if (Volatile.Read(ref entries[index].Published) != 0) detached.Add(entries[index].Value);
            return new(installation, Capacity, Interlocked.Read(ref dropped), Array.AsReadOnly(detached.ToArray()));
        }
        private struct Entry { internal PortraitOwnedScenePassiveContextEvent Value; internal int Published; }
    }
}
