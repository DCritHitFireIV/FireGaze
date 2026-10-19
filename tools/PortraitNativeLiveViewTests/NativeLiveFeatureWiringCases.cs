internal static class NativeLiveFeatureWiringCases
{
    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("feature and window keep native live view explicit and default off", () =>
        {
            var root = FindRoot();
            var feature = File.ReadAllText(Path.Combine(root, "src", "FireGaze", "PortraitPreview", "PortraitPreviewFeature.NativeLive.cs"));
            var window = File.ReadAllText(Path.Combine(root, "src", "FireGaze", "PortraitPreview", "PortraitPreviewWindow.NativeLive.cs"));
            var main = File.ReadAllText(Path.Combine(root, "src", "FireGaze", "PortraitPreview", "PortraitPreviewFeature.cs"));
            check(feature.Contains("private bool nativeLiveRequested;", StringComparison.Ordinal)
                && !feature.Contains("private bool nativeLiveRequested = true", StringComparison.Ordinal),
                "native live intent must begin false and require an explicit user action");
            check(feature.Contains("captureIsolation.Mode", StringComparison.Ordinal)
                && feature.Contains("CapturesDrained", StringComparison.Ordinal)
                && feature.Contains("LightingFitMutationBlocked", StringComparison.Ordinal),
                "feature qualification must preserve the controller capture and lighting barriers");
            check(main.Contains("UpdateNativeLive()", StringComparison.Ordinal)
                && main.Contains("StopNativeLive(PortraitNativeLiveStopReason.UserClosed)", StringComparison.Ordinal),
                "Framework update and preview close must own controller update and retirement");
            check(window.Contains("TryBorrowNativeLive", StringComparison.Ordinal)
                && window.Contains("using var", StringComparison.Ordinal)
                && window.Contains("AddImage", StringComparison.Ordinal),
                "window must hold one borrow through AddImage submission");
            check(!feature.Contains("ApplyLighting", StringComparison.Ordinal)
                && !window.Contains("ApplyLighting", StringComparison.Ordinal)
                && !window.Contains("Camera", StringComparison.Ordinal),
                "stage A wiring must not expose camera or lighting mutation");
        });
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "build-local-preview.ps1")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
