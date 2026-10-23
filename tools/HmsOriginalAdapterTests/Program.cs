using System.Reflection;
using System.Runtime.Loader;
using FireGaze.LocalPreview.Compatibility;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

var work = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
var hooks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/addon/Hooks/dev");
var release = Path.Combine(work, "hms-original-1.0.4.2/unpacked/HMSync.dll");
var dev = @"C:\everyone\HMapSync-patch\dev\HMSync.dll";
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (var directory in new[] { hooks, Path.GetDirectoryName(release)! })
    {
        var path = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
var tests = new (string Name, Action Run)[]
{
    ("both audited binaries attach from file and stream", KnownBinaries),
    ("a changed disk binary is rejected before subscribing", ChangedBinary),
    ("a different audited file cannot authorize the loaded binary", WrongBinary),
    ("public zone signal cancels synchronously and preserves other subscribers", ZoneSignal),
    ("refresh never duplicates the zone subscription", SingleSubscription),
    ("disposing removes only this observer and blocks later refresh", Dispose),
    ("an unloaded HMS detaches and cancels the active preview", Unloaded),
    ("multiple loaded HMS instances disable the trial", Multiple),
    ("busy zone state refuses preview until it clears", Busy),
    ("same territory zone identity change cancels by next poll", ZoneIdentity),
    ("session change cancels by next poll", Session),
    ("source presentation change cancels by next poll", BodyChange),
    ("stable source presentation does not claim a same-ID input", StableBody),
    ("losing the original source cancels by next poll", MissingBody),
    ("native body reader keeps source memory unchanged", BodyReader),
    ("locomotion timeline changes do not end the trial", Locomotion),
    ("only audited active HMS enables clone visibility protection", Visibility),
    ("unload and dispose withdraw clone visibility protection", VisibilityDetached),
    ("loaded audited HMS refuses a missing complete input handoff", HandoffMissing),
    ("lost handoff ends an active trial and withdraws clone protection", HandoffLost),
    ("recovering complete handoff reopens only future trials", HandoffRecovered),
    ("absent HMS does not require optional HMS timeline observers", WithoutHmsHandoff),
    ("observer artifact has no managed patch engine dependency", NoPatchEngine),
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception exception) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + exception.GetBaseException().Message); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

void Equal<T>(T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"expected {expected}; got {actual}");
}
ObserverFixture Attached()
{
    var fixture = new ObserverFixture(release);
    Equal(fixture.Refresh().Allowed, true);
    return fixture;
}
void KnownBinaries()
{
    foreach (var path in new[] { release, dev })
    foreach (var stream in new[] { false, true })
    {
        using var fixture = new ObserverFixture(path, stream);
        Equal(fixture.Refresh().Allowed, true);
        Equal(fixture.ZoneHandlers, 1);
    }
}
void ChangedBinary()
{
    var path = Path.GetTempFileName();
    try
    {
        var bytes = File.ReadAllBytes(release); bytes[^1] ^= 0x40; File.WriteAllBytes(path, bytes);
        using var fixture = new ObserverFixture(release, suppliedFile: path);
        Equal(fixture.Refresh().Allowed, false);
        Equal(fixture.ZoneHandlers, 0);
        Equal(fixture.Runtime.VisibilityProtection, false);
    }
    finally { File.Delete(path); }
}
void WrongBinary()
{
    using var fixture = new ObserverFixture(release, true, dev);
    Equal(fixture.Refresh().Allowed, false);
    Equal(fixture.ZoneHandlers, 0);
}
void ZoneSignal()
{
    using var fixture = Attached();
    var calls = 0;
    Action original = () => calls++;
    fixture.Zone.GetType().GetEvent("ZoneWillChange")!.AddEventHandler(fixture.Zone, original);
    fixture.Runtime.HasActivePreview = true;
    fixture.SignalZone();
    Equal(fixture.Runtime.HasActivePreview, false);
    Equal(calls, 1);
    fixture.Adapter.Dispose();
    fixture.SignalZone();
    Equal(calls, 2);
}
void SingleSubscription()
{
    using var fixture = Attached();
    for (var i = 0; i < 5; i++) Equal(fixture.Refresh().Allowed, true);
    Equal(fixture.ZoneHandlers, 1);
}
void Dispose()
{
    using var fixture = Attached();
    fixture.Runtime.HasActivePreview = true;
    fixture.Adapter.Dispose();
    Equal(fixture.ZoneHandlers, 0);
    Equal(fixture.Runtime.HasActivePreview, false);
    Equal(fixture.Refresh().Allowed, false);
}
void Unloaded()
{
    using var fixture = Attached();
    fixture.Runtime.HasActivePreview = true;
    fixture.Exposure.Loaded = false;
    Equal(fixture.Refresh().Allowed, true);
    Equal(fixture.Runtime.HasActivePreview, false);
    Equal(fixture.ZoneHandlers, 0);
}
void Multiple()
{
    using var fixture = Attached();
    fixture.Runtime.HasActivePreview = true;
    fixture.Host.Plugins = [fixture.Exposed, fixture.Exposed];
    Equal(fixture.Refresh().Allowed, false);
    Equal(fixture.ZoneHandlers, 0);
    Equal(fixture.Runtime.HasActivePreview, false);
}
void Busy()
{
    using var fixture = Attached();
    fixture.SetZone("IsTransitioning", true);
    fixture.Runtime.HasActivePreview = true;
    Equal(fixture.Refresh().Allowed, false);
    Equal(fixture.Runtime.HasActivePreview, false);
    fixture.SetZone("IsTransitioning", false);
    fixture.SetZone("HomeRestoreArmed", true);
    Equal(fixture.Refresh().Allowed, false);
    fixture.SetZone("HomeRestoreArmed", false);
    Equal(fixture.Refresh().Allowed, true);
}
void ZoneIdentity()
{
    using var fixture = Attached();
    fixture.Runtime.HasActivePreview = true;
    fixture.SetZone("CurrentLoadedZone", 123u);
    Equal(fixture.Refresh().Allowed, true);
    Equal(fixture.Runtime.HasActivePreview, false);
}
void Session()
{
    using var fixture = Attached();
    fixture.Runtime.HasActivePreview = true;
    fixture.Session(true);
    Equal(fixture.Refresh().Allowed, true);
    Equal(fixture.Runtime.HasActivePreview, false);
}
void BodyChange()
{
    using var fixture = Attached();
    fixture.Runtime.HasActivePreview = true;
    fixture.Body = fixture.Body!.Value with { MountID = 446, Mount = 0x9000 };
    Equal(fixture.Refresh().Allowed, true);
    Equal(fixture.Runtime.HasActivePreview, false);
}
void StableBody()
{
    using var fixture = Attached();
    fixture.Runtime.HasActivePreview = true;
    Equal(fixture.Refresh().Allowed, true);
    Equal(fixture.Runtime.HasActivePreview, true);
}
void MissingBody()
{
    using var fixture = Attached();
    fixture.Runtime.HasActivePreview = true;
    fixture.Body = null;
    Equal(fixture.Refresh().Allowed, true);
    Equal(fixture.Runtime.HasActivePreview, false);
}
unsafe void BodyReader()
{
    var bytes = new byte[sizeof(NativeCharacter)];
    fixed (byte* memory = bytes)
    {
        var actor = (NativeCharacter*)memory;
        *(uint*)(memory + 0x74) = 17;
        actor->ModeParam = 5;
        actor->Mount.MountId = 446;
        actor->OrnamentData.OrnamentId = 9;
        var before = bytes.ToArray();
        var state = HmsOriginalBodyState.ReadFrom(actor);
        Equal(state.Generation, 17u);
        Equal(state.ModeParam, (byte)5);
        Equal(state.MountID, (ushort)446);
        Equal(state.OrnamentID, (ushort)9);
        Equal(bytes.SequenceEqual(before), true);
    }
}
unsafe void Locomotion()
{
    var bytes = new byte[sizeof(NativeCharacter)];
    fixed (byte* memory = bytes)
    {
        var actor = (NativeCharacter*)memory;
        var before = HmsOriginalBodyState.ReadFrom(actor);
        actor->Timeline.TimelineSequencer.TimelineIds[0] = 5;
        Equal(HmsOriginalBodyState.ReadFrom(actor), before);
    }
}
void Visibility()
{
    using var fixture = Attached();
    Equal(fixture.Runtime.VisibilityProtection, false);
    fixture.Visible(true);
    Equal(fixture.Refresh().Allowed, true);
    Equal(fixture.Runtime.VisibilityProtection, true);
    fixture.SetZone("IsTransitioning", true);
    Equal(fixture.Refresh().Allowed, false);
    Equal(fixture.Runtime.VisibilityProtection, false);
}
void VisibilityDetached()
{
    using var fixture = Attached();
    fixture.Visible(true); fixture.Refresh();
    fixture.Exposure.Loaded = false; fixture.Refresh();
    Equal(fixture.Runtime.VisibilityProtection, false);
    fixture.Exposure.Loaded = true; fixture.Refresh();
    Equal(fixture.Runtime.VisibilityProtection, true);
    fixture.Adapter.Dispose();
    Equal(fixture.Runtime.VisibilityProtection, false);
}
void HandoffMissing()
{
    using var fixture = new ObserverFixture(release);
    fixture.Runtime.HmsInputHandoffStatus = new(false, "second timeline unavailable");
    fixture.Visible(true);
    var decision = fixture.Refresh();
    Equal(decision.Allowed, false);
    Equal(decision.Reason.Contains("second timeline unavailable"), false);
    Equal(fixture.Runtime.VisibilityProtection, false);
}
void HandoffLost()
{
    using var fixture = Attached();
    fixture.Visible(true);
    Equal(fixture.Refresh().Allowed, true);
    Equal(fixture.Runtime.VisibilityProtection, true);
    fixture.Runtime.HasActivePreview = true;
    fixture.Runtime.HmsInputHandoffStatus = new(false, "observer disabled");
    var cleanups = fixture.Runtime.Cleanups;
    Equal(fixture.Refresh().Allowed, false);
    Equal(fixture.Runtime.HasActivePreview, false);
    Equal(fixture.Runtime.Cleanups, cleanups + 1);
    Equal(fixture.Runtime.VisibilityProtection, false);
    // A stable degraded poll must not restore a stale trial or repeatedly tear down.
    Equal(fixture.Refresh().Allowed, false);
    Equal(fixture.Runtime.Cleanups, cleanups + 1);
}
void HandoffRecovered()
{
    using var fixture = Attached();
    fixture.Runtime.HasActivePreview = true;
    fixture.Runtime.HmsInputHandoffStatus = new(false, "observer released");
    Equal(fixture.Refresh().Allowed, false);
    fixture.Runtime.HmsInputHandoffStatus = new(true, "");
    fixture.Visible(true);
    Equal(fixture.Refresh().Allowed, true);
    Equal(fixture.Runtime.HasActivePreview, false);
    Equal(fixture.Runtime.VisibilityProtection, true);
    Equal(fixture.ZoneHandlers, 1);
}
void WithoutHmsHandoff()
{
    using var fixture = new ObserverFixture(release);
    fixture.Exposure.Loaded = false;
    fixture.Runtime.HmsInputHandoffStatus = new(false, "both timelines unavailable");
    Equal(fixture.Refresh().Allowed, true);
    Equal(fixture.ZoneHandlers, 0);
    Equal(fixture.Runtime.Cleanups, 0);
    Equal(fixture.Runtime.VisibilityProtection, false);
}
void NoPatchEngine()
{
    Equal(typeof(HmsOriginalAdapter).Assembly.GetReferencedAssemblies().Any(name =>
        name.Name?.Contains("Harmony", StringComparison.OrdinalIgnoreCase) == true
        || name.Name?.Contains("MonoMod", StringComparison.OrdinalIgnoreCase) == true), false);
}
