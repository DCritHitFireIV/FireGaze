using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using NativeBounds = FFXIVClientStructs.FFXIV.Common.Math.Bounds;

namespace FireGaze.PortraitPreview;

internal interface IPortraitNativeLiveNodeApi
{
    bool IsFrameworkThread { get; }
    // Must freshly resolve the ready native target, including its character allocation generation.
    PortraitIdentity? ReadReadyEditor();
    PortraitCaptureOwner ReadOwner();
    nint ReadEditorAddon();
    nint FindPortrait(nint addon, Func<bool> current);
    PortraitCaptureRect? ReadNodeBounds(nint node);
    IDalamudTextureWrap Retain(nint handle, int width, int height);
}

/// <summary>
/// Framework-only CharacterImage SRV acquisition. No pause/state-key requirement, copy, readback,
/// renderer, camera/light write, native render call, world object or custom draw callback.
/// </summary>
internal sealed unsafe class PortraitNativeLiveNodeSource : IPortraitNativeLiveSource, IDisposable
{
    private readonly IPortraitNativeLiveNodeApi api;
    private readonly IAddonLifecycle? lifecycle;
    private long epoch = 1;
    private int disposed;

    public event Action? Invalidated;
    public bool IsFrameworkThread => Volatile.Read(ref disposed) == 0 && api.IsFrameworkThread;

    public PortraitNativeLiveNodeSource(IFramework framework, IGameGui gameGUI, IObjectTable objects,
        IAddonLifecycle lifecycle, Func<PortraitIdentity?> readReadyEditor)
        : this(new LiveApi(framework, gameGUI, objects, readReadyEditor))
    {
        this.lifecycle = lifecycle;
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "BannerEditor", BeforeFinalize);
    }

    internal PortraitNativeLiveNodeSource(IPortraitNativeLiveNodeApi api) => this.api = api;

    private void BeforeFinalize(AddonEvent type, AddonArgs args) => Invalidate();

    public void Invalidate()
    {
        Interlocked.Increment(ref epoch);
        Invalidated?.Invoke();
    }

    public bool TryAcquire(out PortraitNativeLiveFrame? frame)
    {
        frame = null;
        PortraitCaptureNativeImage? acquired = null;
        try
        {
            if (!IsFrameworkThread || api.ReadReadyEditor() is not { IsValid: true, Generation: > 0 } identity) return false;
            var owner = api.ReadOwner();
            var addon = api.ReadEditorAddon();
            var generation = Volatile.Read(ref epoch);
            bool OwnerCurrent() => Current(identity, owner, addon, generation);
            if (!owner.IsValid || !OwnerCurrent()) return false;
            var component = api.FindPortrait(addon, OwnerCurrent);
            bool ComponentCurrent() => OwnerCurrent() && api.FindPortrait(addon, OwnerCurrent) == component && OwnerCurrent();
            if (component == 0 || !ComponentCurrent()) return false;
            var image = (nint)((AtkComponentPortrait*)component)->CharacterImageNode;
            bool ImageCurrent() => ComponentCurrent() && (nint)((AtkComponentPortrait*)component)->CharacterImageNode == image;
            if (!PortraitCaptureNodeReader.TryAcquire(component, image, true, ImageCurrent,
                api.ReadNodeBounds, api.Retain, out acquired) || acquired is null) return false;
            // The reader's final descriptor read follows its final owner callback. Do not call
            // another native owner/component lookup here: a plugin callback could replace the SRV.
            var key = new PortraitNativeLiveKey(owner, identity, addon, component, image, generation);
            frame = new(key, acquired.Texture, acquired.AllocatedWidth, acquired.AllocatedHeight,
                acquired.ActualWidth, acquired.ActualHeight, acquired.Tint);
            acquired = null;
            return true;
        }
        catch (PortraitCaptureCleanupException)
        {
            // The view owns the admitted acquisition prefix. It must quarantine the failed
            // independent wrapper before returning that prefix's physical lifetime.
            throw;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            PortraitCaptureCleanupException.DisposeAll(acquired?.Texture);
        }
    }

    private bool Current(PortraitIdentity identity, PortraitCaptureOwner owner, nint addonAddress, long generation)
    {
        if (!IsFrameworkThread || generation != Volatile.Read(ref epoch) || api.ReadReadyEditor() != identity
            || api.ReadOwner() != owner || addonAddress == 0 || api.ReadEditorAddon() != addonAddress) return false;
        // The final game API callback may have entered PreFinalize or disposed this source.
        // Check detached lifetime state after that callback, before touching expected allocations.
        if (!api.IsFrameworkThread || Volatile.Read(ref disposed) != 0 || generation != Volatile.Read(ref epoch)) return false;
        var addon = (AtkUnitBase*)addonAddress;
        var editor = (AgentBannerEditor*)identity.Editor;
        if (!addon->IsVisible || !addon->IsReady || addon->Id != editor->AddonId
            || editor->EditorState == null || (nint)editor->EditorState != identity.State) return false;
        var state = editor->EditorState;
        var view = state->CharaView;
        return state->AgentBannerEditor == editor && view != null && (nint)view == identity.Model
            && (view->Agent == null || view->Agent == (AgentInterface*)editor)
            && view->State == 6 && view->CharaViewPortraitCharacterLoaded;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Invalidate();
        lifecycle?.UnregisterListener(AddonEvent.PreFinalize, "BannerEditor", BeforeFinalize);
    }

    private sealed class LiveApi(IFramework framework, IGameGui gameGUI, IObjectTable objects,
        Func<PortraitIdentity?> readReadyEditor) : IPortraitNativeLiveNodeApi
    {
        public bool IsFrameworkThread => framework.IsInFrameworkUpdateThread;
        public PortraitIdentity? ReadReadyEditor() => IsFrameworkThread ? readReadyEditor() : null;
        public PortraitCaptureOwner ReadOwner()
        {
            var local = IsFrameworkThread ? (Character*)(objects.LocalPlayer?.Address ?? 0) : null;
            return local == null ? default : new(local->AccountId, local->ContentId);
        }
        public nint ReadEditorAddon() => IsFrameworkThread ? (nint)gameGUI.GetAddonByName<AtkUnitBase>("BannerEditor") : 0;
        public nint FindPortrait(nint addon, Func<bool> current)
            => PortraitThumbnailSource.FindPortrait(addon, current, ReadComponentType);
        private static ComponentType ReadComponentType(nint component) => ((AtkComponentBase*)component)->GetComponentType();
        public PortraitCaptureRect? ReadNodeBounds(nint node)
        {
            NativeBounds bounds = default;
            ((AtkResNode*)node)->GetBounds(&bounds);
            return new(bounds.Pos1.X, bounds.Pos1.Y, bounds.Width, bounds.Height);
        }
        public IDalamudTextureWrap Retain(nint handle, int width, int height)
            => ((IDalamudTextureWrap)new BorrowedTexture(handle, width, height)).CreateWrapSharingLowLevelResource();
    }

    private sealed class BorrowedTexture(nint handle, int width, int height) : IDalamudTextureWrap
    {
        public ImTextureID Handle => new(handle);
        public int Width => width;
        public int Height => height;
        public void Dispose() { }
    }
}
