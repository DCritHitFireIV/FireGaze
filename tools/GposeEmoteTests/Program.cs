using System.Reflection;

// The reflection adapter lets this suite run RED before the production module exists.
// All state and entry/replay decisions below are exercised on the production memory.
static class Program
{
    static int passed, failed;
    static void Check(bool value, string name)
    {
        if (value) { passed++; Console.WriteLine("PASS " + name); }
        else { failed++; Console.WriteLine("FAIL " + name); }
    }
    static int Main(string[] args)
    {
        var memory = new MemoryDriver();
        Check(memory.CurrentId == 0, "a new session has no invented emote");
        memory.Record(100, 21);
        Check(memory.CurrentId == 21, "an accepted local emote becomes the photo default");
        memory.Record(100, 1);
        Check(memory.CurrentId == 1, "a later native or preview emote replaces the earlier default");
        memory.Record(100, 21, accepted: false);
        Check(memory.CurrentId == 1, "a rejected native input cannot replace the last executed emote");
        memory.Record(100, 0);
        Check(memory.CurrentId == 1, "an idle or stop marker cannot erase the most recent emote");
        memory.Record(100, 50, resetPosture: true);
        memory.Record(100, 52, resetPosture: true);
        Check(memory.CurrentId == 0, "chair sit and ground sit clear the preceding emote");
        memory.Record(100,1);
        memory.Record(0, 21);
        Check(memory.CurrentId == 1, "an unbound observation cannot replace another character's memory");

        long entry = memory.BeginEntry(100);
        Check(entry != 0, "entry captures the last emote before preview cleanup");
        int calls = 0; ushort played = 0; byte pose = 0;
        bool Replay(ushort id, byte p) { calls++; played = id; pose = p; return true; }
        Check(!memory.Replay(100, Replay) && calls == 0, "no local animation executes before the game accepts photo entry");
        memory.CompleteEntry(entry, false);
        Check(!memory.Replay(100, Replay) && calls == 0, "a rejected photo entry cannot apply a local replay");

        entry = memory.BeginEntry(100);
        memory.CompleteEntry(entry, true);
        Check(memory.Replay(100, Replay) && calls == 1 && played == 1 && pose == 0, "accepted entry plays the captured emote once");
        Check(!memory.Replay(100, Replay) && calls == 1, "later frames never restart an already applied photo default");
        memory.EndEntry();
        Check(!memory.Replay(100, Replay) && calls == 1, "leaving photo mode removes its pending replay");
        Check(memory.CurrentId == 1, "leaving photo mode retains the most recent emote for next entry");

        memory.Record(100, 21, requestedPose: 3);
        entry = memory.BeginEntry(100);
        memory.CompleteEntry(entry, true);
        Check(!memory.Replay(101, Replay) && calls == 1, "a different character cannot consume the pending replay");
        Check(memory.Replay(100, Replay) && played == 21 && pose == 3, "the explicit preview pose is retained with its emote");

        memory.EndEntry();
        entry = memory.BeginEntry(100);
        memory.CompleteEntry(entry, true);
        int retries = 0;
        Check(!memory.Replay(100, (id, p) => { retries++; return false; }) && retries == 1, "a clone that cannot play yet does not consume the entry");
        Check(memory.Replay(100, (id, p) => { retries++; return true; }) && retries == 2, "a later ready clone can complete the pending entry");
        Check(!memory.Replay(100, Replay), "a retry that succeeded cannot run a third time");

        memory.EndEntry();
        long old = memory.BeginEntry(100);
        long newer = memory.BeginEntry(100);
        memory.CompleteEntry(old, true);
        Check(!memory.Replay(100, Replay), "a stale entry result cannot arm a newer entry");
        memory.CompleteEntry(newer, true);
        Check(memory.Replay(100, Replay), "the matching native entry result arms the current entry");

        memory.ChangeCharacter(200);
        Check(memory.CurrentId == 0, "changing character clears action memory from the previous character");
        entry = memory.BeginEntry(200);
        memory.CompleteEntry(entry, true);
        Check(!memory.Replay(200, Replay), "no remembered action leaves the native photo behavior alone");
        memory.Record(200, 1);
        memory.ChangeCharacter(0);
        Check(memory.CurrentId == 0, "logout clears the session memory");

        memory.Record(100, 21);
        entry = memory.BeginEntry(100);
        memory.CompleteEntry(entry, true);
        memory.Replay(100, (id, p) =>
        {
            memory.Record(100, 1);
            newer = memory.BeginEntry(100);
            memory.CompleteEntry(newer, true);
            return true;
        });
        Check(memory.Replay(100, Replay) && played == 1, "a reentrant successful replay cannot consume a newer entry");

        NativeGposeHarness.Run(Check);
        if (args.Length == 1) NativeGposeCases.Run(args[0], Check);
        Console.WriteLine($"{passed}/{passed + failed} gpose emote cases passed");
        return failed == 0 ? 0 : 1;
    }
}

sealed class MemoryDriver
{
    readonly Type? type = typeof(Program).Assembly.GetType("FireGaze.LocalPreview.Runtime.GposeEmoteMemory");
    readonly object? instance;
    public MemoryDriver() { if (type != null) instance = Activator.CreateInstance(type); }
    object? Call(string name, params object[] arguments) => type?.GetMethod(name)!.Invoke(instance, arguments);
    public ushort CurrentId => (ushort?)type?.GetProperty("EmoteId")!.GetValue(instance) ?? 0;
    public void Record(ulong owner, ushort id, byte requestedPose = 0, bool accepted = true, bool resetPosture = false)
        => Call("Record", owner, id, requestedPose, accepted, resetPosture);
    public void ChangeCharacter(ulong owner) => Call("ChangeCharacter", owner);
    public long BeginEntry(ulong owner) => (long?)Call("BeginEntry", owner) ?? 0;
    public void CompleteEntry(long token, bool entered) => Call("CompleteEntry", token, entered);
    public void EndEntry() => Call("EndEntry");
    public bool Replay(ulong owner, Func<ushort, byte, bool> callback) => (bool?)Call("TryReplay", owner, callback) ?? false;
}

static class NativeGposeCases
{
    public static void Run(string sqpack, Action<bool, string> check)
    {
        using var game = new Lumina.GameData(sqpack, new Lumina.LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified });
        var sheet = game.GetExcelSheet<Lumina.Excel.Sheets.Emote>()!;
        // Print the current client's posture metadata as evidence for the reset filter.
        foreach (uint id in new uint[] { 1, 21, 50, 52 })
        {
            var row = sheet.GetRow(id);
            Console.WriteLine($"CN emote={id} name={row.Name.ExtractText()} mode={row.EmoteMode.RowId} timeline={row.ActionTimeline[0].RowId}");
        }
        check(sheet.GetRow(50).Name.ExtractText() == "坐下" && sheet.GetRow(50).EmoteMode.RowId == 2
            && sheet.GetRow(52).Name.ExtractText() == "坐下到地上" && sheet.GetRow(52).EmoteMode.RowId == 1,
            "the CN mode 1 and 2 reset fixtures are ground sit and chair sit");
        GposeServiceCases.Run(game, check);
        NativeGposeHarness.RunMountedServiceCases(game,check);
        GposeSittingServiceCases.Run(game,check);
    }
}
