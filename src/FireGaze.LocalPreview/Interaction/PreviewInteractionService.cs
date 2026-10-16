using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Interaction;

public sealed record InteractionCapabilities(bool ObjectMenus, bool ItemMenus, bool NativeEmoteSentenceProbe,
    bool NativeEmoteContextObserved, string Detail);

/// <summary>Collects immutable menu selections. Native display changes belong to the runtime.</summary>
public sealed unsafe class PreviewInteractionService : IDisposable
{
    private readonly IContextMenu menus;
    private readonly IChatGui chat;
    private readonly IObjectTable objects;
    private readonly IDataManager data;
    private readonly IClientState clientState;
    private readonly IAddonLifecycle? addonLifecycle;
    private readonly IPluginLog log;
    private readonly Action<PreviewKind, uint> requestPreview;
    private readonly NativeEmoteLogCapture nativeLogCapture;
    private readonly EmoteHistory history = new();
    private static readonly string[] ChatPanels = ["ChatLogPanel_0", "ChatLogPanel_1", "ChatLogPanel_2", "ChatLogPanel_3"];
    private readonly NativeLinkContext nativeLink = new();
    private long generation;
    private bool disposed;
    private bool chatProbeEnabled;

    public InteractionCapabilities Capabilities { get; private set; } =
        new(true, true, false, false, "原生情感提示整句链接尚未开启；角色和收录物品菜单已注册。");
    public int ObservedEmotes { get; private set; }
    public int ChatMessagesLinked { get; private set; }
    public int ChatMessagesWithoutExactContext { get; private set; }
    public int NativeEmoteMenusObserved { get; private set; }
    public int NativeEmoteLinkEventsObserved { get; private set; }
    public bool NativeLogCaptureEnabled => nativeLogCapture.Enabled;
    public int NativeLogMessagesLinked => nativeLogCapture.MessagesLinked;
    public int UniqueEmoteLogTemplates => nativeLogCapture.UniqueTemplates;
    public int AmbiguousEmoteLogTemplates => nativeLogCapture.AmbiguousTemplates;
    public int UnmatchedNativeLogFormats => nativeLogCapture.FormatsUnmatched;
    public string NativeLogCaptureDetail => nativeLogCapture.Detail;
    public int ItemMenusResolved { get; private set; }
    public string LastMenuContext { get; private set; } = "尚未观察菜单";

    public PreviewInteractionService(IContextMenu menus, IChatGui chat, IGameGui gameGui, IObjectTable objects,
        IDataManager data, IClientState clientState, IGameInteropProvider interop, IPluginLog log,
        Action<PreviewKind, uint> requestPreview,
        Func<(ulong OwnerId, ushort EmoteId)?>? currentNativeEmote = null,
        IAddonLifecycle? addonLifecycle = null)
    {
        this.menus = menus;
        this.chat = chat;
        this.objects = objects;
        this.data = data;
        this.clientState = clientState;
        this.addonLifecycle = addonLifecycle;
        this.log = log;
        this.requestPreview = requestPreview;
        this.nativeLogCapture = new(data, interop, log);
        history.ChangeTerritory(clientState.TerritoryType);
        menus.OnMenuOpened += OnMenuOpened;
        chat.ChatMessage += OnChatMessage;
        clientState.TerritoryChanged += OnTerritoryChanged;
    }

    /// <summary>
    /// Enables exact native emote-log capture and native menu context. Call during
    /// normal feature initialization so every subsequent native sentence is linked.
    /// </summary>
    public bool EnableNativeChatLinks()
    {
        if (disposed) return false;
        if (chatProbeEnabled) return nativeLogCapture.Enabled;
        if (addonLifecycle is null)
        {
            Capabilities = Capabilities with { Detail = "未提供原生界面生命周期服务；整句链接未开启。" };
            return false;
        }
        try
        {
            addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, ChatPanels, BeforeNativeChatEvent);
            addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, ChatPanels, AfterNativeChatEvent);
            var captureReady = nativeLogCapture.Enable();
            chatProbeEnabled = true;
            Capabilities = Capabilities with
            {
                NativeEmoteSentenceProbe = captureReady,
                Detail = captureReady
                    ? "已开启原生情感句捕获：每句绑定发出时的 Emote ID，可经游戏原生菜单试用；仍需实测验收。"
                    : "原生情感句捕获未开启，已有 Emote 链接的菜单观察和基础右键菜单仍可使用。",
            };
            return captureReady;
        }
        catch (Exception ex)
        {
            addonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, ChatPanels, BeforeNativeChatEvent);
            addonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, ChatPanels, AfterNativeChatEvent);
            log.Error(ex, "[LocalPreview] Native chat sentence links could not start.");
            Capabilities = Capabilities with { Detail = "原生情感提示链接启动失败。" };
            return false;
        }
    }

    // Retain the existing diagnostic command API; normal hosts call the method above.
    public bool EnableNativeChatProbe() => EnableNativeChatLinks();

    public void RecordEmote(ulong actorObjectId, ushort emoteId)
    {
        if (disposed || emoteId == 0 || !data.GetExcelSheet<Emote>().TryGetRow(emoteId, out _)) return;
        var actor = objects.SearchById(actorObjectId);
        if (actor is null || actor.ObjectKind != ObjectKind.Pc) return;
        history.Record(Identity(actor), emoteId);
        ObservedEmotes++;
    }

    public void ClearTerritoryCache() => OnTerritoryChanged(clientState.TerritoryType);

    private void OnTerritoryChanged(uint territory)
    {
        generation++;
        history.ChangeTerritory(territory);
        nativeLink.Clear();
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (disposed) return;
        try
        {
            LastMenuContext = $"type={args.MenuType}, addon={args.AddonName ?? "<world>"}, agent=0x{args.AgentPtr:X}";
            if (nativeLink.EmoteId != 0 && args.MenuType == ContextMenuType.Default)
            {
                var id = nativeLink.EmoteId;
                AddItem(args, new(PreviewKind.Emote, id));
                NativeEmoteMenusObserved++;
                Capabilities = Capabilities with
                {
                    NativeEmoteContextObserved = true,
                    Detail = "已观察原生 Emote 链接打开游戏菜单并注入试用项；仍需右键整句人工验收。",
                };
                return;
            }

            if (args.Target is MenuTargetInventory inventory)
            {
                // This is the exact temporary inventory slot used by native item links too.
                // Never fall back to LastLinkedItemId, which can belong to a different message.
                var item = inventory.TargetItem;
                if (item is { } slot && ResolveItem(slot.BaseItemId) is { } request)
                {
                    AddItem(args, request);
                    ItemMenusResolved++;
                }
                return;
            }

            if (args.Target is not MenuTargetDefault target || target.TargetObject is not { } obj) return;
            if (obj.ObjectKind == ObjectKind.Companion)
            {
                if (data.GetExcelSheet<Lumina.Excel.Sheets.Companion>().TryGetRow(obj.BaseId, out _) && obj.BaseId != 0)
                    AddItem(args, new(PreviewKind.Minion, obj.BaseId));
                return;
            }
            if (obj.ObjectKind != ObjectKind.Pc || obj.Address == 0) return;
            var character = (NativeCharacter*)obj.Address;
            var snapshot = new ObjectPreviewSnapshot(
                character->Mode == CharacterModes.Mounted ? character->Mount.MountId : (ushort)0,
                character->CompanionData.CompanionObject != null
                    ? (ushort)character->CompanionData.CompanionObject->BaseId : (ushort)0,
                character->OrnamentData.OrnamentId);
            var emote = history.TryGet(Identity(obj), out var recent) ? (ushort)recent.EmoteId : (ushort)0;
            foreach (var request in PreviewMenuResolver.Resolve(snapshot, emote)) AddItem(args, request);
        }
        catch (Exception ex) { log.Error(ex, "[LocalPreview] Context menu collection failed."); }
    }

    public MenuPreviewRequest? ResolveItem(uint itemId)
    {
        if (!data.GetExcelSheet<Item>().TryGetRow(itemId, out var item) || !item.ItemAction.IsValid) return null;
        var action = item.ItemAction.Value;
        var request = ItemCollectionResolver.Resolve(action.Action.RowId, action.Data[0]);
        return request is { } value && Valid(value) ? value : null;
    }

    private bool Valid(MenuPreviewRequest request) => request.Kind switch
    {
        PreviewKind.Mount => data.GetExcelSheet<Mount>().TryGetRow(request.Id, out _),
        PreviewKind.Minion => data.GetExcelSheet<Lumina.Excel.Sheets.Companion>().TryGetRow(request.Id, out _),
        PreviewKind.Accessory => data.GetExcelSheet<Lumina.Excel.Sheets.Ornament>().TryGetRow(request.Id, out _),
        PreviewKind.Emote => data.GetExcelSheet<Emote>().TryGetRow(request.Id, out _),
        _ => false,
    };

    private void AddItem(IMenuOpenedArgs args, MenuPreviewRequest request)
    {
        if (!Valid(request)) return;
        var menuGeneration = generation;
        args.AddMenuItem(new MenuItem
        {
            Name = request.Kind switch
            {
                PreviewKind.Mount => "试召唤坐骑",
                PreviewKind.Minion => "试召唤宠物",
                PreviewKind.Accessory => "试用时尚配饰",
                _ => "试用情感动作",
            },
            OnClicked = _ =>
            {
                if (!disposed && menuGeneration == generation) requestPreview(request.Kind, request.Id);
            },
        });
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (disposed || !chatProbeEnabled || message.LogKind != XivChatType.StandardEmote) return;
        // Queue printing can occur long after PlayEmote returns. Never bind this message
        // from CurrentNativeEmote, the actor's latest emote, elapsed time or chat prose.
        if (NativeEmoteLinkBuilder.TryReadEmoteId(message.OriginalMessage.Data.Span, out _)) ChatMessagesLinked++;
        else ChatMessagesWithoutExactContext++;
    }

    private void BeforeNativeChatEvent(AddonEvent type, AddonArgs args)
    {
        if (disposed) return;
        ushort id = 0;
        try
        {
            if (args is AddonReceiveEventArgs received
                && (byte)received.AtkEventType == (byte)AtkEventType.LinkMouseClick
                && received.AtkEventData != 0)
            {
                var link = ((AtkEventData*)received.AtkEventData)->LinkData;
                if (link != null && link->Payload != null && NativeEmoteLinkBuilder.TryReadEmoteId(
                    new ReadOnlySeStringSpan(link->Payload).Data, out var observedId)
                    && Valid(new(PreviewKind.Emote, observedId)))
                {
                    id = observedId;
                    NativeEmoteLinkEventsObserved++;
                }
            }
        }
        catch (Exception ex) { log.Error(ex, "[LocalPreview] Native emote event inspection failed; original event remains unchanged."); }
        // Do not retain AtkEventData/LinkData pointers. Only this immutable row ID survives
        // until PostReceiveEvent. Zero masks an unrelated nested event.
        nativeLink.Enter(id);
    }

    private void AfterNativeChatEvent(AddonEvent type, AddonArgs args) => nativeLink.Exit();

    private static ActorIdentity Identity(IGameObject actor) => new(actor.GameObjectId, (ulong)actor.Address);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        generation++;
        menus.OnMenuOpened -= OnMenuOpened;
        chat.ChatMessage -= OnChatMessage;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        if (chatProbeEnabled && addonLifecycle is not null)
        {
            addonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, ChatPanels, BeforeNativeChatEvent);
            addonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, ChatPanels, AfterNativeChatEvent);
        }
        nativeLink.Clear();
        nativeLogCapture.Dispose();
        history.Clear();
    }
}
