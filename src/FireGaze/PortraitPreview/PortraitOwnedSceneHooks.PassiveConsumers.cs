namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitOwnedScenePassiveNativeEvent(long Sequence,
    PortraitOwnedSceneNativeKind TargetKind, long TargetAddress, long Receiver,
    int ArgumentCount, long Argument0, long Argument1, long Argument2, long Argument3, long Argument4,
    long Result, bool OriginalReturned, PortraitNativeThreadStamp EntryThread,
    PortraitNativeThreadStamp ReturnThread, long EnteredAt, long ReturnedAt);

internal sealed record PortraitOwnedScenePassiveNativeEvents(Guid Installation, int Capacity, long Dropped,
    IReadOnlyList<PortraitOwnedScenePassiveNativeEvent> Events)
{
    public bool ProductionAdmission => false;
}

internal sealed partial class PortraitOwnedSceneHooks
{
    private PassiveNativeCollector? passiveNativeCollector;
    internal PortraitOwnedScenePassiveNativeEvents? PassiveNativeEvents
        => Volatile.Read(ref passiveNativeCollector)?.Read();

    private readonly record struct PassiveNativeInvocation(PassiveNativeCollector Collector,
        PortraitNativeThreadStamp Thread, long EnteredAt);

    private PassiveNativeInvocation? BeginPassiveNativeEvent()
    {
        if (!passiveOnly || !installed || closing || stopped
            || Volatile.Read(ref passiveNativeCollector) is not { } collector) return null;
        try
        {
            var now = timestamp();
            var observed = nativeThread();
            return now >= passiveLastTimestamp && now < passiveDeadline
                && observed.NativeID > 0 && observed.ManagedID > 0 && !closing && !stopped
                ? new(collector, observed, now) : null;
        }
        catch (Exception) { return null; }
    }

    private TResult ObservePassiveNative<TResult>(PortraitOwnedSceneNativeTarget target, nint receiver,
        int argumentCount, long argument0, long argument1, long argument2, long argument3, long argument4,
        Func<TResult> invoke, Func<TResult, long> scalarResult)
    {
        var invocation = BeginPassiveNativeEvent();
        TResult result = default!;
        var returned = false;
        try
        {
            result = invoke();
            returned = true;
            return result;
        }
        finally
        {
            CompletePassiveNativeEvent(invocation, target, receiver, argumentCount,
                argument0, argument1, argument2, argument3, argument4,
                returned ? scalarResult(result) : 0, returned);
        }
    }

    private void CompletePassiveNativeEvent(PassiveNativeInvocation? invocation,
        PortraitOwnedSceneNativeTarget target, nint receiver, int argumentCount,
        long argument0, long argument1, long argument2, long argument3, long argument4,
        long result, bool returned)
    {
        if (invocation is not { } entry || !ReferenceEquals(Volatile.Read(ref passiveNativeCollector), entry.Collector)
            || !installed || closing || stopped) return;
        try
        {
            var now = timestamp();
            var observed = nativeThread();
            if (now < entry.EnteredAt || now >= passiveDeadline
                || observed.NativeID <= 0 || observed.ManagedID <= 0 || closing || stopped) return;
            entry.Collector.Append(target, receiver, argumentCount,
                argument0, argument1, argument2, argument3, argument4, result, returned,
                entry.Thread, observed, entry.EnteredAt, now);
        }
        catch (Exception) { /* Passive scalar evidence never changes native Original behavior. */ }
    }

    private sealed class PassiveNativeCollector(Guid installation)
    {
        private const int EventCapacity = 256;
        private readonly Entry[] entries = new Entry[EventCapacity];
        private long claimed, dropped;

        internal void Append(PortraitOwnedSceneNativeTarget target, nint receiver, int argumentCount,
            long argument0, long argument1, long argument2, long argument3, long argument4,
            long result, bool returned, PortraitNativeThreadStamp entryThread,
            PortraitNativeThreadStamp returnThread, long enteredAt, long returnedAt)
        {
            var sequence = Interlocked.Increment(ref claimed);
            if (sequence <= 0 || sequence > EventCapacity)
            {
                Interlocked.Increment(ref dropped);
                return;
            }
            ref var entry = ref entries[(int)sequence - 1];
            entry.Value = new(sequence, target.Kind, (long)target.Address, (long)receiver, argumentCount,
                argument0, argument1, argument2, argument3, argument4, result, returned,
                entryThread, returnThread, enteredAt, returnedAt);
            Volatile.Write(ref entry.Published, 1);
        }

        internal PortraitOwnedScenePassiveNativeEvents Read()
        {
            var detached = new List<PortraitOwnedScenePassiveNativeEvent>(EventCapacity);
            for (var index = 0; index < entries.Length; index++)
                if (Volatile.Read(ref entries[index].Published) != 0) detached.Add(entries[index].Value);
            return new(installation, EventCapacity, Interlocked.Read(ref dropped), Array.AsReadOnly(detached.ToArray()));
        }

        private struct Entry
        {
            internal PortraitOwnedScenePassiveNativeEvent Value;
            internal int Published;
        }
    }
}
