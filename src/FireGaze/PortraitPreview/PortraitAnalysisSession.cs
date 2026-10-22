namespace FireGaze.PortraitPreview;

// Only Framework-observed, opaque equality tokens cross into analysis drawing.
internal sealed record PortraitAnalysisSession(PortraitCaptureOwner Owner, PortraitIdentity Editor,
    PortraitHistoryKey Entry);

internal sealed record PortraitAnalysisSubject(PortraitAnalysisSession Session, PortraitImageScope Scope);

internal sealed record PortraitAnalysisProvenance(string Source, DateTimeOffset At, bool IsRecordTime)
{
    public string Describe(string side) => $"{side} · {Source} · {(IsRecordTime ? "记录于" : "读取于")} {At.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
}
