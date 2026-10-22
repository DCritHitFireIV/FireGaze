namespace FireGaze.UI;

internal sealed record InstallerListScrollSnapshot(
    bool IsOpen,
    bool LayoutChecked,
    bool LayoutCompatible,
    bool HasListContent,
    string RestorePhase,
    bool RestoreFailed,
    float RestoreTarget,
    int RestoreFrames,
    long MaskedRefreshes,
    DateTime LastMaskedLocal,
    bool ReflectionReady);
