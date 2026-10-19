namespace FireGaze.PortraitPreview;

/// <summary>Native ownership is reported separately from a possibly incomplete readback.</summary>
internal sealed record PortraitLightingFitNativeWrite(bool Applied, IReadOnlyList<int>? OwnedParameters);

/// <summary>The only boundaries replaced by tests are the native editor, GPU capture and host publication.</summary>
internal interface IPortraitLightingFitFeatureHost
{
    bool IsFrameworkThread { get; }
    bool TryReadBinding(out PortraitLightingFitBinding binding);
    bool IsEditorOwnerDestroyed(PortraitIdentity identity);
    PortraitLightingFitNativeWrite WriteColors(PortraitLightingFitBinding expected, IReadOnlyList<int> desired);
    void SetHistorySuppressed(bool value);
    Task<IReadOnlyList<PortraitLightRgba>> CaptureAsync(PortraitLightingFitBinding expected);
    bool CommitHistory(PortraitLightingFitBinding expected, IReadOnlyList<int> baseline);
}

/// <summary>
/// Revalidates the complete live binding at every native boundary. Only six RGB fields cross a write;
/// temporary unreadability does not discard a known owner or a retained cleanup obligation.
/// </summary>
internal sealed class PortraitLightingFitFeatureBackend(IPortraitLightingFitFeatureHost host) : IPortraitLightingFitBackend
{
    private readonly Dictionary<string, PortraitIdentity> owners = [];
    private bool historySuppressed;
    public bool IsFrameworkThread => host.IsFrameworkThread;

    public bool TryRead(out PortraitLightingFitState state)
    {
        state = null!;
        if (!TryBinding(out var binding)) return false;
        state = binding.State;
        return true;
    }

    public bool IsEditorOwnerDestroyed(string owner) => IsFrameworkThread && owners.TryGetValue(owner, out var identity)
        && host.IsEditorOwnerDestroyed(identity);

    public PortraitLightingFitWriteResult Write(PortraitLightingFitState expected, IReadOnlyList<int> desired)
    {
        if (!TryBinding(out var binding) || !Same(expected, binding.State) || desired is not { Count: 6 }
            || Enumerable.Range(0, 6).Any(axis => !binding.State.Ranges[axis].Contains(desired[axis]))) return new(false, null);
        var result = host.WriteColors(binding, Array.AsReadOnly(desired.ToArray()));
        // Actual native readback may contain unrelated external changes. Ownership must come from
        // the native writer's attempted channels, including a partially completed first triplet.
        var owned = result.OwnedParameters;
        PortraitLightingFitState? ownedState = owned is { Count: 6 }
            && Enumerable.Range(0, 6).All(axis => binding.State.Ranges[axis].Contains(owned[axis]))
            ? binding.State with { Parameters = Array.AsReadOnly(owned.ToArray()) } : null;
        return new(result.Applied, ownedState);
    }

    public void SetHistorySuppressed(bool value)
    {
        if (!IsFrameworkThread) throw new InvalidOperationException("Lighting history suppression requires Framework.");
        host.SetHistorySuppressed(value);
        historySuppressed = value;
    }

    public Task<IReadOnlyList<PortraitLightRgba>> CaptureAsync(PortraitLightingFitState expected)
    {
        if (!TryBinding(out var binding) || !Same(expected, binding.State) || !binding.Snapshot.Composition.Paused)
            return Task.FromException<IReadOnlyList<PortraitLightRgba>>(new InvalidOperationException("The current paused portrait source is unavailable."));
        return host.CaptureAsync(binding);
    }

    public bool CommitHistory(PortraitLightingFitState expected, IReadOnlyList<int> baseline)
    {
        if (!historySuppressed || !TryBinding(out var binding) || !Same(expected, binding.State)
            || !binding.Snapshot.Composition.Paused || baseline is not { Count: 6 }
            || Enumerable.Range(0, 6).Any(axis => !binding.State.Ranges[axis].Contains(baseline[axis]))) return false;
        return host.CommitHistory(binding, Array.AsReadOnly(baseline.ToArray()));
    }

    private bool TryBinding(out PortraitLightingFitBinding binding)
    {
        binding = null!;
        if (!IsFrameworkThread || !host.TryReadBinding(out var current) || current is null) return false;
        owners[current.State.EditorIdentity] = current.Identity;
        binding = current;
        return true;
    }

    private static bool Same(PortraitLightingFitState? first, PortraitLightingFitState second)
        => first is not null && first.EditorIdentity == second.EditorIdentity && first.FixedStateKey == second.FixedStateKey
            && first.Width == second.Width && first.Height == second.Height && first.Parameters.SequenceEqual(second.Parameters)
            && first.Ranges.SequenceEqual(second.Ranges);
}
