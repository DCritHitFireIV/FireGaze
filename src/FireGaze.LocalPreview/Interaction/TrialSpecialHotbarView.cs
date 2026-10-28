using System;
using System.Numerics;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FireGaze.LocalPreview.Runtime;

namespace FireGaze.LocalPreview.Interaction;

/// <summary>
/// Uses the game's native special bar where its reviewed bridge is available. The local
/// window and keybinding reader are a fallback. Mutations run on Framework; Draw only reads.
/// </summary>
public sealed unsafe class TrialSpecialHotbarView : IDisposable, INativePhysicalCloseState
{
    private readonly IFramework framework;
    private readonly TrialSpecialNativeBarLease nativeBarLease = new();
    private readonly TrialNativeSpecialHotbar? native;
    private readonly ITextureProvider textures;
    private readonly IKeyState keys;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Func<int, long, bool> activate;
    private TrialSpecialBar? publication;
    private string[] bindingLabels = new string[TrialSpecialHotbarModel.ExitSlot + 1];
    private bool pluginTextInput;
    private bool disposed;
    private NativePhysicalCloseState? physicalClose;
    private NativePhysicalCloseState PhysicalClose => physicalClose ??= new(this);
    public bool PhysicalCloseComplete => disposed && PhysicalClose.Complete && (native?.PhysicalCloseComplete ?? true);
    public string? PhysicalCloseFailure => PhysicalClose.Failure ?? native?.PhysicalCloseFailure;
    private bool keyboardUnavailable;
    private sealed record FallbackAnchor(Vector2 Position);
    private FallbackAnchor? fallbackMarketAnchor;

    public TrialSpecialHotbarView(ITextureProvider textures, IKeyState keys, IGameGui gameGui,
        IPluginLog log, Func<int, long, bool> activate, IFramework framework,
        IAddonLifecycle? lifecycle = null, IGameInteropProvider? interop = null)
    {
        this.textures = textures; this.keys = keys; this.gameGui = gameGui; this.log = log; this.activate = activate; this.framework = framework;
        if(lifecycle is not null && interop is not null) native=new(framework,gameGui,lifecycle,interop,log,activate);
    }

    public bool KeyboardAvailable => !disposed && !keyboardUnavailable;
    public bool NativeAvailable => native?.Available==true;
    public bool NativeActive => native?.Active==true;
    public string NativeStatus => native?.Status ?? "原生特殊栏服务不可用";
    public long NativeInputsConsumed => native?.InputsConsumed ?? 0;
    public bool ExecuteNativeSlot(int slot)=>!disposed && native?.ExecuteNativeSlot(slot)==true;
    public void Publish(TrialSpecialBar? bar)
    {
        Volatile.Write(ref publication, disposed ? null : bar);
        native?.Publish(disposed?null:bar);
        UpdateNativeBarVisibility(!disposed && bar is not null && !NativeAvailable);
        UpdateFallbackMarketAnchor(disposed ? null : bar);
    }

    private void UpdateFallbackMarketAnchor(TrialSpecialBar? bar)
    {
        FallbackAnchor? anchor = null;
        // Native pointers are resolved only on Framework. Draw receives detached
        // coordinates and never changes a game window or its saved placement.
        if (framework.IsInFrameworkUpdateThread && !NativeActive && bar?.HasSpecialCommands == true
            && IsMarketBoardOpen())
        {
            var primary = gameGui.GetAddonByName<AtkUnitBase>("_ActionBar", 1);
            if (primary != null && primary->Id != 0 && primary->RootNode != null)
            {
                var position = new Vector2(primary->RootNode->X, primary->RootNode->Y);
                if (float.IsFinite(position.X) && float.IsFinite(position.Y)) anchor = new(position);
            }
        }
        Volatile.Write(ref fallbackMarketAnchor, anchor);
    }

    private bool CanPollKeyboard(bool allowed, bool gameTextInput, bool pluginTyping)
    {
        var bar = Volatile.Read(ref publication);
        var visible = bar is not null && TrialSpecialHotbarVisibilityPolicy.ShouldShow(
            true, gameGui.GameUiHidden, IsMarketBoardOpen(), bar.HasSpecialCommands);
        return TrialSpecialKeyboardRouting.CanHandle(allowed && visible, false, gameTextInput, pluginTyping);
    }

    private void UpdateNativeBarVisibility(bool trialActive)
    {
        // GetAddonByName returns the current allocation. Only this fresh pointer is read or
        // written; the lease retains identity values but never dereferences an old pointer.
        var addon = gameGui.GetAddonByName<AtkUnitBase>("_ActionBarEx", 1);
        var source = Control.GetLocalPlayer();
        var change = nativeBarLease.Observe(trialActive, (nint)addon, addon == null ? (ushort)0 : addon->Id,
            addon != null && addon->IsVisible, (nint)source, source == null ? 0 : *(uint*)((byte*)source + 0x74),
            source == null ? (ushort)0 : source->Mount.MountId, source == null ? (ushort)0 : source->OrnamentData.OrnamentId);
        // The native bridge owns its trial bar. This fallback lease only hides and
        // restores the genuine bar it replaced; None must never overwrite the bridge.
        if (addon != null && change != TrialSpecialBarVisibilityChange.None)
            addon->IsVisible = change == TrialSpecialBarVisibilityChange.Restore
                && !gameGui.GameUiHidden;
    }

    private unsafe bool IsMarketBoardOpen()
    {
        try
        {
            foreach (var name in new[] { "ItemSearch", "ItemSearchResult" })
            {
                var addon = gameGui.GetAddonByName<AtkUnitBase>(name, 1);
                if (addon != null && addon->IsVisible) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Uses the player's existing twelve HOTBAR_EX bindings, never assumed digit keys.
    /// This consumes only the pressed bound keyboard key in the game buffer; it generates no input.
    /// Native-bar clicks are routed separately by TrialSpecialHotbarModel.TryMapNativeAction.</summary>
    public void PollKeyboard(bool allowed)
    {
        if (disposed || NativeActive || keyboardUnavailable || Volatile.Read(ref publication) is not { } bar) return;
        try
        {
            var ui = UIModule.Instance();
            var input = ui == null ? null : ui->GetUIInputData();
            var atk = ui == null ? null : ui->GetRaptureAtkModule();
            if (input == null || atk == null || !CanPollKeyboard(allowed,
                    atk->IsTextInputActive(), Volatile.Read(ref pluginTextInput))) return;
            var labels = new string[TrialSpecialHotbarModel.ExitSlot + 1];
            for (var slot = 0; slot <= TrialSpecialHotbarModel.ExitSlot; slot++)
            {
                var inputId = (InputId)((int)InputId.HOTBAR_EX_1 + slot);
                var bind = input->GetKeybind(inputId);
                if (bind == null) continue;
                foreach (var setting in bind->KeySettings)
                {
                    var key = (int)setting.Key;
                    if (key == 0 || !keys.IsVirtualKeyValid(key)) continue;
                    if (string.IsNullOrEmpty(labels[slot])) labels[slot] = BindingLabel(setting);
                    // Match the actual binding and native press edge. Reading a held key must
                    // not repeat actions and gamepad input must not clear unrelated keys.
                    if (setting.KeyModifier != input->CurrentKeyModifier || !keys[key]
                        || !input->IsInputIdPressed(inputId)) continue;
                    keys[key] = false;
                    activate(slot, bar.Revision);
                    break;
                }
            }
            Volatile.Write(ref bindingLabels, labels);
        }
        catch (Exception ex)
        {
            keyboardUnavailable = true;
            log.Warning(ex, "[LocalPreview] Special hotbar keyboard bridge is unavailable; click actions remain available.");
        }
    }

    private static string BindingLabel(KeySetting setting)
    {
        var key = setting.Key.ToString().Replace("KEY_", "", StringComparison.Ordinal);
        return setting.KeyModifier == 0 ? key : setting.KeyModifier + "+" + key;
    }

    public void Draw()
    {
        if (disposed || NativeActive || Volatile.Read(ref publication) is not { } bar
            || !TrialSpecialHotbarVisibilityPolicy.ShouldShow(true, gameGui.GameUiHidden, IsMarketBoardOpen(), bar.HasSpecialCommands)) return;
        Volatile.Write(ref pluginTextInput, ImGui.GetIO().WantTextInput);
        var viewport = ImGui.GetMainViewport();
        if (Volatile.Read(ref fallbackMarketAnchor) is { } anchor)
            ImGui.SetNextWindowPos(viewport.Pos + anchor.Position, ImGuiCond.Always);
        else
            ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(viewport.WorkSize.X * .5f, viewport.WorkSize.Y * .78f),
                ImGuiCond.FirstUseEver, new Vector2(.5f, .5f));
        ImGui.SetNextWindowBgAlpha(.94f);
        if (!ImGui.Begin("试用动作###FireGaze.LocalPreview.SpecialHotbar",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing))
        { ImGui.End(); return; }
        try
        {
            ImGui.TextUnformatted((bar.IsMount ? "坐骑：" : "时尚配饰：") + bar.Name);
            var labels = Volatile.Read(ref bindingLabels);
            for (var slot = 0; slot <= TrialSpecialHotbarModel.ExitSlot; slot++)
            {
                if (slot > 0) ImGui.SameLine();
                ImGui.BeginGroup();
                ImGui.PushID(slot);
                var action = slot < bar.Actions.Count ? bar.Actions[slot] : null;
                bool clicked;
                if (action is not null && textures.GetFromGameIcon(new GameIconLookup(action.Icon)).GetWrapOrDefault() is { } icon)
                    clicked = ImGui.ImageButton(icon.Handle, new Vector2(44, 44));
                else
                {
                    if (action is null && slot != TrialSpecialHotbarModel.ExitSlot) ImGui.BeginDisabled();
                    clicked = ImGui.Button(slot == TrialSpecialHotbarModel.ExitSlot ? "结束试用##trial-special-exit" : "　##trial-special-" + slot,
                        new Vector2(48, 48));
                    if (action is null && slot != TrialSpecialHotbarModel.ExitSlot) ImGui.EndDisabled();
                }
                if (ImGui.IsItemHovered())
                {
                    if (slot == TrialSpecialHotbarModel.ExitSlot)
                        ImGui.SetTooltip("结束坐骑与时尚配饰试用；如有正在使用的坐骑或配饰，也会下坐骑或收起配饰。");
                    else if (action is not null)
                        ImGui.SetTooltip(action.Name);
                }
                if (clicked) activate(slot, bar.Revision);
                ImGui.TextUnformatted(slot < labels.Length && !string.IsNullOrEmpty(labels[slot]) ? labels[slot] : "—");
                ImGui.PopID();
                ImGui.EndGroup();
            }
        }
        finally { ImGui.End(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Volatile.Write(ref publication, null);
        Volatile.Write(ref fallbackMarketAnchor, null);
        PhysicalClose.Try(() => native?.Dispose(), "Native special bar close failed");
        PhysicalClose.Try(() => {
            if (framework.IsInFrameworkUpdateThread) UpdateNativeBarVisibility(false);
            else framework.RunOnFrameworkThread(() => UpdateNativeBarVisibility(false)).GetAwaiter().GetResult();
        }, "Special bar visibility restoration failed");
        PhysicalClose.Finish();
    }
}
