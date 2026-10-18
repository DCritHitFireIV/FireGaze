using Dalamud.Hooking;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitEditorBounds(float X, float Y, float Width, float Height);

/// <summary>Native calls are isolated behind this boundary for real-structure lifecycle tests.</summary>
internal unsafe interface IPortraitNativeApi
{
    bool IsFrameworkThread { get; }
    AgentBannerEditor* GetEditor();
    bool IsEditorActive(AgentBannerEditor* editor);
    bool TryGetBounds(AgentBannerEditor* editor, out PortraitEditorBounds bounds);
    Character* GetLocalPlayer();
    Character* GetCharacter(CharaViewPortrait* view);
    void SetItem(CharaViewPortrait* view, byte slot, PortraitPiece piece, bool companyCrest);
    void SetGlasses(Character* character, ushort glasses);
    void SetVisibility(CharaViewPortrait* view, bool headVisible, bool weaponVisible, bool visor);
    void HideEditor(AgentBannerEditor* editor);
    bool TryHideOriginalEditor(AgentBannerEditor* editor, AgentBannerEditorState* state);
}

/// <summary>
/// Owns wardrobe changes on the active BannerEditor CharaView only. It never equips an inventory
/// item, changes a gearset/banner record, or writes the world player. All entry points fail closed
/// outside the Framework thread or after an editor/model allocation has changed.
/// </summary>
internal sealed unsafe partial class NativePortraitTarget : IPortraitPreviewTarget, IDisposable
{
    private const int GenerationOffset = 0x74;
    private static readonly byte[] NativeSlots = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12];
    private readonly IPortraitNativeApi api;
    private readonly Func<PortraitIdentity, bool> blockSave;
    private readonly Action<string> error;
    private Hook<AgentBannerEditorState.Delegates.Save>? saveHook;
    private IAgentLifecycle? agentLifecycle;
    private bool saveEventRegistered;
    private NativeSnapshot? original;
    private PortraitLook? originalLook;
    private PortraitIdentity originalIdentity;
    private bool disposed;
    private PortraitPlateMirror? plateMirror;
    public string? LastError { get; private set; }
    public bool PlateMirrorDisposalRestored { get; private set; } = true;
    public bool CanBlockSave => saveHook is { IsEnabled: true } && saveEventRegistered;
    public uint BlockedSaves { get; private set; }
    public uint BlockedSaveEvents { get; private set; }

    public NativePortraitTarget(IFramework framework, IObjectTable objects, IGameGui gameGUI,
        IGameInteropProvider interop, Func<PortraitIdentity, bool> blockSave, Action<string> error,
        IAgentLifecycle agentLifecycle, IAddonLifecycle addonLifecycle)
        : this(new LiveApi(framework, objects, gameGUI, agentLifecycle), blockSave, error)
    {
        plateMirror = new PortraitPlateMirror(framework, gameGUI, objects, addonLifecycle, agentLifecycle, IsCurrent);
        var address = (nint)AgentBannerEditorState.MemberFunctionPointers.Save;
        if (address == 0) { LastError = "当前游戏版本无法阻止肖像保存，暂不能开启临时预览。"; return; }
        try
        {
            saveHook = interop.HookFromAddress<AgentBannerEditorState.Delegates.Save>(address, SaveDetour);
            saveHook.Enable();
            RegisterSaveEvents(agentLifecycle);
        }
        catch (Exception ex)
        {
            LastError = "无法阻止肖像保存，暂不能开启临时预览：" + ex.Message;
            saveHook?.Dispose(); saveHook = null;
        }
    }

    internal NativePortraitTarget(IPortraitNativeApi api, Func<PortraitIdentity, bool> blockSave, Action<string> error)
    {
        this.api = api;
        this.blockSave = blockSave;
        this.error = error;
    }

    public bool TryResolve(out PortraitIdentity identity, out PortraitEditorBounds bounds,
        out AgentBannerEditorState.EditorOpenType openType)
    {
        return Resolve(out identity, out bounds, out openType, requireReady: true);
    }

    private bool Resolve(out PortraitIdentity identity, out PortraitEditorBounds bounds,
        out AgentBannerEditorState.EditorOpenType openType, bool requireReady)
    {
        identity = default; bounds = default; openType = default;
        if (disposed || !api.IsFrameworkThread) return false;
        var editor = api.GetEditor();
        if (editor == null) return false;
        // UI visibility is readiness, not ownership: a temporarily hidden addon can retain the
        // same native editor allocation and still needs its preview restoration/save guard.
        if (requireReady && (!api.IsEditorActive(editor) || !api.TryGetBounds(editor, out bounds))) return false;
        var state = editor->EditorState;
        if (state == null || state->AgentBannerEditor != editor || state->CharaView == null) return false;
        var view = state->CharaView;
        if ((view->Agent != null && view->Agent != (AgentInterface*)editor) || view->State != 6
            || requireReady && !view->CharaViewPortraitCharacterLoaded) return false;
        var type = state->OpenType;
        if (type is not (AgentBannerEditorState.EditorOpenType.Portrait
            or AgentBannerEditorState.EditorOpenType.Gearset or AgentBannerEditorState.EditorOpenType.AdventurerPlate)) return false;
        var local = api.GetLocalPlayer();
        if (local == null) return false;
        var character = api.GetCharacter(view);
        // Native lookups may invoke other plugins; re-check ownership after the final lookup before
        // touching the returned character. Never dereference a pointer supplied by an old identity.
        if (character == null || character == local || api.GetEditor() != editor || editor->EditorState != state
            || state->AgentBannerEditor != editor || state->CharaView != view
            || (view->Agent != null && view->Agent != (AgentInterface*)editor) || character->DrawData.OwnerObject != character) return false;
        var generation = Generation(character);
        if (generation == 0) return false;
        identity = new((nint)editor, (nint)state, (nint)view, generation, (ulong)character);
        openType = type;
        return true;
    }

    public bool IsCurrent(PortraitIdentity identity) => identity.IsValid
        && Resolve(out var current, out _, out _, requireReady: false) && current == identity;

    public PortraitLook? Capture(PortraitIdentity identity)
    {
        if (!TryResolve(out var current, out _, out _) || current != identity) return null;
        var view = (CharaViewPortrait*)identity.Model;
        var look = ReadLook(view);
        var snapshot = new NativeSnapshot(view);
        if (!IsCurrent(identity)) return null;
        original = snapshot; originalLook = look.Copy(); originalIdentity = identity;
        return look;
    }

    public bool Apply(PortraitIdentity identity, PortraitLook look)
    {
        if (look.Pieces.Length != 13 || look.Pieces[12].ItemID > ushort.MaxValue || !IsCurrent(identity)) return false;
        var view = (CharaViewPortrait*)identity.Model;
        var before = new NativeSnapshot(view);
        var current = ReadLook(view);
        if (!IsCurrent(identity)) return false;
        try
        {
            if (original is not null && originalIdentity == identity && originalLook is not null && Equal(look, originalLook))
            {
                Restore(view, original);
                if (!IsCurrent(identity)) return false;
                api.SetGlasses((Character*)identity.Character, (ushort)look.Pieces[12].ItemID);
                if (!IsCurrent(identity)) return false;
                api.SetVisibility(view, look.HeadVisible, look.WeaponVisible, look.Visor);
                return IsCurrent(identity);
            }
            for (var slot = 0; slot < NativeSlots.Length; slot++)
            {
                if (look.Pieces[slot] == current.Pieces[slot]) continue;
                if (!IsCurrent(identity)) return false;
                var nativeSlot = NativeSlots[slot];
                var piece = look.Pieces[slot];
                if (piece.ItemID == 0) RemoveItem(view, nativeSlot);
                else api.SetItem(view, nativeSlot, piece, GetCrest(view, nativeSlot));
                if (!IsCurrent(identity)) return false;
                view->PortraitCharacterData.ItemIds[nativeSlot] = piece.ItemID;
                view->PortraitCharacterData.ItemStain0Ids[nativeSlot] = piece.Stain0;
                view->PortraitCharacterData.ItemStain1Ids[nativeSlot] = piece.Stain1;
            }
            if (look.Pieces[12] != current.Pieces[12])
            {
                if (!IsCurrent(identity)) return false;
                var glasses = (ushort)look.Pieces[12].ItemID;
                api.SetGlasses((Character*)identity.Character, glasses);
                if (!IsCurrent(identity)) return false;
                view->ModelData.GlassesIds[0] = glasses;
                view->PortraitCharacterData.GlassesIds[0] = glasses;
            }
            if (look.HeadVisible != current.HeadVisible || look.WeaponVisible != current.WeaponVisible || look.Visor != current.Visor)
            {
                if (!IsCurrent(identity)) return false;
                api.SetVisibility(view, look.HeadVisible, look.WeaponVisible, look.Visor);
            }
            return IsCurrent(identity);
        }
        catch (Exception ex)
        {
            LastError = "肖像预览更新失败：" + ex.Message;
            // Roll back only while the original allocation is still current. A newer editor must
            // never receive the old snapshot, even when a native callback replaced it mid-update.
            try
            {
                if (IsCurrent(identity))
                {
                    Restore(view, before);
                    api.SetGlasses((Character*)identity.Character, before.CharacterData.GlassesIds[0]);
                    if (IsCurrent(identity)) api.SetVisibility(view, current.HeadVisible, current.WeaponVisible, current.Visor);
                }
            }
            catch (Exception rollbackError) { LastError += "；外观还原失败：" + rollbackError.Message; }
            error(LastError);
            return false;
        }
    }

    private static PortraitLook ReadLook(CharaViewPortrait* view)
    {
        var result = new PortraitPiece[13];
        for (var slot = 0; slot < NativeSlots.Length; slot++)
        {
            var native = NativeSlots[slot];
            result[slot] = new(view->PortraitCharacterData.ItemIds[native], view->PortraitCharacterData.ItemStain0Ids[native], view->PortraitCharacterData.ItemStain1Ids[native]);
            foreach (var item in view->Items)
            {
                if (item.SlotId != native) continue;
                result[slot] = item.GlamourItemId != 0
                    ? new(item.GlamourItemId, item.GlamourStain0Id, item.GlamourStain1Id)
                    : new(item.ItemId, item.Stain0Id, item.Stain1Id);
                break;
            }
        }
        result[12] = new(view->PortraitCharacterData.GlassesIds[0], 0, 0);
        return new(result, !view->PortraitCharacterData.HeadgearHidden, !view->PortraitCharacterData.WeaponHidden, view->PortraitCharacterData.VisorClosed);
    }

    private static bool GetCrest(CharaViewPortrait* view, byte nativeSlot)
    {
        foreach (var item in view->Items) if (item.SlotId == nativeSlot) return item.ApplyCompanyCrest;
        return false;
    }

    private static void RemoveItem(CharaViewPortrait* view, byte nativeSlot)
    {
        // Reviewed CN native remove-slot routine uses the same compact 14-record list and empty
        // SlotId=14 sentinel. SetItemSlotData(0) returns immediately, so it cannot perform clear.
        for (var index = 0; index < view->Items.Length; index++)
        {
            if (view->Items[index].SlotId != nativeSlot) continue;
            for (var next = index + 1; next < view->Items.Length; next++) view->Items[next - 1] = view->Items[next];
            view->Items[^1] = new() { SlotId = 14, ApplyCompanyCrest = true };
            view->CharacterDataCopied = true;
            return;
        }
    }

    private static bool Equal(PortraitLook a, PortraitLook b) => a.HeadVisible == b.HeadVisible
        && a.WeaponVisible == b.WeaponVisible && a.Visor == b.Visor && a.Pieces.SequenceEqual(b.Pieces);

    private static void Restore(CharaViewPortrait* view, NativeSnapshot snapshot)
    {
        snapshot.Items.AsSpan().CopyTo(view->Items);
        // Restore wardrobe fields, preserving edits to appearance, camera, lighting and pose.
        snapshot.ModelData.EquipmentModelIds.CopyTo(view->ModelData.EquipmentModelIds);
        snapshot.ModelData.WeaponModelIds.CopyTo(view->ModelData.WeaponModelIds);
        snapshot.ModelData.GlassesIds.CopyTo(view->ModelData.GlassesIds);
        snapshot.CharacterData.ItemIds.CopyTo(view->PortraitCharacterData.ItemIds);
        snapshot.CharacterData.ItemStain0Ids.CopyTo(view->PortraitCharacterData.ItemStain0Ids);
        snapshot.CharacterData.ItemStain1Ids.CopyTo(view->PortraitCharacterData.ItemStain1Ids);
        snapshot.CharacterData.GlassesIds.CopyTo(view->PortraitCharacterData.GlassesIds);
        view->FreeCompanyCrestBitfield = snapshot.Crests;
        view->CharacterDataCopied = true;
    }

    private void SaveDetour(AgentBannerEditorState* state)
    {
        if (!InterceptSave((nint)state)) saveHook!.Original(state);
    }

    internal void RegisterSaveEvents(IAgentLifecycle lifecycle)
    {
        if (disposed || saveEventRegistered) return;
        lifecycle.RegisterListener(AgentEvent.PreReceiveEvent, Dalamud.Game.Agent.AgentId.BannerEditor, OnAgentEvent);
        agentLifecycle = lifecycle;
        saveEventRegistered = true;
    }

    private void OnAgentEvent(AgentEvent eventType, AgentArgs args)
    {
        if (eventType != AgentEvent.PreReceiveEvent || args is not AgentReceiveEventArgs receive || receive.ReturnValue == 0) return;
        try
        {
            if (InterceptSaveEvent((nint)receive.GetAgentPointer<AgentBannerEditor>(), receive.AtkValues,
                receive.ValueCount, receive.EventKind)) receive.PreventOriginal();
        }
        catch (Exception ex) { LastError = "无法检查本次肖像保存：" + ex.Message; }
    }

    internal bool InterceptSaveEvent(nint requestedEditor, nint eventValues, uint count, ulong kind)
    {
        if (kind != 0 || count != 2 || eventValues == 0 || requestedEditor != originalIdentity.Editor
            || !IsCapturedStateOwner(originalIdentity.State)) return false;
        var values = (AtkValue*)eventValues;
        if (values[0].Type != AtkValueType.Int || values[0].Int != 0
            || values[1].Type != AtkValueType.Int || values[1].Int != 9) return false;
        if (!InterceptSave(originalIdentity.State)) return false;
        // Blocking the whole event also preserves HasChanged and suppresses the native success
        // message that would otherwise run after an intercepted state.Save returned.
        BlockedSaveEvents++;
        return true;
    }

    internal bool InterceptSave(nint requestedState)
    {
        bool blocked;
        try { blocked = ShouldBlockSave(requestedState); }
        catch (Exception ex)
        {
            LastError = "未能确认肖像保存是否已阻止：" + ex.Message;
            // Callback failures must not escape a native detour or save a temporary appearance.
            return IsCapturedStateOwner(requestedState);
        }
        if (!blocked) return false;
        BlockedSaves++;
        try { error("请先关闭 FireGaze修改，再重新保存肖像。"); }
        catch (Exception ex) { LastError = "肖像保存已阻止，但提示显示失败：" + ex.Message; }
        return true;
    }

    // Save has to remain blocked during draw loading; readiness belongs to Open/Capture, not to
    // the editor-owned save guard. This check never dereferences an old character/model pointer.
    internal bool ShouldBlockSave(nint requestedState)
    {
        return IsCapturedStateOwner(requestedState) && blockSave(originalIdentity);
    }

    private bool IsCapturedStateOwner(nint requestedState)
    {
        if (disposed || !api.IsFrameworkThread || !originalIdentity.IsValid || originalIdentity.State != requestedState) return false;
        var editor = api.GetEditor();
        return editor != null && (nint)editor == originalIdentity.Editor && (nint)editor->EditorState == requestedState
            && editor->EditorState->AgentBannerEditor == editor;
    }

    /// <summary>
    /// Emergency unload fallback when restoration failed. Hides only the still-owned native editor;
    /// native Hide destroys its temporary CharaView/state without calling the save entry point.
    /// The user's unsaved editor changes are discarded. Ordinary Close must restore in place.
    /// </summary>
    public bool AbortCapturedEditor()
    {
        if (disposed || !api.IsFrameworkThread) return false;
        if (!IsCapturedStateOwner(originalIdentity.State) || !IsCurrent(originalIdentity)) return true;
        var editor = api.GetEditor();
        try { api.HideEditor(editor); }
        catch (Exception ex) { LastError = "无法关闭临时肖像编辑器：" + ex.Message; }
        if (!IsCapturedStateOwner(originalIdentity.State)) return true;
        // A PreHide listener may prevent ordinary Hide. On unload, use only the original table
        // explicitly supplied by Dalamud, never a guessed function address or a replaced owner.
        if (!IsCurrent(originalIdentity)) return false;
        try
        {
            if (!api.TryHideOriginalEditor(editor, (AgentBannerEditorState*)originalIdentity.State)) return false;
        }
        catch (Exception ex) { LastError = "再次尝试关闭临时肖像编辑器失败：" + ex.Message; return false; }
        return !IsCapturedStateOwner(originalIdentity.State);
    }

    private static uint Generation(Character* character) => *(uint*)((byte*)character + GenerationOffset);
    public void Dispose()
    {
        if (disposed || !api.IsFrameworkThread) return;
        if (plateMirror is not null) plateMirror.DisposalFailed += OnPlateMirrorDisposalFailed;
        plateMirror?.Dispose();
        if (plateMirror is { DisposalRestored: false })
            OnPlateMirrorDisposalFailed(plateMirror.LastError ?? "本地铭牌预览未能还原，请关闭后重新打开铭牌。");
        plateMirror = null;
        disposed = true;
        if (saveEventRegistered) agentLifecycle?.UnregisterListener(AgentEvent.PreReceiveEvent,
            Dalamud.Game.Agent.AgentId.BannerEditor, OnAgentEvent);
        saveEventRegistered = false; agentLifecycle = null;
        saveHook?.Dispose(); saveHook = null; original = null; originalLook = null;
    }

    private void OnPlateMirrorDisposalFailed(string message)
    {
        if (!PlateMirrorDisposalRestored) return;
        PlateMirrorDisposalRestored = false;
        LastError = message;
        try { error(message); }
        catch { /* Native resources must still be released if the unload notice cannot display. */ }
    }

    private sealed class NativeSnapshot(CharaViewPortrait* view)
    {
        public readonly CharaViewItem[] Items = view->Items.ToArray();
        public CharaViewModelData ModelData = view->ModelData;
        public CharaViewCharacterData CharacterData = view->PortraitCharacterData;
        public readonly byte Crests = view->FreeCompanyCrestBitfield;
    }

    private sealed partial class LiveApi(IFramework framework, IObjectTable objects, IGameGui gameGUI,
        IAgentLifecycle agentLifecycle) : IPortraitNativeApi
    {
        public bool IsFrameworkThread => framework.IsInFrameworkUpdateThread;
        public AgentBannerEditor* GetEditor() => AgentBannerEditor.Instance();
        public bool IsEditorActive(AgentBannerEditor* editor) => editor->IsAgentActive();
        public bool TryGetBounds(AgentBannerEditor* editor, out PortraitEditorBounds bounds)
        {
            bounds = default;
            var addon = gameGUI.GetAddonByName<AtkUnitBase>("BannerEditor");
            if (addon == null || !addon->IsVisible || addon->Id != editor->AddonId) return false;
            bounds = new(addon->X, addon->Y, addon->GetScaledWidth(true), addon->GetScaledHeight(true));
            return true;
        }
        public Character* GetLocalPlayer() => (Character*)(objects.LocalPlayer?.Address ?? 0);
        public Character* GetCharacter(CharaViewPortrait* view) => view->GetCharacter();
        public void SetItem(CharaViewPortrait* view, byte slot, PortraitPiece piece, bool crest) => view->SetItemSlotData(slot, piece.ItemID, piece.Stain0, piece.Stain1, 0, crest);
        public void SetGlasses(Character* character, ushort glasses) => character->DrawData.SetGlasses(0, glasses);
        public void HideEditor(AgentBannerEditor* editor) => editor->Hide();
        public bool TryHideOriginalEditor(AgentBannerEditor* editor, AgentBannerEditorState* state)
        {
            var agent = (AgentInterface*)editor;
            var table = (AgentInterface.AgentInterfaceVirtualTable*)agentLifecycle.GetOriginalVirtualTable((nint)agent->VirtualTable);
            if (table == null || table->Hide == null || GetEditor() != editor || editor->EditorState != state
                || state->AgentBannerEditor != editor) return false;
            table->Hide(agent);
            return true;
        }
        public void SetVisibility(CharaViewPortrait* view, bool headVisible, bool weaponVisible, bool visor)
        {
            view->ToggleGearVisibility(!headVisible, !weaponVisible, visor, view->PortraitCharacterData.HideVieraEars);
            view->ModelData.HeadgearHidden = !headVisible;
            view->ModelData.WeaponHidden = !weaponVisible;
            view->ModelData.VisorClosed = visor;
        }
    }
}
