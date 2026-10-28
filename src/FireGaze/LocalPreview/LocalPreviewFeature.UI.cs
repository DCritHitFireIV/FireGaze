using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using FireGaze.LocalPreview.Core;
using FireGaze.LocalPreview.Interaction;
using FireGaze.LocalPreview.Runtime;
using FireGaze.UI;
using Lumina.Excel.Sheets;
using MenuKind = FireGaze.LocalPreview.Interaction.PreviewKind;

namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    private volatile PreviewCatalog catalog = new([]);
    private MinionTrialCollection? minionTrials;
    private TrialCatalogControls trialCatalogControls = new();
    private TrialCatalogControls ReadCatalogControls() => System.Threading.Volatile.Read(ref trialCatalogControls);
    private void UpdateCatalogControls(TrialCatalogAction action)
    {
        if (!disposed) TrialCatalogControls.Update(ref trialCatalogControls, action);
    }
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
        var mountRows = Data.GetExcelSheet<Mount>().Where(e => e.RowId > 0 && e.RowId <= ushort.MaxValue).ToArray();
        static MountCatalogIconSource MountIcon(Mount row) => new(row.RowId, row.Icon, row.ModelChara.RowId,
            row.EquipHead, row.EquipBody, row.EquipLeg, row.EquipFoot, row.MountCustomize.RowId);
        static MountCatalogNameSource MountName(Mount row) => new(MountIcon(row), row.Singular.ToString());
        // Story and task variants can be valid preview mounts with Icon=0. Use
        // only a unique exact-appearance official icon; otherwise keep an honest
        // missing-image card. Keep every supported catalog entry and MountID.
        var mountIcons = new MountCatalogIcons(mountRows.Select(MountIcon));
        var mountNames = new MountCatalogNames(mountRows.Select(MountName));
        var mountVariants = new MountVariantCatalog(mountRows);
        entries.AddRange(mountRows.Select(row =>
            {
                // Classify from the original client row before resolved names or
                // icons can make a story variant resemble a regular mount.
                var view = !row.Singular.IsEmpty && row.Icon > 0 ? MountCatalogView.Regular : MountCatalogView.Special;
                var entry = mountNames.CreateEntry(MountName(row), mountIcons.Resolve(MountIcon(row)));
                return entry is null ? null : entry with
                {
                    MountView = view,
                    MountRepresentativeId = mountVariants.GetRepresentative(row.RowId),
                    HasAdmittedSpecialAction = mountVariants.HasAdmittedSpecialAction(row.RowId),
                };
            })
            .OfType<PreviewCatalogEntry>());
        var companionRows = Data.GetExcelSheet<Companion>()
            .Where(e => e.RowId > 0 && e.RowId <= ushort.MaxValue)
            .ToArray();
        entries.AddRange(companionRows
            .Select(e => new PreviewCatalogEntry(MenuKind.Minion, e.RowId, e.Singular.ToString(), e.Icon)));
        entries.AddRange(Data.GetExcelSheet<Ornament>().Select(e => new PreviewCatalogEntry(MenuKind.Accessory, e.RowId,
            e.Singular.ToString(), e.Icon)));
        entries.AddRange(Data.GetExcelSheet<Emote>().Where(e => e.ActionTimeline[0].RowId != 0)
            .Select(e => new PreviewCatalogEntry(MenuKind.Emote, e.RowId, e.Name.ToString())));
        catalog = new(entries);
        minionTrials = new(entries.Where(entry => entry.Kind == MenuKind.Minion));
        var companionEntries = entries.Where(entry => entry.Kind == MenuKind.Minion)
            .Select(entry =>
            {
                var row = companionRows.FirstOrDefault(candidate => candidate.RowId == entry.Id);
                var source = new CompanionSourceInteractionRow(entry.Id, entry.Name,
                    row.Behavior.RowId, row.Special, row.Unknown6, row.Unknown8);
                var rule = CompanionSourceInteractionRules.FromClientRow(source);
                return new CompanionTrialCatalogEntry(entry.Id, entry.Name)
                {
                    BehaviorId = source.BehaviorId,
                    Special = source.Special,
                    TitleRequired = source.TitleRequired,
                    PokeReaction = source.PokeReaction,
                    ContextualDescription = rule.ContextualDescription,
                };
            }).ToArray();
        companionRequests = new(new CompanionTrialCatalog(companionEntries));
        companionInteractionCatalog = CompanionInteractionDemoCatalogFactory.FromClientNames(companionEntries);
        companionInteractionSession = new(companionInteractionCatalog);
        minionTrials.RestoreSaved(config.LocalPreviewSavedMinions);
        RefreshOwnedMinions();
    }

    private void RefreshOwnedMinions()
    {
        if (minionTrials is null) return;
        try
        {
            var owned = Data.GetExcelSheet<Companion>()
                .Where(row => row.RowId > 0 && row.RowId <= ushort.MaxValue
                    && !string.IsNullOrWhiteSpace(row.Singular.ToString())
                    && Plugin.UnlockState.IsCompanionUnlocked(row))
                .Select(row => row.RowId);
            minionTrials.SetOwned(owned);
        }
        catch
        {
            // Unlock state is not ready during login; keep the previous snapshot
            // and retry on the next presentation frame.
        }
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
        if (!value)
        {
            runtime?.Clear((PreviewSlot)kind);
            if (kind == MenuKind.Minion)
                companions?.RecallAll(CompanionDisplayRetireReason.UserCancelled);
        }
        if (kind == MenuKind.Emote && enabled && runtime is not null)
        {
            DisposeComponent(interaction, "菜单入口");
            if (!NativeAdmissionAvailable()) return;
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
            "将新收到的情感动作提示变为可点击文字。\n例如：「光之战士表演起了默剧。」\n左键点击即可试用，仅自己可见。");
        ImGui.Unindent();
        ImGui.Spacing();

        if (enabled)
        {
            var panels = ReadCatalogControls().Panels;
            var navigationLeft = ImGui.GetCursorPosX();
            var navigationWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
            var navigationUsed = 0f;
            var navigationGap = ImGui.GetStyle().ItemSpacing.X;

            PrepareTrialNavigationButton("坐骑试用", ref navigationUsed, navigationWidth, navigationLeft);
            if (FireGazeSymbols.NeutralButton("坐骑试用###LocalPreviewMountButton", selected: panels.MountVisible))
                UpdateCatalogControls(TrialCatalogAction.Mounts);
            navigationUsed += TrialNavigationButtonWidth("坐骑试用") + navigationGap;

            PrepareTrialNavigationButton("我的宠物", ref navigationUsed, navigationWidth, navigationLeft);
            if (FireGazeSymbols.NeutralButton("我的宠物###LocalPreviewPetButton", selected: panels.PetVisible))
                UpdateCatalogControls(TrialCatalogAction.Pets);
            navigationUsed += TrialNavigationButtonWidth("我的宠物") + navigationGap;

            PrepareTrialNavigationButton("时尚配饰", ref navigationUsed, navigationWidth, navigationLeft);
            if (FireGazeSymbols.NeutralButton("时尚配饰###LocalPreviewAccessoryButton", selected: panels.AccessoryVisible))
                UpdateCatalogControls(TrialCatalogAction.Accessories);
            if (panels.MountVisible) DrawMountCollection();
            else if (panels.PetVisible) DrawPetCollection();
            else if (panels.AccessoryVisible) DrawAccessoryCollection();
        }
        DrawEnableConfirmation();
    }

    private static float TrialNavigationButtonWidth(string text)
        => ImGui.CalcTextSize(text).X + ImGui.GetStyle().FramePadding.X * 2f;

    private static void ContinueTrialToolbar(string label)
    {
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var next = ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X;
        if (next + TrialNavigationButtonWidth(label) <= right) ImGui.SameLine();
    }

    private static void PrepareTrialNavigationButton(string text, ref float used, float availableWidth, float left)
    {
        var width = TrialNavigationButtonWidth(text);
        if (used > 0 && used + width > availableWidth)
        {
            ImGui.SetCursorPosX(left);
            used = 0;
        }
        else if (used > 0) ImGui.SameLine();
    }

    private void DrawEnableConfirmation()
    {
        const string name = "开启本地试用###LocalPreviewConfirm";
        var visible = enableConfirmationVisible;
        if (visible && !ImGui.IsPopupOpen(name)) ImGui.OpenPopup(name);
        ImGui.SetNextWindowSize(new Vector2(ImGui.GetFontSize() * 34, 0), ImGuiCond.Appearing);
        FireGazeWindowPlacement.PrepareModal(new Vector2(ImGui.GetFontSize() * 34, 300));
        if (!ImGui.BeginPopupModal(name, ref visible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!visible && enableConfirmationVisible) CompleteEnableConfirmation(false);
            return;
        }
        ImGui.TextWrapped("本地试用仅自己可见，结束后恢复原状。");
        ImGui.Separator();
        if (ImGui.Button("取消", new Vector2(120, 0)))
        {
            CompleteEnableConfirmation(false);
            ImGui.CloseCurrentPopup();
        }
        if (ImGui.IsWindowAppearing()) ImGui.SetItemDefaultFocus();
        ImGui.SameLine();
        if (ImGui.Button("开启本地试用", new Vector2(ImGui.GetFontSize() * 9f, 0)))
        {
            CompleteEnableConfirmation(true);
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
        if (!visible && enableConfirmationVisible) CompleteEnableConfirmation(false);
    }
}
