namespace FireGaze.PortraitPreview;

internal sealed record PortraitOpeningIssue(string Code, string Message);

/// <summary>A detached, player-facing snapshot. Reading it never reads or changes game state.</summary>
internal sealed partial record PortraitOpeningAvailability(bool CanOpenEditor, bool EditorAlreadyOpen, bool Opening,
    IReadOnlyList<PortraitOpeningIssue> Issues)
{
    internal static PortraitOpeningAvailability Unavailable { get; } = new(false, false, false,
        Array.AsReadOnly(new[] { Issue("StateUnavailable") }));
    public bool HasBlockers => Issues.Count > 0;
    public string PrimaryMessage => Issues.Count > 0 ? Issues[0].Message
        : Opening ? "正在打开铭牌，请稍候。" : EditorAlreadyOpen ? "铭牌编辑器已打开，可以启用 FireGaze 预览。" : "可以打开铭牌。";

    internal static IReadOnlyList<PortraitOpeningIssue> ConditionIssues(int[]? flags, bool editorOpen = false)
    {
        if (flags is not { Length: 112 } || flags.Any(flag => flag is not (0 or 1))) return [Issue("StateUnavailable")];
        var issues = new List<PortraitOpeningIssue>();
        // These indices are the current CN Dalamud ConditionFlag contract. Unknown values remain
        // blocked, with honest generic guidance; a condition is never guessed to mean GPose.
        foreach (var index in Enumerable.Range(0, flags.Length).Where(index => flags[index] == 1 && index != 1 && !(editorOpen && index == 100)))
        {
            var code = index switch
            {
                45 or 51 => "ChangingArea", 2 => "Unconscious", 26 => "InCombat", 27 or 87 => "Casting", 34 or 56 => "DutyBound",
                3 => "Emoting", 4 or 10 or 64 => "Mounted", 5 or 40 or 41 => "Crafting", 6 or 42 or 43 => "Gathering",
                7 => "Melding", 9 or 65 => "Carrying", 25 or 30 or 31 or 32 or 33 or 38 => "Occupied",
                35 or 58 or 78 => "Cutscene", 37 => "Trading", 39 => "Repairing", 48 or 61 => "Jumping",
                49 => "ChocoboTaxi", 50 => "RetainerInteraction", 53 => "LoggingOut", 57 => "MountTransition",
                67 => "Housing", 68 or 90 or 97 => "Transformed", 70 => "Moving", 77 => "Flying",
                80 or 81 => "Swimming", 92 or 93 => "WorldTravel", 94 => "FashionAccessory", 100 => "EditorLoading",
                _ => "RestrictedState",
            };
            issues.Add(Issue(code));
        }
        if (flags[1] != 1 && issues.Count == 0) issues.Add(Issue("CharacterLoading"));
        return issues;
    }

    internal static PortraitOpeningAvailability Finish(bool canOpen, bool editorOpen, bool opening, IEnumerable<PortraitOpeningIssue> issues)
    {
        var ordered = issues.GroupBy(issue => issue.Code).Select(group => group.First()).OrderBy(issue => Priority(issue.Code)).ToArray();
        // A known teleport action is more useful than the overlapping generic casting condition.
        if (ordered.Any(issue => issue.Code == "Teleporting")) ordered = ordered.Where(issue => issue.Code != "Casting").ToArray();
        return new(canOpen && ordered.Length == 0, editorOpen, opening, Array.AsReadOnly(ordered));
    }

    private static int Priority(string code) => code switch
    {
        "RestoreFailed" => 0, "SaveProtectionUnavailable" => 1, "ChangingArea" or "WorldTravel" or "LoggingOut" => 2,
        "CharacterLoading" or "Unconscious" => 3, "InCombat" => 4, "Teleporting" or "Casting" => 5,
        "Moving" => 6, "Restoring" => 20, "PhotosPending" or "PreviousOperationPending" => 21,
        "StateUnavailable" => 30, _ => 10,
    };

    internal static PortraitOpeningIssue Issue(string code) => new(code, code switch
    {
        "RestoreFailed" => "上一次预览尚未完全还原，正在重试。完成前请不要开始新的预览。",
        "SaveProtectionUnavailable" => "暂时无法防止临时外观被保存，因此没有开启预览。请稍后重试。",
        "ChangingArea" => "正在切换区域，请等加载完成并站稳后再打开铭牌。切图期间无法使用肖像同步预览。",
        "WorldTravel" => "正在跨服旅行，请等抵达目的地并站稳后再打开铭牌。",
        "LoggingOut" => "正在退出游戏，请下次登录并加载角色后再使用。",
        "CharacterLoading" => "正在读取角色，请等角色加载完成后再打开铭牌。",
        "Unconscious" => "角色倒下时无法使用，请复活后再试。",
        "InCombat" => "战斗中无法打开铭牌，请脱离战斗后再试。",
        "DutyBound" => "当前副本或任务场景暂不支持打开铭牌，请离开后再试。",
        "Teleporting" => "正在传送，请等传送完成并站稳后再打开铭牌。",
        "Casting" => "正在施法，请等动作结束后再打开铭牌。",
        "Moving" => "角色正在移动，请停下并站稳后再打开铭牌。",
        "Crafting" => "正在制作或准备制作，请结束制作后再打开铭牌。",
        "Gathering" => "正在采集或钓鱼，请结束当前采集后再打开铭牌。",
        "Mounted" => "正在骑乘，请下坐骑并站稳后再打开铭牌。",
        "Cutscene" => "正在观看剧情，请结束剧情后再打开铭牌。",
        "RetainerInteraction" => "正在使用传唤铃，请结束雇员操作后再打开铭牌。",
        "FashionAccessory" => "正在使用时尚配饰，请收起配饰后再打开铭牌。",
        "Emoting" => "正在进行情感动作，请结束动作后再打开铭牌。",
        "Melding" => "正在镶嵌魔晶石，请结束后再打开铭牌。",
        "Carrying" => "正在搬运物品，请放下物品后再打开铭牌。",
        "Occupied" => "正在进行其他交互，请结束当前操作后再打开铭牌。",
        "Trading" => "交易窗口已打开，请结束交易后再打开铭牌。",
        "Repairing" => "正在修理或处理物品，请结束后再打开铭牌。",
        "Jumping" => "角色尚未落地，请落地并站稳后再打开铭牌。",
        "ChocoboTaxi" => "正在乘坐陆行鸟出租，请抵达并下坐骑后再打开铭牌。",
        "MountTransition" => "正在切换骑乘或配饰状态，请等动作结束后再打开铭牌。",
        "Housing" => "正在使用房屋功能，请结束当前操作后再打开铭牌。",
        "Transformed" => "角色处于变身或特殊扮演状态，请恢复普通角色状态后再打开铭牌。",
        "Flying" => "角色正在飞行，请落地并下坐骑后再打开铭牌。",
        "Swimming" => "角色正在游泳或潜水，请回到地面后再打开铭牌。",
        "RestrictedState" => "当前角色状态暂不支持打开铭牌，请结束当前操作后再试。",
        "PreviewActive" => "预览正在进行中，请使用“打开预览窗口”。如需重新打开铭牌，请先结束当前预览。",
        "OtherEditorOpen" => "已打开其他肖像编辑器，请先完成或取消当前编辑，再打开铭牌。",
        "EditorLoading" => "铭牌编辑器正在加载或关闭，请稍候再试。",
        "Restoring" => "正在还原上一次预览，请稍候。完成前无法开始新的预览。",
        "PhotosPending" => "正在完成预览照片，请稍候再打开铭牌。",
        "PreviousOperationPending" => "正在完成上一项预览操作，请稍候再试。",
        "GearsetUnavailable" => "尚未识别当前套装，请先切换到一个已保存的套装后再试。",
        "OpeningTimeout" => "铭牌加载时间较长，请确认游戏加载完成，再重新打开铭牌。",
        "EditorChanged" => "打开过程中角色或编辑窗口发生了变化，本次未能开启预览。请站稳后重新打开铭牌。",
        "EditorDidNotOpen" => "铭牌没有成功打开，请稍后再试。",
        "OpeningInProgress" => "正在打开铭牌，请稍候。",
        "OpeningRequestExpired" => "这次打开请求已失效，请重新点击“打开铭牌”。",
        "OpeningUnavailable" => "本次运行暂时无法继续打开铭牌。请先结束预览并关闭编辑窗口，再重新加载 FireGaze。",
        _ => "暂时无法确认角色或铭牌状态，本次没有开启预览。请稍后重试。",
    });
}
