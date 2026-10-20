using System.Diagnostics;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitOwnedScenePassiveDiagnostic? ownedPassive;
    private PortraitOwnedScenePassiveSafetyState ReadOwnedPassiveSafetyState()
        => ownedPassive?.ReadSafetyState() ?? PortraitOwnedScenePassiveSafetyState.Unknown;

    private void InitializeOwnedPassive(PortraitOwnedScenePassiveDiagnostic? diagnostic = null)
    {
        ownedPassive = diagnostic ?? new(() => Plugin.Framework.IsInFrameworkUpdateThread,
            ReadSourceCensusContext, sourceCensus ?? throw new InvalidOperationException("Source census is not initialized."),
            () => new(new OwnedPassiveEnvironment(CurrentModuleBase())), new DalamudPortraitOwnedSceneHookFactory(Plugin.Interop));
    }
    private static nint CurrentModuleBase()
    {
        using var process = Process.GetCurrentProcess();
        return process.MainModule?.BaseAddress ?? 0;
    }
    private bool TryOwnedPassiveCommand(string[] parts)
    {
        if (parts.Length == 0 || parts[0] != "native-owned") return false;
        if (parts is ["native-owned", "start"])
            Mutate(() =>
            {
                if (ownedPassive is null) Error("原生转发诊断尚未初始化。");
                else if (!ownedPassive.TryStart(out var reason)) Error("原生转发诊断未启动：" + reason);
                else Plugin.Chat.Print("[肖像原生转发诊断] " + ownedPassive.Snapshot.Reason);
            }, preserveSourceCensus: true);
        else if (parts is ["native-owned", "stop"])
            Mutate(() =>
            {
                ownedPassive?.Stop("Stopped explicitly by the user.");
                Plugin.Chat.Print("[肖像原生转发诊断] 已撤销观察，正在等待实际原生回调退出。");
            }, preserveSourceCensus: true);
        else if (parts is ["native-owned", "status"])
            Mutate(() =>
            {
                var state = ownedPassive?.Snapshot;
                Plugin.Chat.Print("[肖像原生转发诊断] " + (state is null ? "Unknown" : $"{state.State}: {state.Reason}"));
            }, preserveSourceCensus: true);
        else Error("用法：/firegaze portrait native-owned start|stop|status");
        return true;
    }
    private void UpdateOwnedPassive() => ownedPassive?.Update();
    private static Task RetainDiagnosticOwnersUntilDrained(PortraitSourceCensusDiagnostic? census,
        PortraitOwnedScenePassiveDiagnostic? owned)
        => PortraitDiagnosticOwnerRetention.DrainAsync(census, owned, census?.DrainPhysicalAsync() ?? Task.CompletedTask,
            owned?.DrainPhysicalAsync() ?? Task.CompletedTask);
    private object? OwnedPassiveDiagnostics()
    {
        var state = ownedPassive?.Snapshot;
        return state is null ? null : new
        {
            state = state.State.ToString(), state.Attempt, state.CensusAttempt, state.Reason,
            safety = ReadOwnedPassiveSafetyState(), productionAdmission = false,
            sourceReturn = state.SourceReturn is { } result ? new
            {
                state = result.State.ToString(), result.Installation, result.SessionEpoch,
                editor = (long)result.Source.Editor, sourceState = (long)result.Source.State, view = (long)result.Source.Model,
                result.Source.Generation, result.Source.Character, result.ExpectedThread, result.Thread,
                result.NativeFrame, result.ArmedAt, result.Deadline, result.Reason, productionAdmission = false,
            } : null,
        };
    }
    private sealed class OwnedPassiveEnvironment(nint module) : IPortraitOwnedSceneHookEnvironment
    {
        public bool IsFrameworkThread => Plugin.Framework.IsInFrameworkUpdateThread;
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool TryValidateInstallation(out string reason) { reason = "Passive forwarding does not grant production scene admission."; return false; }
        public bool IsSourceCurrent(PortraitIdentity source) => false;
        public bool IsSlotIdle(uint slot) => false;
        public bool IsSlotOwnedBy(uint slot, nint view) => false;
        public bool TryReadViewSlot(nint view, out uint slot) { slot = uint.MaxValue; return false; }
    }
}

internal static class PortraitDiagnosticOwnerRetention
{
    internal static async Task DrainAsync(object? census, object? owned, Task censusDrain, Task ownedDrain)
    {
        try { await Task.WhenAll(censusDrain, ownedDrain).ConfigureAwait(false); }
        finally { GC.KeepAlive(census); GC.KeepAlive(owned); }
    }
}
