using System.Reflection;
using FireGaze.LocalPreview.Runtime;

internal static partial class Program
{
    private static int checks, cases, failures;
    private static int Main(string[] args)
    {
        foreach (var (name, run) in FactoryCases().Concat(MetadataCases()).Concat(DetourCases()).Concat(BindingCases()))
        {
            cases++;
            try { run(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        Console.WriteLine($"{cases} cases, {checks} checks, {failures} failures");
        return failures == 0 ? 0 : 1;
    }
    private static void Check(bool value, string reason)
    { checks++; if (!value) throw new InvalidOperationException(reason); }

    private static IEnumerable<(string Name, Action Run)> FactoryCases()
    {
        yield return ("global backend false does not block reviewed local disabled constructor", () => {
            var f = new Fixture();
            var hook = f.Create();
            Check(ReferenceEquals(hook, f.Api.Hook) && !hook.Enabled, "local factory must return a disabled hook");
            Check(f.Api.Events.SequenceEqual(new[] { "qualify", "verify", "follow", "construct", "bind", "enabled", "original", "track" }),
                "qualify and official verifier must precede creation; ownership/binding must precede post-create work");
            Check(f.Owned.Count == 1 && ReferenceEquals(f.Bound, hook), "runtime retains hook and Original holder");
            Check(!f.Api.GlobalBackend, "no global backend mutation");
        });
        foreach (var step in new[] { "qualify", "verify", "follow", "construct", "bind", "enabled", "original", "track" })
        {
            var captured = step;
            yield return ($"failure at {step} denies and preserves cleanup ownership", () => {
                var f = new Fixture(); f.Api.Fail = captured;
                var error = Catch(f.Create);
                Check(error is not null && error.Message.Contains(captured), "report the concrete failed boundary");
                var constructed = f.Api.Events.Contains("construct") && captured != "construct";
                Check(f.Api.Hook.Disposals == (constructed ? 1 : 0), "only a returned known hook is cleaned once");
                Check(f.Owned.Count == 0 && f.Bound is null, "successful cleanup releases failed hook and binding");
                Check(!f.Api.Hook.Enabled, "factory never enables any hook");
            });
        }
        foreach (var changed in new[] { "verifier false", "verifier errors", "follow changes target", "already enabled", "missing original" })
        {
            var captured = changed;
            yield return ($"reject {changed}", () => {
                var f = new Fixture(); f.Api.Variant = captured;
                Check(Catch(f.Create) is not null, "unsafe response must deny");
                var beforeCreation = captured.StartsWith("verifier") || captured.StartsWith("follow");
                Check(f.Api.Events.Contains("construct") != beforeCreation, "precreation rejection cannot reach constructor");
                Check(f.Api.Hook.Disposals == (beforeCreation ? 0 : 1), "returned rejected hook cleaned exactly once");
                Check(f.Owned.Count == 0 && f.Bound is null, "safe cleanup releases all failed holders");
            });
        }
        foreach (var failure in new[] { "bind", "enabled", "original", "track" })
        foreach (var cleanupThrows in new[] { false, true })
        {
            var captured = failure; var cleanup = cleanupThrows;
            yield return ($"{failure} plus cleanup {(cleanup ? "throws" : "refuses")} retains exact hook and owner", () => {
                var f = new Fixture { CleanupFails = true, CleanupThrows = cleanup }; f.Api.Fail = captured;
                Check(Catch(f.Create) is not null, "cannot return failed hook");
                Check(f.Owned.Count == 1 && ReferenceEquals(f.Owned[0], f.Api.Hook), "uncertain teardown keeps concrete strong runtime ownership");
                Check(f.Api.Hook.Disposals == 1, "do not retry uncertain disposal in factory");
                if (captured != "bind") Check(ReferenceEquals(f.Bound, f.Api.Hook), "keep Original holder when teardown uncertain");
            });
        }
        yield return ("invalid address rejected before any verifier or constructor", () => {
            var f = new Fixture();
            Check(Catch(() => f.CreateAt(0)) is not null && f.Api.Events.Count == 0, "zero native address cannot enter external pipeline");
        });
        yield return ("concrete ownership exists before every post-constructor boundary", () => {
            var f = new Fixture();
            f.Api.AfterConstructor = () => Check(f.Owned.Contains(f.Api.Hook), "returned hook must be strongly owned before any fallible read or registration");
            f.Create();
            Check(f.Api.Hook.Disposals == 0, "successful creation is staged, not cleaned or enabled");
        });
    }
    private static Exception? Catch(Func<FakeHook> action)
    { try { action(); return null; } catch (Exception e) { return e; } }

    private sealed class Fixture
    {
        internal readonly List<IDisposable> Owned = [];
        internal readonly FakeApi Api = new();
        internal FakeHook? Bound;
        internal bool CleanupFails, CleanupThrows;
        internal FakeHook Create() => CreateAt(0x606B10);
        internal FakeHook CreateAt(nint address) => ReviewedDisabledHookFactory.Create(Api,address,this,(Action)Callback,Owned,
            h => { if (h is not null) Api.Step("bind"); Bound=h; },
            h => { ((FakeHook)h).Disposals++; if (CleanupThrows) throw new InvalidOperationException("cleanup"); return !CleanupFails; });
        private void Callback() { }
    }
    private sealed class FakeHook : IDisposable
    {
        internal bool Enabled = false;
        internal int Disposals;
        public void Dispose() => Disposals++;
    }
    private sealed class FakeApi : IReviewedDisabledHookApi<FakeHook>
    {
        internal readonly List<string> Events = [];
        internal readonly FakeHook Hook = new();
        internal string? Fail, Variant;
        internal bool GlobalBackend = false;
        internal Action? AfterConstructor;
        private Delegate? detour;
        internal void Step(string name)
        { Events.Add(name); if (Fail == name) throw new InvalidOperationException(name); }
        public void Qualify(object owner, Delegate value) { Step("qualify"); detour=value; }
        public void Verify(nint address, Assembly assembly)
        { Step("verify"); if (Variant?.StartsWith("verifier") == true) throw new InvalidOperationException(Variant); }
        public nint FollowJmp(nint address) { Step("follow"); return Variant == "follow changes target" ? address+1 : address; }
        public FakeHook Construct(nint address, Delegate value, Assembly assembly) { Step("construct"); return Hook; }
        public bool IsEnabled(FakeHook hook) { AfterConstructor?.Invoke(); Step("enabled"); return Variant == "already enabled"; }
        public Delegate Original(FakeHook hook) { AfterConstructor?.Invoke(); Step("original"); return Variant == "missing original" ? null! : detour!; }
        public void TrackScoped(FakeHook hook) { AfterConstructor?.Invoke(); Step("track"); }
    }
}
