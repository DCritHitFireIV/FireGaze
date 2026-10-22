using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using RTManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Concrete SDK own-slot acquisition. Construction performs no native read. The native
/// source-phase owner must be the same owner whose counter/resource ingress issues phase
/// ManagerGeneration; a previous census observation cannot authorize this reader.
/// Default admission refuses every SDK call. No hook or native view is created here.
/// </summary>
internal sealed unsafe class PortraitOwnedSceneRenderTargetNativeApi(
    IPortraitOwnedSceneSlotNativeApi native, IPortraitOwnedSceneSlotPhaseSource phases,
    PortraitOwnedSceneSlotReader slots, Func<bool>? admission = null,
    PortraitOwnedSceneRenderTargetComReader? com = null) : IPortraitOwnedSceneRenderTargetReadApi
{
    public bool TryRead(PortraitOwnedSceneImageOwner imageOwner, IPortraitOwnedSceneLease lease, nint readyView,
        out PortraitOwnedSceneRenderTargetRead value)
    {
        value = default;
        try
        {
            if (!Admitted() || !LayoutMatches() || imageOwner is not { IsValid: true } || imageOwner.Slot >= 8
                || lease is null || !lease.IsCurrent || lease.Source != imageOwner.Source
                || lease.Slot != imageOwner.Slot || lease.Epoch != imageOwner.LeaseEpoch || !lease.CanAccess(readyView)
                || !phases.TryCapture(out var phase) || !Qualified(phase, imageOwner, lease)
                || !slots.TryReadOwnerCandidate(imageOwner.Slot, readyView, phase, out var owner)
                || owner.Stage != PortraitOwnedSceneSlotStage.Prepared || !Current(imageOwner, lease, owner, phase)
                || !ReadTexture(imageOwner, lease, owner, phase, out var first)
                || !ReadTexture(imageOwner, lease, owner, phase, out var second) || first != second
                || !Current(imageOwner, lease, owner, phase)) return false;
            value = first; return true;
        }
        catch (PortraitCaptureCleanupException) { throw; }
        catch (Exception) { return false; }
    }

    private bool ReadTexture(PortraitOwnedSceneImageOwner imageOwner, IPortraitOwnedSceneLease lease,
        PortraitOwnedSceneSlotOwner owner, PortraitOwnedSceneSlotPhase phase, out PortraitOwnedSceneRenderTargetRead value)
    {
        value = default;
        // GetCharaViewTexture has no native bounds check. The check immediately before the
        // real SDK call is mandatory; do not pass ClientObjectId (slot + 0xF0).
        if (imageOwner.Slot >= 8 || !Current(imageOwner, lease, owner, phase)) return false;
        var address = (nint)((RTManager*)phase.Roots.RenderTargets)->GetCharaViewTexture(imageOwner.Slot);
        // The SDK call may enter another plugin. Do not read the returned allocation until
        // the entire owned token and current phase/roots have been revalidated.
        if (!Current(imageOwner, lease, owner, phase) || address == 0
            || !native.TryRead(address, out Texture texture)
            || !native.TryRead((nint)Device.StaticAddressPointers.ppInstance, out nint device) || device == 0
            || !native.TryRead((nint)(&((Device*)device)->D3D11Forwarder), out nint forwarder)
            || !Current(imageOwner, lease, owner, phase)) return false;
        var candidate = new PortraitOwnedSceneRenderTargetRead(imageOwner, owner, phase, address,
            (nint)texture.D3D11ShaderResourceView, (nint)texture.D3D11Texture2D, device, forwarder,
            texture.AllocatedWidth, texture.AllocatedHeight, texture.ActualWidth, texture.ActualHeight,
            (uint)texture.TextureFormat, (uint)texture.Flags, texture.MipLevel, texture.ArraySize);
        if (!candidate.IsKernelValid || com is null
            || !com.TryRead(candidate, () => Current(imageOwner, lease, owner, phase), out var description)
            || !Current(imageOwner, lease, owner, phase)) return false;
        value = candidate with { ComDescription = description }; return value.IsValid;
    }

    private bool Current(PortraitOwnedSceneImageOwner imageOwner, IPortraitOwnedSceneLease lease,
        PortraitOwnedSceneSlotOwner owner, PortraitOwnedSceneSlotPhase phase)
        => Admitted() && Qualified(phase, imageOwner, lease) && lease.CanAccess(owner.View)
            && phases.TryCapture(out var before) && before == phase
            && native.TryReadRoots(out var roots) && roots == phase.Roots
            && slots.TryReadOwned(imageOwner.Slot, owner, phase, out var snapshot)
            && snapshot.Ownership == PortraitOwnedSceneSlotOwnership.Owned
            && native.TryRead(owner.View + 8, out uint state) && state == 6
            && phases.TryCapture(out var after) && after == phase && lease.IsCurrent && Admitted();

    private static bool Qualified(PortraitOwnedSceneSlotPhase phase, PortraitOwnedSceneImageOwner owner,
        IPortraitOwnedSceneLease lease)
        => phase.Source == owner.Source && phase.Roots.IsValid && phase.SessionEpoch > 0 && phase.ManagerGeneration > 0
            && phase.NativeFrame == lease.NativeFrame && phase.PhaseToken > 0 && phase.BuildValidated && phase.InNativePhase
            && phase.ThreadID == Environment.CurrentManagedThreadId && lease.IsCurrent;

    private bool Admitted() { try { return admission?.Invoke() == true; } catch { return false; } }

    internal static bool LayoutMatches() => sizeof(Texture) == 0xC8 && sizeof(RTManager) == 0x740
        && Marshal.OffsetOf<Texture>(nameof(Texture.ActualWidth)) == 0x38
        && Marshal.OffsetOf<Texture>(nameof(Texture.ActualHeight)) == 0x3C
        && Marshal.OffsetOf<Texture>(nameof(Texture.AllocatedWidth)) == 0x40
        && Marshal.OffsetOf<Texture>(nameof(Texture.AllocatedHeight)) == 0x44
        && Marshal.OffsetOf<Texture>(nameof(Texture.TextureFormat)) == 0x58
        && Marshal.OffsetOf<Texture>(nameof(Texture.Flags)) == 0x5C
        && Marshal.OffsetOf<Texture>(nameof(Texture.ArraySize)) == 0x60
        && Marshal.OffsetOf<Texture>(nameof(Texture.D3D11Texture2D)) == 0x68
        && Marshal.OffsetOf<Texture>(nameof(Texture.D3D11ShaderResourceView)) == 0x70
        && Marshal.OffsetOf<Device>(nameof(Device.D3D11Forwarder)) == 0xE0AA8;
}

/// <summary>Only the existing public texture API; no game refcount or native GPU call.</summary>
internal sealed class PortraitOwnedSceneRenderTargetTextures(ITextureProvider provider)
    : IPortraitOwnedSceneRenderTargetTextures
{
    public IDalamudTextureWrap Retain(PortraitOwnedSceneRenderTargetRead value)
    {
        if (!value.IsValid) throw new InvalidOperationException("Owned RT descriptor is incomplete.");
        return ((IDalamudTextureWrap)new Borrowed(value.Srv, checked((int)value.AllocatedWidth),
            checked((int)value.AllocatedHeight))).CreateWrapSharingLowLevelResource();
    }
    public Task<IDalamudTextureWrap> Copy(IDalamudTextureWrap retained, TextureModificationArgs args)
        => provider.CreateFromExistingTextureAsync(retained, args, leaveWrapOpen: true,
            debugName: "FireGaze owned portrait slot copy", cancellationToken: CancellationToken.None);

    private sealed class Borrowed(nint srv, int width, int height) : IDalamudTextureWrap
    {
        public ImTextureID Handle => new(srv);
        public int Width => width;
        public int Height => height;
        public void Dispose() { }
    }
}
