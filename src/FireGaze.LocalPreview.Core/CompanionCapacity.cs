namespace FireGaze.LocalPreview.Core;

/// <summary>
/// Detached result of the CN client's automatic companion-slot census. The native
/// allocator reserves two adjacent entries for one companion (host + child), and
/// only scans the first 240 entries. This value is an observation, never an
/// allocation grant: the native create call remains the owner of the final decision.
/// </summary>
public readonly record struct CompanionCapacityObservation(
    bool IsKnown,
    int AvailablePairs,
    int OccupiedSlots,
    int ScannedSlots,
    string Reason)
{
    public static CompanionCapacityObservation Unknown(string reason)
        => new(false, 0, 0, 0, string.IsNullOrWhiteSpace(reason) ? "capacity census unavailable" : reason.Trim());

    /// <summary>
    /// Counts non-overlapping empty adjacent pairs in the allocator's scan domain.
    /// ObjectKind is deliberately the only input: a reserved child may have a null
    /// object pointer while still consuming the slot, so callers must pass its kind.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> objectKinds, out CompanionCapacityObservation observation,
        int allocatorSlots = 240)
    {
        if (allocatorSlots < 2 || objectKinds.Length < allocatorSlots)
        {
            observation = Unknown("capacity census has an invalid slot range");
            return false;
        }

        var occupied = 0;
        for (var i = 0; i < allocatorSlots; i++)
            if (objectKinds[i] != 0) occupied++;

        var pairs = 0;
        for (var i = 0; i + 1 < allocatorSlots;)
        {
            if (objectKinds[i] == 0 && objectKinds[i + 1] == 0)
            {
                pairs++;
                i += 2;
            }
            else i++;
        }

        observation = new(true, pairs, occupied, allocatorSlots, string.Empty);
        return true;
    }
}
