using System.Numerics;
using Dalamud.Bindings.ImGui;
#if HAS_VISUAL_SYSTEM
using FireGaze.UI;
#endif

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException("FAIL " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}

#if !HAS_VISUAL_SYSTEM
Check(false, "the production palette, theme and symbol implementation is required");
#else
Check(UiHelpers.Muted == FireGazePalette.Muted && UiHelpers.Good == FireGazePalette.Good
    && UiHelpers.Warn == FireGazePalette.Warn && UiHelpers.Bad == FireGazePalette.Bad
    && UiHelpers.Accent == FireGazePalette.Action && UiHelpers.Skip == FireGazePalette.Skip,
    "existing status helpers share the approved semantic palette");
Check(InsStyle.Card == FireGazePalette.Panel && InsStyle.Neutral == FireGazePalette.Muted
    && InsStyle.Bright == FireGazePalette.Text && InsStyle.Pink == FireGazePalette.Action,
    "legacy list helper names no longer introduce a different brand or translucent text");
static double Luminance(Vector4 color)
{
    static double Channel(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    return .2126 * Channel(color.X) + .7152 * Channel(color.Y) + .0722 * Channel(color.Z);
}
static double Contrast(Vector4 left, Vector4 right)
{
    var a = Luminance(left);
    var b = Luminance(right);
    return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
}

foreach (var field in typeof(FireGazePalette).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
{
    if (field.FieldType != typeof(Vector4)) continue;
    var color = (Vector4)field.GetValue(null)!;
    Check(color.W == 1 && float.IsFinite(color.X) && float.IsFinite(color.Y) && float.IsFinite(color.Z)
        && color.X is >= 0 and <= 1 && color.Y is >= 0 and <= 1 && color.Z is >= 0 and <= 1,
        "semantic palette " + field.Name + " is opaque and finite");
}
foreach (var background in new[] { FireGazePalette.Window, FireGazePalette.Panel, FireGazePalette.Raised, FireGazePalette.Hover })
    Check(background.X == background.Y && background.Y == background.Z, "a color-judgment surface is truly achromatic");
foreach (var background in new[] { FireGazePalette.Window, FireGazePalette.Panel, FireGazePalette.Raised, FireGazePalette.Selected })
foreach (var foreground in new[] { FireGazePalette.Text, FireGazePalette.Muted, FireGazePalette.Good, FireGazePalette.Warn, FireGazePalette.Bad, FireGazePalette.Skip })
    Check(Contrast(foreground, background) >= 4.5, "body and status text meet contrast on every supported surface");
foreach (var background in new[] { FireGazePalette.Action, FireGazePalette.ActionHover, FireGazePalette.ActionActive, FireGazePalette.Good, FireGazePalette.Bad })
    Check(Contrast(FireGazePalette.White, background) >= 4.5, "filled semantic actions keep readable text");
Check(Contrast(FireGazePalette.Border, FireGazePalette.Panel) >= 3, "necessary control boundaries remain visible");

var roles = Enum.GetValues<ImGuiCol>().Where(value => value.ToString() is not "Count" and not "COUNT").Distinct().ToArray();
Check(FireGazeTheme.Colors.Select(entry => entry.Role).Distinct().Count() == roles.Length
    && roles.All(role => FireGazeTheme.Colors.Any(entry => entry.Role.Equals(role))),
    "every installed ImGui color is configured so host themes cannot seep through");
Check(FireGazeTheme.FloatStyles.Single(entry => entry.Role == ImGuiStyleVar.Alpha).Value == 1
    && FireGazeTheme.FloatStyles.Single(entry => entry.Role == ImGuiStyleVar.DisabledAlpha).Value == 1,
    "global and disabled alpha cannot erase disabled labels");
var dimensions = Enum.GetValues<ImGuiStyleVar>().Where(value => value.ToString() is not "Count" and not "COUNT").Distinct().ToArray();
Check(FireGazeTheme.FloatStyles.Select(entry => entry.Role).Concat(FireGazeTheme.VectorStyles.Select(entry => entry.Role)).Distinct().Count() == dimensions.Length,
    "all installed style dimensions have one fixed local configuration");

var stack = new RecordingStyleStack();
var host = stack.Snapshot();
using (FireGazeTheme.Push(stack, 1.25f))
{
    Check(stack.Colors[ImGuiCol.WindowBg] == FireGazePalette.Window, "theme is in effect before the drawing callback begins");
    Check(stack.Vectors[ImGuiStyleVar.ItemSpacing] == new Vector2(10, 5), "layout scales once with font size");
    var outer = stack.Snapshot();
    using (FireGazeTheme.Push(stack, 2f))
        Check(stack.Vectors[ImGuiStyleVar.ItemSpacing] == new Vector2(16, 8), "nested independent windows apply their own scale");
    Check(stack.Snapshot() == outer, "nested scope restores the surrounding FireGaze window exactly");
}
Check(stack.Snapshot() == host, "normal disposal restores all external colors and dimensions");
var callbackRan = false;
try
{
    FireGazeTheme.Draw(() =>
    {
        callbackRan = true;
        Check(stack.Colors[ImGuiCol.WindowBg] == FireGazePalette.Window, "window callback observes the theme before Begin");
        throw new ExpectedDrawFailure();
    }, stack, 1f);
}
catch (ExpectedDrawFailure) { }
Check(callbackRan && stack.Snapshot() == host, "a draw exception cannot leak theme state to another plugin");
var scope = FireGazeTheme.Push(stack, 1f);
scope.Dispose();
scope.Dispose();
Check(stack.Snapshot() == host && stack.ColorDepth == 0 && stack.VarDepth == 0, "repeated disposal does not over-pop host style stacks");
foreach (var failureAfter in new[] { 0, 7, roles.Length + 3 })
{
    stack.FailAtPush = failureAfter;
    stack.Pushes = 0;
    try { using var failed = FireGazeTheme.Push(stack, 1f); }
    catch (ExpectedPushFailure) { }
    Check(stack.Snapshot() == host && stack.ColorDepth == 0 && stack.VarDepth == 0, "partial initialization rolls back all successful pushes");
}
stack.FailAtPush = null;
foreach (var scale in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
{
    var rejected = false;
    try { using var invalid = FireGazeTheme.Push(stack, scale); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected && stack.Snapshot() == host, "invalid scale is rejected before touching style state");
}
#endif
DisabledStateTests.Run(Check);
Console.WriteLine($"VisualSystemTests: {checks} passed, 0 failed");

#if HAS_VISUAL_SYSTEM
sealed class ExpectedDrawFailure : Exception;
sealed class ExpectedPushFailure : Exception;
sealed class RecordingStyleStack : IFireGazeStyleStack
{
    public readonly Dictionary<ImGuiCol, Vector4> Colors = Enum.GetValues<ImGuiCol>().Distinct().ToDictionary(role => role, _ => new Vector4(.23f, .34f, .45f, .56f));
    public readonly Dictionary<ImGuiStyleVar, float> Floats = Enum.GetValues<ImGuiStyleVar>().Distinct().ToDictionary(role => role, _ => 7.25f);
    public readonly Dictionary<ImGuiStyleVar, Vector2> Vectors = Enum.GetValues<ImGuiStyleVar>().Distinct().ToDictionary(role => role, _ => new Vector2(9.5f, 8.5f));
    private readonly Stack<(ImGuiCol Role, Vector4 Value)> colors = new();
    private readonly Stack<(ImGuiStyleVar Role, float? Float, Vector2 Vector)> variables = new();
    public int ColorDepth => colors.Count;
    public int VarDepth => variables.Count;
    public int? FailAtPush;
    public int Pushes;
    public string Snapshot() => string.Join('|', Colors.OrderBy(entry => entry.Key).Select(entry => $"c{entry.Key}:{entry.Value}"))
        + string.Join('|', Floats.OrderBy(entry => entry.Key).Select(entry => $"f{entry.Key}:{entry.Value}"))
        + string.Join('|', Vectors.OrderBy(entry => entry.Key).Select(entry => $"v{entry.Key}:{entry.Value}"));
    private void BeforePush() { if (Pushes++ == FailAtPush) throw new ExpectedPushFailure(); }
    public void PushColor(ImGuiCol role, Vector4 value) { BeforePush(); colors.Push((role, Colors[role])); Colors[role] = value; }
    public void PushFloat(ImGuiStyleVar role, float value) { BeforePush(); variables.Push((role, Floats[role], default)); Floats[role] = value; }
    public void PushVector(ImGuiStyleVar role, Vector2 value) { BeforePush(); variables.Push((role, null, Vectors[role])); Vectors[role] = value; }
    public void PopColors(int count) { for (var i = 0; i < count; i++) { var entry = colors.Pop(); Colors[entry.Role] = entry.Value; } }
    public void PopVariables(int count) { for (var i = 0; i < count; i++) { var entry = variables.Pop(); if (entry.Float is { } value) Floats[entry.Role] = value; else Vectors[entry.Role] = entry.Vector; } }
}
#endif
