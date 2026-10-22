namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitAnalysisSession? analysisSession;
    public PortraitAnalysisSession? CurrentAnalysisSession => Volatile.Read(ref analysisSession);

    // Called on Framework after existing observation/mutation. Draw reads one immutable reference.
    private void PublishAnalysisSession()
    {
        var next = !disposed && session.IsActive && historyOwner is { AccountID: not 0, ContentID: not 0 } owner
            && observedIdentity.IsValid && observedIdentity.Generation != 0
            && observedKey is { IsValid: true } entry && entry.ContentID == owner.ContentID
            ? new PortraitAnalysisSession(new(owner.AccountID, owner.ContentID), observedIdentity, entry) : null;
        Volatile.Write(ref analysisSession, next);
    }
}
