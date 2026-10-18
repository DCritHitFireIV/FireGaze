using System.Numerics;
using Dalamud.Bindings.ImGui;
#if HAS_DISABLED_SYSTEM
using FireGaze.UI;
#endif

internal static class DisabledStateTests
{
    public static void Run(Action<bool, string> check)
    {
#if !HAS_DISABLED_SYSTEM
        check(false, "disabled controls need the production state and neutral visual scope");
#else
        // Removing inherited state or neutral colors must fail these assertions;
        // the fake substitutes only native ImGui stack calls, not our state machine.
        var native = new RecordingDisabledStack();
        var state = new FireGazeDisabledState(native);
        var host = native.Snapshot();
        using (state.Push(false))
        {
            check(!state.IsDisabled && !native.IsDisabled && native.ColorDepth == 0,
                "false disabled scope preserves enabled appearance and native input");
            using (state.Push(true))
            {
                check(state.IsDisabled && native.IsDisabled,
                    "true nested scope blocks native input and exposes effective disabled state");
                check(native.Colors[ImGuiCol.Button] == FireGazePalette.Hover
                    && native.Colors[ImGuiCol.Text] == FireGazePalette.Muted,
                    "disabled scope replaces semantic fill with opaque neutral fill and readable text");
            }
            check(!state.IsDisabled && !native.IsDisabled && native.Snapshot() == host,
                "ending true nested scope restores enabled outer appearance");
        }
        check(native.Snapshot() == host && native.DisabledDepth == 0 && native.ColorDepth == 0,
            "normal disabled scope disposal balances all native state");

        using (state.Push(true))
        {
            var outer = native.Snapshot();
            using (state.Push(false))
                check(state.IsDisabled && native.IsDisabled && native.Snapshot() == outer,
                    "false nested scope cannot reenable its disabled parent");
            check(state.IsDisabled && native.Snapshot() == outer,
                "ending inherited scope keeps outer disabled colors and state");
            using (state.Push(true))
                check(state.IsDisabled, "repeated true disabled scopes stay disabled");
            check(native.Snapshot() == outer, "nested true scope restores exact outer palette");
        }
        check(!state.IsDisabled && native.Snapshot() == host,
            "nested disabled scopes restore host state after final disposal");

        foreach (var style in Enum.GetValues<FireGazeButtonStyle>())
        {
            var colors = UiHelpers.SemanticButtonColors(style, true);
            check(colors.Fill == FireGazePalette.Hover && colors.Hover == FireGazePalette.Hover
                && colors.Active == FireGazePalette.Hover && colors.Text == FireGazePalette.Muted,
                style + " helper inside disabled scope cannot restore a red green or blue call to action");
            check(colors.Fill.W == 1 && colors.Text.W == 1,
                style + " disabled helper remains opaque instead of fading labels");
        }
        check(UiHelpers.SemanticButtonColors(FireGazeButtonStyle.Primary, false).Fill == FireGazePalette.Action
            && UiHelpers.SemanticButtonColors(FireGazeButtonStyle.Enable, false).Fill == FireGazePalette.Good
            && UiHelpers.SemanticButtonColors(FireGazeButtonStyle.Danger, false).Fill == FireGazePalette.Bad,
            "enabled semantic buttons retain distinct approved action colors");

        // Semantic styling may be pushed before BeginDisabled, as the real delete toolbar does.
        native.PushColor(ImGuiCol.Button, FireGazePalette.Bad);
        var dangerBefore = native.Snapshot();
        using (state.Push(true))
            check(native.Colors[ImGuiCol.Button] == FireGazePalette.Hover,
                "disabled scope overrides a danger color pushed before it");
        check(native.Snapshot() == dangerBefore,
            "ending disabled scope restores preceding semantic button stack");
        native.PopColors(1);

        try
        {
            using var failedDraw = state.Push(true);
            throw new ExpectedDisabledFailure();
        }
        catch (ExpectedDisabledFailure) { }
        check(!state.IsDisabled && native.Snapshot() == host && native.DisabledDepth == 0,
            "using disabled scope restores native and visual state after a draw exception");

        var once = state.Push(true);
        once.Dispose();
        once.Dispose();
        check(native.Snapshot() == host && native.DisabledDepth == 0 && native.ColorDepth == 0,
            "repeated disabled scope disposal does not pop host state twice");

        foreach (var failAfter in new[] { 0, 3, 13 })
        {
            native.FailAtColorPush = failAfter;
            native.ColorPushes = 0;
            var threw = false;
            try { using var failed = state.Push(true); }
            catch (ExpectedDisabledFailure) { threw = true; }
            check(threw && !state.IsDisabled && native.Snapshot() == host
                && native.DisabledDepth == 0 && native.ColorDepth == 0,
                "partial disabled palette failure rolls back successful colors and native Begin");
        }
        native.FailAtColorPush = null;

        native.FailAtBegin = true;
        var beginFailed = false;
        try { using var failed = state.Push(true); }
        catch (ExpectedDisabledFailure) { beginFailed = true; }
        native.FailAtBegin = false;
        check(beginFailed && !state.IsDisabled && native.Snapshot() == host && native.DisabledDepth == 0,
            "failed native Begin does not invent a disabled scope or pop unrelated state");

        var endRejected = false;
        try { state.End(); }
        catch (InvalidOperationException) { endRejected = true; }
        check(endRejected && native.DisabledDepth == 0 && native.ColorDepth == 0,
            "unbalanced End is rejected before touching native stacks");

        state.Begin(true);
        state.Begin(false);
        state.End();
        check(state.IsDisabled && native.IsDisabled,
            "manual Begin End API inherits disabled parent exactly like using scope");
        state.End();
        check(!state.IsDisabled && native.Snapshot() == host,
            "manual disabled API restores host after balanced calls");
#endif
    }

#if HAS_DISABLED_SYSTEM
    private sealed class ExpectedDisabledFailure : Exception;

    private sealed class RecordingDisabledStack : IFireGazeDisabledStack
    {
        public readonly Dictionary<ImGuiCol, Vector4> Colors = Enum.GetValues<ImGuiCol>().Distinct()
            .ToDictionary(role => role, _ => new Vector4(.23f, .34f, .45f, 1f));
        private readonly Stack<(ImGuiCol Role, Vector4 Value)> colors = new();
        private readonly Stack<bool> disabled = new();
        public int? FailAtColorPush;
        public int ColorPushes;
        public bool FailAtBegin;
        public bool IsDisabled => disabled.TryPeek(out var current) && current;
        public int DisabledDepth => disabled.Count;
        public int ColorDepth => colors.Count;
        public string Snapshot() => string.Join('|', Colors.OrderBy(entry => entry.Key)
            .Select(entry => $"{entry.Key}:{entry.Value}"));
        public void BeginDisabled(bool value)
        {
            if (FailAtBegin) throw new ExpectedDisabledFailure();
            disabled.Push(value || IsDisabled);
        }
        public void EndDisabled() => disabled.Pop();
        public void PushColor(ImGuiCol role, Vector4 value)
        {
            if (ColorPushes++ == FailAtColorPush) throw new ExpectedDisabledFailure();
            colors.Push((role, Colors[role]));
            Colors[role] = value;
        }
        public void PopColors(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var prior = colors.Pop();
                Colors[prior.Role] = prior.Value;
            }
        }
    }
#endif
}
