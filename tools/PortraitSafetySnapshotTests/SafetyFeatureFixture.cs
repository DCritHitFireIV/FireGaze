#if SAFETY_SNAPSHOT_FEATURE
using System.Numerics;
using System.Reflection;
using System.Text.Json;

namespace FireGaze
{
    // Deliberately no game actions, native editor mutations, capture tasks, Poll or Diagnostics
    // facade. The actual adapter must compile against this readonly capability surface.
    internal static class Plugin
    {
        internal static readonly SafetyFramework Framework = new();
        internal static readonly SafetyConditions Conditions = new();
        internal static readonly SafetyObjects Objects = new();
        internal static readonly SafetyClientState ClientState = new();
    }
    internal sealed class SafetyFramework
    {
        internal bool IsInFrameworkUpdateThread;
        internal int Dispatches;
        internal T Dispatch<T>(Func<T> callback)
        {
            Dispatches++;
            var previous = IsInFrameworkUpdateThread;
            IsInFrameworkUpdateThread = true;
            try { return callback(); }
            finally { IsInFrameworkUpdateThread = previous; }
        }
    }
    internal sealed class SafetyConditions { internal nint Address => 0; internal int MaxEntries => 112; }
    internal sealed class SafetyObjects { internal SafetyPlayer? LocalPlayer => null; }
    internal sealed class SafetyClientState { internal uint TerritoryType => 0; }
    internal sealed class SafetyPlayer
    {
        internal nint Address => 0;
        internal ulong GameObjectId => 0;
        internal Vector3 Position => default;
        internal SafetyJob ClassJob => new();
        internal uint CurrentHp => 0;
        internal bool IsCasting => false;
        internal uint CastActionId => 0;
    }
    internal sealed class SafetyJob { internal uint RowId => 0; }
}

namespace FireGaze.PortraitPreview
{
    internal sealed partial class PortraitPreviewFeature
    {
        private bool disposed;
        private readonly SafetyPluginInterface pluginInterface = new();
        private readonly SafetyTarget target = new();
        private readonly SafetySession session = new();
        private readonly SafetyPhotos photos = new();
        private readonly SafetyHistory history = new();
        private readonly PortraitPreviewWindow window = new();
        private readonly SafetyLightDrag lightDrag = new();
        private readonly SafetyLiveImage liveImageService = new();
        private readonly SafetyFit lightingFitAdapter = new(), lightingFitCapture = new(), lightingFitSession = new();
        private bool syncPlate, plateMirrorClosePending, pointerHeld, sampledPointer, sampledKeyboard, lightingFitHistorySuppressed;
        private long lightInputToken;
        private readonly PortraitCaptureIsolation captureIsolation;
        private bool LightingFitMutationBlocked => lightingFitSession.IsActive || lightingFitAdapter.HasPendingCapture || lightingFitCapture.IsBusy;
        internal PortraitPreviewFeature(Func<TimeSpan>? clock = null, Func<PortraitSafetyTrialState>? trials = null)
        { captureIsolation = new(clock); readWorldTrials = trials; }
        private T OnFramework<T>(Func<T> action) => Plugin.Framework.IsInFrameworkUpdateThread ? action() : Plugin.Framework.Dispatch(action);
        private SafetyHistoryKey? GetHistoryKey(SafetyIdentity identity, SafetyEditor captured) => new(1, 0);
        internal IPortraitSafetySnapshotSource Source() => (IPortraitSafetySnapshotSource)Activator.CreateInstance(
            typeof(PortraitPreviewFeature).GetNestedType("SafetySnapshotSource", BindingFlags.NonPublic)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [this], null)!;
        internal int EditorReads => target.ResolveCalls;
        internal int Captures => target.CaptureCalls;
        internal void SetDisposed() => disposed = true;
        internal void MakeEditorUnknown() => target.Ready = false;
        internal void SetMutationCounters(long value)
        {
            lightDrag.CurrentToken = value; lightDrag.LastToken = value;
            lightInputToken = value; window.SetLight(value);
            syncPlate = true; plateMirrorClosePending = true; pointerHeld = true;
            sampledPointer = true; sampledKeyboard = true; lightingFitHistorySuppressed = true;
        }
        internal bool ConfigureLease() => captureIsolation.TryConfigure("history", false, false);
        internal PortraitCaptureLeaseSnapshot Lease => captureIsolation.Peek();
    }
    internal sealed partial class PortraitPreviewWindow
    {
        private bool showLight;
        private long lightPointerToken, lightTokenSerial;
        internal bool HistoryWindowOpen => false;
        internal bool HistoryPhotosView => false;
        internal void SetLight(long value) { showLight = true; lightPointerToken = value; lightTokenSerial = value; }
    }
    internal sealed class SafetyPluginInterface
    {
        internal bool IsDev => true;
        internal FileInfo AssemblyLocation => new(Path.Combine(Path.GetTempPath(), "FireGaze-watched-slot", "FireGaze.dll"));
    }
    internal sealed class SafetyTarget
    {
        internal string SnapshotGameBuild => "audited-test-fixture";
        internal bool Ready = true;
        internal int ResolveCalls, CaptureCalls;
        internal bool CanBlockSave => true;
        internal string? LastError => null;
        internal uint BlockedSaves => 0;
        internal uint BlockedSaveEvents => 0;
        internal bool IsPlateMirrorActive => false;
        internal bool TryResolve(out SafetyIdentity identity, out object? bounds, out string openType)
        { ResolveCalls++; identity = new(3, 4, 5, 9, 6); bounds = null; openType = "Gearset"; return Ready; }
        internal SafetyEditor CaptureEditorState(SafetyIdentity identity) { CaptureCalls++; return new(); }
        internal bool IsCurrent(SafetyIdentity identity) => Ready;
    }
    internal readonly record struct SafetyIdentity(nint Editor, nint State, nint Model, uint Generation, ulong Character);
    internal readonly record struct SafetyHistoryKey(ulong ContentID, int SourceID);
    internal sealed class SafetyEditor
    {
        internal bool IsValid => true;
        internal string ToJSON() => new PortraitEditorSnapshot
        { GameBuild = "fresh-target-fixture", EntryPoint = 1, Composition = new() { CameraYaw = 1 } }.ToJSON();
    }
    internal sealed class SafetySession { internal bool IsActive => false; }
    internal sealed class SafetyPhotos
    {
        // Maintenance is busy even while no image capture is active.
        internal bool IsBusy => true;
        internal object Diagnostics() => new { capturing = false, saved = 7, discarded = 2 };
    }
    internal sealed class SafetyHistory
    {
        internal int Count => 5;
        internal int Bytes => 800;
        internal long? CurrentRecordID(SafetyHistoryKey key) => 99;
    }
    internal sealed class SafetyLightDrag { internal long CurrentToken, LastToken; }
    internal sealed class SafetyLiveImage { internal object Diagnostics => new { busy = false }; }
    internal sealed class SafetyFit
    {
        internal bool IsActive => false;
        internal bool IsBusy => false;
        internal bool HasPendingCleanup => false;
        internal bool HasPendingCapture => false;
    }
}
#endif
