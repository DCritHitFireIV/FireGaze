using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.LocalPreview.Interaction;
using FireGaze.LocalPreview.Runtime;
using FireGaze.UI;
using Lumina.Excel.Sheets;
using MenuKind = FireGaze.LocalPreview.Interaction.PreviewKind;

namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    private volatile PreviewCatalog catalog = new([]);
    private LocalPreviewPreferences? preferences;
    private LocalPreviewPreferences Preferences => preferences ??= new(config);
    private volatile bool enableConfirmationVisible;

    private PreviewMenuOptions MenuOptions() => new(config.LocalPreviewItemMenusEnabled,
        config.LocalPreviewMountEnabled, config.LocalPreviewMinionEnabled,
        config.LocalPreviewAccessoryEnabled, config.LocalPreviewEmoteEnabled);

    private void CreateInteraction()
    {
        interaction = new PreviewInteractionService(ContextMenu, Chat, GameGUI, Objects, Data,
            ClientState, Interop, Log,
            Request,
            () => runtime?.CurrentNativeEmote, AddonLifecycle, MenuOptions,
            () => enabled && Preferences.CanLinkEmoteMessages);
        if (Preferences.CanLinkEmoteMessages) interaction.EnableNativeChatLinks();
    }

    private void LoadCatalog()
    {
        var entries = new List<PreviewCatalogEntry>();
        entries.AddRange(Data.GetExcelSheet<Mount>().Select(e => new PreviewCatalogEntry(MenuKind.Mount, e.RowId, e.Singular.ToString())));
        entries.AddRange(Data.GetExcelSheet<Companion>().Select(e => new PreviewCatalogEntry(MenuKind.Minion, e.RowId, e.Singular.ToString())));
        entries.AddRange(Data.GetExcelSheet<Ornament>().Select(e => new PreviewCatalogEntry(MenuKind.Accessory, e.RowId, e.Singular.ToString())));
        entries.AddRange(Data.GetExcelSheet<Emote>().Where(e => e.ActionTimeline[0].RowId != 0)
            .Select(e => new PreviewCatalogEntry(MenuKind.Emote, e.RowId, e.Name.ToString())));
        catalog = new(entries);
    }

    private static string CategoryName(MenuKind kind) => kind switch
    {
        MenuKind.Mount => "坐骑", MenuKind.Minion => "宠物", MenuKind.Accessory => "时尚配饰", _ => "情感动作",
    };

    private void SetCategory(MenuKind kind, bool value)
        => DispatchMutation(() => SetCategoryCore(kind, value));

    private void SetCategoryCore(MenuKind kind, bool value)
    {
        if (disposed) return;
        switch (kind)
        {
            case MenuKind.Mount: config.LocalPreviewMountEnabled = value; break;
            case MenuKind.Minion: config.LocalPreviewMinionEnabled = value; break;
            case MenuKind.Accessory: config.LocalPreviewAccessoryEnabled = value; break;
            case MenuKind.Emote: config.LocalPreviewEmoteEnabled = value; break;
            default: return;
        }
        if (!value) runtime?.Clear((PreviewSlot)kind);
        if (kind == MenuKind.Emote && enabled && runtime is not null)
        {
            interaction?.Dispose();
            CreateInteraction();
        }
        Cleanup(saveConfig, "保存分类开关");
    }

    private void SetItemMenus(bool value)
        => DispatchMutation(() => SetItemMenusCore(value));

    private void SetItemMenusCore(bool value)
    {
        if (disposed) return;
        config.LocalPreviewItemMenusEnabled = value;
        Cleanup(saveConfig, "保存物品右键开关");
    }

    private void RequestEnabled(bool value)
    {
        if (disposed) return;
        if (value)
        {
            Preferences.RequestEnable();
            enableConfirmationVisible = true;
        }
        else
        {
            enableConfirmationVisible = false;
            Preferences.CompleteEnable(false);
            SetEnabled(false);
        }
    }

    private void CompleteEnableConfirmation(bool accepted)
    {
        enableConfirmationVisible = false;
        if (!accepted) { Preferences.CompleteEnable(false); return; }
        DispatchMutation(() =>
        {
            if (Preferences.CompleteEnable(true)) SetEnabledCore(true);
        });
    }

    private void SetChatLinks(bool value)
        => DispatchMutation(() =>
        {
            if (config.LocalPreviewChatLinksEnabled == value) return;
            Preferences.SetChatLinksEnabled(value);
            if (enabled && value) interaction?.EnableNativeChatLinks();
            Cleanup(saveConfig, "保存情感动作文字试用开关");
        });

    public void DrawContent()
    {
        if (disposed) return;
        var currentEnabled = enabled;
        if (ImGui.Checkbox("开启右键本地试用###LocalPreviewEnabled", ref currentEnabled)) RequestEnabled(currentEnabled);
        ImGui.Indent();
        ImGui.TextWrapped("右键试用聊天、布告板或背包里的物品，如坐骑角笛、演技教材。也可以右键玩家，试用对方正在使用的坐骑、宠物、时尚配饰，或上一次使用的情感动作。");
        ImGui.TextWrapped("支持坐骑、宠物、时尚配饰、情感动作。");
        ImGui.Unindent();

        ImGui.Spacing();
        var chatLinks = config.LocalPreviewChatLinksEnabled;
        using (UiHelpers.Disabled(!enabled))
        {
            if (ImGui.Checkbox("开启对情感动作文字提示的试用###LocalPreviewChatLinks", ref chatLinks)) SetChatLinks(chatLinks);
        }
        ImGui.Indent();
        UiHelpers.ColoredWrapped(UiHelpers.Muted,
            "开启后，聊天栏里的「光之战士表演起了默剧。」这整句提示会变成可点击的文字；左键点击就会让自己的角色试用该动作，对其他人不可见。关闭时，新提示保持原样，不可点击。");
        ImGui.Unindent();

        if (enabled)
        {
            ImGui.Spacing();
            ImGui.Separator();
            UiHelpers.ColoredWrapped(UiHelpers.Muted, !initialized || runtime is null
                ? "试用功能正在初始化，请稍后再试。" : !coexistence.Allowed ? coexistence.Reason : lastMessage);
            if (ReadActiveEffects().Length != 0 && ImGui.Button("结束全部试用###LocalPreviewCancel")) Cancel();
        }
        DrawEnableConfirmation();
    }

    private void DrawEnableConfirmation()
    {
        const string name = "开启右键本地试用###LocalPreviewConfirm";
        var visible = enableConfirmationVisible;
        if (visible && !ImGui.IsPopupOpen(name)) ImGui.OpenPopup(name);
        ImGui.SetNextWindowSize(new Vector2(ImGui.GetFontSize() * 34, 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(name, ref visible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!visible && enableConfirmationVisible) CompleteEnableConfirmation(false);
            return;
        }
        ImGui.TextWrapped("开启此功能会在本地显示对坐骑、时尚配饰等的试用");
        ImGui.TextWrapped("例如在海都试用坐骑，其他人眼里你还是在正常走路");
        ImGui.TextWrapped("试用的情感动作只供本人预览参考动作是否购买，其他玩家不会收到");
        ImGui.Separator();
        if (ImGui.Button("取消", new Vector2(120, 0)))
        {
            CompleteEnableConfirmation(false);
            ImGui.CloseCurrentPopup();
        }
        if (ImGui.IsWindowAppearing()) ImGui.SetItemDefaultFocus();
        ImGui.SameLine();
        if (ImGui.Button("开启", new Vector2(120, 0)))
        {
            CompleteEnableConfirmation(true);
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
        if (!visible && enableConfirmationVisible) CompleteEnableConfirmation(false);
    }
}
