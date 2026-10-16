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

var menuOptions = new PreviewMenuOptions();
// A settings command must validate the full request before touching preferences.
// Numeric enum spellings and bool-like values must not select a setting silently.
var optionCommandType = typeof(PreviewKind).Assembly.GetType("FireGaze.LocalPreview.Interaction.PreviewOptionCommand");
Check(optionCommandType is not null, "settings commands expose a validated production parser");
var tryParseOption = optionCommandType!.GetMethod("TryParse")!;
object? ParseOption(string[]? tokens, bool expectedSuccess, string label)
{
    object?[] args = [tokens, null];
    var success = (bool)tryParseOption.Invoke(null, args)!;
    Check(success == expectedSuccess, label);
    if (!success)
        Check(args[1]!.Equals(Activator.CreateInstance(optionCommandType)), label + " leaves no stale request");
    return args[1];
}
foreach (var (name, expectedOption) in new[]
{
    ("mount", "Mount"), ("minion", "Minion"), ("accessory", "Accessory"),
    ("emote", "Emote"), ("items", "Items"),
})
    foreach (var (value, expectedEnabled) in new[] { ("on", true), ("off", false) })
    {
        var parsed = ParseOption(["option", name, value], true, $"accepts {name} {value}")!;
        Check(optionCommandType.GetProperty("Option")!.GetValue(parsed)!.ToString() == expectedOption,
            $"{name} targets only its own preference");
        Check((bool)optionCommandType.GetProperty("Enabled")!.GetValue(parsed)! == expectedEnabled,
            $"{name} {value} retains the requested enabled state");
    }
var mixedCaseOption = ParseOption(["OpTiOn", "AcCeSsOrY", "OfF"], true, "settings command names ignore case")!;
Check(optionCommandType.GetProperty("Option")!.GetValue(mixedCaseOption)!.ToString() == "Accessory" &&
    !(bool)optionCommandType.GetProperty("Enabled")!.GetValue(mixedCaseOption)!, "mixed case does not change the requested preference or value");
foreach (var (tokens, name) in new (string[]?, string)[]
{
    (null, "null request"), ([], "empty request"), (["option"], "missing preference and value"),
    (["option", "mount"], "missing value"), (["option", "mount", "on", "off"], "extra argument"),
    (["mount", "mount", "on"], "wrong command"), (["option", "all", "on"], "unknown preference"),
    (["option", "0", "on"], "numeric valid-enum value"), (["option", "99", "on"], "numeric unknown-enum value"),
    (["option", "Mount,Minion", "on"], "combined enum names"), (["option", "mount", "1"], "numeric true"),
    (["option", "mount", "0"], "numeric false"), (["option", "mount", "true"], "boolean true spelling"),
    (["option", "mount", "false"], "boolean false spelling"), (["option", "mount", "toggle"], "implicit toggle"),
    (["option", "", "on"], "empty preference"), (["option", "mount", ""], "empty value"),
})
    ParseOption(tokens, false, "rejects " + name);
// AtkTextNode replaces MouseClick data with a zeroed LinkData union. Only the
// synchronous parent dispatch identifies its button; no frame/time cache is valid.
var mouseContextType=typeof(NativeLinkContext).Assembly.GetType("FireGaze.LocalPreview.Interaction.NativeMouseDispatchContext");
Check(mouseContextType is not null,"native link click retains its synchronous original mouse dispatch");
var mouseContext=Activator.CreateInstance(mouseContextType!)!;
void MouseEnter(nint node,byte? button)=>mouseContextType!.GetMethod("Enter")!.Invoke(mouseContext,[node,button]);
void MouseExit()=>mouseContextType!.GetMethod("Exit")!.Invoke(mouseContext,null);
byte? MouseButton()=>(byte?)mouseContextType!.GetProperty("Button")!.GetValue(mouseContext);
MouseEnter(100,(byte)1); MouseEnter(100,null);
Check(MouseButton()==1,"zeroed link event inherits exact enclosing right click");
MouseExit(); MouseEnter(200,null);
Check(MouseButton() is null,"another node cannot inherit a pending right click");
MouseExit(); MouseEnter(100,(byte)0); MouseEnter(100,null);
Check(MouseButton()==0,"nested left click masks enclosing right click");
MouseExit(); MouseExit();
Check(MouseButton()==1,"nested dispatch restores its enclosing click");
MouseExit(); MouseExit();
Check(MouseButton() is null,"completed dispatch cannot leak a stale button");
MouseEnter(100,null);
Check(MouseButton() is null,"direct synthetic link event never guesses the last click");
MouseExit();
// The game emits an Emote LinkMouseClick but does not open a context menu for it.
// The fallback must open once for the current right click, never for left clicks
// or when the native handler already opened a menu.
var emoteMenuPolicy=typeof(NativeLinkContext).Assembly.GetType("FireGaze.LocalPreview.Interaction.NativeEmoteMenuPolicy");
Check(emoteMenuPolicy is not null,"native emote right-click menu fallback exists");
bool OpenEmoteMenu(ushort id,bool right,int before,int after,uint owner)
    =>(bool)emoteMenuPolicy!.GetMethod("ShouldOpen")!.Invoke(null,new object[]{id,right,before,after,owner})!;
Check(OpenEmoteMenu(21,true,0,0,81),"right-click full sentence requests a native menu");
Check(!OpenEmoteMenu(21,false,0,0,81),"left-click never requests the trial context menu");
Check(!OpenEmoteMenu(21,true,2,3,81),"an existing native menu is not opened a second time");
Check(!OpenEmoteMenu(0,true,0,0,81),"unrelated native chat event cannot inherit earlier emote");
Check(!OpenEmoteMenu(21,true,0,0,0),"missing chat addon owner prevents a fallback menu");
// Chat item links belong to the native DEFAULT menu. Reading an inventory slot
// or a remembered link would miss this route or attach the previous item's action.
var chatItem = new ChatItemMenuContext(true, "ChatLog", 81, 100, 100, 81, 200, true, 52272);
Check(ChatItemMenuResolver.TryResolve(chatItem, out var chatItemId) && chatItemId == 52272,
    "default ChatLog menu resolves the current startup key without an inventory target");
foreach (var panel in new[] { "ChatLogPanel_0", "ChatLogPanel_1", "ChatLogPanel_2", "ChatLogPanel_3" })
    Check(ChatItemMenuResolver.TryResolve(chatItem with { AddonName = panel }, out chatItemId) && chatItemId == 52272,
        "detached native chat panel resolves its own current item: " + panel);
foreach (var (context, name) in new (ChatItemMenuContext, string)[]
{
    (chatItem with { IsDefaultMenu = false }, "inventory menu"),
    (chatItem with { AddonName = null }, "world menu"),
    (chatItem with { AddonName = "Inventory" }, "another addon"),
    (chatItem with { AddonName = "ChatLogPanel_10" }, "unrecognized chat panel"),
    (chatItem with { ParentAddonId = 0 }, "missing parent addon"),
    (chatItem with { ContextAgent = 0, MenuAgent = 0 }, "missing context agent"),
    (chatItem with { ChatAgent = 0 }, "missing chat agent"),
    (chatItem with { MenuAgent = 101 }, "another native agent"),
    (chatItem with { OwnerAddonId = 82 }, "another context owner"),
    (chatItem with { HasChatEventInterface = false }, "player link instead of item link"),
    (chatItem with { ContextItemId = 0 }, "empty current item after an earlier valid link"),
    (chatItem with { ContextItemId = 500000 }, "empty collectible item"),
    (chatItem with { ContextItemId = 1000000 }, "empty HQ item"),
    (chatItem with { ContextItemId = uint.MaxValue }, "invalid native item id"),
})
    Check(!ChatItemMenuResolver.TryResolve(context, out chatItemId) && chatItemId == 0,
        "chat item route rejects stale item context for " + name);
Check(ChatItemMenuResolver.TryResolve(chatItem with { ContextItemId = 52289 }, out chatItemId) && chatItemId == 52289,
    "a later accessory link cannot inherit the earlier startup key id");
Check(ChatItemMenuResolver.TryResolve(chatItem with { ContextItemId = 1052272 }, out chatItemId) && chatItemId == 52272,
    "chat item route normalizes the native HQ marker before collection lookup");
Check(ChatItemMenuResolver.TryResolve(chatItem with { ContextItemId = 552272 }, out chatItemId) && chatItemId == 52272,
    "chat item route normalizes the native collectible marker before collection lookup");
Check(!ChatItemMenuResolver.TryResolve(chatItem with { ContextItemId = 2052272 }, out chatItemId) && chatItemId == 0,
    "an event item cannot be folded onto a collection unlock item");
var catalog = new PreviewCatalog([
    new(PreviewKind.Mount, 446, "灰色轮式工程车"), new(PreviewKind.Mount, 440, "宇宙魔导猎手"),
    new(PreviewKind.Accessory, 58, "古董提灯"), new(PreviewKind.Minion, 1, " Test Pet "),
    new(PreviewKind.Mount, 0, "无效"), new(PreviewKind.Mount, 65536, "超范围"),
    new(PreviewKind.Mount, 1, " "), new((PreviewKind)99, 1, "未知类型"),
]);
Check(catalog.Search(PreviewKind.Mount, "").Select(e => e.Id).Order().SequenceEqual(new uint[] { 440, 446 }), "catalog retains all valid mounts and excludes malformed entries");
Check(catalog.Search(PreviewKind.Mount, " 工程车 ").Single().Id == 446, "catalog trims query and searches localized names");
Check(catalog.Search(PreviewKind.Accessory, "58").Single().Name == "古董提灯", "catalog searches exact numeric collection id");
Check(catalog.Search(PreviewKind.Mount, "58").Count == 0, "catalog search never leaks another category");
Check(catalog.Search(PreviewKind.Minion, "test").Single().Name == "Test Pet", "catalog search ignores case and normalizes name whitespace");
Check(catalog.Search((PreviewKind)99, "").Count == 0, "catalog rejects unknown kind");
Check(Enum.GetValues<PreviewKind>().All(kind => menuOptions.Allows(kind, false) && menuOptions.Allows(kind, true)), "new preferences enable all four kinds and item links");
var noItems = menuOptions with { Items = false };
Check(Enum.GetValues<PreviewKind>().All(kind => !noItems.Allows(kind, true) && noItems.Allows(kind, false)), "item toggle blocks only item menus and preserves player entries");
foreach (var kind in Enum.GetValues<PreviewKind>())
{
    var options = kind switch
    {
        PreviewKind.Mount => menuOptions with { Mount = false },
        PreviewKind.Minion => menuOptions with { Minion = false },
        PreviewKind.Accessory => menuOptions with { Accessory = false },
        _ => menuOptions with { Emote = false },
    };
    Check(!options.Allows(kind, false) && !options.Allows(kind, true), "disabled category cannot start from either source: " + kind);
    Check(Enum.GetValues<PreviewKind>().Where(other => other != kind).All(other => options.Allows(other, false)), "category toggle retains every other kind: " + kind);
}
Check(!menuOptions.Allows((PreviewKind)99, false), "unknown category cannot become actionable");

// Wrong action classification or using the Item row ID as the collection ID must fail.
Check(ItemCollectionResolver.Resolve(1322, 277) == new MenuPreviewRequest(PreviewKind.Mount, 277), "mount unlock uses its collection id");
Check(ItemCollectionResolver.Resolve(853, 45) == new MenuPreviewRequest(PreviewKind.Minion, 45), "minion unlock mapping");
Check(ItemCollectionResolver.Resolve(20086, 8) == new MenuPreviewRequest(PreviewKind.Accessory, 8), "accessory unlock mapping");
Check(ItemCollectionResolver.Resolve(37312, 62) is null, "facewear unlock actions cannot become fashion accessories");
Check(ItemCollectionResolver.Resolve(2633, 64) is null, "general unlock items cannot impersonate collection items");
Check(ItemCollectionResolver.Resolve(1322, 0) is null, "empty collection id rejected");
var unlockLinks = new Dictionary<uint, uint>
{
    [134] = 315, [130] = 315, [109] = 98, [115] = 259,
    [0] = 315, [65536] = 315, [12] = 0, [42] = 70000,
};
Check(ItemCollectionResolver.ResolveAll(2633, 315, unlockLinks).SequenceEqual(new MenuPreviewRequest[]
{
    new(PreviewKind.Emote, 130), new(PreviewKind.Emote, 134),
}), "learning item exposes every exact numeric unlock match in stable order and rejects invalid action ids");
Check(ItemCollectionResolver.ResolveAll(2633, 98, unlockLinks).Single() == new MenuPreviewRequest(PreviewKind.Emote, 109), "textbook unlock link is distinct from its learned emote id");
Check(ItemCollectionResolver.ResolveAll(2633, 109, unlockLinks).Count == 0, "another generic unlock cannot match merely by having a numeric emote row id");
Check(ItemCollectionResolver.ResolveAll(2633, 0, unlockLinks).Count == 0, "an empty unlock link cannot expose default locked actions");
Check(ItemCollectionResolver.ResolveAll(2633, 70000, unlockLinks).Single() == new MenuPreviewRequest(PreviewKind.Emote, 42), "unlock links are not incorrectly limited to the emote id range");
Check(ItemCollectionResolver.ResolveAll(1322, 446, unlockLinks).Single() == new MenuPreviewRequest(PreviewKind.Mount, 446), "all-result resolver preserves existing mount item behavior");
Check(ItemCollectionResolver.ResolveAll(99999, 315, unlockLinks).Count == 0, "a different item action cannot reuse textbook unlock mappings");
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
    var emoteBooks = items.Where(item => item.ItemAction.IsValid && item.Name.ToString().Contains("演技教材", StringComparison.Ordinal)).ToArray();
    var emoteUnlockLinks = emotes.Where(e => e.RowId != 0 && e.UnlockLink != 0).ToDictionary(e => e.RowId, e => e.UnlockLink);
    IReadOnlyList<MenuPreviewRequest> ResolveUnlock(uint action, uint link)
        => ItemCollectionResolver.ResolveAll(action, link, emoteUnlockLinks);
    foreach (var (itemId, kind, collectionId) in new (uint, PreviewKind, uint)[]
    {
        (52272, PreviewKind.Mount, 446), (52271, PreviewKind.Mount, 445), (52268, PreviewKind.Mount, 440),
        (6003, PreviewKind.Minion, 1), (6004, PreviewKind.Minion, 2),
        (51744, PreviewKind.Accessory, 56), (52289, PreviewKind.Accessory, 58),
    })
    {
        Check(ChatItemMenuResolver.TryResolve(chatItem with { ContextItemId = itemId }, out var selectedId),
            "current CN collection item reaches the native chat context route: " + itemId);
        var selected = items.GetRow(selectedId).ItemAction.Value;
        Check(ResolveUnlock(selected.Action.RowId, selected.Data[0]).Single() == new MenuPreviewRequest(kind, collectionId),
            "current CN chat collection item reaches its exact preview action: " + itemId);
    }
    var supportedCollectionItems = items.Where(item => item.ItemAction.IsValid
        && item.ItemAction.Value.Action.RowId is 1322 or 853 or 20086).ToArray();
    foreach (var item in supportedCollectionItems)
    {
        var action = item.ItemAction.Value;
        var requests = ResolveUnlock(action.Action.RowId, action.Data[0]);
        Check(requests.Count == 1 && requests[0].Kind == (action.Action.RowId switch
        {
            1322 => PreviewKind.Mount, 853 => PreviewKind.Minion, _ => PreviewKind.Accessory,
        }), "every current CN mount minion and fashion item resolves to its proper category: " + item.RowId);
    }
    var facewearItems = items.Where(item => item.ItemAction.IsValid && item.ItemAction.Value.Action.RowId == 37312).ToArray();
    Check(facewearItems.Length > 0 && facewearItems.All(item => ResolveUnlock(37312, item.ItemAction.Value.Data[0]).Count == 0),
        "current CN facewear unlock items never offer fashion accessory previews");
    Console.WriteLine($"CN supported collection items={supportedCollectionItems.Length} excludedFacewearItems={facewearItems.Length}");
    // Literals were read independently from the current CN ItemAction and Emote
    // sheets. Data[0] is UnlockLink, never an Emote row ID for Action 2633.
    foreach (var (itemId, expected) in new (uint, uint[])[]
    {
        (12040, [109]), (12041, [115]), (15842, [130, 134]), (24225, [191, 295]),
        (32830, [224, 225, 226, 227]), (40357, [258, 259, 260, 261, 262]),
        (46792, [298, 299, 300, 301, 302, 303]), (46793, [305, 306]), (52294, [330, 331]),
    })
    {
        var item = items.GetRow(itemId);
        var action = item.ItemAction.Value;
        Check(ResolveUnlock(action.Action.RowId, action.Data[0]).SequenceEqual(expected.Select(id => new MenuPreviewRequest(PreviewKind.Emote, id))),
            "CN emote textbook resolves every learned action instead of interpreting UnlockLink as an emote: " + itemId);
        Console.WriteLine($"CN textbook={itemId} name={item.Name} unlockLink={action.Data[0]} emotes="
            + string.Join(",", expected.Select(id => $"{id}:{emotes.GetRow(id).Name}")));
    }
    Check(emoteBooks.Length > 0, "current CN textbook fixture contains real learning items");
    foreach (var book in emoteBooks)
    {
        var action = book.ItemAction.Value;
        var requests = ResolveUnlock(action.Action.RowId, action.Data[0]);
        Check(requests.Count > 0 && requests.All(request => request.Kind == PreviewKind.Emote
            && emotes.GetRow(request.Id).UnlockLink == action.Data[0]), "CN textbook has only its actual unlocked emotes: " + book.RowId);
    }
    var falsePositives = items.Where(item => item.ItemAction.IsValid && item.ItemAction.Value.Action.RowId == 2633
        && !item.Name.ToString().Contains("演技教材", StringComparison.Ordinal))
        .Where(item => ResolveUnlock(2633, item.ItemAction.Value.Data[0]).Count != 0).ToArray();
    Check(falsePositives.Length == 0, "hairstyles maps and other generic unlocks cannot become emote previews");
    Console.WriteLine($"CN emote textbooks={emoteBooks.Length} supportedVariants={emoteBooks.Sum(book => ResolveUnlock(2633, book.ItemAction.Value.Data[0]).Count)}");
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
