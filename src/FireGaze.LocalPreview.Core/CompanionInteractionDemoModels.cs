using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace FireGaze.LocalPreview.Core;

/// <summary>Player-facing categories whose actions can be previewed locally.</summary>
public enum CompanionInteractionDemoKind
{
    ShoulderLong,
    ShoulderOnSummon,
    ShoulderLeavesOnMove,
    Swim,
    SummonPerformance,
    StaticUnderfoot,
    StaticFront,
    StaticHeight,
    PairInteraction,
    SameSpecies,
    FollowDistance,
    GenderSpecial,
    BattleAction,
}

/// <summary>Audience represented by a battle-action demo. These are not combat targets.</summary>
[Flags]
public enum CompanionBattleDemoAudience
{
    None = 0,
    Enemy = 1,
    Player = 2,
}

/// <summary>Role gender read from the character snapshot. The demo never changes it.</summary>
public enum CompanionCharacterGender
{
    Male,
    Female,
    Unknown,
}

/// <summary>Catalog row supplied by the host from the complete client/Wiki mapping.</summary>
public sealed record CompanionInteractionDemoEntry(
    uint CompanionId,
    string Name,
    CompanionInteractionDemoKind Kind,
    CompanionBattleDemoAudience BattleAudience)
{
    public uint Icon { get; init; }
    /// <summary>
    /// Detached source evidence for this row. It is descriptive only; queueing or rendering
    /// still requires a separately admitted native consumer and visual readback.
    /// </summary>
    public CompanionSourceInteractionRule? SourceInteractionRule { get; init; }
}

/// <summary>
/// Explicit execution contract for a local preview action. All values are deliberately
/// affirmative so a future native adapter cannot accidentally turn a demo into combat.
/// </summary>
public readonly record struct CompanionInteractionDemoExecution(
    bool IsLocalOnly,
    bool EntersCombat,
    bool UsesHostileTargetApi);

/// <summary>Detached request returned to UI and scheduling layers.</summary>
public readonly record struct CompanionInteractionDemoRequest(
    long RequestId,
    uint CompanionId,
    string Name,
    CompanionInteractionDemoKind Kind,
    CompanionBattleDemoAudience Audience,
    CompanionCharacterGender CharacterGender,
    bool ForcedPreview,
    string Description,
    CompanionInteractionDemoExecution Execution)
{
    public bool PlaybackRequested { get; init; } = true;
}

public readonly record struct CompanionInteractionDemoRequestResult(
    bool Accepted,
    CompanionInteractionDemoRequest? Request,
    string Reason)
{
    public static CompanionInteractionDemoRequestResult Rejected(string reason)
        => new(false, null, reason);
}

/// <summary>
/// A detached audit of the interaction rows known to the current client catalog.
/// Missing rows remain visible to diagnostics and never become guessed capabilities.
/// </summary>
public sealed record CompanionInteractionCatalogCoverage(
    IReadOnlyDictionary<CompanionInteractionDemoKind, IReadOnlyList<string>> ExpectedNames,
    IReadOnlyDictionary<CompanionInteractionDemoKind, IReadOnlyList<string>> ResolvedNames,
    IReadOnlyList<string> MissingNames)
{
    public bool IsComplete => MissingNames.Count == 0;
}

/// <summary>
/// Native-free catalog and request factory for special interaction demonstrations.
/// The host is responsible for loading the full mapping; this type never guesses missing IDs.
/// </summary>
public sealed class CompanionInteractionDemoCatalog
{
    public const string FemaleConditionDescription = "若玩家角色性别为女性，则会有特殊交互动作。";
    public const string EnemyBattleDescription = "召唤以下宠物进入战斗时，宠物会对敌人有战斗的交互动作。";
    public const string PlayerBattleDescription = "召唤以下宠物进入战斗时，宠物会对玩家有应援的交互动作。";

    private readonly CompanionInteractionDemoEntry[] entries;
    private long nextRequestId;

    public CompanionInteractionDemoCatalog(IEnumerable<CompanionInteractionDemoEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        this.entries = entries
            .Where(entry => entry.CompanionId != 0 && !string.IsNullOrWhiteSpace(entry.Name)
                && Enum.IsDefined(entry.Kind))
            .Select(entry => entry with { Name = entry.Name.Trim() })
            .DistinctBy(entry => (entry.CompanionId, entry.Kind, entry.BattleAudience))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCulture)
            .ThenBy(entry => entry.CompanionId)
            .ToArray();
    }

    public IReadOnlyList<CompanionInteractionDemoEntry> Entries
        => Array.AsReadOnly(entries);

    public IReadOnlyList<CompanionInteractionDemoEntry> Search(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        if (normalized.Length == 0) return Entries;
        return entries
            .Where(entry => entry.Name.Contains(normalized, StringComparison.CurrentCultureIgnoreCase)
                || string.Equals(entry.CompanionId.ToString(CultureInfo.InvariantCulture), normalized,
                    StringComparison.Ordinal))
            .ToArray();
    }

    public CompanionInteractionDemoRequestResult RequestGenderPreview(
        uint companionId, CompanionCharacterGender characterGender)
    {
        if (!Enum.IsDefined(characterGender))
            return CompanionInteractionDemoRequestResult.Rejected("character gender is unknown");
        var entry = entries.FirstOrDefault(candidate => candidate.CompanionId == companionId
            && candidate.Kind == CompanionInteractionDemoKind.GenderSpecial);
        if (entry is null)
            return CompanionInteractionDemoRequestResult.Rejected("companion is not a gender interaction entry");

        // Male characters use an explicit local preview so they can see the action that
        // the native game normally gates to female characters. No role data is changed.
        var request = new CompanionInteractionDemoRequest(
            checked(++nextRequestId), entry.CompanionId, entry.Name, entry.Kind,
            CompanionBattleDemoAudience.None, characterGender,
            characterGender != CompanionCharacterGender.Female,
            FemaleConditionDescription,
            LocalPreviewExecution());
        return new(true, request, string.Empty);
    }

    public CompanionInteractionDemoRequestResult RequestBattleDemo(
        uint companionId, CompanionBattleDemoAudience audience)
    {
        if (audience is not (CompanionBattleDemoAudience.Enemy or CompanionBattleDemoAudience.Player))
            return CompanionInteractionDemoRequestResult.Rejected("battle demo audience is invalid");
        var entry = entries.FirstOrDefault(candidate => candidate.CompanionId == companionId
            && candidate.Kind == CompanionInteractionDemoKind.BattleAction
            && candidate.BattleAudience.HasFlag(audience));
        if (entry is null)
            return CompanionInteractionDemoRequestResult.Rejected("companion does not support this battle demo");

        var request = new CompanionInteractionDemoRequest(
            checked(++nextRequestId), entry.CompanionId, entry.Name, entry.Kind,
            audience, CompanionCharacterGender.Unknown, false,
            audience == CompanionBattleDemoAudience.Enemy ? EnemyBattleDescription : PlayerBattleDescription,
            LocalPreviewExecution());
        return new(true, request, string.Empty);
    }

    private static CompanionInteractionDemoExecution LocalPreviewExecution()
        => new(IsLocalOnly: true, EntersCombat: false, UsesHostileTargetApi: false);
}

/// <summary>
/// Builds the demo catalog from the current client's localized companion rows.  IDs are
/// deliberately resolved at runtime: the same name can have a different row id between
/// client data revisions, while an unknown name stays absent instead of being guessed.
/// </summary>
public static class CompanionInteractionDemoCatalogFactory
{
    private static readonly HashSet<string> GenderNames = new(StringComparer.CurrentCulture)
    {
        "迷你奥尔特罗斯", "迷你玛格奈",
    };

    private static readonly HashSet<string> EnemyNames = new(StringComparer.CurrentCulture)
    {
        "长须小豹", "小狼", "迷你凯因", "长须小黑豹", "自律型魔导浮游炮",
        "格斯塔拉", "冬贝利玩偶", "迷你埃斯蒂尼安",
    };

    private static readonly HashSet<string> PlayerNames = new(StringComparer.CurrentCulture)
    {
        "拉拉队小姑娘",
    };

    private static readonly (CompanionInteractionDemoKind Kind, string[] Names)[] InteractionGroups =
    [
        (CompanionInteractionDemoKind.ShoulderLong, [
            "奥猴宝宝", "迷你娜娜莫", "狐獴", "梦魔娃娃", "尘世幻龙", "暗淡之云", "幼龙玩偶",
            "猪小仙", "擦擦羽衣精", "乌洛萨帕", "斯卡拉乌洛萨帕"]),
        (CompanionInteractionDemoKind.ShoulderOnSummon, [
            "食果花鼠", "小熊猫", "幻栗斋", "猫头小鹰", "风筝小猫", "迷你奥尔·迪", "无齿翼龙幼崽",
            "海鹦", "小绿鸟", "乌小鸦", "尼姆乌洛萨帕", "小树袋熊", "青鸟", "不说话的蜂鸟",
            "蓝闪蝶", "旅雀儿", "银山雀儿"]),
        (CompanionInteractionDemoKind.ShoulderLeavesOnMove, [
            "浅瞳", "白鼬", "鼯鼠", "猎鹰", "迦楼罗之形", "朱孔雀", "海小鸥", "游星鸟",
            "小小巨嘴鸟", "迷雾秃鹰"]),
        (CompanionInteractionDemoKind.Swim, [
            "暗斗鱼苗", "聪慧小海豚", "小小海牛", "软软的海兽", "多玛金鱼儿", "铃小龙", "鳐鳐",
            "小甚平", "莫莫拉·莫拉！！！", "沧小龙", "人鱼公主"]),
        (CompanionInteractionDemoKind.SummonPerformance, ["神龙玩偶"]),
        (CompanionInteractionDemoKind.StaticUnderfoot, ["软软垫子"]),
        (CompanionInteractionDemoKind.StaticFront, ["绵绵坐垫", "浪人的篝火"]),
        (CompanionInteractionDemoKind.StaticHeight, ["冒险者野餐篮", "发条太阳", "发条月亮"]),
        (CompanionInteractionDemoKind.PairInteraction, [
            "长须小豹", "小狼", "长须小黑豹", "发条巴哈姆特", "迷你路易索瓦", "迷你埃斯蒂尼安",
            "迷你尼德霍格", "吉多", "迷你艾克斯迪司", "神龙玩偶", "迷你欧米茄", "迷你玛格奈",
            "迷你纱都", "迷你欧米茄M", "迷你欧米茄F", "迷你吉坦", "迷你嘉妮特", "迷你莱楠",
            "猫秘玩偶", "迷你鲁纳尔", "迷你第一世界雅·修特拉", "海德林偶像", "佐迪亚克偶像",
            "迷你普罗姆", "迷你帕罗姆", "普萨玛忒", "那伊阿德", "得摩斯"]),
        (CompanionInteractionDemoKind.SameSpecies, [
            "狛犬", "朱雀机关人偶", "布莉娜", "卡尔克", "莫古力娃娃", "邮差莫古力娃娃", "萌神乐团",
            "道士莫古力娃娃", "花篮莫古力"]),
        (CompanionInteractionDemoKind.FollowDistance, ["鲸头鹳"]),
    ];

    /// <summary>
    /// The reviewed client/Wiki mapping expressed as data, so every row can be audited
    /// against the localized catalog supplied by the current client. Callers receive
    /// read-only copies and cannot mutate the mapping used by the factory.
    /// </summary>
    public static IReadOnlyDictionary<CompanionInteractionDemoKind, IReadOnlyList<string>> ExpectedInteractionNames
        => BuildExpectedNames();

    public static CompanionInteractionCatalogCoverage MeasureCoverage(
        IEnumerable<CompanionInteractionDemoEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var expected = BuildExpectedNames();
        var normalized = entries
            .Select(entry => (entry.Kind, Name: NormalizeName(entry.Name)))
            .Where(entry => entry.Name.Length != 0)
            .ToHashSet();
        var resolved = new Dictionary<CompanionInteractionDemoKind, IReadOnlyList<string>>();
        var missing = new List<string>();
        foreach (var (kind, names) in expected)
        {
            var found = names.Where(name => normalized.Contains((kind, NormalizeName(name)))).ToArray();
            resolved[kind] = Array.AsReadOnly(found);
            foreach (var name in names)
                if (!normalized.Contains((kind, NormalizeName(name)))) missing.Add($"{kind}:{name}");
        }
        return new(expected,
            new ReadOnlyDictionary<CompanionInteractionDemoKind, IReadOnlyList<string>>(resolved),
            Array.AsReadOnly(missing.ToArray()));
    }

    public static CompanionInteractionDemoCatalog FromClientNames(
        IEnumerable<CompanionTrialCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var mapped = new List<CompanionInteractionDemoEntry>();
        void Add(CompanionTrialCatalogEntry source, CompanionInteractionDemoKind kind,
            CompanionBattleDemoAudience audience = CompanionBattleDemoAudience.None)
            => mapped.Add(new(source.CompanionId, source.Name, kind, audience)
            {
                SourceInteractionRule = source.SourceInteractionRule,
            });

        foreach (var entry in entries)
        {
            var normalized = NormalizeName(entry.Name);
            if (GenderNames.Any(name => NormalizeName(name) == normalized))
                Add(entry, CompanionInteractionDemoKind.GenderSpecial);
            if (EnemyNames.Any(name => NormalizeName(name) == normalized))
                Add(entry, CompanionInteractionDemoKind.BattleAction, CompanionBattleDemoAudience.Enemy);
            if (PlayerNames.Any(name => NormalizeName(name) == normalized))
                Add(entry, CompanionInteractionDemoKind.BattleAction, CompanionBattleDemoAudience.Player);
            foreach (var (kind, names) in InteractionGroups)
                if (names.Any(name => NormalizeName(name) == normalized))
                    Add(entry, kind);
        }
        return new(mapped);
    }

    private static IReadOnlyDictionary<CompanionInteractionDemoKind, IReadOnlyList<string>> BuildExpectedNames()
    {
        var groups = new Dictionary<CompanionInteractionDemoKind, List<string>>();
        void Add(CompanionInteractionDemoKind kind, IEnumerable<string> names)
        {
            if (!groups.TryGetValue(kind, out var list)) groups[kind] = list = [];
            foreach (var name in names.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim()).Distinct(StringComparer.CurrentCulture)) list.Add(name);
        }
        Add(CompanionInteractionDemoKind.GenderSpecial, GenderNames);
        Add(CompanionInteractionDemoKind.BattleAction, EnemyNames);
        Add(CompanionInteractionDemoKind.BattleAction, PlayerNames);
        foreach (var (kind, names) in InteractionGroups) Add(kind, names);
        return new ReadOnlyDictionary<CompanionInteractionDemoKind, IReadOnlyList<string>>(groups.ToDictionary(x => x.Key,
            x => (IReadOnlyList<string>)Array.AsReadOnly(x.Value
                .OrderBy(name => name, StringComparer.CurrentCulture).ToArray())));
    }

    private static string NormalizeName(string? name)
        => string.Concat((name ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC)
            .Where(character => !char.IsWhiteSpace(character) && character is not '·' and not '・'));
}

/// <summary>
/// Owns explicit demo requests only. Real combat is observed outside this model and
/// immediately clears the local queue; adding a demo never mutates combat state.
/// </summary>
public sealed class CompanionInteractionDemoSession
{
    private readonly CompanionInteractionDemoCatalog catalog;
    private readonly List<CompanionInteractionDemoRequest> active = [];

    public CompanionInteractionDemoSession(CompanionInteractionDemoCatalog catalog)
        => this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public IReadOnlyList<CompanionInteractionDemoRequest> Active
        => Array.AsReadOnly(active.ToArray());

    public CompanionInteractionDemoRequestResult AddGenderPreview(
        uint companionId, CompanionCharacterGender characterGender)
        => Add(catalog.RequestGenderPreview(companionId, characterGender));

    public CompanionInteractionDemoRequestResult AddBattleDemo(
        uint companionId, CompanionBattleDemoAudience audience)
        => Add(catalog.RequestBattleDemo(companionId, audience));

    public bool Remove(long requestId)
    {
        var index = active.FindIndex(request => request.RequestId == requestId);
        if (index < 0) return false;
        active.RemoveAt(index);
        return true;
    }

    public bool PausePlayback(long requestID)
    {
        var index = active.FindIndex(request => request.RequestId == requestID);
        if (index < 0 || !active[index].PlaybackRequested) return false;
        active[index] = active[index] with { PlaybackRequested = false };
        return true;
    }

    public bool CancelForRealCombat()
    {
        if (active.Count == 0) return false;
        active.Clear();
        return true;
    }

    private CompanionInteractionDemoRequestResult Add(CompanionInteractionDemoRequestResult result)
    {
        if (result is not { Accepted: true, Request: { } request }) return result;
        var index = active.FindIndex(x => x.CompanionId == request.CompanionId
            && x.Kind == request.Kind && x.Audience == request.Audience);
        if (index >= 0) request = active[index] with { PlaybackRequested = !active[index].PlaybackRequested };
        if (request.PlaybackRequested)
            for (var i = 0; i < active.Count; i++) active[i] = active[i] with { PlaybackRequested = false };
        if (index >= 0) active[index] = request;
        else active.Add(request);
        return new(true, request, string.Empty);
    }
}

public static class CompanionInteractionDemoExecutionPolicy
{
    /// <summary>Local demos are never allowed to turn on the game's combat state.</summary>
    public static bool ShouldEnterCombat(IEnumerable<CompanionInteractionDemoRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return requests.Any(request => request.Execution.EntersCombat);
    }

    /// <summary>True combat is an external cancellation boundary for all local demos.</summary>
    public static bool ShouldCancelForRealCombat(bool inCombat) => inCombat;
}
