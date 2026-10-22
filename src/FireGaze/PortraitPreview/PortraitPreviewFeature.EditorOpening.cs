using System.Diagnostics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitEditorOpeningController editorOpening = null!;
    private bool editorOpeningSubscribed;
    private double nextOpeningAvailability;
    private string? lastOpeningPlayerError;
    public PortraitOpeningAvailability OpeningAvailability => editorOpening.ReadAvailability();
    public PortraitOpeningFailure? LastOpeningFailure => editorOpening.ReadLastFailure();

    private void InitializeEditorOpening(PortraitEditorOpeningController? controller = null)
    {
        editorOpening = controller ?? new(ReadEditorOpeningSample,
            () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, ReadEditorOpeningIssues);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "BannerEditor", BeforeEditorOpeningFinalize);
        Plugin.ClientState.Logout += EditorOpeningLogout;
        Plugin.ClientState.TerritoryChanged += EditorOpeningTerritoryChanged;
        editorOpeningSubscribed = true;
    }

    // Diagnostics/safety sampling must not perform native reads, progress readiness or revoke an
    // expired receipt. Framework Update and lifecycle callbacks own those explicit transitions.
    private PortraitEditorOpeningReceipt ReadEditorOpeningReceipt() => editorOpening.Read();
    private void UpdateEditorOpening()
    {
        editorOpening.Update();
        if (editorOpening.Read().Status is "ReturnedPending" or "Ready") return;
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now < nextOpeningAvailability) return;
        nextOpeningAvailability = now + 0.15;
        editorOpening.RefreshAvailability();
        if (!OpeningAvailability.HasBlockers) lastOpeningPlayerError = null;
    }

    private void ReportOpeningUnavailable(PortraitOpeningAvailability unavailable)
    {
        var message = string.Join("\n", unavailable.Issues.Select(issue => issue.Message));
        if (string.IsNullOrWhiteSpace(message)) message = unavailable.PrimaryMessage;
        if (lastOpeningPlayerError == message) return;
        lastOpeningPlayerError = message;
        Error(message);
    }

    private void OpenEditorWithReceipt(string kind, string nonce)
    {
        editorOpening.RefreshAvailability(kind);
        var available = OpeningAvailability;
        if (available.Opening) { SetStatus("正在打开铭牌，请稍候。"); return; }
        if (available.EditorAlreadyOpen && !available.HasBlockers)
        { SetStatus("铭牌编辑器已打开，可以启用 FireGaze 预览。"); return; }
        if (!available.CanOpenEditor)
        { ReportOpeningUnavailable(available); return; }
        if (editorOpening.Open(kind, nonce, () => OpenEditorCore(kind)))
        { lastOpeningPlayerError = null; return; }
        var failure = editorOpening.ReadLastFailure();
        Plugin.Log.Warning("[肖像同步预览] Editor opening rejected: {Reason}", failure?.TechnicalReason ?? editorOpening.Read().Reason ?? "Invalid or repeated request.");
        ReportOpeningUnavailable(OpeningAvailability.CanOpenEditor
            ? PortraitOpeningAvailability.Finish(false, false, false, [PortraitOpeningAvailability.Issue("OpeningRequestExpired")])
            : OpeningAvailability);
    }

    private void BeforeEditorOpeningFinalize(AddonEvent type, AddonArgs args)
        => editorOpening.Invalidate("BannerEditor PreFinalize revoked native editor ownership.");
    private void EditorOpeningLogout(int type, int code)
        => editorOpening.Invalidate("Logout revoked source ownership.");
    private void EditorOpeningTerritoryChanged(uint territory)
        => editorOpening.Invalidate("Territory change revoked source ownership.");

    private void DisposeEditorOpening()
    {
        editorOpening.Invalidate("Feature disposal revoked native editor ownership.");
        if (!editorOpeningSubscribed) return;
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "BannerEditor", BeforeEditorOpeningFinalize);
        Plugin.ClientState.Logout -= EditorOpeningLogout;
        Plugin.ClientState.TerritoryChanged -= EditorOpeningTerritoryChanged;
        editorOpeningSubscribed = false;
    }
}
