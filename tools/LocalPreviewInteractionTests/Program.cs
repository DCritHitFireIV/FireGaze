using FireGaze.LocalPreview.Interaction;
using Lumina.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

var passed = 0;
void Check(bool result, string label)
{
    if (!result) throw new InvalidOperationException("FAIL: " + label);
    passed++;
}

// Wrong action classification or using the Item row ID as the collection ID must fail.
Check(ItemCollectionResolver.Resolve(1322, 277) == new MenuPreviewRequest(PreviewKind.Mount, 277), "mount unlock uses its collection id");
Check(ItemCollectionResolver.Resolve(853, 45) == new MenuPreviewRequest(PreviewKind.Minion, 45), "minion unlock mapping");
Check(ItemCollectionResolver.Resolve(20086, 8) == new MenuPreviewRequest(PreviewKind.Accessory, 8), "accessory unlock mapping");
Check(ItemCollectionResolver.Resolve(37312, 62) == new MenuPreviewRequest(PreviewKind.Accessory, 62), "second accessory action mapping");
Check(ItemCollectionResolver.Resolve(2633, 64) is null, "general unlock items cannot impersonate collection items");
Check(ItemCollectionResolver.Resolve(1322, 0) is null, "empty collection id rejected");
Check(PreviewMenuResolver.Resolve(new(10, 21, 32), 43).SequenceEqual(new MenuPreviewRequest[]
{
    new(PreviewKind.Mount, 10), new(PreviewKind.Minion, 21), new(PreviewKind.Accessory, 32), new(PreviewKind.Emote, 43),
}), "player context offers each observed collection independently");
Check(PreviewMenuResolver.Resolve(new(0, 0, 0), 0).Count == 0, "empty player context adds no trial entries");

var source = new SeStringBuilder().PushLinkCharacter("Test Player", 1167).Append("Test Player").PopLink().Append("做出了敬礼的动作。").ToArray();
var linked = NativeEmoteLinkBuilder.Wrap(source, 42);
Check(linked.Length > 0, "native emote sentence payload exists");
var linkCount = 0;
var endCount = 0;
foreach (var payload in new ReadOnlySeStringSpan(linked))
{
    if (payload.Type != ReadOnlySePayloadType.Macro || payload.MacroCode != MacroCode.Link) continue;
    Check(payload.TryGetExpression(out var kind) && kind.TryGetUInt(out var value), "link has numeric native type");
    kind.TryGetUInt(out var linkKind);
    if (linkKind == 15)
    {
        linkCount++;
        Check(payload.TryGetExpression(out _, out var id) && id.TryGetUInt(out var emoteId) && emoteId == 42, "native sentence embeds immutable observed emote id");
    }
    if (linkKind == 206) endCount++;
    Check(linkKind is 15 or 206, "nested player links do not steal native sentence context");
}
Check(linkCount == 1 && endCount == 1, "sentence has exactly one balanced native emote link");
Check(new ReadOnlySeStringSpan(linked).ExtractText() == "Test Player做出了敬礼的动作。", "whole sentence text survives linking");
Check(NativeEmoteLinkBuilder.Wrap(source, 0).Length == 0, "zero emote cannot become clickable");
Check(NativeEmoteLinkBuilder.Wrap(linked, 42).SequenceEqual(linked), "repeated wrapping cannot create nested links");
Check(NativeEmoteLinkBuilder.TryReadEmoteId(linked, out var decoded) && decoded == 42, "native link callback decodes fixed message action");
Check(!NativeEmoteLinkBuilder.TryReadEmoteId(source, out _), "player link cannot inherit previous emote context");
Check(!NativeEmoteLinkBuilder.TryReadEmoteId(new byte[] { 2, 39 }, out _), "truncated payload rejected without changing callback flow");
var scopes = new NativeLinkContext();
scopes.Enter(42);
Check(scopes.EmoteId == 42, "native menu reads active message emote");
scopes.Enter(0);
Check(scopes.EmoteId == 0, "unrelated nested event cannot reuse outer chat message");
scopes.Exit();
Check(scopes.EmoteId == 42, "returning from nested event restores outer message");
scopes.Exit();
Check(scopes.EmoteId == 0, "post event clears all transient chat context");
scopes.Enter(43);
scopes.Clear();
scopes.Exit();
Check(scopes.EmoteId == 0, "disable clears native event context even before late post event");
var templates = new EmoteLogTemplateIndex();
templates.Add(42, new byte[] { 1, 2, 3 });
templates.Add(42, new byte[] { 1, 2, 3 });
Check(templates.TryResolve(new byte[] { 1, 2, 3 }, out var templateId) && templateId == 42, "exact native template maps to its unique emote");
Check(!templates.TryResolve(new byte[] { 1, 2, 4 }, out _), "similar text cannot impersonate native emote template");
templates.Add(43, new byte[] { 1, 2, 3 });
Check(!templates.TryResolve(new byte[] { 1, 2, 3 }, out _), "shared template never guesses between emote ids");
templates.Add(0, new byte[] { 7 });
templates.Add(44, []);
Check(!templates.TryResolve(new byte[] { 7 }, out _) && !templates.TryResolve([], out _), "invalid native templates never become actionable");
templates.Add(45, new byte[] { 8, 9 });
templates.ExcludeNonEmoteTemplate(new byte[] { 8, 9 });
Check(!templates.TryResolve(new byte[] { 8, 9 }, out _), "system log sharing emote template cannot become a false clickable action");

// Missing native queue identity, reusing the latest action, or keeping a retired
// queue slot must fail these consumer-visible fixed-message identity checks.
var queueBindings = new NativeEmoteQueueBindings();
queueBindings.CaptureEmission((ushort)42, Array.Empty<nint>(), new nint[] { 100 });
queueBindings.CaptureEmission((ushort)43, new nint[] { 100 }, new nint[] { 100, 200 });
ushort exactQueueId;
Check(queueBindings.TryGet((nint)100, out exactQueueId) && exactQueueId == 42, "an earlier queued sentence keeps its original emote after a later native action");
Check(queueBindings.TryGet((nint)200, out exactQueueId) && exactQueueId == 43, "different actions with identical prose retain independent numeric identities");
queueBindings.RetainLive(new nint[] { 100, 200 });
queueBindings.CompleteFormat((nint)100, 0);
Check(queueBindings.TryGet((nint)100, out exactQueueId) && exactQueueId == 42, "native format retry retains exact identity across delayed queue processing");
queueBindings.CompleteFormat((nint)100, 1);
Check(!queueBindings.TryGet((nint)100, out exactQueueId), "successful format retires the queue binding before its slot can be reused");
queueBindings.CompleteFormat((nint)200, -1);
Check(!queueBindings.TryGet((nint)200, out exactQueueId), "native rejected format retires the queue binding without converting minus one to true");
queueBindings.CaptureEmission((ushort)44, Array.Empty<nint>(), new nint[] { 100 });
queueBindings.RetainLive(Array.Empty<nint>());
Check(!queueBindings.TryGet((nint)100, out exactQueueId), "queue removal clears a message that was filtered before formatting");
queueBindings.CaptureEmission((ushort)45, Array.Empty<nint>(), new nint[] { 100 });
Check(queueBindings.TryGet((nint)100, out exactQueueId) && exactQueueId == 45, "reused queue storage belongs only to the new native emission");
queueBindings.CaptureEmission((ushort)46, new nint[] { 100 }, new nint[] { 100 });
Check(queueBindings.TryGet((nint)100, out exactQueueId) && exactQueueId == 45, "an action that adds no log cannot steal an already queued sentence");
queueBindings.CaptureEmission((ushort)47, new nint[] { 100 }, new nint[] { 100, 300, 400 });
Check(!queueBindings.TryGet((nint)300, out exactQueueId) && !queueBindings.TryGet((nint)400, out exactQueueId), "unexpected multiple emissions cannot guess a message owner");
queueBindings.CaptureEmission((ushort)48, new nint[] { 100 }, new nint[] { 100, 500 });
queueBindings.CaptureEmission((ushort)49, new nint[] { 100 }, new nint[] { 100, 500, 600 });
Check(queueBindings.TryGet((nint)500, out exactQueueId) && exactQueueId == 48
    && queueBindings.TryGet((nint)600, out exactQueueId) && exactQueueId == 49,
    "nested native emissions keep their own queue item instead of inheriting the outer action");
queueBindings.Clear();
Check(!queueBindings.TryGet((nint)500, out exactQueueId), "disposal clears all pending queue identities");
Console.WriteLine($"PASS: {passed} interaction checks");

var sqpackOption = Array.IndexOf(args, "--sqpack");
if (sqpackOption >= 0 && sqpackOption + 1 < args.Length)
{
    using var gameData = new Lumina.GameData(args[sqpackOption + 1], new Lumina.LuminaOptions
    {
        DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified,
        PanicOnSheetChecksumMismatch = false,
    });
    var items = gameData.GetExcelSheet<Lumina.Excel.Sheets.Item>() ?? throw new InvalidOperationException("CN Item sheet unavailable");
    var terms = new[] { "灰色轮式工程车", "挖掘型强力机兵", "宇宙魔导猎手", "干草叉", "古董提灯" };
    foreach (var term in terms)
    {
        var matches = items.Where(item => item.Name.ToString().Contains(term, StringComparison.Ordinal)).ToArray();
        Check(matches.Length > 0, "CN acceptance item exists: " + term);
        foreach (var item in matches)
        {
            var action = item.ItemAction.Value;
            var request = ItemCollectionResolver.Resolve(action.Action.RowId, action.Data[0]);
            Console.WriteLine($"CN item={item.RowId} name={item.Name} action={action.Action.RowId} collection={action.Data[0]} resolved={request}");
            Check(request is not null, "CN collection mapping exists: " + term);
        }
    }
    var emotes = gameData.GetExcelSheet<Lumina.Excel.Sheets.Emote>() ?? throw new InvalidOperationException("CN Emote sheet unavailable");
    var logIndex = new EmoteLogTemplateIndex();
    var emotesWithLogs = 0;
    var emoteLogIds = new HashSet<uint>();
    foreach (var emote in emotes)
    {
        if (emote.RowId == 0 || emote.RowId > ushort.MaxValue) continue;
        if (emote.LogMessageTargeted.RowId != 0 || emote.LogMessageUntargeted.RowId != 0) emotesWithLogs++;
        if (emote.LogMessageTargeted.RowId != 0) { emoteLogIds.Add(emote.LogMessageTargeted.RowId); logIndex.Add((ushort)emote.RowId, emote.LogMessageTargeted.Value.Text.Data.Span); }
        if (emote.LogMessageUntargeted.RowId != 0) { emoteLogIds.Add(emote.LogMessageUntargeted.RowId); logIndex.Add((ushort)emote.RowId, emote.LogMessageUntargeted.Value.Text.Data.Span); }
    }
    foreach (var logRow in gameData.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>()!)
        if (!emoteLogIds.Contains(logRow.RowId)) logIndex.ExcludeNonEmoteTemplate(logRow.Text.Data.Span);
    Console.WriteLine($"CN emotes={emotes.Count} withLogRows={emotesWithLogs} uniqueTemplates={logIndex.UniqueCount} ambiguousTemplates={logIndex.AmbiguousCount}");
    foreach (var conflict in logIndex.ConflictEmoteGroups)
        Console.WriteLine("CN ambiguous template emotes=" + string.Join(",", conflict));
    var cnBindings = new NativeEmoteQueueBindings();
    var cnQueue = new List<nint>();
    var cnMessageIds = new List<ushort>();
    foreach (var emote in emotes)
    {
        if (emote.RowId == 0 || emote.RowId > ushort.MaxValue
            || (emote.LogMessageTargeted.RowId == 0 && emote.LogMessageUntargeted.RowId == 0)) continue;
        var before = cnQueue.ToArray();
        cnQueue.Add((nint)emote.RowId);
        cnMessageIds.Add((ushort)emote.RowId);
        cnBindings.CaptureEmission((ushort)emote.RowId, before, cnQueue.ToArray());
    }
    foreach (var id in cnMessageIds)
    {
        Check(cnBindings.TryGet((nint)id, out var immutableId) && immutableId == id,
            "CN native emission keeps exact action after every later queued action: " + id);
        var sentence = NativeEmoteLinkBuilder.Wrap(new SeStringBuilder().Append("原生情感动作提示。").ToArray(), immutableId);
        Check(NativeEmoteLinkBuilder.TryReadEmoteId(sentence, out var menuId) && menuId == id,
            "CN sentence menu resolves numeric action even when all prose is identical: " + id);
    }
    Console.WriteLine($"CN exact queue and sentence identity coverage={cnMessageIds.Count}/{emotesWithLogs}");
    Console.WriteLine($"PASS: {passed} interaction checks including current CN item data");
}
