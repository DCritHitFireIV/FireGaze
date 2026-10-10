using FireGaze.UIText;

// 批量落盘时 stdout 可能被重定向：强制 UTF-8，否则中文按系统 ANSI 码页写出去就不是合法 JSON 了。
Console.OutputEncoding = System.Text.Encoding.UTF8;

// 规则文件（伴生程序集 / 额外 UI 特性）默认按当前目录找：CI 在仓库根目录跑、本地也从仓库根跑。
// （插件里由 Plugin 构造时改指插件目录，会后写覆盖）
if (string.IsNullOrEmpty(UITextRules.PluginDirectory))
{
    UITextRules.PluginDirectory = Directory.GetCurrentDirectory();
}

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

if (args.Length >= 3 && args[1] == "--patch")
{
    // 诊断用：拿一个译文包给 DLL 打补丁（与 --revert 对称，可做还原→重打的往返验证）。
    //   UITextProbe <插件.dll> --patch <译文包.json> [输出.dll]
    var patchPackJson = File.ReadAllText(args[2], System.Text.Encoding.UTF8);
    var patchPack = UITextPack.FromJSON(patchPackJson, out var patchPackError);
    if (patchPack is null)
    {
        Console.WriteLine($"{{\"ok\":false,\"error\":\"读不动译文包：{patchPackError}\"}}");
        return 1;
    }

    var patchOutput = args.Length >= 4 ? args[3] : Path.Combine(Path.GetTempPath(), "uit-patch-" + Guid.NewGuid().ToString("N")[..8] + ".dll");
    var patchOutcome = UITextPatcher.Patch(positional[0], patchOutput, patchPack);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
        new
        {
            ok = patchOutcome.Ok,
            noMatch = patchOutcome.NoMatch,
            patchedTotal = patchOutcome.PatchedTotal,
            patchedLiterals = patchOutcome.PatchedLiterals,
            candidates = patchOutcome.Candidates,
            missing = patchOutcome.Missing.Count,
            error = patchOutcome.Error,
            output = patchOutput,
        },
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    return patchOutcome.Ok ? 0 : 1;
}

if (args.Length >= 3 && args[1] == "--revert")
{
    // 诊断用：拿一个译文包去反向还原一个 DLL（补丁记录丢了时的自证/体检）。
    //   UITextProbe <插件.dll> --revert <译文包.json> [输出.dll]
    // 输出 JSON：{ ok, patchedTotal, patchedLiterals, candidates, error, output }
    var packJson = File.ReadAllText(args[2], System.Text.Encoding.UTF8);
    var revertPack = UITextPack.FromJSON(packJson, out var packError);
    if (revertPack is null)
    {
        Console.WriteLine($"{{\"ok\":false,\"error\":\"读不动译文包：{packError}\"}}");
        return 1;
    }

    var output = args.Length >= 4 ? args[3] : Path.Combine(Path.GetTempPath(), "uit-revert-" + Guid.NewGuid().ToString("N")[..8] + ".dll");
    var revertOutcome = UITextPatcher.Revert(positional[0], output, revertPack);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
        new
        {
            ok = revertOutcome.Ok,
            patchedTotal = revertOutcome.PatchedTotal,
            patchedLiterals = revertOutcome.PatchedLiterals,
            candidates = revertOutcome.Candidates,
            error = revertOutcome.Error,
            output,
        },
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    return revertOutcome.Ok ? 0 : 1;
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

if (args.Length >= 2 && args[1] == "--attributes")
{
    // 自定义特性参数里的界面文字（UIAttribute / TweakName…），落 JSON 给库生成脚本用。
    var attributeResult = UIStringExtractor.Extract(positional[0]);
    var attributeJson = System.Text.Json.JsonSerializer.Serialize(
        attributeResult.Attributes.Select(a => new { a.Owner, a.Attribute, a.Value }),
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    Console.WriteLine(attributeJson);
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
            e.IsFunctionalIdentifier,
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
Console.WriteLine($"UI 候选 {result.UICount} · 灰名单 {result.AmbiguousCount} · 资源文本 {result.Resources.Count} · 属性文本 {result.Attributes.Count} · 总字面量 {result.Entries.Count}");
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
