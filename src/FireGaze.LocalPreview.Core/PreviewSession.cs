namespace FireGaze.LocalPreview.Core;

public enum PreviewKind { Emote, Mount, Minion, Accessory }
public readonly record struct PreviewRequest(PreviewKind Kind, uint Id);
public enum PreviewExitReason { Teleport, TerritoryChange, Death, Combat, CraftingPreparation, Logout, FeatureDisabled, PluginReload, UserCancelled }

public sealed class PreviewSnapshot
{
    internal PreviewSnapshot(long epoch, IEnumerable<PreviewRequest> active)
    {
        Epoch = epoch;
        Active = Array.AsReadOnly(active.ToArray());
    }

    public long Epoch { get; }
    public IReadOnlyList<PreviewRequest> Active { get; }
}

public sealed record PreviewTransition(PreviewSnapshot Before, PreviewSnapshot After);

public sealed class NativeAttempt
{
    internal NativeAttempt(PreviewSession owner, PreviewSnapshot before, long epoch)
    {
        Owner = owner;
        Before = before;
        Epoch = epoch;
    }

    internal PreviewSession Owner { get; }
    public PreviewSnapshot Before { get; }
    public long Epoch { get; }
}

public sealed record NativeResolution(bool Applied, bool Accepted, PreviewTransition Transition);

/// <summary>Owns preview intent only. The host supplies compatibility and applies actor visuals.</summary>
public sealed class PreviewSession
{
    private NativeAttempt? pendingNative;

    public PreviewSnapshot Snapshot { get; private set; } = new(0, []);

    public PreviewTransition StartPreview(PreviewRequest request, IEnumerable<PreviewKind> conflicts)
    {
        if (request.Id == 0 || !Enum.IsDefined(request.Kind))
            throw new ArgumentOutOfRangeException(nameof(request));
        ArgumentNullException.ThrowIfNull(conflicts);
        var removed = conflicts.ToHashSet();
        if (removed.Any(kind => !Enum.IsDefined(kind)))
            throw new ArgumentOutOfRangeException(nameof(conflicts));
        removed.Add(request.Kind);
        return Replace(Snapshot.Active.Where(active => !removed.Contains(active.Kind)).Append(request));
    }

    public PreviewTransition StopPreview(PreviewKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return Replace(Snapshot.Active.Where(active => active.Kind != kind));
    }

    public PreviewTransition Exit(PreviewExitReason reason, long? expectedEpoch = null)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return expectedEpoch is not null && !IsCurrent(expectedEpoch.Value)
            ? new(Snapshot, Snapshot)
            : Replace([]);
    }

    public bool IsCurrent(long epoch) => Snapshot.Epoch == epoch;

    public NativeAttempt BeginNative()
    {
        if (pendingNative is not null)
            throw new InvalidOperationException("A native input is already pending. Suppress nested internal calls.");
        var before = Snapshot;
        Replace([]);
        pendingNative = new(this, before, Snapshot.Epoch);
        return pendingNative;
    }

    public NativeResolution CompleteNative(NativeAttempt attempt, bool accepted)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!ReferenceEquals(attempt.Owner, this) || !ReferenceEquals(pendingNative, attempt) || !IsCurrent(attempt.Epoch))
            return new(false, accepted, new(Snapshot, Snapshot));

        return new(true, accepted, Replace(accepted ? [] : attempt.Before.Active));
    }

    private PreviewTransition Replace(IEnumerable<PreviewRequest> active)
    {
        var before = Snapshot;
        Snapshot = new(checked(before.Epoch + 1), active);
        pendingNative = null;
        return new(before, Snapshot);
    }
}
