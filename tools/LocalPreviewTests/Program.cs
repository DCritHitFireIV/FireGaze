using FireGaze.LocalPreview.Core;

var tests = new List<(string Name, Action Run)>
{
    ("new preview becomes active", () =>
    {
        var session = new PreviewSession();
        session.StartPreview(new PreviewRequest(PreviewKind.Mount, 42), []);
        AssertActive(session, new PreviewRequest(PreviewKind.Mount, 42));
    }),
    ("new preview removes only supplied conflicts and its previous kind", () =>
    {
        var session = new PreviewSession();
        session.StartPreview(new PreviewRequest(PreviewKind.Mount, 42), []);
        session.StartPreview(new PreviewRequest(PreviewKind.Minion, 7), []);
        session.StartPreview(new PreviewRequest(PreviewKind.Accessory, 9), [PreviewKind.Mount]);
        session.StartPreview(new PreviewRequest(PreviewKind.Accessory, 11), []);
        AssertActive(session, new PreviewRequest(PreviewKind.Minion, 7), new PreviewRequest(PreviewKind.Accessory, 11));
    }),
    ("snapshot remains unchanged after replacing a preview", () =>
    {
        var session = new PreviewSession();
        session.StartPreview(new PreviewRequest(PreviewKind.Mount, 42), []);
        var before = session.Snapshot;
        session.StartPreview(new PreviewRequest(PreviewKind.Mount, 43), []);
        Equal(1, before.Active.Count);
        Equal(new PreviewRequest(PreviewKind.Mount, 42), before.Active.Single());
        True(!session.IsCurrent(before.Epoch), "old snapshot must be stale");
    }),
    ("stopping one category preserves compatible categories", () =>
    {
        var session = new PreviewSession();
        session.StartPreview(new PreviewRequest(PreviewKind.Mount, 42), []);
        session.StartPreview(new PreviewRequest(PreviewKind.Minion, 7), []);
        session.StopPreview(PreviewKind.Mount);
        AssertActive(session, new PreviewRequest(PreviewKind.Minion, 7));
    }),
    ("native attempt hides previews until rejected and restores exact prior intent", () =>
    {
        var session = new PreviewSession();
        session.StartPreview(new PreviewRequest(PreviewKind.Mount, 42), []);
        session.StartPreview(new PreviewRequest(PreviewKind.Minion, 7), []);
        var attempt = session.BeginNative();
        AssertActive(session);
        Equal(2, attempt.Before.Active.Count);
        var resolution = session.CompleteNative(attempt, false);
        True(resolution.Applied, "fresh rejection must apply");
        AssertActive(session, new PreviewRequest(PreviewKind.Mount, 42), new PreviewRequest(PreviewKind.Minion, 7));
    }),
    ("accepted native action clears all categories and cannot be restored by duplicate rejection", () =>
    {
        var session = new PreviewSession();
        session.StartPreview(new PreviewRequest(PreviewKind.Emote, 10), []);
        var attempt = session.BeginNative();
        True(session.CompleteNative(attempt, true).Applied, "fresh acceptance must apply");
        True(!session.CompleteNative(attempt, false).Applied, "duplicate must be ignored");
        AssertActive(session);
    }),
    ("new preview prevents stale rejection from restoring old preview", () =>
    {
        var session = new PreviewSession();
        session.StartPreview(new PreviewRequest(PreviewKind.Mount, 42), []);
        var attempt = session.BeginNative();
        session.StartPreview(new PreviewRequest(PreviewKind.Accessory, 11), []);
        True(!session.CompleteNative(attempt, false).Applied, "old rejection must be ignored");
        AssertActive(session, new PreviewRequest(PreviewKind.Accessory, 11));
    }),
    ("every global exit invalidates suspended previews without resuming them", () =>
    {
        foreach (var reason in Enum.GetValues<PreviewExitReason>())
        {
            var session = new PreviewSession();
            session.StartPreview(new PreviewRequest(PreviewKind.Minion, 7), []);
            var attempt = session.BeginNative();
            session.Exit(reason);
            True(!session.IsCurrent(attempt.Epoch), $"{reason} must advance the epoch even while suspended");
            True(!session.CompleteNative(attempt, false).Applied, $"{reason} must invalidate pending restore");
            AssertActive(session);
        }
    }),
    ("stale asynchronous exit cannot clear a newer preview", () =>
    {
        var session = new PreviewSession();
        session.StartPreview(new PreviewRequest(PreviewKind.Mount, 42), []);
        var oldEpoch = session.Snapshot.Epoch;
        session.StartPreview(new PreviewRequest(PreviewKind.Mount, 43), []);
        session.Exit(PreviewExitReason.UserCancelled, oldEpoch);
        AssertActive(session, new PreviewRequest(PreviewKind.Mount, 43));
    }),
    ("attempt from another session cannot complete an unrelated pending operation", () =>
    {
        var first = new PreviewSession();
        var second = new PreviewSession();
        first.StartPreview(new PreviewRequest(PreviewKind.Mount, 42), []);
        second.StartPreview(new PreviewRequest(PreviewKind.Minion, 7), []);
        var wrong = first.BeginNative();
        var correct = second.BeginNative();
        True(!second.CompleteNative(wrong, true).Applied, "foreign attempt must be ignored");
        True(second.CompleteNative(correct, false).Applied, "own attempt must remain usable");
        AssertActive(second, new PreviewRequest(PreviewKind.Minion, 7));
    }),
    ("invalid request cannot replace existing preview", () =>
    {
        var session = new PreviewSession();
        session.StartPreview(new PreviewRequest(PreviewKind.Mount, 42), []);
        Throws<ArgumentOutOfRangeException>(() => session.StartPreview(new PreviewRequest(PreviewKind.Mount, 0), []));
        AssertActive(session, new PreviewRequest(PreviewKind.Mount, 42));
    }),
    ("gate suspends before exactly one synchronous original call and applies accepted empty state", () =>
    {
        var session = MountedSession();
        var gate = new NativeInputGate(session);
        var trace = new List<string>();
        var count = 0;
        var result = gate.Invoke(() =>
        {
            count++;
            trace.Add("original");
            AssertActive(session);
            return 23;
        }, value => value == 23,
        before => { Equal(42u, before.Active.Single().Id); trace.Add("suspend"); },
        current => { Equal(0, current.Active.Count); trace.Add("apply"); });
        Equal(23, result);
        Equal(1, count);
        Equal("suspend,original,apply", string.Join(',', trace));
        AssertActive(session);
    }),
    ("gate rejection applies restored preview after forwarding original input once", () =>
    {
        var session = MountedSession();
        var gate = new NativeInputGate(session);
        PreviewSnapshot? applied = null;
        var result = gate.Invoke(() => false, value => value, _ => { }, state => applied = state);
        Equal(false, result);
        True(applied is not null, "rejected action must reapply preview");
        Equal(42u, applied!.Active.Single().Id);
        AssertActive(session, new PreviewRequest(PreviewKind.Mount, 42));
    }),
    ("nested suppression bypasses preview transaction until every scope ends", () =>
    {
        var session = MountedSession();
        var gate = new NativeInputGate(session);
        var first = gate.Suppress();
        var second = gate.Suppress();
        first.Dispose();
        var result = gate.Invoke(() => true, _ => throw new Exception("must bypass classification"),
            _ => throw new Exception("must bypass suspension"), _ => throw new Exception("must bypass application"));
        True(result && gate.IsSuppressed, "nested internal call must pass through");
        AssertActive(session, new PreviewRequest(PreviewKind.Mount, 42));
        second.Dispose();
        second.Dispose();
        True(!gate.IsSuppressed, "scope disposal must be balanced and idempotent");
        gate.Invoke(() => true, value => value, _ => { }, _ => { });
        AssertActive(session);
    }),
    ("gate suppresses internal native calls made during suspension", () =>
    {
        var session = MountedSession();
        var gate = new NativeInputGate(session);
        var internalCalls = 0;
        gate.Invoke(() => false, value => value,
            _ => gate.Invoke(() => ++internalCalls, _ => throw new Exception("nested classification"),
                _ => throw new Exception("nested suspension"), _ => throw new Exception("nested application")),
            _ => { });
        Equal(1, internalCalls);
        AssertActive(session, new PreviewRequest(PreviewKind.Mount, 42));
    }),
    ("original exception restores preview and is rethrown without replay", () =>
    {
        var session = MountedSession();
        var gate = new NativeInputGate(session);
        var error = new InvalidOperationException("native failure");
        var count = 0;
        PreviewSnapshot? applied = null;
        var actual = Throws<InvalidOperationException>(() => gate.Invoke<bool>(() =>
        {
            count++;
            throw error;
        }, value => value, _ => { }, state => applied = state));
        True(ReferenceEquals(error, actual), "original error must be preserved");
        Equal(1, count);
        True(applied is not null, "exception must apply rejected current state");
        AssertActive(session, new PreviewRequest(PreviewKind.Mount, 42));
        True(!gate.IsSuppressed, "exception must release suppression");
    }),
    ("suspension failure still forwards native input once and honors acceptance", () =>
    {
        var session = MountedSession();
        var gate = new NativeInputGate(session);
        var count = 0;
        var error = new InvalidOperationException("visual suspension failed");
        var actual = Throws<InvalidOperationException>(() => gate.Invoke(() => { count++; return true; }, value => value,
            _ => throw error, _ => { }));
        True(ReferenceEquals(error, actual), "suspension error must be preserved");
        Equal(1, count);
        AssertActive(session);
    }),
    ("application failure after acceptance never resurrects old preview or repeats input", () =>
    {
        var session = MountedSession();
        var gate = new NativeInputGate(session);
        var count = 0;
        Throws<InvalidOperationException>(() => gate.Invoke(() => { count++; return true; }, value => value,
            _ => { }, _ => throw new InvalidOperationException("visual cleanup failed")));
        Equal(1, count);
        AssertActive(session);
        True(!gate.IsSuppressed, "application exception must release suppression");
    }),
    ("newer state during native input prevents stale application callback", () =>
    {
        var session = MountedSession();
        var gate = new NativeInputGate(session);
        var applications = 0;
        gate.Invoke(() =>
        {
            session.Exit(PreviewExitReason.TerritoryChange);
            session.StartPreview(new PreviewRequest(PreviewKind.Minion, 7), []);
            return false;
        }, value => value, _ => { }, _ => applications++);
        Equal(0, applications);
        AssertActive(session, new PreviewRequest(PreviewKind.Minion, 7));
    }),
    ("acceptance observer failure conservatively clears preview after forwarding input", () =>
    {
        var session = MountedSession();
        var gate = new NativeInputGate(session);
        var count = 0;
        Throws<InvalidOperationException>(() => gate.Invoke(() => { count++; return true; },
            _ => throw new InvalidOperationException("acceptance observation failed"), _ => { }, _ => { }));
        Equal(1, count);
        AssertActive(session);
    }),
    ("actor menu uses most recent action for the exact actor instance", () =>
    {
        var history = new EmoteHistory();
        var actor = new ActorIdentity(100, 1);
        history.ChangeTerritory(55);
        history.Record(actor, 10);
        history.Record(actor, 12);
        True(history.TryGet(actor, out var result), "recorded actor must resolve");
        Equal(new EmoteReference(55, actor, 12), result);
        True(!history.TryGet(new ActorIdentity(100, 2), out _), "reused object id must not inherit previous instance action");
    }),
    ("chat action stays bound to its original action across later actions and territory changes", () =>
    {
        var history = new EmoteHistory();
        var actor = new ActorIdentity(100, 1);
        history.ChangeTerritory(55);
        var chat = history.BindChat(actor, 10);
        history.Record(actor, 12);
        history.ChangeTerritory(66);
        history.Record(actor, 14);
        Equal(new EmoteReference(55, actor, 10), chat);
        True(history.TryGet(actor, out var current), "new territory action must resolve");
        Equal(14u, current.EmoteId);
    }),
    ("territory boundary forgets actor actions even when entering same territory id", () =>
    {
        var history = new EmoteHistory();
        var actor = new ActorIdentity(100, 1);
        history.ChangeTerritory(55);
        history.Record(actor, 10);
        True(history.TryGet(actor, out _), "precondition actor action must be cached");
        history.ChangeTerritory(66);
        True(!history.TryGet(actor, out _), "new territory must clear actor history");
        history.Record(actor, 12);
        history.ChangeTerritory(66);
        True(!history.TryGet(actor, out _), "new instance of same territory must clear actor history");
    }),
    ("clearing history invalidates actor menu while preserving already bound chat action", () =>
    {
        var history = new EmoteHistory();
        var actor = new ActorIdentity(100, 1);
        history.ChangeTerritory(55);
        history.Record(actor, 10);
        True(history.TryGet(actor, out _), "precondition actor action must be cached");
        var chat = history.BindChat(actor, 10);
        history.Clear();
        True(!history.TryGet(actor, out _), "logout or disable must clear actor menu cache");
        Equal(new EmoteReference(55, actor, 10), chat);
    }),
    ("binding chat does not rewrite an actor's later action cache", () =>
    {
        var history = new EmoteHistory();
        var actor = new ActorIdentity(100, 1);
        history.ChangeTerritory(55);
        history.Record(actor, 12);
        history.BindChat(actor, 10);
        True(history.TryGet(actor, out var current), "actor action must remain available");
        Equal(12u, current.EmoteId);
    }),
    ("invalid action event cannot poison previously recorded actor action", () =>
    {
        var history = new EmoteHistory();
        var actor = new ActorIdentity(100, 1);
        history.ChangeTerritory(55);
        history.Record(actor, 10);
        Throws<ArgumentOutOfRangeException>(() => history.Record(actor, 0));
        True(history.TryGet(actor, out var current), "valid actor action must remain available");
        Equal(10u, current.EmoteId);
    }),
};

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failures++; Console.WriteLine($"FAIL {test.Name}: {error.Message}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} checks passed.");
return failures == 0 ? 0 : 1;

static void AssertActive(PreviewSession session, params PreviewRequest[] expected)
{
    var actual = session.Snapshot.Active.OrderBy(x => x.Kind).ToArray();
    var wanted = expected.OrderBy(x => x.Kind).ToArray();
    True(actual.SequenceEqual(wanted), $"expected [{string.Join(", ", wanted)}], got [{string.Join(", ", actual)}]");
}

static PreviewSession MountedSession()
{
    var session = new PreviewSession();
    session.StartPreview(new PreviewRequest(PreviewKind.Mount, 42), []);
    return session;
}

static void Equal<T>(T expected, T actual) => True(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");
static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static T Throws<T>(Action run) where T : Exception
{
    try { run(); } catch (T error) { return error; }
    throw new InvalidOperationException($"expected {typeof(T).Name}");
}
