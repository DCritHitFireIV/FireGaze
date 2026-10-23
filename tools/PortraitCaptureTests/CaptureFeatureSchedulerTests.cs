// Native ImGui is outside this test boundary. This alias is used only by the
// source-linked Feature.Capture partial; every scheduler branch stays real.
global using ImGui = CaptureSchedulerImGui;

using System.Numerics;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal static class CaptureFeatureSchedulerTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool value, string message)
        { checks++; if (!value) throw new InvalidOperationException("Feature capture scheduler: " + message); }

        // Break caught: resetting the viewport every observation defeats the
        // retry deadline and continually restarts an unchanged source's settling.
        using (var b = new SchedulerFixture())
        {
            b.Start();
            b.Api.Pending.SetResult(null); b.Feature.CaptureTick();
            Check(b.Feature.CaptureError is { Length: > 0 }, "completed null supplies the actual scheduler's retry signal");
            b.Feature.CaptureRetryAt(DateTime.MaxValue);
            for (var frame = 0; frame < 4; frame++)
            { b.Feature.CaptureTick(); b.Feature.CaptureDraw(); b.Advance(); }
            Check(b.Api.Captures == 1 && b.Feature.CaptureError is { Length: > 0 },
                "a retry before its deadline retains the failed request without another physical capture");

            // No clock abstraction replaces DateTime.UtcNow in production. The
            // test host chooses past/future deadlines and executes the real comparison.
            b.Feature.CaptureRetryAt(DateTime.MinValue);
            b.Feature.CaptureTick(); b.Feature.CaptureDraw(); b.Feature.CaptureTick();
            Check(b.Feature.CaptureError is null && b.Api.Captures == 1,
                "an expired deadline creates a fresh Request that must settle before copying");
            b.Advance(); b.Feature.CaptureTick();
            Check(b.Api.Captures == 2, "the real Feature retry path eventually starts a second copy");
            for (var frame = 0; frame < 4; frame++)
            { b.Advance(); b.Feature.CaptureTick(); b.Feature.CaptureDraw(); }
            Check(b.Api.Captures == 2 && b.Feature.CaptureBusy,
                "unchanged viewport and cleared error cannot duplicate or reset the pending retry");
            b.Api.Complete(new(576, 960, [new(new CountingCaptureTexture())]));
            b.Feature.CaptureTick(); b.Feature.CaptureDraw(); b.Feature.CaptureTick();
            using var image = b.Feature.AcquireCurrentLiveImage();
            Check(image is { Width: 576, Height: 960 } && b.Api.Composes == 1 && b.Feature.CaptureError is null,
                "automatic Feature retry publishes a genuine replacement without a manual Request");
        }

        // Break caught: Draw suppression or isolation expiry must not submit a
        // new Request simply because an old transient error is due for retry.
        using (var b = new SchedulerFixture())
        {
            b.Start(); b.Api.Pending.SetResult(null); b.Feature.CaptureTick();
            b.Feature.CaptureRetryAt(DateTime.MinValue); b.Feature.CaptureSuppress(true);
            b.Feature.CaptureTick(); b.Feature.CaptureDraw(); b.Advance(); b.Feature.CaptureTick();
            Check(b.Api.Captures == 1 && b.Feature.CaptureError is { Length: > 0 },
                "a suppressed Draw leaves the due retry unsent");
            b.Feature.CaptureSuppress(false); b.Feature.CaptureDraw(); b.Feature.CaptureTick();
            b.Advance(); b.Feature.CaptureTick();
            Check(b.Api.Captures == 2, "lifting Draw suppression allows the existing due retry to settle");
            var pending = b.Api.Pending;
            b.ExpireIsolation(); b.Feature.CaptureTick(); b.Feature.CaptureDraw();
            Check(b.Feature.CaptureBusy && !pending.Task.IsCompleted && b.Api.Captures == 2,
                "expired isolation retains the unfinished physical task and starts none");
            var late = new CountingCaptureTexture(); pending.SetResult(new(576, 960, [new(late)]));
            b.Feature.CaptureTick(); b.Feature.CaptureDraw(); b.Feature.CaptureTick();
            Check(!b.Feature.CaptureBusy && late.Disposes == 1 && b.Api.Composes == 0
                && b.Feature.AcquireCurrentLiveImage() is null,
                "isolation revocation releases a late retry without composition or publication");
        }

        // Break caught: a closed preview must revoke an already queued retry
        // while keeping its actual task owned until the provider finishes.
        using (var b = new SchedulerFixture())
        {
            b.Start(); var pending = b.Api.Pending;
            b.Feature.CaptureCloseSession(); b.Feature.CaptureTick(); b.Feature.CaptureDraw();
            Check(b.Feature.CaptureBusy && b.Api.Captures == 1 && !pending.Task.IsCompleted,
                "real session close does not abandon the pending copy or submit a replacement");
            var late = new CountingCaptureTexture(); pending.SetResult(new(576, 960, [new(late)]));
            b.Advance(); b.Feature.CaptureTick(); b.Feature.CaptureDraw(); b.Feature.CaptureTick();
            Check(!b.Feature.CaptureBusy && late.Disposes == 1 && b.Api.Composes == 0
                && b.Feature.AcquireCurrentLiveImage() is null && b.Api.Captures == 1,
                "late closed-session pixels cannot be displayed or trigger another capture");
        }
        Console.WriteLine($"CaptureFeatureSchedulerTests: {checks} checks passed; real Feature partial/service/gate/isolation, controlled renderer/provider boundaries.");
        return checks;
    }

    private sealed class SchedulerFixture : IDisposable
    {
        internal readonly FakeCaptureApi Api = new();
        internal readonly PortraitPreviewFeature Feature;
        private DateTimeOffset captureClock = DateTimeOffset.UnixEpoch;
        private TimeSpan isolationClock;
        internal SchedulerFixture()
        {
            Feature = new(Api, () => captureClock, () => isolationClock);
            CaptureSchedulerImGui.Viewport = new(Api.Sample.Viewport.ID, Api.Sample.Viewport.Position, Api.Sample.Viewport.Size);
        }
        internal void Advance() => captureClock = captureClock.AddMilliseconds(250);
        internal void ExpireIsolation() => isolationClock = TimeSpan.FromSeconds(61);
        internal void Start()
        {
            Feature.CaptureTick(); Feature.CaptureDraw(); Feature.CaptureTick();
            Advance(); Feature.CaptureTick();
            if (Api.Captures != 1) throw new InvalidOperationException("real Feature did not start one settled source capture");
        }
        public void Dispose()
        {
            Feature.CaptureDispose(); Api.Pending.TrySetResult(null);
            Feature.CaptureDrain.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }
}

internal static class CaptureSchedulerImGui
{
    internal readonly record struct ViewportFrame(uint ID, Vector2 Pos, Vector2 Size);
    internal static ViewportFrame Viewport;
    public static ViewportFrame GetMainViewport() => Viewport;
}

namespace FireGaze
{
    // InitializeLiveCapture is compiled but deliberately never called. Accidentally
    // entering live initialization fails here instead of contacting any game API.
    internal static class Plugin
    {
        internal static IFramework Framework => throw new InvalidOperationException("Live initialization is outside the scheduler test boundary.");
        internal static IGameGui GameGUI => throw new InvalidOperationException("No game GUI is attached.");
        internal static IObjectTable Objects => throw new InvalidOperationException("No game objects are attached.");
        internal static IAddonLifecycle AddonLifecycle => throw new InvalidOperationException("No game lifecycle is attached.");
        internal static ITextureProvider Textures => throw new InvalidOperationException("No GPU provider is attached.");
        internal static ITextureReadbackProvider TextureReadback => throw new InvalidOperationException("No GPU readback is attached.");
    }
}

namespace FireGaze.PortraitPreview
{
    internal sealed unsafe partial class PortraitPreviewFeature
    {
        private readonly SchedulerTarget target = new();
        private readonly PortraitPreviewSession session;
        private readonly PortraitCaptureIsolation captureIsolation;
        private bool LightingFitHistorySuppressed;
        private bool pointerHeld => false;
        private bool sampledKeyboard => false;
        internal PortraitPreviewFeature(IPortraitLiveImageApi api, Func<DateTimeOffset> clock, Func<TimeSpan> isolationClock)
        {
            session = new(target); captureIsolation = new(isolationClock);
            if (!captureIsolation.TryConfigure("live", false, false) || !session.Open(target.Identity))
                throw new InvalidOperationException("Scheduler host failed to open its local session.");
            liveImageService = new(api, clock);
        }
        private static bool CancelLightingFitForEdit() => true;
        private static void Mutate(Action action) => action();
        internal void CaptureTick()
        { session.Validate(); ObserveLiveCaptureState(); liveImageService!.Update(captureIsolation.LiveEnabled && session.IsActive); }
        internal void CaptureDraw() => DrawLiveCapture();
        internal void CaptureRetryAt(DateTime deadline) => nextCaptureRetry = deadline;
        internal void CaptureSuppress(bool value) => LightingFitHistorySuppressed = value;
        internal void CaptureCloseSession() { if (!session.Close()) throw new InvalidOperationException("Local session did not close."); }
        internal bool CaptureBusy => liveImageService!.IsBusy;
        internal string? CaptureError => liveImageService!.Error;
        internal void CaptureDispose() => liveImageService!.Dispose();
        internal Task CaptureDrain => liveImageService!.DrainPhysicalAsync();

        // The native target is an external boundary. Its ready immutable snapshot
        // stays fixed; the real Feature hashes it and owns retry timing/requests.
        private sealed class SchedulerTarget : IPortraitPreviewTarget
        {
            internal PortraitIdentity Identity { get; } = new(11, 12, 13, 14, 15);
            internal bool CanBlockSave => true;
            internal bool TryResolve(out PortraitIdentity identity, out int bounds, out int openType)
            { identity = Identity; bounds = openType = 0; return true; }
            internal SchedulerSnapshot CaptureEditorState(PortraitIdentity identity) => new();
            public bool IsCurrent(PortraitIdentity identity) => identity == Identity;
            public PortraitLook Capture(PortraitIdentity identity) => new(new PortraitPiece[13], true, true, false);
            public bool Apply(PortraitIdentity identity, PortraitLook look) => true;
        }
        private sealed class SchedulerSnapshot
        {
            internal SchedulerComposition Composition { get; } = new();
            internal string ToJSON() => "{\"paused\":true,\"pose\":1}";
        }
        private sealed class SchedulerComposition { internal bool Paused => true; }
    }
}
