namespace FireGaze.PortraitPreview;

internal sealed record PortraitLightingFitView(bool Busy,bool CanStart,string Status,
    PortraitLightingFitSessionPhase Phase,PortraitLightingFitSessionOutcome Outcome,int Width,int Height,
    string SourceKey,string EditorOwnerKey,string FixedStateKey,bool CleanupPending,bool CapturePending,
    string CaptureStateKey = "");
