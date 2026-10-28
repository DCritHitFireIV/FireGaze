namespace FireGaze.LocalPreview.Interaction;

/// <summary>
/// Shared player-facing state for mount and accessory catalogs. The view reads one immutable value;
/// it never infers a transition from a partially updated runtime effect list.
/// </summary>
public enum TrialPhase
{
    Ready,
    Active,
    Switching,
    Stopping,
    Unknown,
}

public sealed record TrialPresentation(TrialPhase Phase, uint ActiveId, string ActiveName)
{
    public static TrialPresentation Ready { get; } = new(TrialPhase.Ready, 0, string.Empty);
    public static TrialPresentation Switching { get; } = new(TrialPhase.Switching, 0, string.Empty);
    public static TrialPresentation Stopping { get; } = new(TrialPhase.Stopping, 0, string.Empty);
    public static TrialPresentation Unknown { get; } = new(TrialPhase.Unknown, 0, string.Empty);

    public bool CanStart => Phase is TrialPhase.Ready or TrialPhase.Active;
    public bool CanStop => Phase is TrialPhase.Active or TrialPhase.Switching;

    public string StatusText => Phase switch
    {
        TrialPhase.Ready => "未开始",
        TrialPhase.Active => string.IsNullOrWhiteSpace(ActiveName)
            ? "试用中" : $"试用中 · {ActiveName}",
        TrialPhase.Switching => "正在切换",
        TrialPhase.Stopping => "正在结束试用",
        _ => "暂时无法确认试用状态，请稍后重试。",
    };

    public static TrialPresentation Active(uint id, string? name)
        => new(TrialPhase.Active, id, name?.Trim() ?? string.Empty);

    public static TrialPresentation Observe(uint activeId, string? activeName,
        bool switchPending, bool stopPending)
        => stopPending ? Stopping
            : switchPending ? Switching
            : activeId != 0 ? Active(activeId, activeName) : Ready;
}
