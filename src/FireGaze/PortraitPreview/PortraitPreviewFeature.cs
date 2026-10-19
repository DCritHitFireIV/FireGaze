using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using FireGaze.UI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CabinetRow = Lumina.Excel.Sheets.Cabinet;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature : IPortraitPreviewViewModel, IDisposable
{
    private readonly NativePortraitTarget target;
    private readonly PortraitPreviewSession session;
    private readonly OwnedEquipmentCatalog owned;
    private readonly OwnedNativeInventoryReader inventory;
    private readonly OwnedEquipmentCacheFile cache;
    private readonly OwnedCacheWriter cacheWriter;
    private readonly OwnedCacheSavePolicy ownedSavePolicy;
    private readonly PortraitPreviewWindow window;
    private readonly WindowSystem windows = new("FireGazePortrait");
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICallGateProvider<string> diagnostics;
    private Dictionary<PortraitSlot, PortraitBrowserItem[]> browser = [];
    private OwnedEquipmentView ownedView = new(null, [], []);
    private Dictionary<uint, Item> equipment = [];
    private Dictionary<uint, Glasses> glasses = [];
    private Dictionary<uint, PortraitBrowserItem> metadata = [];
    private string[] sourceSummary = [];
    private PortraitDye[] dyes = [];
    private string status = "请打开肖像编辑器";
    private Vector2 buttonPosition;
    private bool editorAvailable;
    private DateTime nextInventoryRead;
    private bool disposed;
    private readonly PortraitCaptureIsolation captureIsolation = new();
    private readonly PortraitPhaseTrace runtimeTrace = new(message => Plugin.Log.Information("[PortraitTrace] {Stage}", message));

    public PortraitPreviewFeature(IDalamudPluginInterface pluginInterface, string configDirectory)
    {
        this.pluginInterface = pluginInterface;
        equipment = Plugin.DataManager.GetExcelSheet<Item>().Where(row => row.EquipSlotCategory.RowId != 0 && row.RowId != 0)
            .ToDictionary(row => row.RowId);
        glasses = Plugin.DataManager.GetExcelSheet<Glasses>().Where(row => row.RowId > 0 && row.Model != 0).ToDictionary(row => row.RowId);
        dyes = Plugin.DataManager.GetExcelSheet<Stain>().Where(row => row.RowId <= byte.MaxValue && !string.IsNullOrWhiteSpace(row.Name.ToString()))
            .Select(row => new PortraitDye((byte)row.RowId, row.Name.ToString(), row.Color, row.Shade, row.SubOrder)).ToArray();
        owned = new(id => equipment.ContainsKey(id % 1_000_000) ? id % 1_000_000 : null);
        cache = new(configDirectory);
        cache.Load(owned, out _);
        cacheWriter = new(json => cache.SaveJSON(json, out _));
        ownedSavePolicy = new(cacheWriter, owned.ContentRevision);
        var cabinetItems = Plugin.DataManager.GetExcelSheet<CabinetRow>().Select(row => new OwnedCabinetItem(row.RowId, row.Item.RowId)).ToArray();
        inventory = new(() => Plugin.Framework.IsInFrameworkUpdateThread, CurrentCharacter, () => cabinetItems, IsRetainerInventoryOpen);
        target = new(Plugin.Framework, Plugin.Objects, Plugin.GameGUI, Plugin.Interop,
            identity => session?.BlockSave(identity) == true, Error, Plugin.AgentLifecycle, Plugin.AddonLifecycle);
        session = new(target);
        InitializeEditing(configDirectory);
        InitializeLibrary(configDirectory);
        InitializePhotos(configDirectory);
        InitializeLiveCapture();
        InitializeLightingFit();
        window = new(this, pluginInterface, Plugin.Textures, photos);
        windows.AddWindow(window);
        diagnostics = pluginInterface.GetIpcProvider<string>("FireGaze.PortraitPreview.Diagnostics");
        diagnostics.RegisterFunc(() => OnFramework(Diagnostics));
        Plugin.Framework.Update += Update;
        pluginInterface.UiBuilder.Draw += Draw;
    }

    public bool IsActive => session.IsActive;
    public PortraitLook? Current => session.Current;
    public string Status => plateMirrorCloseError ?? cachePersistenceWarning ?? status;
    public IReadOnlyList<string> SourceSummary => ownedView.Character == CurrentCharacter() ? sourceSummary : [];
    public IReadOnlyList<PortraitDye> Dyes => dyes;
    public IReadOnlyList<PortraitBrowserItem> GetItems(PortraitSlot slot) => browser.TryGetValue(slot, out var values) ? values : [];
    public PortraitBrowserItem? GetItem(PortraitSlot slot, uint id)
    {
        if (slot == PortraitSlot.Facewear)
            return glasses.TryGetValue(id, out var face) ? new(id, face.Name.ToString(), (uint)Math.Max(0, face.Icon), 0, true, "") : null;
        return metadata.GetValueOrDefault(id);
    }

    private string Diagnostics()
    {
        var resolved = target.TryResolve(out var identity, out var bounds, out var openType);
        return JsonSerializer.Serialize(new
        {
            version = typeof(Plugin).Assembly.GetName().Version?.ToString(), editorAvailable, resolved,
            openType = openType.ToString(), identity = new { editor = (long)identity.Editor, state = (long)identity.State,
                model = (long)identity.Model, identity.Generation, identity.Character }, bounds,
            canBlockSave = target.CanBlockSave, blockedSaves = target.BlockedSaves, blockedSaveEvents = target.BlockedSaveEvents, nativeError = target.LastError,
            active = IsActive, status = Status, look = Current, sourceSummary, sources = ownedView.Sources,
            ownedCount = ownedView.Items.Count, browser = browser.ToDictionary(p => p.Key.ToString(), p => p.Value),
            editorState = resolved ? target.CaptureEditorState(identity) : null,
            syncPlate, plateMirrorClosePending, plateMirrorActive = target.IsPlateMirrorActive, plateMirrorError = target.PlateMirrorError,
            plateMirrorDisposalRestored = target.PlateMirrorDisposalRestored,
            canUndo, canRedo, historyStatus, historyCount = history.Count, historyBytes = history.Bytes,
            historyRows, appearanceGroups,
            historyPhotos = photos.Diagnostics(),
            captureIsolation = captureIsolation.Mode,
            runtimeTrace = new { active = runtimeTrace.ActiveStages, slowStages = runtimeTrace.SlowStages },
            liveCapture = liveImageService?.Diagnostics,
            historyPhotosView = window.HistoryPhotosView, historyWindowOpen = window.HistoryWindowOpen,
            lightingFit = LightingFit,
            lightingFitDetail = LightingFitDiagnostics,
            glamourPlates = glamourPlateView is { } plate ? new { status = plate.Status.ToString(), readAt = plate.ReadAt, reason = plate.Reason, count = plate.Plates.Count,
                plates = plate.Plates.Select(value => new { index = value.Index + 1, pieces = value.Pieces }),
                writes = glamourPlateCache.CompletedWrites, failedWrites = glamourPlateCache.FailedWrites, savedRevision = glamourPlateCache.SavedRevision,
                readOnly = glamourPlateCache.IsReadOnly, lastWriteSucceeded = glamourPlateCache.LastWriteSucceeded } : null,
            ownedCache = new { contentRevision = owned.ContentRevision, savedRevision = cacheWriter.SavedRevision,
                outstandingRevision = cacheWriter.OutstandingRevision, writes = cacheWriter.CompletedWrites, failedWrites = cacheWriter.FailedWrites },
            favorites = new { count = favoriteEntries.Count, status = favoritesStatus, readOnly = favoritesStore.IsReadOnly,
                entries = favoriteEntries.Select(value => new { value.ID, value.Name, value.Tags, value.SourceName }) },
        });
    }

    private T OnFramework<T>(Func<T> action) => Plugin.Framework.IsInFrameworkUpdateThread
        ? action() : Plugin.Framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();

    private static bool IsRetainerInventoryOpen(ulong retainerID)
    {
        // The concrete retainer identity is checked again by the inventory reader.
        var normal = Plugin.GameGUI.GetAddonByName<AtkUnitBase>("InventoryRetainer");
        var large = Plugin.GameGUI.GetAddonByName<AtkUnitBase>("InventoryRetainerLarge");
        return normal != null && normal->IsVisible && normal->IsFullyLoaded()
            || large != null && large->IsVisible && large->IsFullyLoaded();
    }

    private OwnedCharacterKey? CurrentCharacter()
    {
        var player = Plugin.Objects.LocalPlayer;
        if (!Plugin.ClientState.IsLoggedIn || player is null || player.Address == 0) return null;
        var native = (NativeCharacter*)player.Address;
        var state = PlayerState.Instance();
        if (state == null || !state->IsLoaded || native->ContentId == 0 || native->AccountId == 0 || native->ContentId != state->ContentId) return null;
        return new(native->AccountId, native->ContentId);
    }

    private void Update(Dalamud.Plugin.Services.IFramework framework)
    {
        if (disposed) return;
        try
        {
            using (runtimeTrace.Measure("update.retainer-loads")) inventory.ObserveRetainerLoads();
            using (runtimeTrace.Measure("update.validate")) session.Validate();
            using (runtimeTrace.Measure("update.lighting-fit")) PollLightingFit();
            using (runtimeTrace.Measure("update.editing")) PollEditing();
            using (runtimeTrace.Measure("update.capture-state")) ObserveLiveCaptureState();
            using (runtimeTrace.Measure("update.library")) PollLibrary();
            using (runtimeTrace.Measure("update.photos")) PollPhotos();
            using (runtimeTrace.Measure("update.live-image")) liveImageService?.Update(captureIsolation.LiveEnabled && session.IsActive);
            using (runtimeTrace.Measure("update.resolve"))
            {
                editorAvailable = target.TryResolve(out _, out var bounds, out _);
                if (editorAvailable)
                {
                    buttonPosition = new(bounds.X + bounds.Width + 8, bounds.Y + 30);
                    window.SetOwnerBounds(bounds);
                }
            }
            if (!session.IsActive && window.IsOpen)
            {
                window.CloseAuxiliaryWindows();
                window.IsOpen = false;
            }
            if (PortraitInventoryRefreshPolicy.ShouldRefresh(session.IsActive, DateTime.UtcNow, nextInventoryRead))
            {
                nextInventoryRead = DateTime.UtcNow.AddSeconds(2);
                using (runtimeTrace.Measure("update.inventory")) RefreshInventory();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[肖像同步预览] 更新失败");
            status = "肖像或物品信息尚未加载完成";
        }
    }

    private void RefreshInventory()
    {
        var capture = inventory.Capture();
        if (capture.Character is not { } character) { owned.DeactivateCharacter(); ownedView = new(null, [], []); sourceSummary = []; retainerRosterView = null; browser = []; return; }
        owned.ActivateCharacter(character);
        foreach (var source in capture.Sources)
        {
            owned.RegisterSource(source.Source);
            if (source.Records is not null) owned.ApplyRead(character, source.Source, DateTimeOffset.UtcNow, source.Records);
            else owned.MarkUnavailable(character, source.Source, source.Reason);
        }
        // Each complete observation batch queues changed data immediately; success is acknowledged
        // by the worker, so an unchanged subsequent batch also retries a previous disk failure.
        QueueOwnedCacheIfNeeded();
        ownedView = owned.BuildView(DateTimeOffset.UtcNow);
        retainerRosterView = capture.RetainerRoster;
        var known = ownedView.Items.Select(item => item.ItemID).Concat(Current?.Pieces.Take(12).Select(piece => piece.ItemID) ?? []).Distinct();
        metadata = known.Where(equipment.ContainsKey).ToDictionary(id => id, id => BrowserItem(equipment[id]));
        foreach (var retainer in capture.Retainers) retainerDisplayNames[retainer.RetainerID] = retainer.Name;
        sourceSummary = PortraitEquipmentSourceSummary.Build(ownedView, retainerRosterView).ToArray();
        var next = new Dictionary<PortraitSlot, PortraitBrowserItem[]>();
        foreach (var slot in Enum.GetValues<PortraitSlot>())
        {
            if (slot == PortraitSlot.Facewear)
            {
                var state = PlayerState.Instance();
                next[slot] = state == null ? [] : glasses.Values.Where(row => state->IsGlassesUnlocked((ushort)row.RowId))
                    .Select(row => new PortraitBrowserItem(row.RowId, row.Name.ToString(), (uint)Math.Max(0, row.Icon), 0, true, "")).OrderBy(row => row.Name).ToArray();
            }
            else
                next[slot] = ownedView.Items.Where(entry => equipment.TryGetValue(entry.ItemID, out var item) && Matches(item.EquipSlotCategory.Value, slot))
                    .Select(entry => metadata[entry.ItemID] with { Sources = entry.Variants.Select(variant => variant.Source.Kind).Distinct().ToArray() })
                    .OrderBy(item => item.Name).ToArray();
        }
        browser = next;
    }

    private PortraitBrowserItem BrowserItem(Item row)
    {
        var state = PlayerState.Instance();
        var restriction = row.EquipRestriction.Value;
        var reason = row.ModelMain == 0 ? "该装备没有可用外观模型" : "";
        if (state != null && row.EquipRestriction.RowId != 0)
        {
            var raceAllowed = state->Race switch
            {
                1 => restriction.Hyur, 2 => restriction.Elezen, 3 => restriction.Lalafell, 4 => restriction.Miqote,
                5 => restriction.Roegadyn, 6 => restriction.AuRa, 7 => restriction.Hrothgar, 8 => restriction.Viera, _ => false,
            };
            if (!raceAllowed || (state->Sex == 0 ? !restriction.Male : !restriction.Female)) reason = "该外观不支持当前角色的种族或性别";
        }
        return new(row.RowId, row.Name.ToString(), row.Icon, row.DyeCount, reason.Length == 0, reason,
            Convert.ToUInt32(row.LevelItem.RowId), Convert.ToUInt32(row.LevelEquip));
    }

    private static bool Matches(EquipSlotCategory row, PortraitSlot slot) => slot switch
    {
        PortraitSlot.MainHand => row.MainHand > 0, PortraitSlot.OffHand => row.OffHand > 0,
        PortraitSlot.Head => row.Head > 0, PortraitSlot.Body => row.Body > 0, PortraitSlot.Hands => row.Gloves > 0,
        PortraitSlot.Legs => row.Legs > 0, PortraitSlot.Feet => row.Feet > 0, PortraitSlot.Ears => row.Ears > 0,
        PortraitSlot.Neck => row.Neck > 0, PortraitSlot.Wrists => row.Wrists > 0,
        PortraitSlot.RightRing => row.FingerR > 0, PortraitSlot.LeftRing => row.FingerL > 0, _ => false,
    };

    public void OpenPreview() => Mutate(() =>
    {
        runtimeTrace.Arm();
        using var openPhase = runtimeTrace.Measure("open");
        using (runtimeTrace.Measure("open.cancel-lighting")) if (!CancelLightingFitForEdit()) return;
        if (!target.CanBlockSave) { Error(target.LastError ?? "暂时无法阻止肖像保存，为避免保存临时外观，本次未开启预览。请稍后重试。"); return; }
        PortraitIdentity identity;
        using (runtimeTrace.Measure("open.resolve"))
            if (!target.TryResolve(out identity, out _, out _)) { Error("请先打开肖像编辑器，等待模型加载完成。"); return; }
        using (runtimeTrace.Measure("open.session"))
            if (!session.Open(identity)) { Error("请先打开肖像编辑器，等待模型加载完成。"); return; }
        using (runtimeTrace.Measure("open.inventory")) RefreshInventory();
        status = "肖像预览已开启。";
        window.IsOpen = true;
        window.BringToFront();
    });

    public void Equip(PortraitSlot slot, uint id) => Mutate(() =>
    {
        if (!SynchronizeCurrentLook()) return;
        var item = GetItems(slot).FirstOrDefault(row => row.ID == id);
        if (item is not { Available: true }) { Error(item?.Reason ?? "尚未在已读取的物品中找到此装备。"); return; }
        var variant = ownedView.Items.FirstOrDefault(row => row.ItemID == id)?.Variants.FirstOrDefault();
        HandleHistoryResult(historyBatch.Flush());
        if (!session.Replace(slot, new(id, item.DyeChannels > 0 ? variant?.Stain1 ?? 0 : (byte)0, item.DyeChannels > 1 ? variant?.Stain2 ?? 0 : (byte)0)))
            Error("暂时无法预览此装备，请等待肖像加载后重试。");
        else RecordDirectChange("更换装备：" + item.Name);
    });

    public void Clear(PortraitSlot slot) => Mutate(() =>
    {
        if (!SynchronizeCurrentLook()) return;
        HandleHistoryResult(historyBatch.Flush());
        if (!session.Replace(slot, default)) Error("暂时无法清空此部位，请稍后重试。");
        else RecordDirectChange("清空装备部位");
    });

    public void Dye(PortraitSlot slot, int channel, byte stain) => Mutate(() =>
    {
        if (!SynchronizeCurrentLook()) return;
        if (Current is not { } look || (uint)slot >= 13 || channel is < 0 or > 1) return;
        var piece = look.Pieces[(int)slot];
        var item = GetItem(slot, piece.ItemID);
        if (item is null || channel >= item.DyeChannels || (stain != 0 && !dyes.Any(row => row.ID == stain))) return;
        var next = channel == 0 ? piece with { Stain0 = stain } : piece with { Stain1 = stain };
        HandleHistoryResult(historyBatch.Flush());
        if (!session.Replace(slot, next)) Error("暂时无法预览此染色，请稍后重试。");
        else RecordDirectChange($"染色 {channel + 1}：" + (dyes.FirstOrDefault(dye => dye.ID == stain)?.Name ?? "无"));
    });

    public void SetVisibility(bool head, bool weapon, bool visor) => Mutate(() =>
    {
        if (!SynchronizeCurrentLook()) return;
        HandleHistoryResult(historyBatch.Flush());
        if (!session.SetVisibility(head, weapon, visor)) Error("暂时无法更改装备显示，请稍后重试。");
        else RecordDirectChange("装备显示设置");
    });

    public void ClosePreview() => Mutate(() =>
    {
        if (!CancelLightingFitForEdit()) { Error("正在结束灯光调整，请稍候再关闭预览。"); return; }
        FlushEditing();
        if (!session.Close())
        {
            window.IsOpen = true;
            Error("临时外观尚未还原，请等待肖像加载后再次关闭。");
            return;
        }
        window.IsOpen = false;
        liveImageService?.Close();
        requestedCaptureViewport = null;
        SynchronizeAfterClose();
        QueueOwnedCacheIfNeeded();
        status = "临时外观已还原，现在可以保存肖像。";
    });

    private void Error(string message)
    {
        status = message;
        // Disposal can finish after a native callback unwinds. Keep that eventual failure in
        // the log even when the feature has already detached its update and draw handlers.
        if (disposed) Plugin.Log.Error("[肖像同步预览] 卸载收尾未完成：{Error}", message);
        var playerMessage = disposed && !target.PlateMirrorDisposalRestored
            ? "本地铭牌预览未能还原，请关闭后重新打开铭牌。" : message;
        Plugin.Chat.PrintError("[肖像同步预览] " + playerMessage);
    }

    private void Mutate(System.Action action)
    {
        if (disposed) return;
        if (Plugin.Framework.IsInFrameworkUpdateThread) action();
        else _ = Plugin.Framework.RunOnFrameworkThread(() => { if (!disposed) action(); });
    }

    private void Draw()
    {
        if (disposed) return;
        using var theme = FireGazeTheme.Push();
        using var portraitSurface = FireGazeTheme.PushPortrait();
        pointerHeld = ImGui.IsMouseDown(ImGuiMouseButton.Left) || ImGui.IsMouseDown(ImGuiMouseButton.Right)
            || ImGui.IsMouseDown(ImGuiMouseButton.Middle);
        if (editorAvailable)
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.Pos + buttonPosition, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(1f);
            if (ImGui.Begin("###FireGazePortraitAnchor", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
            {
                if (ImGui.Button(IsActive ? "关闭预览" : "FireGaze预览"))
                {
                    if (IsActive) ClosePreview(); else OpenPreview();
                }
            }
            ImGui.End();
        }
        using (runtimeTrace.Measure("draw.photos")) photos.Draw(pointerHeld);
        using (runtimeTrace.Measure("draw.live-image")) DrawLiveCapture();
        using (runtimeTrace.Measure("draw.lighting-fit")) DrawLightingFit();
        using (runtimeTrace.Measure("draw.windows")) windows.Draw();
    }

    public void OnCommand(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts is ["open"]) OpenPreview();
        else if (parts is ["close"]) ClosePreview();
        else if (parts is ["status"]) Plugin.Chat.Print("[肖像同步预览] " + Status);
        else if (parts is ["capture-probe", var captureMode]) Mutate(() =>
        {
            var busy = photos.IsBusy || liveImageService?.IsBusy == true || LightingFitMutationBlocked;
            if (!captureIsolation.TryConfigure(captureMode, session.IsActive, busy))
            { Error("请先关闭预览并等待图像任务结束；诊断通道仅支持 off、history 或 live。"); return; }
            Plugin.Log.Information("[PortraitTrace] capture isolation changed to {Mode}", captureIsolation.Mode);
        });
        else if (parts is ["undo"]) Undo();
        else if (parts is ["redo"]) Redo();
        else if (parts is ["light-fit", "cancel"]) CancelLightingFit();
        else if (parts.Length > 0 && parts[0] == "light-fit") LightingFitCommand(parts);
        else if (parts is ["playback", "pause"]) SetPortraitPlayback(true);
        else if (parts is ["playback", "play"]) SetPortraitPlayback(false);
        else if (parts is ["history", var view] && view is "list" or "photos") Mutate(() =>
        {
            if (!session.IsActive) { Error("请先打开 FireGaze预览。"); return; }
            window.OpenHistory(view == "photos");
        });
        else if (parts is ["restore", var record] && long.TryParse(record, out var recordID) && recordID > 0) RestoreHistory(recordID);
        else if (parts is ["favorite", "save"]) SaveFavorite($"肖像 {DateTime.Now:yyyy-MM-dd HH:mm:ss}", []);
        else if (parts is ["favorite", "restore", var favorite] && Guid.TryParseExact(favorite, "D", out var favoriteID)) RestoreFavorite(favoriteID);
        else if (parts is ["favorite", "remove", var removeFavorite] && Guid.TryParseExact(removeFavorite, "D", out var removeFavoriteID)) RemoveFavorite(removeFavoriteID);
        else if (parts is ["plate", var plateNumber] && int.TryParse(plateNumber, out var plateIndex) && plateIndex is >= 1 and <= 20) ApplyGlamourPlate(plateIndex - 1);
        else if (parts is ["favorites"]) Mutate(() => window.OpenFavorites());
        else if (parts is ["appearance", var group, var option] && int.TryParse(option, out var appearanceValue)) ChangeAppearance(group, appearanceValue);
        else if (parts is ["sync", "on"]) SetSyncPlate(true);
        else if (parts is ["sync", "off"]) SetSyncPlate(false);
        else if (parts is ["equip", var slot, var value] && Enum.TryParse<PortraitSlot>(slot, true, out var parsed) && Enum.IsDefined(parsed) && uint.TryParse(value, out var id)) Equip(parsed, id);
        else if (parts is ["clear", var name] && Enum.TryParse<PortraitSlot>(name, true, out var clear) && Enum.IsDefined(clear)) Clear(clear);
        else if (parts is ["editor", var kind]) Mutate(() => OpenEditor(kind));
        else if (parts is ["dye", var dyeSlot, var dyeChannel, var dyeValue] && Enum.TryParse<PortraitSlot>(dyeSlot, true, out var dyeParsed)
            && Enum.IsDefined(dyeParsed) && int.TryParse(dyeChannel, out var channel) && byte.TryParse(dyeValue, out var stain)) Dye(dyeParsed, channel, stain);
        else if (parts is ["visibility", var head, var weapon, var visor]
            && bool.TryParse(head, out var h) && bool.TryParse(weapon, out var w) && bool.TryParse(visor, out var v)) SetVisibility(h, w, v);
        else if (parts is ["save-probe"]) Mutate(() =>
        {
            // Exercise the actual native guard only while it is known to reject this owner.
            if (!target.CanBlockSave || !target.TryResolve(out var owner, out _, out _) || !session.BlockSave(owner))
            { Error("必须先打开 FireGaze预览，才能验证保存保护。"); return; }
            ((AgentBannerEditorState*)owner.State)->Save();
        });
        else if (parts is ["save-event-probe"]) Mutate(() =>
        {
            if (!target.CanBlockSave || !target.TryResolve(out var owner, out _, out _) || !session.BlockSave(owner))
            { Error("必须先打开 FireGaze预览，才能验证保存保护。"); return; }
            AtkValue* values = stackalloc AtkValue[2];
            values[0] = new() { Type = AtkValueType.Int, Int = 0 };
            values[1] = new() { Type = AtkValueType.Int, Int = 9 };
            AtkValue result = default;
            ((AgentBannerEditor*)owner.Editor)->ReceiveEvent(&result, values, 2, 0);
        });
        else Error("用法：/firegaze portrait open|close|status；light-space window");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte OpenPlate(AgentBannerEditor* editor);

    private void OpenEditor(string kind)
    {
        var editor = AgentBannerEditor.Instance();
        if (editor == null) return;
        if (kind == "close")
        {
            // Native cancel destroys this editor's temporary model without saving it.
            if (!session.IsActive || target.TryResolve(out var owner, out _, out _) && session.BlockSave(owner))
                if (editor->IsAgentActive()) editor->Hide();
            return;
        }
        if (session.IsActive) { Error("请先关闭预览，再切换肖像编辑器。"); return; }
        if (editor->IsAgentActive()) { Error("请先关闭已有肖像编辑器。"); return; }
        if (kind == "gearset")
        {
            var gearsets = RaptureGearsetModule.Instance();
            if (gearsets == null) return;
            for (var enabled = 0; enabled < 100; enabled++)
            {
                if (gearsets->EnabledGearsetIndex2EntryIndex[enabled] != gearsets->CurrentGearsetIndex) continue;
                editor->OpenForGearset(enabled);
                return;
            }
            Error("当前装备套装尚未就绪。");
        }
        else if (kind == "plate")
        {
            const string pattern = "40 53 48 83 EC 30 48 83 3D ?? ?? ?? ?? 00 48 8B D9 0F 84 ?? ?? ?? ?? 48 8B 41 28 48 85 C0 74 17 83 B8 D8 03 00 00 02 75 0E 48 8B 01 FF 50 28 32 C0";
            try { Marshal.GetDelegateForFunctionPointer<OpenPlate>(Plugin.SigScanner.ScanText(pattern))(editor); }
            catch (Exception ex) { Error("当前游戏版本无法打开铭牌肖像编辑器：" + ex.Message); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        void Release()
        {
            if (!CancelLightingFitForEdit()) _ = target.AbortCapturedEditor();
            DisposeLightingFit();
            photos.Dispose();
            liveImageService?.Dispose();
            liveCaptureAPI?.Dispose();
            FlushEditing();
            if (!session.Close() && !target.AbortCapturedEditor())
                Plugin.Log.Error("[肖像同步预览] 外观还原及编辑器关闭均失败：{Error}", target.LastError ?? "未知原因");
            QueueOwnedCacheIfNeeded();
            DisposeLibrary();
            target.Dispose();
        }
        disposed = true;
        Plugin.Framework.Update -= Update;
        pluginInterface.UiBuilder.Draw -= Draw;
        if (Plugin.Framework.IsInFrameworkUpdateThread) Release();
        else Plugin.Framework.RunOnFrameworkThread(Release).GetAwaiter().GetResult();
        cacheWriter.Dispose();
        history.Dispose();
        diagnostics.UnregisterFunc();
        windows.RemoveAllWindows();
        window.Dispose();
    }
}
