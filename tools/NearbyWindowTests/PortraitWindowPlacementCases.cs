using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FireGaze.PortraitPreview;
using FireGaze.UI;

internal static class PortraitWindowPlacementCases
{
    internal static void Run(Action<bool, string> check)
    {
        // The real portrait positioning partials and placement helper run unchanged. The only
        // detached boundary is ImGui and the native editor's rectangle; no game/GPU is touched.
        ImGui.Known[2] = new(2, new(2000,100), new(1600,900));
        ImGui.CurrentViewport = ImGui.Known[3];
        ImGui.Position = new(5100,250); ImGui.Size = new(400,300);
        FireGazeWindowPlacement.Remember("FireGazePortraitPreview");
        FireGazeWindowPlacement.Remember("FireGazePortraitLightingSpace");
        FireGazeWindowPlacement.Remember("FireGazePortraitHistory");
        FireGazeWindowPlacement.Remember("FireGazePortraitLight");
        FireGazeWindowPlacement.Remember("FireGazePortraitAnalysis");
        ImGui.Frame++;
        ImGui.CurrentViewport = ImGui.Main;
        var window = new PortraitPreviewWindow();
        window.EditorBoundsForTest = new(100,100,600,700);
        window.PreDraw();
        check(window.SizeConstraints.MaximumSize == new Vector2(776,576),
            "actual preview PreDraw uses its moved 800x600 viewport rather than the main display");
        check(window.SizeConstraints.MinimumSize == new Vector2(700,540),
            "preview keeps its familiar minimum when it fits the actual viewport");
        ImGuiHelpers.GlobalScale = 2;
        window.PreDraw();
        check(window.SizeConstraints.MaximumSize == new Vector2(376,276)
            && window.SizeConstraints.MinimumSize == new Vector2(376,276),
            "actual preview constraints stay reachable after the host applies two-times scale");
        ImGuiHelpers.GlobalScale = 1;
        window.PrepareHistoryForTest();
        check(ImGui.Maximum == new Vector2(776,576),
            "actual history window constraints follow its moved viewport");
        window.PrepareLightForTest();
        check(ImGui.Maximum == new Vector2(560,576),
            "actual main-light window constraints fit its moved viewport without enlarging its normal cap");

        // Curve opening is covered at the caller boundary in PortraitAnalysisWindowTests.
        // Here both the default command route and the explicit opener route use the real
        // OpenLightingFit and PrepareLightingFitWindow methods.
        ImGui.Frame += 3;
        ImGui.CurrentViewport = ImGui.Main;
        ImGui.Position = new(100,100); ImGui.Size = new(600,700);
        FireGazeWindowPlacement.Remember("FireGazePortraitPreview");
        ImGui.CurrentViewport = ImGui.Known[3];
        ImGui.Position = new(5100,250); ImGui.Size = new(400,300);
        FireGazeWindowPlacement.Remember("FireGazePortraitAnalysis");
        ImGui.CurrentViewport = ImGui.Main;
        window.OpenLightingFit(); window.PrepareFitForTest();
        check(ImGui.NextViewport == 1 && ImGui.NextPosition == new Vector2(712,100),
            "command-opened curves retain the main preview as their default owner");
        window.OpenFitFromAnalysisForTest(); window.PrepareFitForTest();
        check(ImGui.NextViewport == 3 && ImGui.NextPosition.X >= 5000 && ImGui.NextPosition.X + ImGui.NextSize.X <= 5800,
            "analysis-opened curves appear on the analysis window's viewport");
        check(ImGui.Maximum == new Vector2(776,576),
            "analysis-opened curve constraints follow that secondary viewport");
        check(window.CaptureScopeForTest == PortraitImageScope.Portrait && window.FitOpenForTest,
            "opening curves keeps the existing portrait capture scope behavior");
        ImGui.CurrentViewport = ImGui.Known[2];
        ImGui.Position = new(2600,200); ImGui.Size = new(500,700);
        FireGazeWindowPlacement.Remember("FireGazePortraitLightingFit");
        ImGui.Frame++; ImGui.CurrentViewport = ImGui.Main; ImGui.NextViewport = 999;
        window.OpenFitFromAnalysisForTest(); window.PrepareFitForTest();
        check(ImGui.NextViewport == 999 && ImGui.PositionCondition == ImGuiCond.Appearing
            && ImGui.Maximum == new Vector2(1576,876),
            "reopening an already-visible curve tool does not pull a moved window back to its opener");

        // The native editor remains on the game's main viewport. Its adjacent button must
        // follow the editor, yet fit near either edge on the first frame before Begin sizes it.
        var feature = new PortraitPreviewFeature();
        feature.DrawAnchorForTest(new(100,100,600,700));
        check(ImGui.NextPosition.X >= 700 && ImGui.NextPosition.Y == 130,
            "native editor button stays beside a fitting editor at its familiar top offset");
        feature.DrawAnchorForTest(new(1800,100,120,700));
        var buttonSize = new Vector2(104,41); // text 80 + frame padding 8 + window padding 16.
        check(ImGui.NextPosition.X >= 0 && ImGui.NextPosition.X + buttonSize.X <= 1920
            && ImGui.NextPosition.X < 1800,
            "native editor's first-frame button remains reachable when the editor touches the right edge");
        feature.DrawAnchorForTest(new(1800,1050,120,30));
        check(ImGui.NextPosition.X >= 0 && ImGui.NextPosition.X + buttonSize.X <= 1920
            && ImGui.NextPosition.Y >= 0 && ImGui.NextPosition.Y + buttonSize.Y <= 1080,
            "native editor's adjacent button remains reachable at the bottom-right corner");
        feature.DrawAnchorForTest(new(300,200,600,700));
        check(ImGui.NextPosition.X >= 900 && ImGui.NextPosition.Y == 230 && ImGui.PositionCondition == ImGuiCond.Always,
            "editor-bound button follows native editor movement without changing movable child policy");
    }
}

namespace Dalamud.Interface.Utility
{
    internal static class ImGuiHelpers { public static float GlobalScale = 1; }
}
namespace Dalamud.Interface.Windowing
{
    internal struct WindowSizeConstraints { public Vector2 MinimumSize, MaximumSize; }
    internal class Window
    {
        public Vector2? Size;
        public ImGuiCond SizeCondition;
        public WindowSizeConstraints SizeConstraints;
        public virtual void PreDraw() { }
    }
}
namespace FireGaze.PortraitPreview
{
    internal readonly record struct PortraitEditorBounds(float X, float Y, float Width, float Height);
    internal enum PortraitImageScope { Portrait, VisibleAdventurerPlate }
    internal sealed partial class PlacementModel
    {
        public PortraitImageScope Scope = PortraitImageScope.VisibleAdventurerPlate;
        public void SetLiveCaptureScope(PortraitImageScope scope) => Scope = scope;
        public bool CanUndo => true;
        public bool CanRedo => true;
        public int UndoCalls, RedoCalls, FavoriteCalls, SyncCalls, NativeLiveCalls, CloseCalls;
        public void ClosePreview() => CloseCalls++;
        public bool SyncPlate { get; private set; }
        public bool NativeLiveEnabled { get; set; }
        public string Status { get; set; } = "";
        public bool StatusIsError { get; set; }
        public IReadOnlyList<string> SourceSummary => [];
        public void Undo() => UndoCalls++;
        public void Redo() => RedoCalls++;
        public void SaveFavorite(string name, IReadOnlyCollection<string> tags) => FavoriteCalls++;
        public void SetSyncPlate(bool value) { SyncPlate = value; SyncCalls++; }
        public void SetNativeLiveEnabled(bool value) { NativeLiveEnabled = value; NativeLiveCalls++; }
        public PortraitNativeLiveBorrow? TryBorrowNativeLive() => NativeLiveEnabled ? new() : null;
    }
    internal readonly record struct NativeLiveImage(nint Handle, int PixelWidth, int PixelHeight,
        Vector2 UV0, Vector2 UV1, uint Tint);
    internal sealed class PortraitNativeLiveBorrow : IDisposable
    {
        public bool TryGetImage(out NativeLiveImage image)
        { image = new(99, 400, 600, Vector2.Zero, Vector2.One, uint.MaxValue); return true; }
        public void Dispose() { }
    }
    internal sealed partial class PortraitPreviewWindow : Dalamud.Interface.Windowing.Window
    {
        private static float Scale => ImGui.GetFontSize() / 17f;
        private readonly PlacementModel feature = new();
        private PortraitEditorBounds? ownerBounds = null;
        private bool showLightingFit;
        private bool showHistory, showFavorites, showGlamourPlates, appearanceTab, showDesignShowcase;
        private bool showSpaceLight = false, showAnalysis = false;
        internal PlacementModel ModelForTest => feature;
        internal bool DesignTaskForTest => showDesignShowcase;
        internal (bool History, bool Favorites, bool Templates, bool Space, bool Analysis, bool Beauty) ToolStateForTest
            => (showHistory, showFavorites, showGlamourPlates, showSpaceLight, showAnalysis, appearanceTab);
        internal void DrawHeaderForTest() => DrawWorkspaceHeader();
        private void OpenFavorites() => showFavorites = true;
        internal void DrawPortraitForTest() => DrawWorkspacePortrait();
        internal void DrawSourceForTest() => DrawSourceRecords();
        internal bool DrawFooterForTest()
        {
            var method = GetType().GetMethod("DrawWorkspaceFooter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (method is null) return false;
            method.Invoke(this, null); return true;
        }
        internal void PrepareHistoryForTest() => PrepareHistoryWindow();
        internal void PrepareLightForTest() => PrepareLightWindow();
        internal void PrepareFitForTest() => PrepareLightingFitWindow();
        internal PortraitImageScope CaptureScopeForTest => feature.Scope;
        internal bool FitOpenForTest => showLightingFit;
        internal PortraitEditorBounds EditorBoundsForTest { set => ownerBounds = value; }
        internal void OpenFitFromAnalysisForTest()
        {
            var method = GetType().GetMethod("OpenLightingFit")!;
            // The baseline has no opener argument, so exercise its real existing route
            // before asserting the missing behavior; there is no test-only production branch.
            method.Invoke(this, method.GetParameters().Length == 0 ? [] : ["FireGazePortraitAnalysis"]);
        }
    }
    internal sealed unsafe partial class PortraitPreviewFeature
    {
        private bool editorAvailable = true;
        private bool IsActive => false;
        private void OpenPreview() { }
        private void ClosePreview() { }
        internal void DrawAnchorForTest(PortraitEditorBounds bounds)
        { SetPreviewAnchorBounds(bounds); DrawPreviewAnchor(); }
    }
}
