using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitSourceCensusDiagnostic? sourceCensus;
    private long sourceCensusSessionEpoch = 1;
    private Task sourceCensusPhysicalDrain = Task.CompletedTask;
    private bool sourceCensusSubscribed;

    public bool SourceCensusBlocksOperations => sourceCensus?.BlocksOperations == true || ownedPassive?.BlocksOperations == true;
    private PortraitSourceCensusSafetyState ReadSourceCensusSafetyState()
        => sourceCensus?.ReadSafetyState() ?? PortraitSourceCensusSafetyState.Unknown;

    private void InitializeSourceCensus(PortraitSourceCensusDiagnostic? diagnostic = null)
    {
        sourceCensus = diagnostic ?? new(() => Plugin.Framework.IsInFrameworkUpdateThread,
            ReadSourceCensusContext, new DalamudPortraitNativePhaseHostFactory(Plugin.Interop));
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "BannerEditor", BeforeSourceCensusFinalize);
        Plugin.ClientState.Logout += SourceCensusLogout;
        Plugin.ClientState.TerritoryChanged += SourceCensusTerritoryChanged;
        sourceCensusSubscribed = true;
    }

    private PortraitSourceCensusContext? ReadSourceCensusContext()
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread || !session.IsActive || !target.CanBlockSave
            || !target.TryResolve(out var source, out _, out _) || !session.BlockSave(source)
            || CurrentCharacter() is not { } owner) return null;
        var reason = captureIsolation.Mode != "off" ? "Capture isolation must be off."
            : NativeLiveEnabled || nativeLiveController?.IsPhysicallyIdle == false ? "Native-live work has not physically retired."
            : LightingFitMutationBlocked || lightDrag.IsActive ? "A lighting operation is active."
            : photos.IsBusy || !photos.IsPhysicallyIdle ? "Photo work has not physically retired."
            : liveImageService is { IsPhysicallyIdle: false } ? "Live-image work has not physically retired."
            : lightingFitCapture is { IsPhysicallyIdle: false } ? "Lighting capture has not physically retired."
            : !window.IsSourceCensusIdle ? "An analysis or comparison image is still owned."
            : null;
        return new(source, owner.AccountID, owner.ContentID, Interlocked.Read(ref sourceCensusSessionEpoch), reason);
    }

    private bool TrySourceCensusCommand(string[] parts)
    {
        if (parts.Length == 0 || parts[0] != "native-census") return false;
        if (parts is ["native-census", "start"])
            Mutate(() =>
            {
                if (ownedPassive?.BlocksOperations == true) Error("原生转发诊断尚未完成实际退出。");
                else if (sourceCensus is null) Error("只读八槽诊断尚未初始化。");
                else if (!sourceCensus.TryStart(out var reason)) Error("只读八槽诊断未启动：" + reason);
                else Plugin.Chat.Print("[肖像只读诊断] " + sourceCensus.Snapshot.Reason);
            }, preserveSourceCensus: true);
        else if (parts is ["native-census", "stop"])
            Mutate(() =>
            {
                sourceCensus?.Stop("Stopped explicitly by the user.");
                Plugin.Chat.Print("[肖像只读诊断] 已停止发证，正在确认原生回调退出。");
            }, preserveSourceCensus: true);
        else if (parts is ["native-census", "status"])
            Mutate(() =>
            {
                var state = sourceCensus?.Snapshot;
                Plugin.Chat.Print("[肖像只读诊断] " + (state is null ? "Off" : $"{state.State}: {state.Reason}")
                    + (state?.Result is { } result ? $" 八槽={result.Rows.Count}，仅历史观察。" : ""));
            }, preserveSourceCensus: true);
        else Error("用法：/firegaze portrait native-census start|stop|status");
        return true;
    }

    private void UpdateSourceCensus() { sourceCensus?.Update(); UpdateOwnedPassive(); }

    private void InvalidateSourceCensusSession(string reason)
    {
        ownedPassive?.Stop(reason);
        sourceCensus?.Revoke(reason);
        while (true)
        {
            var previous = Interlocked.Read(ref sourceCensusSessionEpoch);
            var next = previous is > 0 and < long.MaxValue ? previous + 1 : 0;
            if (Interlocked.CompareExchange(ref sourceCensusSessionEpoch, next, previous) == previous) break;
        }
    }

    private void BeforeSourceCensusFinalize(AddonEvent type, AddonArgs args)
        => InvalidateSourceCensusSession("BannerEditor PreFinalize revoked the source session.");
    private void SourceCensusLogout(int type, int code)
        => InvalidateSourceCensusSession("Logout revoked the source owner.");
    private void SourceCensusTerritoryChanged(uint territory)
        => InvalidateSourceCensusSession("Territory change revoked the source owner.");

    private void StopSourceCensusForMutation()
    {
        InvalidateSourceCensusSession("A Feature mutation revoked the diagnostic session.");
        sourceCensus?.Stop("A Feature mutation stopped the diagnostic.");
    }

    private void DisposeSourceCensus()
    {
        InvalidateSourceCensusSession("Feature disposal revoked the diagnostic session.");
        if (sourceCensusSubscribed)
        {
            Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "BannerEditor", BeforeSourceCensusFinalize);
            Plugin.ClientState.Logout -= SourceCensusLogout;
            Plugin.ClientState.TerritoryChanged -= SourceCensusTerritoryChanged;
            sourceCensusSubscribed = false;
        }
        sourceCensus?.Stop("Feature disposal awaits confirmed native retirement.");
        sourceCensusPhysicalDrain = RetainDiagnosticOwnersUntilDrained(sourceCensus, ownedPassive);
        // Keep the controller strongly owned through the actual physical drain, including faults.
    }

    private object? SourceCensusDiagnostics()
    {
        var state = sourceCensus?.Snapshot;
        if (state is null) return null;
        object? Host(PortraitNativePhaseHostSnapshot? value) => value is null ? null : new
        {
            state = value.State.ToString(), value.Hooks, value.InFlight, value.BackendIdentity, value.Detail,
            plan = value.Plan is { } plan ? new { module = (long)plan.ModuleBase, plan.ModuleSize, plan.OnDiskEXESHA256,
                plan.OnDiskSDKSHA256, plan.SDKMVID, targets = plan.Targets.Select(target => new { phase = target.Phase.ToString(), address = (long)target.Address }) } : null,
        };
        return new
        {
            state = state.State.ToString(), state.Attempt, state.Reason, state.ExpectedThread, state.ObservedThread,
            context = state.Context is { } context ? new { editor = (long)context.Source.Editor, sourceState = (long)context.Source.State,
                view = (long)context.Source.Model, character = context.Source.Character, context.Source.Generation, context.SessionEpoch } : null,
            result = state.Result is { } result ? new { result.Complete, result.ProductionAdmission, result.Phase.NativeFrame,
                result.Phase.SessionEpoch, result.Phase.ManagerGeneration,
                rows = result.Rows.Select(row => new { row.Slot, kind = row.Kind.ToString(), character = (long)row.Character,
                    row.ObjectKind, row.ObjectIndex, row.MemoryIndex, camera = (long)row.RegisteredCamera, row.ActiveCount, row.RenderActive,
                    offscreenCamera = (long)row.RetainedOffscreenCamera, viewCamera = (long)row.RetainedViewCamera }) } : null,
            source = Host(state.Source), counters = Host(state.Counters), resources = Host(state.Resources),
            productionAdmission = false,
        };
    }
}
