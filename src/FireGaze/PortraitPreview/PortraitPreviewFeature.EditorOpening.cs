using System.Diagnostics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitEditorOpeningController editorOpening = null!;
    private bool editorOpeningSubscribed;

    private void InitializeEditorOpening(PortraitEditorOpeningController? controller = null)
    {
        editorOpening = controller ?? new(ReadEditorOpeningSample,
            () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "BannerEditor", BeforeEditorOpeningFinalize);
        Plugin.ClientState.Logout += EditorOpeningLogout;
        Plugin.ClientState.TerritoryChanged += EditorOpeningTerritoryChanged;
        editorOpeningSubscribed = true;
    }

    // Diagnostics/safety sampling must not perform native reads, progress readiness or revoke an
    // expired receipt. Framework Update and lifecycle callbacks own those explicit transitions.
    private PortraitEditorOpeningReceipt ReadEditorOpeningReceipt() => editorOpening.Read();
    private void UpdateEditorOpening() => editorOpening.Update();

    private void OpenEditorWithReceipt(string kind, string nonce)
    {
        if (!editorOpening.Open(kind, nonce, () => OpenEditorCore(kind)))
            Error("编辑器打开凭据未成立：" + (editorOpening.Read().Reason ?? "无效或重复的请求标识。"));
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
