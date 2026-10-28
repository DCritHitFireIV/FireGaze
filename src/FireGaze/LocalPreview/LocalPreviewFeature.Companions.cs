using FireGaze.LocalPreview.Core;
using FireGaze.LocalPreview.Interaction;
using PreviewKind = FireGaze.LocalPreview.Interaction.PreviewKind;

namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    private MultiCompanionRuntime? companions;
    private Runtime.NativeCompanionTargetEmoteRouter? companionEmotes;
    private DateTime nextOwnedCompanionRead;
    private uint? selectedPetID;
    private long observedCompanionCancellationRevision;
    private CompanionTrialRequestModel? companionRequests;
    private CompanionInteractionDemoCatalog? companionInteractionCatalog;
    private CompanionInteractionDemoSession? companionInteractionSession;
    private Dictionary<long, CompanionDisplayLease>? companionInteractionLeases;
    private string companionSearch = string.Empty;
    private readonly HashSet<uint> selectedPetCatalogIds = [];
    private readonly HashSet<long> selectedPetRequestIds = [];
    private long? petCatalogSelectionAnchor;
    private long? petRequestSelectionAnchor;
    // Normal construction initializes this map. Offline lifecycle fixtures can
    // intentionally bypass construction, so publication and queued mutations use
    // the same lazy instance instead of dereferencing a null field.
    private CompanionTrialLeaseMap? companionLeaseMap = new();
    private CompanionTrialLeaseMap CompanionLeaseMap => companionLeaseMap ??= new();
    private PetPresentation petPresentation = new([], [], [], [], null, false, false, 30, 30, [], [], [], false, [], [], [], 0, 0, 0, null);

    private void InitializeCompanions()
    {
        companions = new(Framework, ClientState, Condition, Data, Log);
        companions.SetMultiCompanionEnabled(config.LocalPreviewMultiCompanionEnabled);
        companions.SetMaximumCompanions(config.LocalPreviewMaximumCompanions);
        try
        {
            companionEmotes = new(Interop, Framework, Log,
                address => companions?.OwnsChild(address) == true,
                (address, emote) => companions?.TryAdmitInteraction(address, emote) == true);
            companions.AttachBatchBeckon(companionEmotes);
        }
        catch (Exception failure)
        {
            if (ObservePhysicalCloseFailure(failure)) throw;
            Log.Warning(failure, "[LocalPreview] 宠物动作适配暂不可用");
        }
    }

    private void SetMultiCompanionEnabled(bool value)
        => DispatchMutation(() =>
        {
            if (config.LocalPreviewMultiCompanionEnabled == value) return;
            config.LocalPreviewMultiCompanionEnabled = value;
            companions?.SetMultiCompanionEnabled(value);
            if (!value) ResetCompanionRequests();
            Cleanup(saveConfig, "保存多宠物试用开关");
        });

    private void SetShowCompanionNames(bool value)
        => DispatchMutation(() =>
        {
            if (config.LocalPreviewShowCompanionNames == value) return;
            config.LocalPreviewShowCompanionNames = value;
            Cleanup(saveConfig, "保存宠物名称显示设置");
            RefreshCompanionNames();
        });

    private void SetMaximumCompanions(int value)
        => DispatchMutation(() =>
        {
            if (value < 0) return;
            if (config.LocalPreviewMaximumCompanions == value) return;
            if (value == 0 && config.LocalPreviewMaximumCompanions > 0)
                config.LocalPreviewLastLimitedCompanions = config.LocalPreviewMaximumCompanions;
            config.LocalPreviewMaximumCompanions = value;
            companions?.SetMaximumCompanions(value);
            Cleanup(saveConfig, "保存同时试用宠物上限");
        });

    private sealed record PetLeaseBinding(PreviewCatalogEntry Entry, CompanionTrialRequest Request,
        CompanionDisplayLease Lease);

    private sealed record PetPresentation(PreviewCatalogEntry[] Owned, PreviewCatalogEntry[] Active,
        CompanionDisplayLease[] Leases, uint[] Saved, uint? Selected, bool MultiCompanionEnabled,
        bool ShowNames, int MaximumCompanions, int LastLimitedCompanions,
        CompanionTrialRequest[] Requests, CompanionInteractionDemoEntry[] InteractionEntries,
        CompanionInteractionDemoRequest[] ActiveInteractionDemos, bool SameSpeciesMode, uint[] SelectedPetIds,
        long[] SelectedPetRequestIds, PetLeaseBinding[] ActiveBindings, int PendingBatchInteractions,
        int CapacityPendingCount, int DeferredCreationCount, CompanionDisplayLease? SelectedLease);

    private void UpdateCompanions()
    {
        if (Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat])
        {
            companionInteractionSession?.CancelForRealCombat();
            companions?.CancelInteractionDemo();
            companionInteractionLeases?.Clear();
        }
        companions?.Update(enabled && coexistence.Allowed);
        if (companions is { } current && observedCompanionCancellationRevision != current.CancellationRevision)
            ResetCompanionRequests();
        if (companions?.HasInteractionDemoRequest != true && companionInteractionSession is { } demos)
            foreach (var demo in demos.Active.Where(x => x.PlaybackRequested)) demos.PausePlayback(demo.RequestId);
        selectedPetID = companions?.SelectedCompanionID;
        if (DateTime.UtcNow >= nextOwnedCompanionRead)
        {
            nextOwnedCompanionRead = DateTime.UtcNow.AddSeconds(1);
            RefreshOwnedMinions();
        }
    }

    private void PublishCompanions()
    {
        var owned = minionTrials?.OwnedIds.ToHashSet() ?? [];
        var entries = catalog.Search(PreviewKind.Minion, "");
        var leases = disposed ? [] : companions?.Snapshot.Leases.ToArray() ?? [];
        var selected = companions?.SelectedCompanionID;
        selectedPetID = selected;
        var byID = entries.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        var requests = companionRequests?.Requests.ToArray() ?? [];
        CompanionLeaseMap.Reconcile(requests, leases);
        var requestsByID = requests.ToDictionary(x => x.RequestId);
        var activeBindings = leases
            .Where(x => x.State is CompanionDisplayState.Active or CompanionDisplayState.NativeHidden)
            .Where(x => byID.ContainsKey(x.CompanionId))
            .Select(lease =>
            {
                var request = CompanionLeaseMap.TryGetRequest(lease, out var requestID)
                    && requestsByID.TryGetValue(requestID, out var bound)
                    ? bound
                    : new CompanionTrialRequest(0, lease.CompanionId, 0, byID[lease.CompanionId].Name);
                return new PetLeaseBinding(byID[lease.CompanionId], request, lease);
            })
            .ToArray();
        var active = activeBindings.Select(x => x.Entry).ToArray();
        var interactionEntries = companionInteractionCatalog?.Entries
            .Select(entry => entry with { Icon = byID.TryGetValue(entry.CompanionId, out var icon) ? icon.Icon : 0 })
            .ToArray() ?? [];
        var activeDemos = companionInteractionSession?.Active.ToArray() ?? [];
        var state = new PetPresentation(entries.Where(x => owned.Contains(x.Id)).ToArray(),
            active, leases, config.LocalPreviewSavedMinions?.ToArray() ?? [], selected,
            config.LocalPreviewMultiCompanionEnabled, config.LocalPreviewShowCompanionNames,
            config.LocalPreviewMaximumCompanions, config.LocalPreviewLastLimitedCompanions,
            requests, interactionEntries, activeDemos,
            companionRequests?.Mode == CompanionTrialRequestMode.SameSpeciesInteraction,
            selectedPetCatalogIds?.ToArray() ?? [], selectedPetRequestIds?.ToArray() ?? [], activeBindings,
            companions?.PendingBatchInteractionCount ?? 0,
            companions?.CapacityPendingCount ?? 0, companions?.DeferredCreationCount ?? 0,
            companions?.SelectedLease);
        System.Threading.Volatile.Write(ref petPresentation, state);
    }

    private void SetSameSpeciesMode(bool enabled)
        => DispatchMutation(() =>
        {
            companionRequests?.SetMode(enabled
                ? CompanionTrialRequestMode.SameSpeciesInteraction
                : CompanionTrialRequestMode.Normal);
            PublishCompanions();
        });

    private void RequestCompanion(uint id, bool repeatRequested = false)
        => DispatchMutation(() => RequestCompanionCore(id, repeatRequested: repeatRequested));

    private CompanionDisplayLease? RequestCompanionCore(uint id, bool restoreSaved = false, bool publish = true,
        bool repeatRequested = false)
    {
        if (companions is null || companionRequests is null) return null;
        var allowDuplicate = restoreSaved || repeatRequested
            || companionRequests.Mode == CompanionTrialRequestMode.SameSpeciesInteraction;
        if (!allowDuplicate && companions.Snapshot.Leases.FirstOrDefault(x => x.CompanionId == id
            && x.State == CompanionDisplayState.Failed) is { LeaseId: > 0 } failed)
        {
            var replacement = RetryCompanionLeaseCore(failed);
            if (publish) PublishCompanions();
            return replacement;
        }
        var request = restoreSaved || repeatRequested ? companionRequests.Restore(id) : companionRequests.Request(id);
        if (!request.Accepted)
        {
            lastMessage = request.ExistingCount >= CompanionTrialRequestModel.MaxInstancesPerCompanion
                ? "同一种宠物最多同时试用 30 只，请先收回几只。"
                : request.ExistingCount > 0 ? "这只宠物已经在请求列表中。" : "当前无法添加这只宠物。";
            return null;
        }
        if (!companions.TryRequest(id, allowDuplicate, out var lease, out var reason))
        {
            companionRequests.Remove(request.Request!.Value.RequestId);
            lastMessage = reason;
            Chat.PrintError("[本地试用] " + reason);
            if (publish) PublishCompanions();
            return null;
        }
        CompanionLeaseMap.Bind(request.Request!.Value, lease);
        lastMessage = "正在召唤本地宠物";
        if (publish) PublishCompanions();
        return lease;
    }

    private void RetryCompanions(CompanionDisplayLease[] leases)
        => DispatchMutation(() =>
        {
            foreach (var lease in leases) RetryCompanionLeaseCore(lease);
            PublishCompanions();
        });

    private CompanionDisplayLease? RetryCompanionLeaseCore(CompanionDisplayLease failed)
    {
        if (companions is null) return null;
        var hasRequest = CompanionLeaseMap.TryGetRequest(failed, out var requestID);
        if (!companions.TryRetryLease(failed.LeaseId, failed.Generation, out var replacement, out var reason))
        {
            lastMessage = reason;
            return null;
        }
        var request = companionRequests?.Requests.FirstOrDefault(x => x.RequestId == requestID);
        if (hasRequest && request is { RequestId: > 0 } current)
            CompanionLeaseMap.Bind(current, replacement);
        lastMessage = "正在重新召唤这只宠物";
        return replacement;
    }

    private void RecallCompanionLease(CompanionDisplayLease lease)
        => DispatchMutation(() =>
        {
            RecallCompanionLeaseCore(lease);
            PublishCompanions();
        });

    private void RecallCompanionLeaseCore(CompanionDisplayLease lease)
    {
        var hasRequest = CompanionLeaseMap.TryGetRequest(lease, out var requestID);
        if (companions?.RecallLease(lease.LeaseId, lease.Generation) != true) return;
        if (hasRequest && requestID > 0)
        {
            companionRequests?.Remove(requestID);
            CompanionLeaseMap.Remove(requestID);
            selectedPetRequestIds.Remove(requestID);
        }
    }

    private void AddGenderInteraction(uint id)
        => DispatchMutation(() =>
        {
            var result = companionInteractionSession?.AddGenderPreview(id,
                companions?.ReadInteractionCharacterGender() ?? CompanionCharacterGender.Unknown);
            ApplyCompanionInteractionDemo(result, "当前宠物没有可演示的性别互动。");
            PublishCompanions();
        });

    private void AddBattleInteraction(uint id, CompanionBattleDemoAudience audience)
        => DispatchMutation(() =>
        {
            var result = companionInteractionSession?.AddBattleDemo(id, audience);
            ApplyCompanionInteractionDemo(result, "当前宠物没有这类可演示的战斗互动。");
            PublishCompanions();
        });

    private void ApplyCompanionInteractionDemo(CompanionInteractionDemoRequestResult? result, string unavailableMessage)
    {
        if (result is not { Accepted: true, Request: { } request })
        {
            lastMessage = unavailableMessage;
            return;
        }
        if (!request.PlaybackRequested)
        {
            companions?.CancelInteractionDemo();
            return;
        }

        var started = false;
        try
        {
            var lease = FindCompanionInteractionLease(request.RequestId, request.CompanionId)
                ?? RequestCompanionCore(request.CompanionId, publish: false);
            if (lease is { } owned && companions?.SetInteractionDemo(owned, request.Kind, true) == true)
            {
                (companionInteractionLeases ??= [])[request.RequestId] = owned;
                started = true;
            }
        }
        finally
        {
            if (!started)
            {
                companions?.CancelInteractionDemo();
                companionInteractionSession?.PausePlayback(request.RequestId);
                lastMessage = "暂时无法开始互动，请稍后重试。";
            }
        }
    }

    private CompanionDisplayLease? FindCompanionInteractionLease(long requestID, uint companionID)
    {
        var current = companions?.Snapshot.Leases.Where(x => x.CompanionId == companionID
            && x.State is CompanionDisplayState.Pending or CompanionDisplayState.Active or CompanionDisplayState.NativeHidden)
            .ToArray() ?? [];
        if (companionInteractionLeases?.TryGetValue(requestID, out var bound) == true
            && current.FirstOrDefault(x => x.LeaseId == bound.LeaseId && x.Generation == bound.Generation)
                is { LeaseId: > 0 } retained)
            return retained;
        if (companions?.SelectedLease is { } selected
            && current.FirstOrDefault(x => x.LeaseId == selected.LeaseId && x.Generation == selected.Generation)
                is { LeaseId: > 0 } target)
            return target;
        var candidate = current.OrderBy(x => x.State == CompanionDisplayState.Active ? 0
                : x.State == CompanionDisplayState.NativeHidden ? 1 : 2)
            .ThenBy(x => x.LeaseId).FirstOrDefault();
        return candidate.LeaseId > 0 ? candidate : null;
    }

    private void CancelInteractionDemos()
        => DispatchMutation(() =>
        {
            companionInteractionSession?.CancelForRealCombat();
            companions?.CancelInteractionDemo();
            companionInteractionLeases?.Clear();
            PublishCompanions();
        });

    private void SelectPetCatalog(uint id, uint[] displayed, bool extendRange, bool toggle)
        => DispatchMutation(() =>
        {
            var result = CompanionRangeSelection.Apply(displayed.Select(x => (long)x).ToArray(),
                selectedPetCatalogIds.Select(x => (long)x).ToArray(), petCatalogSelectionAnchor, id, extendRange, toggle);
            selectedPetCatalogIds.Clear();
            selectedPetCatalogIds.UnionWith(result.Selected.Select(x => (uint)x));
            selectedPetRequestIds.Clear();
            petCatalogSelectionAnchor = result.Anchor;
            PublishCompanions();
        });

    private void ClearPetSelection()
        => DispatchMutation(() =>
        {
            selectedPetCatalogIds.Clear();
            selectedPetRequestIds.Clear();
            petCatalogSelectionAnchor = null;
            petRequestSelectionAnchor = null;
            PublishCompanions();
        });

    private void SelectPetRequests(long requestID, long[] displayed, bool extendRange, bool toggle)
        => DispatchMutation(() =>
        {
            if (requestID <= 0) return;
            var result = CompanionRangeSelection.Apply(displayed, selectedPetRequestIds,
                petRequestSelectionAnchor, requestID, extendRange, toggle);
            selectedPetRequestIds.Clear();
            selectedPetRequestIds.UnionWith(result.Selected);
            selectedPetCatalogIds.Clear();
            petRequestSelectionAnchor = result.Anchor;
            PublishCompanions();
        });

    private void SummonSelectedPets()
        => DispatchMutation(() =>
        {
            foreach (var id in selectedPetCatalogIds.ToArray()) RequestCompanionCore(id, publish: false);
            PublishCompanions();
        });

    /// <summary>
    /// Enqueues the captured visible category in catalog order. The configured unlimited
    /// mode removes FireGaze's request quota; the native client still creates actors
    /// through its bounded FIFO allocator and exposes later rows as waiting entries.
    /// </summary>
    private void SummonAllPets(uint[] displayedIDs)
    {
        // The UI owns filtering. Capture only immutable IDs before dispatch so changing
        // the category or search while Framework is busy cannot expand this batch.
        var batch = displayedIDs.Distinct().ToArray();
        DispatchMutation(() =>
        {
            if (companions is null || companionRequests is null) return;
            if (!config.LocalPreviewMultiCompanionEnabled)
            {
                lastMessage = "请先在“我的宠物”里启用多宠物试用。";
                PublishCompanions();
                return;
            }
            if (config.LocalPreviewMaximumCompanions > 0)
            {
                lastMessage = "请先打开“不限数量”，再召唤全部宠物。";
                PublishCompanions();
                return;
            }

            var accepted = 0;
            foreach (var id in batch)
            {
                var before = companionRequests.Requests.Count;
                RequestCompanionCore(id, publish: false);
                if (companionRequests.Requests.Count > before) accepted++;
            }
            lastMessage = accepted == 0 ? "没有新的宠物需要召唤。" : $"已加入 {accepted} 只宠物，正在依次显示。";
            PublishCompanions();
        });
    }

    private void SummonRepeatedSelectedPets(uint[] speciesIDs, int count)
    {
        // Freeze the UI's category/search/selection before Framework runs. The explicit
        // quantity action grants repeats only to this batch, not ordinary icon clicks.
        var species = speciesIDs.Distinct().ToArray();
        var quantity = Math.Clamp(count, 1, CompanionTrialRequestModel.MaxInstancesPerCompanion);
        DispatchMutation(() =>
        {
            foreach (var id in species)
                for (var i = 0; i < quantity; i++)
                    if (RequestCompanionCore(id, publish: false, repeatRequested: true) is null) break;
            PublishCompanions();
        });
    }

    private void RecallSelectedPets()
        => DispatchMutation(() =>
        {
            foreach (var requestID in selectedPetRequestIds.ToArray())
                if (CompanionLeaseMap.TryGetLease(requestID, out var requestLease))
                    RecallCompanionLeaseCore(requestLease);
            foreach (var lease in companions?.Snapshot.Leases
                .Where(x => selectedPetCatalogIds.Contains(x.CompanionId)).ToArray() ?? [])
                RecallCompanionLeaseCore(lease);
            selectedPetCatalogIds.Clear();
            selectedPetRequestIds.Clear();
            PublishCompanions();
        });

    private void BeckonSelectedPets()
        => DispatchMutation(() =>
        {
            if (companions is null || selectedPetRequestIds.Count == 0) return;
            var leases = selectedPetRequestIds
                .Select(id => CompanionLeaseMap.TryGetLease(id, out var lease) ? lease : default)
                .Where(lease => lease.LeaseId > 0)
                .ToArray();
            var queued = companions.QueueBeckon(leases);
            lastMessage = queued > 0
                ? "已发送招手请求。"
                : "当前没有可以响应的已召唤宠物。";
            PublishCompanions();
        });

    private void RecallCompanion(uint id) => DispatchMutation(() =>
    {
        if (selectedPetID == id) selectedPetID = null;
        foreach (var lease in companions?.Snapshot.Leases.Where(x => x.CompanionId == id).ToArray() ?? [])
            RecallCompanionLeaseCore(lease);
        foreach (var request in companionRequests?.Requests.Where(x => x.CompanionId == id).ToArray() ?? [])
        {
            companionRequests?.Remove(request.RequestId);
            CompanionLeaseMap.Remove(request.RequestId);
            selectedPetRequestIds.Remove(request.RequestId);
        }
        PublishCompanions();
    });

    private void RecallCompanions() => DispatchMutation(() =>
    {
        companions?.RecallAll(CompanionDisplayRetireReason.UserCancelled);
        ResetCompanionRequests();
        PublishCompanions();
    });

    private void ResetCompanionRequests()
    {
        observedCompanionCancellationRevision = companions?.CancellationRevision ?? 0;
        selectedPetID = null;
        CompanionLeaseMap.Clear();
        selectedPetCatalogIds?.Clear();
        selectedPetRequestIds?.Clear();
        petCatalogSelectionAnchor = null;
        petRequestSelectionAnchor = null;
        companionRequests?.Clear();
        companionInteractionSession?.CancelForRealCombat();
        companions?.CancelInteractionDemo();
        companionInteractionLeases?.Clear();
    }

    private void SelectCompanion(uint id) => DispatchMutation(() => SelectCompanionCore(id));

    private void SelectCompanionLease(CompanionDisplayLease lease)
        => DispatchMutation(() =>
        {
            lastMessage = companions?.SelectLease(lease.LeaseId, lease.Generation) == true
                ? "已选中本地宠物，可对它使用动作。"
                : "这只宠物尚未准备好，暂时无法选中。";
            PublishCompanions();
        });

    private void SelectCompanionCore(uint id)
    {
        if (companions?.Select(id) == true)
        {
            selectedPetID = id;
            lastMessage = "已选中本地宠物，可对它使用动作。";
            return;
        }

        if (selectedPetID == id) selectedPetID = null;
        lastMessage = "这只宠物尚未准备好，暂时无法选中。";
    }

    private void SaveCompanions() => DispatchMutation(() =>
    {
        var ids = CompanionSavedGroupPolicy.SelectPersistable(companions?.Snapshot.Leases ?? []);
        if (ids.Length == 0) return;
        config.LocalPreviewSavedMinions = ids;
        minionTrials?.RestoreSaved(ids);
        Cleanup(saveConfig, "保存宠物组");
    });

    private void LoadCompanions() => DispatchMutation(() =>
    {
        // Snapshot the one slot before any request can re-enter configuration saving.
        if (!enabled || !coexistence.Allowed || companions is null) return;
        // Saved groups are a local preview convenience. They may contain an item the
        // character does not own, so validate against the client catalog rather than
        // silently dropping it on load.
        var validCatalog = catalog.Search(PreviewKind.Minion, "").Select(x => x.Id).ToHashSet();
        var started = 0;
        var skipped = 0;
        var missing = CompanionSavedGroupPolicy.SelectMissing(config.LocalPreviewSavedMinions?.ToArray() ?? [],
            companions.Snapshot.Leases);
        foreach (var id in missing)
        {
            var before = companions.Snapshot.Leases.Count;
            if (validCatalog.Contains(id))
            {
                RequestCompanionCore(id, restoreSaved: true, publish: false);
                if (companions.Snapshot.Leases.Count > before) started++;
                else skipped++;
            }
            else skipped++;
        }
        lastMessage = skipped == 0 ? $"已请求召唤 {started} 只宠物" : $"已请求召唤 {started} 只宠物，另有 {skipped} 只暂时无法召唤。";
    });

    private void ClearSavedCompanions() => DispatchMutation(() =>
    {
        config.LocalPreviewSavedMinions = [];
        minionTrials?.ClearSaved();
        Cleanup(saveConfig, "清除宠物组");
    });

    private void CompanionCommand(string[] parts)
    {
        if (pluginInterface?.IsDev == true && parts is [_, "demo", "stop"])
        {
            CancelInteractionDemos();
            return;
        }
        if (pluginInterface?.IsDev == true && parts is [_, "demo", var kind, var demoID]
            && uint.TryParse(demoID, out var companionID))
        {
            if (kind == "gender") AddGenderInteraction(companionID);
            else if (kind == "enemy") AddBattleInteraction(companionID, CompanionBattleDemoAudience.Enemy);
            else if (kind == "player") AddBattleInteraction(companionID, CompanionBattleDemoAudience.Player);
            return;
        }
        if (TryDevelopmentIdleFollowCommand(parts)) return;
        if (TryDevelopmentFollowLeaseDiagnosticsCommand(parts)) return;
        if (pluginInterface?.IsDev == true && parts is [_, "follow", "off"])
            companions?.SetFollowDiagnostics(false, null, out _);
        else if (pluginInterface?.IsDev == true && parts is [_, "follow", "on", var followID] && uint.TryParse(followID, out var followCompanion))
        {
            if (companions is not null && !companions.SetFollowDiagnostics(true, followCompanion, out var reason))
                Log.Debug("[LocalPreview] Follow diagnostics unavailable: {Reason}", reason);
        }
        else if (pluginInterface?.IsDev == true && parts is [_, "names", "on" or "off"])
            SetShowCompanionNames(parts[2] == "on");
        else if (pluginInterface?.IsDev == true && parts is [_, "maximum", var maximum] && int.TryParse(maximum, out var maximumCount))
            SetMaximumCompanions(maximumCount);
        else if (parts is [_, "recall", var recall] && uint.TryParse(recall, out var recallId)) RecallCompanion(recallId);
        else if (parts is [_, "all"]) RecallCompanions();
        else if (parts is [_, "save"]) SaveCompanions();
        else if (parts is [_, "load"]) LoadCompanions();
        else if (pluginInterface?.IsDev == true && parts is [_, "clear"]) ClearSavedCompanions();
        else if (pluginInterface?.IsDev == true && parts is [_, "probe"])
            ProbeCompanionTargets(null);
        else if (pluginInterface?.IsDev == true && parts is [_, "probe", var probe] && uint.TryParse(probe, out var probeId))
            ProbeCompanionTargets(probeId);
        else if (pluginInterface?.IsDev == true && parts is [_, "target", var target] && uint.TryParse(target, out var targetId))
            SelectCompanionCore(targetId);
    }

    private void ProbeCompanionTargets(uint? id)
    {
        var state = companions?.ProbeTargets(id) ?? "unavailable";
        lastMessage = state switch
        {
            "captured" => "宠物点击检测已记录。",
            "rate-limited" => "请稍候再进行宠物点击检测。",
            "no-active-companion" => "没有可检测的已召唤宠物。",
            _ => "当前无法进行宠物点击检测。",
        };
        Chat.Print("[本地试用验证] " + lastMessage);
    }
}
