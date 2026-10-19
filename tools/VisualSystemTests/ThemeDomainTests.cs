using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

internal static class ThemeDomainTests
{
    public static void Run(Action<bool, string> check)
    {
        // Catches both incomplete foreground overrides and helpers that cache a color
        // from whichever domain happened to initialize their type first.
        var stack = new RecordingStyleStack();
        var external = stack.Snapshot();
        var defaultMuted = UiHelpers.Muted;
        using (FireGazeTheme.Push(stack, 1f))
        {
            var main = stack.Snapshot();
            var mainText = InsStyle.Bright;
            check(Luminance(stack.Colors[ImGuiCol.WindowBg]) < .08,
                "information windows present a low luminance surface before Begin");
            CheckConsumers(stack, check, "information");
            using (FireGazeTheme.PushPortrait(stack))
            {
                check(Luminance(stack.Colors[ImGuiCol.WindowBg]) > .9,
                    "portrait windows and floating tools retain a white work surface");
                check(UiHelpers.Muted != defaultMuted && InsStyle.Bright != mainText,
                    "real status and list consumers resolve the active portrait domain instead of cached main colors");
                CheckConsumers(stack, check, "portrait");
                DisabledStateTests.Run(check);
                var portrait = stack.Snapshot();
                using (FireGazeTheme.Push(stack, 1f))
                {
                    CheckConsumers(stack, check, "nested independent information window");
                    check(Luminance(stack.Colors[ImGuiCol.WindowBg]) < .08,
                        "an information window nested in portrait drawing gets its own domain");
                }
                check(stack.Snapshot() == portrait,
                    "ending nested information drawing restores the complete portrait color stack");
                CheckConsumers(stack, check, "restored portrait");
                using (FireGazeTheme.PushPortrait(stack))
                    CheckConsumers(stack, check, "nested portrait floating tool");
                check(stack.Snapshot() == portrait,
                    "a nested portrait tool restores the surrounding portrait domain");
                foreach (var failAt in new[] { 0, 7, FireGazeTheme.Colors.Count + 3 })
                {
                    stack.FailAtPush = failAt;
                    stack.Pushes = 0;
                    var threw = false;
                    try { using var partial = FireGazeTheme.Push(stack, 1f); }
                    catch (ExpectedPushFailure) { threw = true; }
                    check(threw && stack.Snapshot() == portrait && FireGazePalette.CurrentDomain == FireGazeColorDomain.Portrait
                        && UiHelpers.Muted == FireGazePalette.ForDomain(FireGazeColorDomain.Portrait).Muted,
                        "a partially pushed information window restores the enclosing white portrait domain");
                }
                stack.FailAtPush = null;
            }
            check(stack.Snapshot() == main && UiHelpers.Muted == defaultMuted && InsStyle.Bright == mainText,
                "closing portrait drawing restores both native and semantic information colors");
            try
            {
                using var portrait = FireGazeTheme.PushPortrait(stack);
                throw new ExpectedDrawFailure();
            }
            catch (ExpectedDrawFailure) { }
            check(stack.Snapshot() == main && UiHelpers.Muted == defaultMuted,
                "a portrait draw exception restores the information palette for the next window");
            foreach (var failAt in new[] { 0, 7, 14 })
            {
                stack.FailAtPush = failAt;
                stack.Pushes = 0;
                var threw = false;
                try { using var partial = FireGazeTheme.PushPortrait(stack); }
                catch (ExpectedPushFailure) { threw = true; }
                check(threw && stack.Snapshot() == main && UiHelpers.Muted == defaultMuted,
                    "a partially pushed portrait scope rolls back native colors and semantic domain");
            }
            stack.FailAtPush = null;
            var once = FireGazeTheme.PushPortrait(stack);
            once.Dispose(); once.Dispose();
            check(stack.Snapshot() == main && UiHelpers.Muted == defaultMuted,
                "repeated portrait disposal cannot pop its information parent twice");
        }
        check(stack.Snapshot() == external && stack.ColorDepth == 0 && stack.VarDepth == 0,
            "both domains restore the unrelated caller style and dimensions");
        var mainMuted = UiHelpers.Muted;
        using (FireGazePalette.Enter(FireGazeColorDomain.Portrait))
        {
            var workerMuted = Task.Run(() => UiHelpers.Muted).GetAwaiter().GetResult();
            check(workerMuted == mainMuted && UiHelpers.Muted != workerMuted,
                "managed palette scopes stay on the UI thread instead of leaking into worker consumers");
        }
    }

    private static void CheckConsumers(RecordingStyleStack stack, Action<bool, string> check, string domain)
    {
        var surfaces = new[] { ImGuiCol.WindowBg, ImGuiCol.ChildBg, ImGuiCol.PopupBg,
            ImGuiCol.FrameBg, ImGuiCol.FrameBgHovered, ImGuiCol.FrameBgActive, ImGuiCol.Header };
        var consumers = new (string Name, Vector4 Color)[]
        {
            ("body", stack.Colors[ImGuiCol.Text]), ("native disabled", stack.Colors[ImGuiCol.TextDisabled]),
            ("muted detail", UiHelpers.Muted), ("ready status", UiHelpers.Good), ("warning", UiHelpers.Warn),
            ("failure", UiHelpers.Bad), ("information link", UiHelpers.Info), ("skipped status", UiHelpers.Skip),
            ("list caption", InsStyle.Neutral), ("list name", InsStyle.Bright), ("liked mark", InsStyle.LikedRed),
        };
        foreach (var (name, foreground) in consumers)
            check(surfaces.All(role => Contrast(foreground, stack.Colors[role]) >= 4.5),
                $"{domain}: actual {name} consumer stays readable across its information surfaces");
        check(Contrast(InsStyle.AvatarText, InsStyle.CardActive) >= 4.5,
            $"{domain}: the real missing-icon avatar keeps readable lettering");
        foreach (var style in Enum.GetValues<FireGazeButtonStyle>())
        {
            var colors = UiHelpers.SemanticButtonColors(style, false);
            check(new[] { colors.Fill, colors.Hover, colors.Active }.All(fill => Contrast(colors.Text, fill) >= 4.5),
                $"{domain}: {style} labels stay readable across native button states");
            colors = UiHelpers.SemanticButtonColors(style, true);
            check(Contrast(colors.Text, colors.Fill) >= 4.5,
                $"{domain}: {style} disabled helper keeps readable text");
        }
        foreach (var role in new[] { ImGuiCol.CheckMark, ImGuiCol.SliderGrab, ImGuiCol.SliderGrabActive })
            check(new[] { ImGuiCol.FrameBg, ImGuiCol.FrameBgHovered, ImGuiCol.FrameBgActive }
                .All(surface => Contrast(stack.Colors[role], stack.Colors[surface]) >= 3),
                $"{domain}: {role} remains visible on the actual input fills");
    }

    private static double Luminance(Vector4 color)
    {
        static double Linear(double x) => x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4);
        return .2126 * Linear(color.X) + .7152 * Linear(color.Y) + .0722 * Linear(color.Z);
    }
    private static double Contrast(Vector4 left, Vector4 right)
    {
        var a = Luminance(left); var b = Luminance(right);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
