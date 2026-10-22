using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Agent;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FireGaze.PortraitPreview;

internal unsafe interface IPortraitPlateOwnerApi
{
    bool IsFrameworkThread { get; }
    bool IsCurrent(PortraitPlateBinding binding);
    AgentCharaCard* GetOwner();
    void HideOwner(AgentCharaCard* owner);
    bool TryHideOriginalOwner(AgentCharaCard* owner, PortraitPlateBinding binding);
}

internal static unsafe class PortraitPlateOwnerClose
{
    public static bool TryClose(IPortraitPlateOwnerApi api, PortraitPlateBinding binding)
    {
        if (!api.IsFrameworkThread || !api.IsCurrent(binding)) return false;
        var owner = api.GetOwner();
        if (!Owns(api, owner, binding)) return false;
        api.HideOwner(owner);
        if (!api.IsCurrent(binding)) return true;
        return Owns(api, owner, binding) && api.TryHideOriginalOwner(owner, binding) && !api.IsCurrent(binding);
    }

    internal static bool Owns(IPortraitPlateOwnerApi api, AgentCharaCard* owner, PortraitPlateBinding binding)
        => owner != null && api.GetOwner() == owner && owner->Data != null
            && (nint)owner->Data == binding.Storage && owner->AddonId == binding.AddonID
            && owner->Data->AccountId == binding.AccountID && owner->Data->ContentId == binding.ContentID;
}

internal sealed unsafe partial class PortraitPlateMirror
{
    private readonly IAddonLifecycle? addonLifecycle;
    private readonly LiveApi? liveAPI;
    private enum ImageKind { Character, Frame, Accent }
    private sealed class Lifetime { public long Generation = 1; }

    public PortraitPlateMirror(IFramework framework, IGameGui gameGUI, IObjectTable objects,
        IAddonLifecycle addonLifecycle, IAgentLifecycle agentLifecycle, Func<PortraitIdentity, bool> isEditorCurrent)
    {
        var lifetime = new Lifetime();
        liveAPI = new(framework, gameGUI, objects, agentLifecycle, isEditorCurrent, lifetime, ImageKind.Character);
        api = liveAPI;
        overlays = [new(new LiveApi(framework, gameGUI, objects, agentLifecycle, isEditorCurrent, lifetime, ImageKind.Frame)),
            new(new LiveApi(framework, gameGUI, objects, agentLifecycle, isEditorCurrent, lifetime, ImageKind.Accent))];
        this.addonLifecycle = addonLifecycle;
        // The image asset must be restored before native ULD finalization destroys its texture.
        // A per-addon generation prevents same-address reuse from appearing to be an old owner.
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CharaCard", BeforeFinalize);
    }

    private void BeforeFinalize(AddonEvent eventType, AddonArgs args)
    {
        if (IsActive) _ = Close();
        liveAPI?.AdvanceGeneration();
    }

    partial void UnregisterNativeEvents()
    {
        addonLifecycle?.UnregisterListener(AddonEvent.PreFinalize, "CharaCard", BeforeFinalize);
    }

    private sealed class LiveApi(IFramework framework, IGameGui gameGUI, IObjectTable objects,
        IAgentLifecycle agentLifecycle, Func<PortraitIdentity, bool> isEditorCurrent, Lifetime lifetime, ImageKind kind)
        : IPortraitPlateMirrorApi, IPortraitPlateOwnerApi
    {
        public bool IsFrameworkThread => framework.IsInFrameworkUpdateThread;
        public void AdvanceGeneration() => lifetime.Generation++;
        public bool IsEditorCurrent(PortraitIdentity identity) => isEditorCurrent(identity);

        public nint GetSourceTexture(PortraitIdentity identity)
        {
            if (!IsFrameworkThread || !IsEditorCurrent(identity)) return 0;
            var image = GetSourceNode(identity);
            var asset = image == null ? null : GetAsset(image);
            return asset == null ? 0 : (nint)asset->AtkTexture.GetKernelTexture();
        }

        public PortraitPlateImageState GetSourceImage(PortraitIdentity identity)
        {
            var image = GetSourceNode(identity);
            if (image == null) throw new InvalidOperationException("编辑器肖像图层未就绪。");
            return ReadImageState(image);
        }

        private AtkImageNode* GetSourceNode(PortraitIdentity identity)
        {
            if (!IsFrameworkThread || !IsEditorCurrent(identity)) return null;
            var addon = gameGUI.GetAddonByName<AtkUnitBase>("BannerEditor");
            var editor = AgentBannerEditor.Instance();
            if (addon == null || editor == null || (nint)editor != identity.Editor || addon->Id != editor->AddonId) return null;
            return SelectImage(FindPortrait(addon), kind);
        }

        private static AtkComponentPortrait* FindPortrait(AtkUnitBase* addon)
        {
            var manager = &addon->UldManager;
            if (manager->NodeList == null || manager->NodeListCount is 0 or > 4096) return null;
            AtkComponentPortrait* match = null;
            for (var index = 0; index < manager->NodeListCount; index++)
            {
                var node = manager->NodeList[index];
                if (node == null || (ushort)node->Type < 1000) continue;
                var component = ((AtkComponentNode*)node)->Component;
                if (component == null || component->OwnerNode != (AtkComponentNode*)node || component->GetComponentType() != ComponentType.Portrait) continue;
                if (match != null) return null;
                match = (AtkComponentPortrait*)component;
            }
            return match;
        }

        private static AtkImageNode* SelectImage(AtkComponentPortrait* component, ImageKind selected) => component == null ? null
            : selected == ImageKind.Character ? component->CharacterImageNode
            : selected == ImageKind.Frame ? component->FrameImageNode : component->AccentImageNode;

        private static bool OwnsImage(AtkComponentPortrait* component, AtkImageNode* image)
        {
            if (component == null || image == null || image->Type != NodeType.Image) return false;
            var manager = &component->UldManager;
            if (manager->NodeList == null || manager->NodeListCount is 0 or > 4096) return false;
            for (var index = 0; index < manager->NodeListCount; index++) if (manager->NodeList[index] == (AtkResNode*)image) return true;
            return false;
        }

        private bool TryGetOwner(out AtkUnitBase* addon, out AgentCharaCard.Storage* storage, bool requireVisible)
        {
            addon = null; storage = null;
            if (!IsFrameworkThread) return false;
            var agent = AgentCharaCard.Instance();
            var local = (Character*)(objects.LocalPlayer?.Address ?? 0);
            if (agent == null || local == null || requireVisible && !agent->IsAgentActive()) return false;
            storage = agent->Data;
            addon = gameGUI.GetAddonByName<AtkUnitBase>("CharaCard");
            return storage != null && addon != null && (!requireVisible || addon->IsVisible) && addon->Id == agent->AddonId
                && storage->AccountId != 0 && storage->ContentId != 0
                && storage->AccountId == local->AccountId && storage->ContentId == local->ContentId;
        }

        public bool TryResolve(PortraitIdentity identity, out PortraitPlateBinding result)
        {
            result = default;
            if (!IsEditorCurrent(identity) || !TryGetOwner(out var addon, out var storage, requireVisible: true) || storage->PortraitTexture == null) return false;
            var component = FindPortrait(addon);
            var match = SelectImage(component, kind);
            var asset = match == null ? null : GetAsset(match);
            if (!OwnsImage(component, match) || asset == null || !IsExclusiveImage(component, match, asset)) return false;
            if (kind == ImageKind.Character && asset->AtkTexture.GetKernelTexture() != storage->PortraitTexture) return false;
            result = new((nint)addon, addon->Id, (nint)match, (nint)asset, match->PartId, (nint)storage,
                storage->AccountId, storage->ContentId, lifetime.Generation, (nint)component);
            return IsEditorCurrent(identity) && IsCurrent(result);
        }

        public bool IsCurrent(PortraitPlateBinding expected)
        {
            if (expected.Generation != lifetime.Generation || !TryGetOwner(out var addon, out var storage, requireVisible: false)
                || expected.Addon != (nint)addon || expected.AddonID != addon->Id || expected.Storage != (nint)storage
                || expected.AccountID != storage->AccountId || expected.ContentID != storage->ContentId) return false;
            var component = FindPortrait(addon);
            if (component == null || (nint)component != expected.Component) return false;
            var image = SelectImage(component, kind);
            // Only dereference freshly enumerated live nodes, never a saved pointer alone.
            if (!OwnsImage(component, image) || (nint)image != expected.Node) return false;
            var asset = GetAsset(image);
            return image->PartId == expected.PartID && asset != null && (nint)asset == expected.Asset
                && IsExclusiveImage(component, image, asset);
        }

        private static AtkUldAsset* GetAsset(AtkImageNode* image)
        {
            var parts = image->PartsList;
            return parts == null || parts->Parts == null || parts->PartCount is 0 or > 4096 || image->PartId >= parts->PartCount
                ? null : parts->Parts[image->PartId].UldAsset;
        }

        private static bool IsExclusiveImage(AtkComponentPortrait* component, AtkImageNode* expectedImage, AtkUldAsset* expectedAsset)
        {
            // Native runtime portrait images use an independent dynamic parts list (Id=0,
            // one part). UldManager's node cleanup releases that single asset's AtkTexture.
            // Static ULD atlases or shared image assets are deliberately not borrowed.
            var parts = expectedImage->PartsList;
            if (parts == null || parts->Id != 0 || parts->PartCount != 1 || expectedImage->PartId != 0) return false;
            var manager = &component->UldManager;
            for (var index = 0; index < manager->NodeListCount; index++)
            {
                var node = manager->NodeList[index];
                if (node == null || node->Type != NodeType.Image || node == (AtkResNode*)expectedImage) continue;
                var image = (AtkImageNode*)node;
                if (image->PartsList == parts || GetAsset(image) == expectedAsset) return false;
            }
            return true;
        }

        private static PortraitPlateImageState ReadImageState(AtkImageNode* image)
        {
            var parts = image->PartsList;
            if (parts == null || parts->Parts == null || image->PartId >= parts->PartCount) throw new InvalidOperationException("肖像图层裁切未就绪。");
            var part = parts->Parts[image->PartId];
            return new(part.U, part.V, part.Width, part.Height, (image->NodeFlags & NodeFlags.Visible) != 0);
        }

        public PortraitPlateImageState ReadImage(PortraitPlateBinding active)
        {
            if (!IsCurrent(active)) throw new InvalidOperationException("铭牌图像所有者已改变。");
            return ReadImageState((AtkImageNode*)active.Node);
        }

        public bool TryWriteImage(PortraitPlateBinding active, PortraitPlateImageState expected, PortraitPlateImageState replacement)
        {
            if (!IsCurrent(active)) return false;
            var image = (AtkImageNode*)active.Node;
            if (ReadImageState(image) != expected) return false;
            var part = &image->PartsList->Parts[image->PartId];
            part->U = replacement.U; part->V = replacement.V; part->Width = replacement.Width; part->Height = replacement.Height;
            image->NodeFlags = replacement.Visible ? image->NodeFlags | NodeFlags.Visible : image->NodeFlags & ~NodeFlags.Visible;
            return true;
        }

        public AtkTexture ReadTexture(PortraitPlateBinding active)
        {
            if (!IsCurrent(active)) throw new InvalidOperationException("铭牌图像所有者已改变。");
            return ((AtkUldAsset*)active.Asset)->AtkTexture;
        }

        public AtkTexture CreateRetainedTexture(PortraitIdentity identity, nint source)
        {
            if (!IsFrameworkThread || source == 0) throw new InvalidOperationException("肖像渲染纹理未就绪。");
            AtkTexture texture = default;
            texture.Ctor();
            // Ctor and GetCharaViewTexture can enter native hooks. Re-resolve after Ctor,
            // then retain the freshly returned target before any other native operation.
            if (!IsEditorCurrent(identity) || GetSourceTexture(identity) != source || !IsEditorCurrent(identity))
                throw new InvalidOperationException("肖像渲染纹理所有者已改变。");
            var kernel = (Texture*)source;
            if (kernel->VirtualTable == null || kernel->VirtualTable->IncRef == null || kernel->VirtualTable->DecRef == null)
                throw new InvalidOperationException("肖像纹理引用接口未就绪。");
            kernel->IncRef();
            texture.KernelTexture = kernel;
            texture.TextureType = TextureType.KernelTexture;
            return texture;
        }

        public bool TryWriteTexture(PortraitPlateBinding active, AtkTexture expected, AtkTexture replacement)
        {
            if (!IsCurrent(active)) return false;
            var destination = &((AtkUldAsset*)active.Asset)->AtkTexture;
            if (!Same(*destination, expected)) return false;
            *destination = replacement;
            return true;
        }

        public void ReleaseTexture(ref AtkTexture texture)
        {
            if (!IsFrameworkThread) throw new InvalidOperationException("纹理释放必须在游戏框架线程执行。");
            texture.ReleaseTexture();
            texture = default;
        }

        public bool TryCloseOwner(PortraitPlateBinding active) => PortraitPlateOwnerClose.TryClose(this, active);
        public AgentCharaCard* GetOwner() => AgentCharaCard.Instance();
        public void HideOwner(AgentCharaCard* owner) => owner->Hide();

        public bool TryHideOriginalOwner(AgentCharaCard* owner, PortraitPlateBinding active)
        {
            // Another plugin may prevent ordinary Hide. On unload, use the original table
            // supplied by Dalamud only after revalidating this exact local plate allocation.
            if (!IsFrameworkThread || !IsCurrent(active) || !PortraitPlateOwnerClose.Owns(this, owner, active)) return false;
            var agent = (AgentInterface*)owner;
            var table = (AgentInterface.AgentInterfaceVirtualTable*)agentLifecycle.GetOriginalVirtualTable((nint)agent->VirtualTable);
            if (table == null || table->Hide == null || !IsCurrent(active) || !PortraitPlateOwnerClose.Owns(this, owner, active)) return false;
            table->Hide(agent);
            return true;
        }
    }
}
