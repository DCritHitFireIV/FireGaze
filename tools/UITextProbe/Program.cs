using FireGaze.UIText;

// 批量落盘时 stdout 可能被重定向：强制 UTF-8，否则中文按系统 ANSI 码页写出去就不是合法 JSON 了。
Console.OutputEncoding = System.Text.Encoding.UTF8;

// 离线看抽取器对某个插件 DLL 的判定：
//   dotnet run --project tools/UITextProbe -- <插件.dll> [--all] [--json]
// 默认只打印 UI 候选 + 灰名单；--all 连排除的一起看（调规则用）。

var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var showAll = args.Contains("--all");
var asJson = args.Contains("--json");

if (positional.Length == 0)
{
    Console.WriteLine("用法：UITextProbe <插件.dll> [--all] [--json]");
    return 1;
}

if (args.Contains("--trace"))
{
    UIStringExtractor.Trace = Console.Error.WriteLine;

    // --trace <关键词>：只打这个方法的逐条 IL（[il]），其余方法只打 [ui]/[danger] 汇总
    var traceIndex = Array.IndexOf(args, "--trace");
    if (traceIndex + 1 < args.Length && !args[traceIndex + 1].StartsWith("--", StringComparison.Ordinal))
    {
        UIStringExtractor.TraceKey = args[traceIndex + 1];
    }
}

if (args.Length >= 3 && args[1] == "--types")
{
    // 调试用：列出类型/方法原始名（看编译器生成名与上下文映射）
    using var module = dnlib.DotNet.ModuleDefMD.Load(positional[0]);
    foreach (var type in module.GetTypes())
    {
        foreach (var method in type.Methods)
        {
            if (type.Name.String.Contains(args[2], StringComparison.OrdinalIgnoreCase)
                || method.Name.String.Contains(args[2], StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{type.Name} :: {method.Name}");
            }
        }
    }

    return 0;
}

if (args.Length >= 4 && args[1] == "--predicate")
{
    var type = args[2];
    var method = args[3];
    Console.WriteLine($"type      = {type}");
    Console.WriteLine($"method    = {method}");
    Console.WriteLine($"IsLog     = {UICallSemantics.IsLogCall(type, method)}");
    Console.WriteLine($"IsProducer= {UICallSemantics.IsStringProducer(type, method)}");
    Console.WriteLine($"IsUI      = {UICallSemantics.IsUICall(type, method)}");
    Console.WriteLine($"IsDanger  = {UICallSemantics.IsDangerousCall(type, method)}");
    return 0;
}

if (args.Length >= 2 && args[1] == "--strings")
{
    // 调试用：把程序集里所有 ldstr 字面量打出来（对比两个版本被人改了什么）
    using var module = dnlib.DotNet.ModuleDefMD.Load(positional[0]);
    var all = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var type in module.GetTypes())
    {
        foreach (var method in type.Methods)
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code == dnlib.DotNet.Emit.Code.Ldstr && instruction.Operand is string text)
                {
                    all.Add(text);
                }
            }
        }
    }

    foreach (var text in all)
    {
        Console.WriteLine(text.Replace('\r', ' ').Replace('\n', '↵'));
    }

    return 0;
}

if (args.Length >= 2 && args[1] == "--resources")
{
    // 资源型本地化（内嵌 .resources）的 key / 值，落 JSON 给库生成脚本用。
    var resourceResult = UIStringExtractor.Extract(positional[0]);
    var resourceJson = System.Text.Json.JsonSerializer.Serialize(
        resourceResult.Resources.Select(r => new { r.Container, r.Key, r.Value }),
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    Console.WriteLine(resourceJson);
    return 0;
}

var result = UIStringExtractor.Extract(positional[0]);
if (result.Error is not null)
{
    Console.WriteLine($"✗ 抽取失败：{result.Error}");
    return 1;
}

if (asJson)
{
    var json = System.Text.Json.JsonSerializer.Serialize(
        result.Entries.Select(e => new
        {
            e.Original,
            e.Context,
            Role = e.Role.ToString(),
            e.Reason,
            e.PreserveID,
        }),
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    Console.WriteLine(json);
    return 0;
}

var rows = result.Entries
    .Where(e => showAll || e.Role != UITextRole.Excluded)
    .OrderBy(e => e.Role)
    .ThenBy(e => e.Context, StringComparer.Ordinal)
    .ThenBy(e => e.Original, StringComparer.Ordinal)
    .ToArray();

Console.WriteLine($"文件：{result.AssemblyPath}");
Console.WriteLine($"UI 候选 {result.UICount} · 灰名单 {result.AmbiguousCount} · 资源文本 {result.Resources.Count} · 总字面量 {result.Entries.Count}");
Console.WriteLine(new string('-', 120));
foreach (var e in rows)
{
    var mark = e.Role switch
    {
        UITextRole.UI => "✔",
        UITextRole.Ambiguous => "?",
        _ => "×",
    };
    var id = e.PreserveID ? " [ID]" : string.Empty;
    Console.WriteLine($"{mark}{id} {e.Context,-38} {Truncate(e.Original, 52),-54} {e.Reason}");
}

return 0;

static string Truncate(string text, int max) =>
    text.Length <= max ? text : text[..(max - 1)] + "…";
