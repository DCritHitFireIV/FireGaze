using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Dalamud.Hooking;
using FireGaze.LocalPreview.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI;
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
    private readonly Func<PreviewMenuOptions> options;
    private readonly IReadOnlyDictionary<uint, uint> emoteUnlockLinks;
    private readonly NativeEmoteLogCapture nativeLogCapture;
    private readonly IGameInteropProvider interop;
    private Hook<AtkResNode.Delegates.DispatchEvent>? dispatchEvent;
    [ThreadStatic] private static NativeMouseDispatchContext? mouseDispatch;
    private readonly EmoteHistory history = new();
    private static readonly string[] ChatPanels = ["ChatLog", "ChatLogPanel_0", "ChatLogPanel_1", "ChatLogPanel_2", "ChatLogPanel_3"];
    private readonly NativeLinkContext nativeLink = new();
    private readonly Stack<EmoteMenuInvocation> nativeClicks = new();
    private readonly record struct EmoteMenuInvocation(bool RightClick, int MenusBefore, uint OwnerAddon);
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
    public string LastNativeEmoteEvent { get; private set; } = "尚未观察情感链接事件";

    public PreviewInteractionService(IContextMenu menus, IChatGui chat, IGameGui gameGui, IObjectTable objects,
        IDataManager data, IClientState clientState, IGameInteropProvider interop, IPluginLog log,
        Action<PreviewKind, uint> requestPreview,
        Func<(ulong OwnerId, ushort EmoteId)?>? currentNativeEmote = null,
        IAddonLifecycle? addonLifecycle = null, Func<PreviewMenuOptions>? options = null)
    {
        this.menus = menus;
        this.chat = chat;
        this.objects = objects;
        this.data = data;
        this.clientState = clientState;
        this.addonLifecycle = addonLifecycle;
        this.log = log;
        this.interop = interop;
        this.requestPreview = requestPreview;
        this.options = options ?? (() => new());
        this.emoteUnlockLinks = data.GetExcelSheet<Emote>().Where(emote => emote.RowId != 0 && emote.UnlockLink != 0)
            .ToDictionary(emote => emote.RowId, emote => emote.UnlockLink);
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
            // AtkTextNode.DispatchEvent constructs LinkMouseClick recursively from
            // MouseClick and deliberately zeroes the rest of its data. Preserve the
            // original button only for that synchronous, same-node call chain.
            dispatchEvent = interop.HookFromAddress<AtkResNode.Delegates.DispatchEvent>(
                AtkResNode.Addresses.DispatchEvent.Value, DispatchNativeEvent);
            dispatchEvent.Enable();
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
            dispatchEvent?.Dispose();
            dispatchEvent = null;
            addonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, ChatPanels, BeforeNativeChatEvent);
            addonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, ChatPanels, AfterNativeChatEvent);
            log.Error(ex, "[LocalPreview] Native chat sentence links could not start.");
            Capabilities = Capabilities with { Detail = "原生情感提示链接启动失败。" };
            return false;
        }
    }

    // Retain the existing diagnostic command API; normal hosts call the method above.
    public bool EnableNativeChatProbe() => EnableNativeChatLinks();

    private bool DispatchNativeEvent(AtkResNode* node, AtkEventDispatcher.Event* evt)
    {
        var context = mouseDispatch ??= new();
        context.Enter((nint)node, evt != null && evt->State.EventType == AtkEventType.MouseClick
            ? evt->EventData.MouseData.ButtonId : null);
        try { return dispatchEvent!.Original(node, evt); }
        finally { context.Exit(); }
    }

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
        nativeClicks.Clear();
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
                if (!options().Items) return;
                // Inventory menus expose their exact selected slot. Chat links
                // use a DEFAULT menu and their own current ChatLog context below.
                var item = inventory.TargetItem;
                if (item is { } slot)
                {
                    var requests = ResolveItems(slot.BaseItemId);
                    if (requests.Count == 1) AddItem(args, requests[0], fromItem: true);
                    else if (requests.Count > 1) AddEmoteSubmenu(args, requests);
                    if (requests.Count > 0) ItemMenusResolved++;
                }
                return;
            }

            if (TryGetChatItem(args, out var chatItemId))
            {
                if (!options().Items) return;
                var requests = ResolveItems(chatItemId);
                if (requests.Count == 1) AddItem(args, requests[0], fromItem: true);
                else if (requests.Count > 1) AddEmoteSubmenu(args, requests);
                if (requests.Count > 0) ItemMenusResolved++;
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

    public IReadOnlyList<MenuPreviewRequest> ResolveItems(uint itemId)
    {
        if (!data.GetExcelSheet<Item>().TryGetRow(itemId, out var item) || !item.ItemAction.IsValid) return [];
        var action = item.ItemAction.Value;
        return ItemCollectionResolver.ResolveAll(action.Action.RowId, action.Data[0], emoteUnlockLinks)
            .Where(Valid).ToArray();
    }

    private static bool TryGetChatItem(IMenuOpenedArgs args, out uint itemId)
    {
        itemId = 0;
        if (args.MenuType != ContextMenuType.Default || args.AddonPtr == 0
            || !ChatItemMenuResolver.IsChatAddon(args.AddonName)) return false;
        var context = AgentContext.Instance();
        var chatLog = AgentChatLog.Instance();
        if (context == null || chatLog == null || args.AgentPtr != (nint)context) return false;
        var addon = (AtkUnitBase*)args.AddonPtr;
        // Chat item links open the DEFAULT menu, not the inventory menu. This
        // field belongs to that menu's current ChatLog owner; no last-link cache.
        return ChatItemMenuResolver.TryResolve(new(true, args.AddonName, addon->Id, args.AgentPtr,
            (nint)context, context->OwnerAddon, (nint)chatLog, args.EventInterfaces.Contains((nint)chatLog),
            chatLog->ContextItemId), out itemId);
    }

    private bool Valid(MenuPreviewRequest request) => request.Kind switch
    {
        PreviewKind.Mount => data.GetExcelSheet<Mount>().TryGetRow(request.Id, out _),
        PreviewKind.Minion => data.GetExcelSheet<Lumina.Excel.Sheets.Companion>().TryGetRow(request.Id, out _),
        PreviewKind.Accessory => data.GetExcelSheet<Lumina.Excel.Sheets.Ornament>().TryGetRow(request.Id, out _),
        PreviewKind.Emote => data.GetExcelSheet<Emote>().TryGetRow(request.Id, out _),
        _ => false,
    };

    private void AddItem(IMenuOpenedArgs args, MenuPreviewRequest request, bool fromItem = false)
    {
        if (!options().Allows(request.Kind, fromItem) || !Valid(request)) return;
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
                if (!disposed && menuGeneration == generation && options().Allows(request.Kind, fromItem))
                    requestPreview(request.Kind, request.Id);
            },
        });
    }

    private void AddEmoteSubmenu(IMenuOpenedArgs args, IReadOnlyList<MenuPreviewRequest> requests)
    {
        if (!options().Allows(PreviewKind.Emote, fromItem: true)) return;
        var menuGeneration = generation;
        var selections = requests.Where(request => request.Kind == PreviewKind.Emote && Valid(request)).ToArray();
        if (selections.Length == 0) return;
        args.AddMenuItem(new MenuItem
        {
            Name = "试用情感动作",
            IsSubmenu = true,
            OnClicked = clicked =>
            {
                if (!CanSelect()) return;
                var children = selections.Select(request => (IMenuItem)new MenuItem
                {
                    Name = EmoteName(request.Id),
                    OnClicked = _ =>
                    {
                        if (CanSelect()) requestPreview(request.Kind, request.Id);
                    },
                }).ToArray();
                clicked.OpenSubmenu("试用情感动作", children);
            },
        });

        bool CanSelect() => !disposed && menuGeneration == generation
            && options().Allows(PreviewKind.Emote, fromItem: true);
    }

    private string EmoteName(uint id)
    {
        var name = data.GetExcelSheet<Emote>().GetRow(id).Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? $"情感动作 #{id}" : name;
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
        bool rightClick = false;
        uint ownerAddon = 0;
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
                    ownerAddon = received.Addon.IsNull ? 0U : ((AtkUnitBase*)received.Addon.Address)->Id;
                    // LinkData is a pointer in this event's union, not AtkMouseData.
                    rightClick = mouseDispatch?.Button == 1;
                    var input = UIInputData.Instance();
                    if (input != null)
                    {
                        var raw = input->CursorInputs;
                        var filtered = input->UIFilteredCursorInputs;
                        LastNativeEmoteEvent = $"emote={id}, right={rightClick}, dispatchButton={mouseDispatch?.Button.ToString() ?? "none"}, raw={(int)raw.MouseButtonHeldFlags}/{(int)raw.MouseButtonPressedFlags}/{(int)raw.MouseButtonReleasedFlags}, filtered={(int)filtered.MouseButtonHeldFlags}/{(int)filtered.MouseButtonPressedFlags}/{(int)filtered.MouseButtonReleasedFlags}, owner={ownerAddon}";
                    }
                }
            }
        }
        catch (Exception ex) { log.Error(ex, "[LocalPreview] Native emote event inspection failed; original event remains unchanged."); }
        // Do not retain AtkEventData/LinkData pointers. Only this immutable row ID survives
        // until PostReceiveEvent. Zero masks an unrelated nested event.
        nativeLink.Enter(id);
        nativeClicks.Push(new(rightClick, NativeEmoteMenusObserved, ownerAddon));
    }

    private void AfterNativeChatEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (disposed || !nativeClicks.TryPop(out var click) || !options().Emote
                || !NativeEmoteMenuPolicy.ShouldOpen(nativeLink.EmoteId, click.RightClick,
                    click.MenusBefore, NativeEmoteMenusObserved, click.OwnerAddon)) return;
            var context = AgentContext.Instance();
            if (context == null || !Valid(new(PreviewKind.Emote, nativeLink.EmoteId))) return;
            // Native Emote links do not provide their own context menu on this client.
            // A disabled native heading makes the ordinary ContextMenu nonempty; Dalamud
            // then adds the actionable entry through the same OnMenuOpened callback.
            // Do not SetMenuTitle: that selects AddonContextMenuTitle instead.
            context->ClearMenu();
            context->AddMenuItem(EmoteName(nativeLink.EmoteId), null, 0, disabled: true);
            context->OpenContextMenuForAddon(click.OwnerAddon, true);
        }
        catch (Exception ex) { log.Error(ex, "[LocalPreview] Native emote context menu could not open."); }
        finally { nativeLink.Exit(); }
    }

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
        nativeClicks.Clear();
        nativeLogCapture.Dispose();
        dispatchEvent?.Dispose();
        dispatchEvent = null;
        history.Clear();
    }
}
