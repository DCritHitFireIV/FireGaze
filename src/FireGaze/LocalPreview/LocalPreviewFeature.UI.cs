using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.LocalPreview.Interaction;
using FireGaze.LocalPreview.Runtime;
using Lumina.Excel.Sheets;
using MenuKind = FireGaze.LocalPreview.Interaction.PreviewKind;

namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    private PreviewCatalog catalog = new([]);
    private readonly Dictionary<MenuKind, string> queries = new();
    private readonly Dictionary<MenuKind, IReadOnlyList<PreviewCatalogEntry>> filtered = new();

    private PreviewMenuOptions MenuOptions() => new(config.LocalPreviewItemMenusEnabled,
        config.LocalPreviewMountEnabled, config.LocalPreviewMinionEnabled,
        config.LocalPreviewAccessoryEnabled, config.LocalPreviewEmoteEnabled);

    private void CreateInteraction()
    {
        interaction = new PreviewInteractionService(ContextMenu, Chat, GameGUI, Objects, Data,
            ClientState, Interop, Log, Request, () => runtime?.CurrentNativeEmote, AddonLifecycle, MenuOptions);
        if (config.LocalPreviewEmoteEnabled) interaction.EnableNativeChatLinks();
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
        filtered.Clear();
    }

    private static string CategoryName(MenuKind kind) => kind switch
    {
        MenuKind.Mount => "坐骑", MenuKind.Minion => "宠物", MenuKind.Accessory => "时尚配饰", _ => "情感动作",
    };

    private static string ActionLabel(MenuKind kind) => kind switch
    {
        MenuKind.Mount => "试召唤坐骑", MenuKind.Minion => "试召唤宠物",
        MenuKind.Accessory => "试用时尚配饰", _ => "试用情感动作",
    };

    private void SetCategory(MenuKind kind, bool value)
        => OnFramework(() => { SetCategoryCore(kind, value); return true; });

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
        => OnFramework(() => { SetItemMenusCore(value); return true; });

    private void SetItemMenusCore(bool value)
    {
        if (disposed) return;
        config.LocalPreviewItemMenusEnabled = value;
        Cleanup(saveConfig, "保存物品右键开关");
    }

    public void DrawContent()
    {
        if (disposed) return;
        ImGui.TextWrapped("在本地预览坐骑、宠物、时尚配饰和情感动作。仅自己可见，不解锁收藏，不改变移动或跳跃规则。");
        var currentEnabled = enabled;
        if (ImGui.Checkbox("开启本地试用", ref currentEnabled)) SetEnabled(currentEnabled);
        var itemMenus = config.LocalPreviewItemMenusEnabled;
        if (ImGui.Checkbox("开启右键对物品的试穿", ref itemMenus)) SetItemMenus(itemMenus);
        ImGui.TextDisabled("支持背包及聊天中的收录物品，也支持右键玩家、场景宠物和情感动作提示。");
        foreach (var kind in Enum.GetValues<MenuKind>())
        {
            if (kind != MenuKind.Mount) ImGui.SameLine();
            var allowed = MenuOptions().Allows(kind, false);
            if (ImGui.Checkbox(CategoryName(kind) + "##enable", ref allowed)) SetCategory(kind, allowed);
        }
        ImGui.TextWrapped(lastMessage);
        if (!coexistence.Allowed) ImGui.TextWrapped(coexistence.Reason);
        if (ImGui.Button("结束全部试用")) Cancel();
        if (runtime is not null)
            foreach (var effect in runtime.ActiveEffects)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"结束{CategoryName((MenuKind)effect.Slot)}##{effect.Slot}")) runtime.Clear(effect.Slot);
            }
        ImGui.Separator();
        if (!initialized || runtime is null) { ImGui.TextWrapped("试用功能正在初始化，请稍后再试。"); return; }
        if (!ImGui.BeginTabBar("###LocalPreviewCatalog")) return;
        foreach (var kind in Enum.GetValues<MenuKind>())
        {
            if (!ImGui.BeginTabItem(CategoryName(kind))) continue;
            DrawCatalog(kind);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawCatalog(MenuKind kind)
    {
        ImGui.PushID((int)kind);
        var query = queries.GetValueOrDefault(kind, "");
        ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 9));
        if (ImGui.InputTextWithHint("##search", "搜索名称或编号", ref query, 160) || !filtered.ContainsKey(kind))
        {
            queries[kind] = query;
            filtered[kind] = catalog.Search(kind, query);
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{filtered[kind].Count} 项");
        if (kind == MenuKind.Emote) ImGui.TextDisabled("列出当前客户端含可播放动作资源的全部命名情感动作。");
        ImGui.BeginDisabled(!enabled || !MenuOptions().Allows(kind, false) || !coexistence.Allowed);
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("###catalog", 2, flags, new Vector2(0, Math.Max(100, ImGui.GetContentRegionAvail().Y))))
        {
            ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("试用", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(ActionLabel(kind)).X + ImGui.GetStyle().FramePadding.X * 2);
            var clipper = new ImGuiListClipper();
            clipper.Begin(filtered[kind].Count, ImGui.GetFrameHeight() + ImGui.GetStyle().CellPadding.Y * 2);
            while (clipper.Step())
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var entry = filtered[kind][i];
                    ImGui.PushID((int)entry.Id);
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted($"{entry.Name}  #{entry.Id}");
                    ImGui.TableNextColumn();
                    if (ImGui.Button(ActionLabel(kind))) Request(kind, entry.Id);
                    ImGui.PopID();
                }
            ImGui.EndTable();
        }
        ImGui.EndDisabled();
        ImGui.PopID();
    }
}
