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
public sealed unsafe class TrialSpecialHotbarView : IDisposable
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
    private bool keyboardUnavailable;

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
        if (addon != null && change != TrialSpecialBarVisibilityChange.None)
            addon->IsVisible = change == TrialSpecialBarVisibilityChange.Restore;
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
            if (input == null || atk == null || !TrialSpecialKeyboardRouting.CanHandle(allowed,
                    gameGui.GameUiHidden, atk->IsTextInputActive(), Volatile.Read(ref pluginTextInput))) return;
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
        if (disposed || NativeActive || gameGui.GameUiHidden || Volatile.Read(ref publication) is not { } bar) return;
        Volatile.Write(ref pluginTextInput, ImGui.GetIO().WantTextInput);
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(viewport.WorkSize.X * .5f, viewport.WorkSize.Y * .78f),
            ImGuiCond.FirstUseEver, new Vector2(.5f, .5f));
        ImGui.SetNextWindowBgAlpha(.94f);
        if (!ImGui.Begin("试用特殊热键栏###FireGaze.LocalPreview.SpecialHotbar",
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
                    clicked = ImGui.Button(slot == TrialSpecialHotbarModel.ExitSlot ? "退出##trial-special-exit" : "　##trial-special-" + slot,
                        new Vector2(48, 48));
                    if (action is null && slot != TrialSpecialHotbarModel.ExitSlot) ImGui.EndDisabled();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(slot == TrialSpecialHotbarModel.ExitSlot ? "结束全部试用，并解除实际使用的坐骑/时尚配饰"
                        : action is null ? "空槽位" : action.Name + "（仅本地试用）");
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
        native?.Dispose();
        if (framework.IsInFrameworkUpdateThread) UpdateNativeBarVisibility(false);
        else framework.RunOnFrameworkThread(() => UpdateNativeBarVisibility(false)).GetAwaiter().GetResult();
    }
}
