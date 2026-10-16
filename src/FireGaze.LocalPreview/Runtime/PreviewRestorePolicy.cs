namespace FireGaze.LocalPreview.Runtime;

public enum PreviewExitReason { Manual, Busy, ZoneChanged, ActorChanged }
public readonly record struct PreviewRestoreDecision(bool TouchActor, bool RestoreBaseline, bool RestoreMode);

/// <summary>Baseline presentation is restored only while that presentation remains legal.</summary>
public static class PreviewRestorePolicy
{
    public static PreviewRestoreDecision Decide(PreviewExitReason reason, bool actorAllowsPreview, bool ownsMode,
        bool bindingCurrent = true)
    {
        if (!bindingCurrent || reason is PreviewExitReason.ActorChanged or PreviewExitReason.ZoneChanged) return new(false,false,false);
        bool baseline = reason == PreviewExitReason.Manual && actorAllowsPreview;
        return new(true,baseline,baseline && ownsMode);
    }
}
