using System.Reflection;
using FireGaze.Translate;

internal static class ResponsiveListTests
{
    public static void Run(Action<bool, string> check)
    {
        var assembly = typeof(TranslationIndexEntry).Assembly;
        var layout = assembly.GetType("FireGaze.UI.PluginListLayout")!;
        var columns = layout.GetMethod("MeasureLegacyColumns")!;
        foreach (var sample in new (float Width, float Actions)[] { (544f, 410f), (684f, 610f), (1088f, 820f) })
        {
            var result = columns.Invoke(null, [sample.Width, 14f, sample.Actions])!;
            float Read(string name) => (float)result.GetType().GetProperty(name)!.GetValue(result)!;
            check(Read("Content") > 0 && Read("Actions") > 0
                && Read("Content") + Read("Actions") + Read("Gap") <= sample.Width + .001f,
                $"narrow plugin list keeps content and every action inside its {sample.Width}px column budget");
        }

        var flow = layout.GetMethod("MeasureActionFlow");
        check(flow is not null, "action flow measures wrapping before either real list paints its row");
        var positions = (Array)flow!.Invoke(null, [300f, new[] { 120f, 76f, 76f, 76f }, new[] { 0f, 12f, 12f, 10f }, false])!;
        var expected = new (float X, int Row)[] { (0f, 0), (132f, 0), (220f, 0), (0f, 1) };
        for (var i = 0; i < positions.Length; i++)
        {
            var position = positions.GetValue(i)!;
            check((float)position.GetType().GetProperty("X")!.GetValue(position)! == expected[i].X
                && (int)position.GetType().GetProperty("Row")!.GetValue(position)! == expected[i].Row,
                "translation action " + i + " wraps without being hidden or adding a blank row");
        }
        positions = (Array)flow.Invoke(null, [180f, new[] { 112f, 76f, 44f }, new[] { 0f, 8f, 8f }, true])!;
        expected = [(68f, 0), (52f, 1), (136f, 1)];
        for (var i = 0; i < positions.Length; i++)
        {
            var position = positions.GetValue(i)!;
            check((float)position.GetType().GetProperty("X")!.GetValue(position)! == expected[i].X
                && (int)position.GetType().GetProperty("Row")!.GetValue(position)! == expected[i].Row,
                "discovery action " + i + " stays right aligned within its own wrapped row");
        }

        var fit = assembly.GetType("FireGaze.UI.UiHelpers")!.GetMethod("FitText", BindingFlags.Static | BindingFlags.NonPublic);
        check(fit is not null, "fitted text uses the measured width instead of an eight-character floor");
        Func<string, float> measure = text => System.Globalization.StringInfo.ParseCombiningCharacters(text).Length * 17f;
        foreach (var sample in new (string Text, float Width, string Expected)[]
        {
            ("插件名称", 0f, ""), ("插件名称", 1f, ""),
            ("插件名称", 16f, ""), ("插件名称", 17f, "…"),
            ("插件名称", 34f, "插…"), ("插件名称", 68f, "插件名称"),
            ("A\U0001F525e\u0301B", 34f, "A…"),
        })
        {
            var text = (string)fit!.Invoke(null, [sample.Text, sample.Width, measure])!;
            check(text == sample.Expected && measure(text) <= sample.Width,
                $"fitted text stays within {sample.Width}px without splitting a text element");
        }

        using var product = dnlib.DotNet.ModuleDefMD.Load(assembly.Location);
        foreach (var sample in new (string Type, string Method, string Call)[]
        {
            ("FireGaze.UI.UITextTab", "DrawRowActions", "MeasureActionFlow"),
            ("FireGaze.UI.DiscoveryTab", "DrawRow", "MeasureActionFlow"),
            ("FireGaze.UI.UiHelpers", "Fitted", "FitText"),
        })
        {
            var method = product.GetTypes().Single(t => t.FullName == sample.Type).Methods.Single(m => m.Name == sample.Method);
            check(method.Body.Instructions.Any(i => i.Operand is dnlib.DotNet.IMethod called && called.Name == sample.Call),
                sample.Type + "." + sample.Method + " consumes the bounded layout instead of leaving it in an unused helper");
        }
    }
}
