using System.Runtime.InteropServices;
using FireGaze.LocalPreview.Core;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using SheetCompanion = Lumina.Excel.Sheets.Companion;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace FireGaze.LocalPreview;

internal sealed unsafe partial class MultiCompanionRuntime
{
    private NativeCompanionBatchBeckonAdapter? batchBeckonAdapter;
    private Func<uint, ushort, bool> batchBeckonCatalogMatches;
    private Func<bool> batchBeckonFocused;
    private readonly Queue<CompanionBatchInteractionRequest> batchBeckonObservations = [];
    private readonly Queue<NativeCompanionBatchBeckonResult> batchBeckonEvidence = [];
    private CompanionBatchInteractionRequest? deferredBatchBeckon;
    private NativeCompanionFollowReader? batchBeckonFocusReader;
    internal NativeCompanionBatchBeckonResult[] BatchBeckonEvidence => batchBeckonEvidence.ToArray();
    internal string? BatchBeckonStopReason { get; private set; }

    internal void AttachBatchBeckon(NativeCompanionTargetEmoteRouter router)
    {
        if (!framework.IsInFrameworkUpdateThread || disposed) return;
        CancelBatchBeckon("adapter-replaced");
        batchBeckonAdapter = new(framework, router, ResolveBatchBeckon);
        batchBeckonAdapter.ResetScene(sceneEpoch);
    }

    private bool BatchBeckonCatalogMatches(uint id, ushort model)
        => id > 0 && model > 0 && data.GetExcelSheet<SheetCompanion>().TryGetRow(id, out var row)
            && row.Model.RowId == model;

    private bool ReadBatchBeckonFocus()
    {
        batchBeckonFocusReader ??= new(framework);
        return Marshal.OffsetOf<NativeFramework>(nameof(NativeFramework.CursorInputs)).ToInt32()
                + Marshal.OffsetOf<CursorInputData>(nameof(CursorInputData.IsGameWindowFocused)).ToInt32() == 0xA28
            && NativeCompanionFollowReader.Read(batchBeckonFocusReader,
                (nint)NativeFramework.StaticAddressPointers.ppInstance, out nint nativeFramework)
            && nativeFramework > 0 && NativeCompanionFollowReader.Read(batchBeckonFocusReader,
                nativeFramework + 0xA28, out byte focused) && focused != 0;
    }

    private NativeCompanionBeckonBinding? ResolveBatchBeckon(CompanionBatchInteractionRequest request)
    {
        if (disposed || !trialPolicy.Enabled || !batchBeckonFocused() || request.SceneEpoch != sceneEpoch
            || !CanRun(out var source) || !BindingCurrent(source) || NativeMountActive(source)) return null;
        var lease = session.Snapshot.Leases.FirstOrDefault(x => x.LeaseId == request.LeaseId
            && x.Generation == request.Generation && x.State == CompanionDisplayState.Active);
        if (lease.LeaseId == 0 || !actors.TryGetValue(lease.LeaseId, out var entry)
            || entry.Generation != lease.Generation || entry.Actor.IsDisposed || !entry.Actor.IsReady) return null;
        var child = entry.Actor.Companion;
        if (child == null || !entry.Actor.IsCurrentCompanion(child)) return null;
        // Special0 retains the native decision whether this species responds at all.
        // A valid catalog identity is not permission for shoulder/special placement.
        var qualified = child->Special == 0
            && batchBeckonCatalogMatches(lease.CompanionId, entry.Actor.ExpectedModelId);
        return new(new(sceneEpoch, ownerID, ownerGeneration, lease.LeaseId, lease.Generation),
            (nint)child, (nint)source, *(uint*)((byte*)child + 0x74), qualified);
    }

    private void UpdateBatchBeckon(IReadOnlyList<CompanionDisplayLease> leases)
    {
        if (batchBeckonAdapter is not { } adapter) return;
        if (batchInteractions.PendingCount == 0 && batchBeckonObservations.Count == 0
            && !deferredBatchBeckon.HasValue) return;
        if (!batchBeckonFocused()) { CancelBatchBeckon("game-window-unfocused"); return; }
        // Observe only a fixed amount of retained work, even for the entire catalog.
        var count = Math.Min(4, batchBeckonObservations.Count);
        for (var index = 0; index < count; index++)
        {
            if (!batchBeckonObservations.TryDequeue(out var request)) break;
            var result = adapter.Observe(request);
            if (result.Status == NativeCompanionBatchBeckonStatus.Pending) batchBeckonObservations.Enqueue(request);
            else RetainBatchBeckonEvidence(result);
        }
        var active = leases.Where(x => x.State == CompanionDisplayState.Active)
            .Select(x => new CompanionBatchInteractionActor(x.LeaseId, x.Generation, x.State)).ToArray();
        CompanionBatchInteractionRequest next;
        if (deferredBatchBeckon is { } deferred) { next = deferred; deferredBatchBeckon = null; }
        else if (!batchInteractions.TryTakeForFrame(sceneEpoch, active, out next)) return;
        var submitted = adapter.Submit(next, frameworkFrame);
        if (submitted.Status == NativeCompanionBatchBeckonStatus.FrameBudgetReached) deferredBatchBeckon = next;
        else
        {
            RetainBatchBeckonEvidence(submitted);
            if (submitted.Status == NativeCompanionBatchBeckonStatus.Queued) batchBeckonObservations.Enqueue(next);
        }
    }

    private void RetainBatchBeckonEvidence(NativeCompanionBatchBeckonResult result)
    {
        if (batchBeckonEvidence.Count >= 32) batchBeckonEvidence.Dequeue();
        batchBeckonEvidence.Enqueue(result);
    }

    private void CancelBatchBeckonObservations(string reason)
    {
        while (batchBeckonObservations.TryDequeue(out var request))
            RetainBatchBeckonEvidence(new(request, NativeCompanionBatchBeckonStatus.Cancelled, Reason: reason));
        batchBeckonAdapter?.CancelAll();
        BatchBeckonStopReason = reason;
    }

    private void CancelBatchBeckonLease(CompanionDisplayLease lease)
    {
        batchInteractions.CancelLease(lease.LeaseId, lease.Generation);
        batchBeckonAdapter?.CancelLease(lease.LeaseId, lease.Generation);
        if (deferredBatchBeckon is { } deferred && deferred.LeaseId == lease.LeaseId
            && deferred.Generation == lease.Generation) deferredBatchBeckon = null;
        var count = batchBeckonObservations.Count;
        for (var index = 0; index < count; index++)
        {
            var request = batchBeckonObservations.Dequeue();
            if (request.LeaseId == lease.LeaseId && request.Generation == lease.Generation)
                RetainBatchBeckonEvidence(new(request, NativeCompanionBatchBeckonStatus.Cancelled, Reason: "pet-recalled"));
            else batchBeckonObservations.Enqueue(request);
        }
    }

    private void CancelBatchBeckon(string reason)
    {
        batchInteractions.CancelAll();
        deferredBatchBeckon = null;
        CancelBatchBeckonObservations(reason);
    }
}
