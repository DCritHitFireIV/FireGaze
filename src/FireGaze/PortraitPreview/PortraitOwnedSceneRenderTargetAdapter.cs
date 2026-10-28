using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

// Pointer values are equality tokens only. This object never crosses into a task.
internal readonly record struct PortraitOwnedSceneRenderTargetRead(PortraitOwnedSceneImageOwner Owner,
    PortraitOwnedSceneSlotOwner NativeOwner, PortraitOwnedSceneSlotPhase Phase,
    nint Texture, nint Srv, nint Resource, nint KernelDevice, nint DeviceForwarder,
    uint AllocatedWidth, uint AllocatedHeight, uint ActualWidth, uint ActualHeight,
    uint Format, uint Flags, byte MipLevels, byte ArraySize,
    PortraitOwnedSceneRenderTargetComDescription? ComDescription = null)
{
    internal bool IsValid => IsKernelValid && ComDescription is { IsValid: true };
    internal bool IsKernelValid => Owner.IsValid && Owner.Slot < 8 && NativeOwner.View != 0
        && NativeOwner.Character != 0 && NativeOwner.CharacterGeneration > 0 && NativeOwner.Context != 0
        && NativeOwner.Camera != 0 && NativeOwner.Stage == PortraitOwnedSceneSlotStage.Prepared
        && Phase.Source == Owner.Source && Phase.Roots.IsValid && Phase.InNativePhase && Phase.BuildValidated
        && Phase.SessionEpoch > 0 && Phase.ManagerGeneration > 0 && Phase.PhaseToken > 0
        && Phase.ThreadID == Environment.CurrentManagedThreadId && Texture != 0 && Srv != 0 && Resource != 0
        && KernelDevice != 0 && DeviceForwarder != 0 && Format != 0
        && AllocatedWidth is > 0 and <= 16384 && AllocatedHeight is > 0 and <= 16384
        && ActualWidth > 0 && ActualHeight > 0 && ActualWidth <= AllocatedWidth && ActualHeight <= AllocatedHeight
        && MipLevels > 0 && ArraySize <= 1;

    internal bool SameResource(PortraitOwnedSceneRenderTargetRead other)
        => this with { Phase = Phase with { NativeFrame = other.Phase.NativeFrame, PhaseToken = other.Phase.PhaseToken } } == other;
}

internal interface IPortraitOwnedSceneRenderTargetReadApi
{
    bool TryRead(PortraitOwnedSceneImageOwner owner, IPortraitOwnedSceneLease lease, nint readyView,
        out PortraitOwnedSceneRenderTargetRead value);
}

internal interface IPortraitOwnedSceneRenderTargetTextures
{
    IDalamudTextureWrap Retain(PortraitOwnedSceneRenderTargetRead value);
    Task<IDalamudTextureWrap> Copy(IDalamudTextureWrap retained, TextureModificationArgs args);
}

internal sealed class PortraitOwnedSceneRenderTargetAdapter(
    IPortraitOwnedSceneRenderTargetReadApi read,
    IPortraitOwnedSceneRenderTargetTextures textures,
    Func<bool>? admission = null) : IDisposable
{
    private readonly PortraitCaptureRetirement acquisitions = new();
    private PortraitOwnedSceneRenderTargetRead? last;
    private long descriptorGeneration;
    private long resourceRevision = 1;
    private bool disposed, busy;
    internal PortraitOwnedSceneImageSource<IDalamudTextureWrap> Images { get; } = new();
    internal bool IsPhysicallyClean => Images.IsPhysicallyClean && acquisitions.CleanupFailures == 0;
    // The SAME active owner must call this before resource/device mutation Original,
    // including while a provider task is pending. It performs no native read or cleanup.
    internal void RevokeResourceEpoch()
    {
        Interlocked.Increment(ref resourceRevision);
        Images.InvalidateNativeSource();
    }
    internal bool TryCopy(PortraitOwnedSceneController controller, IPortraitOwnedSceneLease lease,
        PortraitOwnedSceneFirstFrameIdentity? receipt = null)
    {
        if (disposed || busy || !Admitted() || controller.ImageOwner is not { } owner
            || !owner.IsValid || !lease.IsCurrent || lease.Slot != owner.Slot || owner.Slot >= 8
            || lease.Epoch != owner.LeaseEpoch || lease.Source != owner.Source
            || Images.InFlight != 0 || !acquisitions.TryEnter(out var held)) return false;
        busy = true;
        var revision = Volatile.Read(ref resourceRevision);
        IDalamudTextureWrap? retained = null;
        Prepared? prepared = null;
        var started = false;
        var prefixValid = false;
        try
        {
            bool Current() => !disposed && revision > 0 && revision == Volatile.Read(ref resourceRevision)
                && Admitted() && lease.IsCurrent && controller.IsImageOwnerCurrent(owner);
            var used = controller.TryUseReady(owner, lease, view =>
            {
                try
                {
                if (!Current() || !lease.CanAccess(view) || !read.TryRead(owner, lease, view, out var first)
                    || !first.IsValid || first.Owner != owner || first.NativeOwner.View != view
                    || receipt is not null && !ReceiptMatches(receipt, first, lease)) return;
                retained = textures.Retain(first); // Independent AddRef via public Dalamud wrap.
                if (retained is null || !Current() || !read.TryRead(owner, lease, view, out var afterRetain)
                    || afterRetain != first) return;
                if (last is not { } previous || !previous.SameResource(first))
                {
                    Images.InvalidateNativeSource();
                    descriptorGeneration = checked(descriptorGeneration + 1);
                    last = first;
                }
                var descriptor = new PortraitOwnedSceneImageDescriptor(owner, descriptorGeneration,
                    checked((int)first.ActualWidth), checked((int)first.ActualHeight), 0, 0,
                    first.ActualWidth/(float)first.AllocatedWidth, first.ActualHeight/(float)first.AllocatedHeight);
                var args = new TextureModificationArgs { Uv0 = Vector2.Zero, Uv1 = new(descriptor.U1, descriptor.V1) };
                // Prepared has no lease, native pointer, raw descriptor or native callback.
                prepared = new(textures, retained, args, acquisitions, held);
                retained = null; held = null!;
                started = Images.TryCopyWithinReadyView(controller, lease, descriptor, prepared.Start, receipt);
                if (!started) return;
                // Provider invocation has an external synchronous prefix. Revalidate all native
                // values before allowing its managed task to publish even an already-done copy.
                prefixValid = Current() && read.TryRead(owner, lease, view, out var afterSubmit) && afterSubmit == first;
                }
                catch (Exception error)
                {
                    // The real ready runtime can catch/convert callback exceptions. Transfer
                    // failed COM ownership before that boundary can hide physical quarantine.
                    acquisitions.ObserveFailure(error);
                    throw;
                }
            });
            var accepted = used && started && prefixValid && Current();
            if (!accepted) Images.InvalidateNativeSource();
            prepared?.Confirm(accepted);
            return accepted;
        }
        catch (Exception error)
        {
            acquisitions.ObserveFailure(error);
            Images.InvalidateNativeSource();
            prepared?.Confirm(false);
            return false;
        }
        finally
        {
            prepared?.ReleaseIfNotStarted();
            acquisitions.Release(retained);
            held?.Dispose();
            busy = false;
        }
    }
    private static bool ReceiptMatches(PortraitOwnedSceneFirstFrameIdentity receipt,
        PortraitOwnedSceneRenderTargetRead value, IPortraitOwnedSceneLease lease)
        => receipt.IsValid && receipt.SessionEpoch == value.Phase.SessionEpoch
            && receipt.NativeFrame == value.Phase.NativeFrame && receipt.NativeFrame == lease.NativeFrame
            && receipt.Source == new PortraitOwnedSceneFirstFrameSource((long)value.Owner.Source.Editor,
                (long)value.Owner.Source.State, (long)value.Owner.Source.Model,
                value.Owner.Source.Generation, value.Owner.Source.Character)
            && receipt.Owner == new PortraitOwnedSceneFirstFrameOwner(value.Owner.Session,
                value.Owner.Revision, value.Owner.Slot, value.Owner.LeaseEpoch);
    private bool Admitted() { try { return admission?.Invoke() == true; } catch { return false; } }
    public void Dispose() { disposed = true; Images.Dispose(); }
    internal Task DrainPhysicalAsync() => PortraitCaptureRetirement.CombineOwned(this, Images.DrainPhysicalAsync(), acquisitions.Seal());

    // This is the only object retained by the asynchronous copy. All its inputs are managed
    // ownership or detached UV numbers. It owns the independent SRV until the official task
    // finishes; cancellation/close never pretends the provider stopped using it.
    private sealed class Prepared(IPortraitOwnedSceneRenderTargetTextures textures, IDalamudTextureWrap input,
        TextureModificationArgs args, PortraitCaptureRetirement physical, IDisposable held)
    {
        private readonly TaskCompletionSource<bool> confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool started, released;
        internal void Confirm(bool accepted) => confirmation.TrySetResult(accepted);
        internal Task<IDalamudTextureWrap> Start()
        {
            if (started) throw new InvalidOperationException("Prepared owned copy started twice.");
            started = true;
            Task<IDalamudTextureWrap> task;
            try { task = textures.Copy(input, args) ?? throw new InvalidOperationException("Provider returned no task."); }
            catch (Exception error) { task = Task.FromException<IDalamudTextureWrap>(error); }
            return Complete(task);
        }
        private async Task<IDalamudTextureWrap> Complete(Task<IDalamudTextureWrap> task)
        {
            IDalamudTextureWrap? result = null;
            try
            {
                result = await task.ConfigureAwait(false);
                if (ReferenceEquals(result, input))
                {
                    var error = new PortraitCaptureCleanupException([input],
                        [new InvalidOperationException("Provider returned its retained source wrap.")]);
                    physical.ObserveFailure(error); result = null; throw error;
                }
                if (!await confirmation.Task.ConfigureAwait(false)) throw new InvalidOperationException("Owned RT prefix invalidated.");
                ReleaseInput();
                if (physical.CleanupFailures != 0) throw new PortraitCaptureCleanupException([input],
                    [new InvalidOperationException("Retained source release was not confirmed.")]);
                return result ?? throw new InvalidOperationException("Provider returned no owned image.");
            }
            catch { physical.Release(result); throw; }
            finally { ReleaseInput(); held.Dispose(); }
        }
        private void ReleaseInput() { if (released) return; released = true; physical.Release(input); }
        internal void ReleaseIfNotStarted() { if (started) return; ReleaseInput(); held.Dispose(); }
    }
}
