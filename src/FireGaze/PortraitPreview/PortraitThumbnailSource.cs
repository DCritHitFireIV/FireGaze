using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FireGaze.PortraitPreview;

/// <summary>Owned character snapshot and retained static atlases. Character includes the background.</summary>
internal sealed class PortraitThumbnailLayers(IDalamudTextureWrap character, IDalamudTextureWrap? frame,
    IDalamudTextureWrap? accent, Vector2? frameUV0 = null, Vector2? frameUV1 = null,
    Vector2? accentUV0 = null, Vector2? accentUV1 = null) : IDisposable
{
    public const int Width = 160;
    public const int Height = 256;
    public IDalamudTextureWrap Character { get; } = character;
    public IDalamudTextureWrap? Frame { get; } = frame;
    public IDalamudTextureWrap? Accent { get; } = accent;
    public Vector2 FrameUV0 { get; } = frameUV0 ?? Vector2.Zero;
    public Vector2 FrameUV1 { get; } = frameUV1 ?? Vector2.One;
    public Vector2 AccentUV0 { get; } = accentUV0 ?? Vector2.Zero;
    public Vector2 AccentUV1 { get; } = accentUV1 ?? Vector2.One;
    private int disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Character.Dispose(); Frame?.Dispose(); Accent?.Dispose();
    }
}

internal sealed record PortraitThumbnailInput(IDalamudTextureWrap Texture, TextureModificationArgs CopyArgs);

/// <summary>Independent SRV references, never native editor pointers.</summary>
internal sealed class PortraitThumbnailInputs(PortraitThumbnailInput character, PortraitThumbnailInput? frame,
    PortraitThumbnailInput? accent) : IDisposable
{
    public PortraitThumbnailInput Character { get; } = character;
    private PortraitThumbnailInput? frame = frame;
    private PortraitThumbnailInput? accent = accent;
    private int disposed;

    // Transfer the already owned SRV reference instead of AddRefing a fake or native texture again.
    public PortraitThumbnailInput? TakeFrame() => Interlocked.Exchange(ref frame, null);
    public PortraitThumbnailInput? TakeAccent() => Interlocked.Exchange(ref accent, null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Character.Texture.Dispose(); TakeFrame()?.Texture.Dispose(); TakeAccent()?.Texture.Dispose();
    }
}

internal interface IPortraitThumbnailSourceApi
{
    bool IsFrameworkThread { get; }
    bool IsCurrent(PortraitIdentity identity);
    bool TryAcquire(PortraitIdentity identity, out PortraitThumbnailInputs? inputs);
    Task<IDalamudTextureWrap> CopyAsync(PortraitThumbnailInput source, CancellationToken cancellationToken);
}

/// <summary>
/// Reads only the current BannerEditor image layers on Framework, retaining their COM resources
/// before asynchronous work. The mutable character target is copied through the public service;
/// static overlays retain their SRVs and UVs for one final draw, avoiding double alpha multiplication.
/// Pixel copying is scheduled by Dalamud at Present; callers must discard a result if the editor
/// state changed while copying. No native editor pointer is dereferenced after the first await.
/// </summary>
internal sealed class PortraitThumbnailSource
{
    private readonly IPortraitThumbnailSourceApi api;

    public PortraitThumbnailSource(IFramework framework, IGameGui gameGUI, ITextureProvider textures,
        Func<PortraitIdentity, bool> isCurrent)
        : this(new LiveApi(framework, gameGUI, textures, isCurrent)) { }

    internal PortraitThumbnailSource(IPortraitThumbnailSourceApi api) => this.api = api;

    public async Task<PortraitThumbnailLayers?> CaptureAsync(PortraitIdentity identity,
        CancellationToken cancellationToken = default, PortraitPhotoTrace.Operation? operation = null)
    {
        operation ??= PortraitPhotoTrace.Operation.Silent;
        cancellationToken.ThrowIfCancellationRequested();
        if (!identity.IsValid || !api.IsFrameworkThread || !api.IsCurrent(identity)) return null;
        PortraitThumbnailInputs? inputs = null;
        if (!operation.Call(PortraitPhotoPhase.SourceAcquire, () => api.TryAcquire(identity, out inputs)) || inputs is null) return null;
        using (inputs)
        {
            if (!api.IsCurrent(identity)) return null;
            IDalamudTextureWrap? character = null;
            PortraitThumbnailInput? frame = null, accent = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                character = await operation.CallAsync(PortraitPhotoPhase.SourceCopy,
                    () => api.CopyAsync(inputs.Character, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                frame = inputs.TakeFrame();
                accent = inputs.TakeAccent();
                var result = new PortraitThumbnailLayers(character, frame?.Texture, accent?.Texture,
                    frame?.CopyArgs.Uv0, frame is null ? null : EffectiveUV1(frame.CopyArgs),
                    accent?.CopyArgs.Uv0, accent is null ? null : EffectiveUV1(accent.CopyArgs));
                character = null; frame = null; accent = null;
                return result;
            }
            finally
            {
                character?.Dispose(); frame?.Texture.Dispose(); accent?.Texture.Dispose();
            }
        }
    }

    private static Vector2 EffectiveUV1(TextureModificationArgs args)
        => args.Uv1 == Vector2.Zero ? Vector2.One : args.Uv1;

    internal static bool TryCreateCopyArgs(uint width, uint height, ushort u, ushort v,
        ushort cropWidth, ushort cropHeight, out TextureModificationArgs args)
    {
        args = default;
        if (width is 0 or > 16384 || height is 0 or > 16384 || cropWidth == 0 || cropHeight == 0
            || (uint)u + cropWidth > width || (uint)v + cropHeight > height) return false;
        args = new()
        {
            NewWidth = PortraitThumbnailLayers.Width,
            NewHeight = PortraitThumbnailLayers.Height,
            DxgiFormat = 28, // UI atlases may be BC compressed; render targets must use RGBA8.
            Uv0 = new((float)u / width, (float)v / height),
            Uv1 = new((float)(u + cropWidth) / width, (float)(v + cropHeight) / height),
        };
        return true;
    }

    // A portrait's ULD part stores its logical display size, not a crop inside the CharaView
    // render target. Actual dimensions can be smaller than its padded GPU allocation.
    internal static bool TryCreateCharacterCopyArgs(uint allocatedWidth, uint allocatedHeight,
        uint actualWidth, uint actualHeight, out TextureModificationArgs args)
    {
        args = default;
        return actualWidth is > 0 and <= 16384 && actualHeight is > 0 and <= 16384
            && TryCreateCopyArgs(allocatedWidth, allocatedHeight, 0, 0,
                (ushort)actualWidth, (ushort)actualHeight, out args);
    }

    /// <summary>Stops before touching a saved ULD pointer if a native lookup replaces its owner.</summary>
    internal static unsafe nint FindPortrait(nint addonAddress, Func<bool> isOwnerCurrent,
        Func<nint, ComponentType> getComponentType)
    {
        if (addonAddress == 0 || !isOwnerCurrent()) return 0;
        var addon = (AtkUnitBase*)addonAddress;
        var nodes = addon->UldManager.NodeList;
        var count = addon->UldManager.NodeListCount;
        if (nodes == null || count is 0 or > 4096) return 0;
        nint match = 0;
        for (var index = 0; index < count; index++)
        {
            if (!isOwnerCurrent() || addon->UldManager.NodeList != nodes || addon->UldManager.NodeListCount != count) return 0;
            var node = nodes[index];
            if (node == null || (ushort)node->Type < 1000) continue;
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null || component->OwnerNode != (AtkComponentNode*)node) continue;
            var type = getComponentType((nint)component);
            // This callback can enter other plugins. Validate before the loop advances or uses
            // any pointer retained across that call, including a nonmatching component.
            if (!isOwnerCurrent() || addon->UldManager.NodeList != nodes || addon->UldManager.NodeListCount != count
                || nodes[index] != node || ((AtkComponentNode*)node)->Component != component) return 0;
            if (type != ComponentType.Portrait) continue;
            if (match != 0) return 0;
            match = (nint)component;
        }
        return match;
    }

    private sealed class LiveApi(IFramework framework, IGameGui gameGUI, ITextureProvider textures,
        Func<PortraitIdentity, bool> isCurrent) : IPortraitThumbnailSourceApi
    {
        private enum Layer { Character, Frame, Accent }
        public bool IsFrameworkThread => framework.IsInFrameworkUpdateThread;
        public bool IsCurrent(PortraitIdentity identity) => IsFrameworkThread && isCurrent(identity);

        public bool TryAcquire(PortraitIdentity identity, out PortraitThumbnailInputs? inputs)
        {
            inputs = null;
            if (!IsFrameworkThread || !IsCurrent(identity)) return false;
            PortraitThumbnailInput? character = null, frame = null, accent = null;
            try
            {
                if (!TryAcquireLayer(identity, Layer.Character, out character) || character is null
                    || !IsCurrent(identity) || !TryAcquireLayer(identity, Layer.Frame, out frame)
                    || !IsCurrent(identity) || !TryAcquireLayer(identity, Layer.Accent, out accent)
                    || !IsCurrent(identity)) return false;
                inputs = new(character, frame, accent);
                character = null; frame = null; accent = null;
                return true;
            }
            finally
            {
                character?.Texture.Dispose(); frame?.Texture.Dispose(); accent?.Texture.Dispose();
            }
        }

        public Task<IDalamudTextureWrap> CopyAsync(PortraitThumbnailInput source, CancellationToken cancellationToken)
            => textures.CreateFromExistingTextureAsync(source.Texture, source.CopyArgs, leaveWrapOpen: true,
                debugName: "FireGaze portrait history layer", cancellationToken: cancellationToken);

        private unsafe bool TryAcquireLayer(PortraitIdentity identity, Layer layer, out PortraitThumbnailInput? input)
        {
            input = null;
            if (!IsFrameworkThread || !IsCurrent(identity)) return false;
            var editor = AgentBannerEditor.Instance();
            var addon = gameGUI.GetAddonByName<AtkUnitBase>("BannerEditor");
            if (editor == null || addon == null || !addon->IsVisible || (nint)editor != identity.Editor
                || addon->Id != editor->AddonId || (nint)editor->EditorState != identity.State
                || editor->EditorState == null || (nint)editor->EditorState->CharaView != identity.Model) return false;
            var addonAddress = (nint)addon;
            var portrait = (AtkComponentPortrait*)FindPortrait(addonAddress,
                () => IsSourceCurrent(identity, addonAddress), ReadComponentType);
            // Component type resolution is a native call. Re-resolve the owner before reading its nodes.
            if (portrait == null || gameGUI.GetAddonByName<AtkUnitBase>("BannerEditor") != addon
                || AgentBannerEditor.Instance() != editor || !addon->IsVisible
                || addon->Id != editor->AddonId || (nint)editor->EditorState != identity.State
                || editor->EditorState == null || (nint)editor->EditorState->CharaView != identity.Model) return false;
            var image = layer == Layer.Character ? portrait->CharacterImageNode
                : layer == Layer.Frame ? portrait->FrameImageNode : portrait->AccentImageNode;
            if (!OwnsImage(portrait, image) || image == null) return false;
            if ((image->NodeFlags & NodeFlags.Visible) == 0) return layer != Layer.Character;
            var parts = image->PartsList;
            if (parts == null || parts->Parts == null || parts->PartCount is 0 or > 4096
                || image->PartId >= parts->PartCount) return false;
            var part = parts->Parts[image->PartId];
            var asset = part.UldAsset;
            if (asset == null) return false;
            // Read the tagged union directly; never call a game texture method or change its refcount.
            var kernel = asset->AtkTexture.TextureType == TextureType.KernelTexture ? asset->AtkTexture.KernelTexture
                : asset->AtkTexture.TextureType == TextureType.Resource && asset->AtkTexture.Resource != null
                    ? asset->AtkTexture.Resource->KernelTextureObject : null;
            if (kernel == null || kernel->D3D11ShaderResourceView == null) return false;
            var valid = layer == Layer.Character
                ? TryCreateCharacterCopyArgs(kernel->AllocatedWidth, kernel->AllocatedHeight,
                    kernel->ActualWidth, kernel->ActualHeight, out var args)
                : TryCreateCopyArgs(kernel->AllocatedWidth, kernel->AllocatedHeight,
                    part.U, part.V, part.Width, part.Height, out args);
            if (!valid) return false;
            IDalamudTextureWrap borrowed = new BorrowedTextureWrap((nint)kernel->D3D11ShaderResourceView,
                checked((int)kernel->AllocatedWidth), checked((int)kernel->AllocatedHeight));
            // The public default implementation AddRefs the SRV and returns a Dalamud-owned wrap.
            // The native Kernel::Texture pointer is never kept and never touched by Dispose.
            var retained = borrowed.CreateWrapSharingLowLevelResource();
            input = new(retained, args);
            return true;
        }

        private unsafe bool IsSourceCurrent(PortraitIdentity identity, nint expectedAddon)
        {
            if (!IsFrameworkThread || !IsCurrent(identity)) return false;
            var editor = AgentBannerEditor.Instance();
            var addon = gameGUI.GetAddonByName<AtkUnitBase>("BannerEditor");
            return editor != null && addon != null && (nint)addon == expectedAddon && addon->IsVisible
                && (nint)editor == identity.Editor && addon->Id == editor->AddonId
                && editor->EditorState != null && (nint)editor->EditorState == identity.State
                && (nint)editor->EditorState->CharaView == identity.Model;
        }

        private static unsafe ComponentType ReadComponentType(nint component)
            => ((AtkComponentBase*)component)->GetComponentType();

        private static unsafe bool OwnsImage(AtkComponentPortrait* portrait, AtkImageNode* image)
        {
            if (image == null || image->Type != NodeType.Image) return false;
            var manager = &portrait->UldManager;
            if (manager->NodeList == null || manager->NodeListCount is 0 or > 4096) return false;
            for (var index = 0; index < manager->NodeListCount; index++)
                if (manager->NodeList[index] == (AtkResNode*)image) return true;
            return false;
        }
    }

    /// <summary>A synchronous bridge used only to request a public owned SRV reference.</summary>
    private sealed class BorrowedTextureWrap(nint handle, int width, int height) : IDalamudTextureWrap
    {
        public ImTextureID Handle => new(handle);
        public int Width { get; } = width;
        public int Height { get; } = height;
        public void Dispose() { }
    }
}
