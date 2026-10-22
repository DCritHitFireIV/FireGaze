namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private long playerErrorRevision;

    private bool ApplyWithFailureNotice(Func<bool> apply, string fallback)
    {
        var before = playerErrorRevision;
        var applied = apply();
        if (!applied) ReportFailureIfUnreported(before, fallback);
        return applied;
    }

    private void ReportFailureIfUnreported(long before, string message)
    {
        if (playerErrorRevision == before) Error(message);
    }
}
