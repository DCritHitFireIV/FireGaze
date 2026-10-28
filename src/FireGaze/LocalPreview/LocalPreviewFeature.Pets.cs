using Dalamud.Bindings.ImGui;
using FireGaze.LocalPreview.Core;
using FireGaze.LocalPreview.Interaction;
using FireGaze.UI;
using PreviewKind = FireGaze.LocalPreview.Interaction.PreviewKind;

namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    private int companionInteractionFilter;
    private int sameSpeciesSummonCount = 2;
    private bool companionSelectionMode;
    private bool companionInteractionDemoMode;
    private static readonly CompanionInteractionDemoKind[] CompanionFilterKinds = Enum.GetValues<CompanionInteractionDemoKind>();
    private static readonly string[] CompanionFilterLabels = ["全部宠物", "一般宠物",
        .. CompanionFilterKinds.Select(CompanionCategoryLabel)];

    private static string CompanionCategoryLabel(CompanionInteractionDemoKind kind) => kind switch
    {
        CompanionInteractionDemoKind.ShoulderLong => "肩膀停留",
        CompanionInteractionDemoKind.ShoulderOnSummon => "召唤后上肩",
        CompanionInteractionDemoKind.ShoulderLeavesOnMove => "移动后离肩",
        CompanionInteractionDemoKind.Swim => "水面游泳",
        CompanionInteractionDemoKind.SummonPerformance => "召唤表演",
        CompanionInteractionDemoKind.StaticUnderfoot => "脚下停留",
        CompanionInteractionDemoKind.StaticFront => "前方停留",
        CompanionInteractionDemoKind.StaticHeight => "固定高度",
        CompanionInteractionDemoKind.PairInteraction => "宠物间互动",
        CompanionInteractionDemoKind.SameSpecies => "同种互动",
        CompanionInteractionDemoKind.FollowDistance => "特殊跟随",
        CompanionInteractionDemoKind.GenderSpecial => "性别特殊互动",
        CompanionInteractionDemoKind.BattleAction => "战斗与应援动作",
        _ => "其他",
    };

    private void DrawPetCollection()
    {
        var petView = ReadCatalogControls().PetView;
        var state = System.Threading.Volatile.Read(ref petPresentation);
        var stopPending = IsTrialStopPending();
        var summonUnavailableReason = stopPending ? "正在结束试用"
            : !state.MultiCompanionEnabled ? "先启用多宠物试用" : null;
        DrawPetCollectionHeader(state, summonUnavailableReason);
        if (companionInteractionDemoMode)
        {
            DrawCompanionInteractionDemos(state);
            return;
        }

        var search = companionSearch;
        DrawCatalogSearchAndScale("###LocalPreviewCompanionSearch", "搜索宠物", ref search);
        if (search != companionSearch)
            companionSearch = search;
        var allPets = catalog.Search(PreviewKind.Minion, "");
        var filterWidth = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("分类").X - ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.SetNextItemWidth(MathF.Max(1f, MathF.Min(filterWidth, ImGui.GetFontSize() * 12f)));
        ImGui.Combo("分类###LocalPreviewInteractionFilter", ref companionInteractionFilter,
            CompanionFilterLabels, CompanionFilterLabels.Length);

        var query = search.Trim();
        bool MatchesCategory(PreviewCatalogEntry entry)
            => companionInteractionFilter == 0
                || companionInteractionFilter == 1 && !state.InteractionEntries.Any(x => x.CompanionId == entry.Id)
                || companionInteractionFilter >= 2 && companionInteractionFilter - 2 < CompanionFilterKinds.Length
                    && state.InteractionEntries.Any(x => x.CompanionId == entry.Id
                        && x.Kind == CompanionFilterKinds[companionInteractionFilter - 2]);
        bool Matches(PreviewCatalogEntry entry)
            => MatchesCategory(entry)
                && (query.Length == 0
                || entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || entry.Id.ToString() == query);
        // Search narrows what is shown, while “召唤全部” means the complete
        // selected category.  Capture this immutable category snapshot before
        // dispatch so later tab/search changes cannot alter the batch.
        var summonCandidates = allPets.Where(MatchesCategory).ToArray();
        var sameSpeciesCategory = companionInteractionFilter >= 2
            && companionInteractionFilter - 2 < CompanionFilterKinds.Length
            && CompanionFilterKinds[companionInteractionFilter - 2] == CompanionInteractionDemoKind.SameSpecies;

        if (petView == MinionTrialView.Owned && sameSpeciesCategory)
        {
            var repeatedSpecies = allPets.Where(Matches)
                .Where(entry => !companionSelectionMode || state.SelectedPetIds.Contains(entry.Id))
                .Select(entry => entry.Id).ToArray();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5f);
            ImGui.InputInt("每种数量###LocalPreviewRepeatedCount", ref sameSpeciesSummonCount);
            sameSpeciesSummonCount = Math.Clamp(sameSpeciesSummonCount, 1, CompanionTrialRequestModel.MaxInstancesPerCompanion);
            ContinueTrialToolbar("按数量召唤");
            using (UiHelpers.Disabled(summonUnavailableReason is not null || repeatedSpecies.Length == 0))
                if (ImGui.Button("按数量召唤###LocalPreviewSummonRepeatedPets"))
                    SummonRepeatedSelectedPets(repeatedSpecies, sameSpeciesSummonCount);
            DrawPetSummonDisabledReason(summonUnavailableReason
                ?? (repeatedSpecies.Length == 0 ? companionSelectionMode ? "先选择要召唤的宠物" : "没有匹配的宠物" : null));
            ImGui.Spacing();
        }

        var viewLabel = petView == MinionTrialView.Summoned ? "全部宠物" : $"已召唤 {state.Active.Length}";
        if (FireGazeSymbols.NeutralButton(viewLabel + "###LocalPreviewPetView", selected: petView == MinionTrialView.Summoned))
            UpdateCatalogControls(petView == MinionTrialView.Summoned ? TrialCatalogAction.Owned : TrialCatalogAction.Summoned);
        ContinueTrialToolbar("框选模式");
        if (FireGazeSymbols.NeutralButton("框选模式###LocalPreviewCompanionSelectionMode",
            selected: companionSelectionMode))
        {
            companionSelectionMode = !companionSelectionMode;
            if (!companionSelectionMode) ClearPetSelection();
        }
        ContinueTrialToolbar("召唤全部");
        var canSummonAll = summonUnavailableReason is null
            && state.MaximumCompanions == 0 && summonCandidates.Length != 0;
        using (UiHelpers.Disabled(!canSummonAll))
            if (ImGui.Button("召唤全部###LocalPreviewSummonAllPets"))
                SummonAllPets(summonCandidates.Select(x => x.Id).ToArray());
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canSummonAll)
            ImGui.SetTooltip(stopPending ? "正在结束试用"
                : !state.MultiCompanionEnabled ? "先启用多宠物试用"
                : summonCandidates.Length == 0 ? "当前分类没有可召唤的宠物" : "在设置中打开“不限数量”");

        var selectedCount = companionSelectionMode && petView == MinionTrialView.Owned
            ? state.SelectedPetIds.Length
            : companionSelectionMode ? state.SelectedPetRequestIds.Length : 0;
        if (companionSelectionMode && selectedCount > 0)
        {
            ImGui.Spacing();
            if (petView == MinionTrialView.Owned)
            {
                using (UiHelpers.Disabled(summonUnavailableReason is not null))
                    if (ImGui.Button($"召唤选中 ({selectedCount})###LocalPreviewSummonSelected")) SummonSelectedPets();
                DrawPetSummonDisabledReason(summonUnavailableReason);
                ContinueTrialToolbar($"收回选中 ({selectedCount})");
            }
            if (ImGui.Button($"收回选中 ({selectedCount})###LocalPreviewRecallSelected")) RecallSelectedPets();
            if (petView == MinionTrialView.Summoned)
            {
                ContinueTrialToolbar("招手");
                if (ImGui.Button($"招手###LocalPreviewBeckonSelected")) BeckonSelectedPets();
            }
        }

        var displayed = state.Leases.Count(x => x.State == CompanionDisplayState.Active);
        var pending = state.Leases.Count(x => x.State == CompanionDisplayState.Pending);
        var capacityPending = Math.Clamp(state.CapacityPendingCount, 0, pending);
        var retiring = state.Leases.Count(x => x.State == CompanionDisplayState.Retiring);
        if (pending > 0)
            ImGui.TextDisabled($"已显示 {displayed} · 等待 {pending}");
        if (capacityPending > 0)
            UiHelpers.ColoredWrapped(FireGazePalette.Muted, $"当前可显示数量已满，{capacityPending} 只等待空位。");
        if (retiring > 0) ImGui.TextDisabled($"回收中 {retiring}");
        var hidden = state.Leases.Count(x => x.State == CompanionDisplayState.NativeHidden);
        if (hidden > 0) ImGui.TextDisabled($"正在恢复 {hidden} 只宠物");
        var failed = state.Leases.Where(x => x.State == CompanionDisplayState.Failed).ToArray();
        if (failed.Length > 0)
        {
            ImGui.TextDisabled($"{failed.Length} 只宠物未能显示");
            ContinueTrialToolbar("重新召唤");
            using (UiHelpers.Disabled(summonUnavailableReason is not null))
                if (ImGui.SmallButton("重新召唤###LocalPreviewRetryPets"))
                    RetryCompanions(failed);
            DrawPetSummonDisabledReason(summonUnavailableReason);
        }
        petView = ReadCatalogControls().PetView;
        var displayedBindings = state.ActiveBindings.Where(x => Matches(x.Entry)).ToArray();
        var entries = petView == MinionTrialView.Owned
            ? allPets.Where(Matches).ToArray()
            : displayedBindings.Select(x => x.Entry).ToArray();
        if (entries.Length == 0)
        {
            var hasSource = petView == MinionTrialView.Owned
                ? allPets.Count != 0 : state.ActiveBindings.Length != 0;
            ImGui.TextDisabled(hasSource ? "没有找到匹配的宠物"
                : petView == MinionTrialView.Owned ? "暂无可显示的宠物" : "还没有已召唤的宠物");
            return;
        }
        var targetEntries = state.ActiveBindings.Where(x => x.Lease.State == CompanionDisplayState.Active).ToArray();
        if (petView == MinionTrialView.Summoned && targetEntries.Length > 0)
        {
            string TargetName(PetLeaseBinding binding)
            {
                var siblings = targetEntries.Where(x => x.Entry.Id == binding.Entry.Id).ToArray();
                return siblings.Length <= 1 ? binding.Entry.Name
                    : $"{binding.Entry.Name} · {Array.IndexOf(siblings, binding) + 1}";
            }
            var selectedEntry = state.SelectedLease is { } selectedLease
                ? targetEntries.FirstOrDefault(x => x.Lease.LeaseId == selectedLease.LeaseId
                    && x.Lease.Generation == selectedLease.Generation) : null;
            var targetLabel = selectedEntry is null ? "选择宠物" : TargetName(selectedEntry);
            var targetWidth = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("动作目标").X - ImGui.GetStyle().ItemInnerSpacing.X;
            ImGui.SetNextItemWidth(MathF.Max(1f, targetWidth));
            if (ImGui.BeginCombo("动作目标###LocalPreviewPetTarget", targetLabel))
            {
                foreach (var entry in targetEntries)
                {
                    var chosen = state.SelectedLease is { } target && target.LeaseId == entry.Lease.LeaseId
                        && target.Generation == entry.Lease.Generation;
                    if (ImGui.Selectable($"{TargetName(entry)}##LocalPreviewPetTarget{entry.Lease.LeaseId}", chosen))
                        SelectCompanionLease(entry.Lease);
                    if (chosen) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }
        DrawTrialIconTable("###LocalPreviewPetMasonry", entries,
            (entry, index) =>
            {
                if (petView == MinionTrialView.Owned)
                {
                    if (companionSelectionMode)
                        SelectPetCatalog(entry.Id, entries.Select(x => x.Id).ToArray(),
                            ImGui.GetIO().KeyShift, ImGui.GetIO().KeyCtrl);
                    else
                        RequestCompanion(entry.Id, repeatRequested: sameSpeciesCategory);
                }
                else if (index < displayedBindings.Length)
                {
                    if (companionSelectionMode)
                        SelectPetRequests(displayedBindings[index].Request.RequestId,
                            displayedBindings.Select(x => x.Request.RequestId).ToArray(),
                            ImGui.GetIO().KeyShift, ImGui.GetIO().KeyCtrl);
                }
            },
            (entry, index) =>
            {
                if (petView == MinionTrialView.Summoned && index < displayedBindings.Length)
                    RecallCompanionLease(displayedBindings[index].Lease);
                else if (petView == MinionTrialView.Owned) RecallCompanion(entry.Id);
            },
            (entry, index) => petView == MinionTrialView.Owned
                ? companionSelectionMode && state.SelectedPetIds.Contains(entry.Id)
                : index < displayedBindings.Length
                    && companionSelectionMode && state.SelectedPetRequestIds.Contains(displayedBindings[index].Request.RequestId),
            isDisabled: (entry, _) => petView == MinionTrialView.Owned && summonUnavailableReason is not null);
    }

    private void DrawPetCollectionHeader(PetPresentation state, string? summonUnavailableReason)
    {
        const string stopLabel = "结束全部试用";
        var left = ImGui.GetCursorPosX();
        var available = ImGui.GetContentRegionAvail().X;
        var right = left + available;
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var settingsWidth = ImGui.CalcTextSize("设置").X + ImGui.GetStyle().FramePadding.X * 2f;
        var savedWidth = ImGui.CalcTextSize("保存组").X + ImGui.GetStyle().FramePadding.X * 2f;
        var stopWidth = ImGui.CalcTextSize(stopLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
        var actionsWidth = settingsWidth + savedWidth + stopWidth + gap * 2f;
        var multiCompanion = state.MultiCompanionEnabled;
        if (ImGui.Checkbox("启用多宠物试用###LocalPreviewMultiCompanionEnabled", ref multiCompanion))
            SetMultiCompanionEnabled(multiCompanion);

        var interactionLabel = companionInteractionDemoMode ? "返回宠物" : "宠物互动";
        ContinueTrialToolbar(interactionLabel);
        if (FireGazeSymbols.NeutralButton(interactionLabel + "###LocalPreviewCompanionInteractionMode",
            selected: companionInteractionDemoMode))
            companionInteractionDemoMode = !companionInteractionDemoMode;

        var screenRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (ImGui.GetItemRectMax().X + gap + actionsWidth <= screenRight)
            ImGui.SameLine(right - actionsWidth);
        else ImGui.SetCursorPosX(left);

        if (ImGui.Button("设置###LocalPreviewPetSettingsButton"))
            ImGui.OpenPopup("###LocalPreviewPetSettings");
        ContinueTrialToolbar("保存组");
        if (ImGui.Button("保存组###LocalPreviewSavedGroupButton"))
            ImGui.OpenPopup("###LocalPreviewSavedGroup");
        screenRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (ImGui.GetItemRectMax().X + gap + stopWidth <= screenRight)
            ImGui.SameLine(MathF.Max(left, right - stopWidth));
        else ImGui.SetCursorPosX(MathF.Max(left, right - stopWidth));
        DrawStopAllAction("###LocalPreviewRecallAllPets");

        DrawPetSettingsPopup(state);
        DrawPetSavedGroupPopup(state, summonUnavailableReason);
    }

    private void DrawPetSettingsPopup(PetPresentation state)
    {
        if (!ImGui.BeginPopup("###LocalPreviewPetSettings")) return;
        var showNames = state.ShowNames;
        if (ImGui.Checkbox("显示名称###LocalPreviewShowCompanionNames", ref showNames)) SetShowCompanionNames(showNames);
        var unlimited = state.MaximumCompanions == 0;
        if (ImGui.Checkbox("不限数量###LocalPreviewUnlimitedCompanions", ref unlimited))
            SetMaximumCompanions(unlimited ? 0 : state.LastLimitedCompanions);
        var maximum = state.MaximumCompanions > 0 ? state.MaximumCompanions : state.LastLimitedCompanions;
        using (UiHelpers.Disabled(unlimited))
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7f);
            if (ImGui.InputInt("数量上限###LocalPreviewMaximumCompanions", ref maximum) && maximum > 0)
                SetMaximumCompanions(maximum);
        }
        ImGui.Separator();
        var repeat = state.SameSpeciesMode;
        if (ImGui.Checkbox("允许重复召唤###LocalPreviewSameSpecies", ref repeat)) SetSameSpeciesMode(repeat);
        ImGui.EndPopup();
    }

    private static void DrawPetSummonDisabledReason(string? reason)
    {
        if (reason is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(reason);
    }

    private void DrawPetSavedGroupPopup(PetPresentation state, string? summonUnavailableReason)
    {
        if (!ImGui.BeginPopup("###LocalPreviewSavedGroup")) return;
        using (UiHelpers.Disabled(!CompanionSavedGroupPolicy.CanSave(state.Leases)))
            if (ImGui.Selectable("保存当前组###LocalPreviewSavePets")) SaveCompanions();
        using (UiHelpers.Disabled(state.Saved.Length == 0))
        {
            using (UiHelpers.Disabled(summonUnavailableReason is not null))
                if (ImGui.Selectable("召唤保存组###LocalPreviewLoadPets")) LoadCompanions();
            DrawPetSummonDisabledReason(summonUnavailableReason
                ?? (state.Saved.Length == 0 ? "还没有保存组" : null));
            if (ImGui.Selectable("清除保存组###LocalPreviewClearSavedPets")) ClearSavedCompanions();
        }
        ImGui.EndPopup();
    }

    private void DrawCompanionInteractionDemos(PetPresentation state)
    {
        var unavailable = IsTrialStopPending() ? "正在结束试用"
            : !state.MultiCompanionEnabled ? "先启用多宠物试用" : null;
        DrawGroup(CompanionInteractionDemoKind.GenderSpecial, CompanionBattleDemoAudience.None,
            CompanionInteractionDemoCatalog.FemaleConditionDescription);
        DrawGroup(CompanionInteractionDemoKind.BattleAction, CompanionBattleDemoAudience.Enemy,
            CompanionInteractionDemoCatalog.EnemyBattleDescription);
        DrawGroup(CompanionInteractionDemoKind.BattleAction, CompanionBattleDemoAudience.Player,
            CompanionInteractionDemoCatalog.PlayerBattleDescription);

        void DrawGroup(CompanionInteractionDemoKind kind, CompanionBattleDemoAudience audience, string description)
        {
            var entries = state.InteractionEntries.Where(x => x.Kind == kind
                && x.BattleAudience == audience).ToArray();
            if (entries.Length == 0) return;
            ImGui.Spacing();
            UiHelpers.ColoredWrapped(FireGazePalette.Muted, description);
            foreach (var entry in entries)
            {
                ImGui.PushID($"CompanionDemo:{kind}:{audience}:{entry.CompanionId}");
                var selected = state.ActiveInteractionDemos.Any(x => x.CompanionId == entry.CompanionId
                    && x.Kind == kind && x.Audience == audience && x.PlaybackRequested);
                var clicked = DrawTrialIcon(new(PreviewKind.Minion, entry.CompanionId, entry.Name, entry.Icon),
                    selected, disabled: unavailable is not null);
                DrawPetSummonDisabledReason(unavailable);
                if (clicked)
                {
                    if (kind == CompanionInteractionDemoKind.GenderSpecial) AddGenderInteraction(entry.CompanionId);
                    else AddBattleInteraction(entry.CompanionId, audience);
                }
                ImGui.PopID();
            }
        }
    }
}
