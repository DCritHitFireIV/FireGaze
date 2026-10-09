using System.Reflection;
using FireGaze.Internal;

internal static class InstallerTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var install = typeof(PluginInstallerBridge).GetMethod("InstallFromServicesAsync", BindingFlags.Static | BindingFlags.NonPublic);
        check(install is not null, "discovery can install through the native installer without opening a search page");
        async Task<(bool Ok, bool Already, bool Running, string Message)> Run(InstallFixture f, string? repo = "https://example.com/master.json", bool official = false, int timeout = 1000)
        {
            var task = (Task)install!.Invoke(null, [f.UI, f.Manager, "Target", repo, official, TimeSpan.FromMilliseconds(timeout)])!;
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            object Value(string name) => result.GetType().GetProperty(name)!.GetValue(result)!;
            return ((bool)Value("Ok"), (bool)Value("AlreadyInstalled"), (bool)Value("StillRunning"), (string)Value("Message"));
        }
        var f = new InstallFixture();
        var result = await Run(f);
        check(result.Ok && !result.Already && !result.Running && f.Manager.InstalledPlugins.Single().InternalName == "Target",
            "one-click installation succeeds only after the native installed state is ready");
        f = new();
        f.Manager.AvailablePlugins.Insert(0, new() { SourceRepo = new() { PluginMasterUrl = "https://wrong.example/master.json" } });
        result = await Run(f);
        check(result.Ok && f.Manager.InstalledPlugins.Single().InstalledFromUrl == "https://example.com/master.json",
            "same-name manifests from another repository are never installed");
        f = new();
        result = await Run(f, "https://wrong.example/master.json");
        check(!result.Ok && f.Manager.InstalledPlugins.Count == 0, "missing exact source never falls back to a same-name plugin");
        f = new();
        f.Manager.AvailablePlugins[0].SourceRepo.PluginMasterUrl = "https://example.com/master.json?channel=other";
        result = await Run(f, "https://example.com/master.json?channel=chosen");
        check(!result.Ok && f.Manager.InstalledPlugins.Count == 0, "different repository query strings cannot select the wrong channel");
        f = new();
        f.Manager.AvailablePlugins[0].SourceRepo.IsThirdParty = false;
        result = await Run(f, null, true);
        check(result.Ok && f.Manager.InstalledPlugins.Single().InstalledFromUrl == "OFFICIAL", "official installs use only the native official source");
        f = new();
        f.Manager.SafeMode = true;
        result = await Run(f);
        check(!result.Ok && f.Manager.InstalledPlugins.Count == 0, "safe mode cannot be bypassed by discovery installation");
        foreach (var gate in new[] { "PluginsReady", "ReposReady", "Eligible", "Outdated", "Incompatible", "Busy" })
        {
            f = new();
            switch (gate)
            {
                case "PluginsReady": f.Manager.PluginsReady = false; break;
                case "ReposReady": f.Manager.ReposReady = false; break;
                case "Eligible": f.Manager.Eligible = false; break;
                case "Outdated": f.Window.Outdated = true; break;
                case "Incompatible": f.Window.Incompatible = true; break;
                case "Busy": f.Window.updateStatus = InstallStatus.InProgress; break;
            }
            result = await Run(f);
            check(!result.Ok && f.Manager.InstalledPlugins.Count == 0, gate + " blocks installation with an actionable failure");
        }
        f = new();
        f.Manager.AvailablePlugins.Add(new());
        result = await Run(f);
        check(!result.Ok && f.Manager.InstalledPlugins.Count == 0, "ambiguous same-source manifests do not silently pick one");
        f = new();
        f.Manager.AvailablePlugins[0].IsTestingExclusive = true;
        result = await Run(f);
        check(!result.Ok && f.Manager.InstalledPlugins.Count == 0, "testing-exclusive manifests cannot bypass testing opt-in");
        f.Manager.TestingAllowed = true;
        result = await Run(f);
        check(result.Ok && f.Window.UsedTesting, "allowed testing-exclusive plugins use the testing build");
        f = new();
        f.Manager.InstalledPlugins.Add(new());
        result = await Run(f);
        check(result.Ok && result.Already && f.Manager.InstalledPlugins.Count == 1, "already installed plugins are not reinstalled or counted as new");
        f = new();
        f.Window.ResultState = "LoadError";
        result = await Run(f);
        check(!result.Ok && !result.Running, "a plugin present in the list with LoadError is not reported as successful");
        f = new();
        f.Window.Failure = "download failed";
        result = await Run(f);
        check(!result.Ok && result.Message.Contains("download failed"), "native download failure is preserved for retry and diagnostics");
        f = new();
        f.Window.Deferred = true;
        var active = Run(f);
        var second = await Run(new());
        check(!active.IsCompleted && !second.Ok, "concurrent one-click requests cannot race the native installer");
        f.Window.Finish();
        result = await active;
        check(result.Ok, "an asynchronous native install is observed to completion");
        f = new();
        f.Window.Deferred = true;
        result = await Run(f, timeout: 20);
        check(!result.Ok && result.Running && f.Window.installStatus == InstallStatus.InProgress,
            "an observation timeout never claims success or cancels a native installation");
        f.Window.Finish();
        var installedState = typeof(PluginInstallerBridge).GetMethod("IsSuccessfullyInstalled", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var state in new[] { "Loading", "LoadError", "Loaded", "Unloaded" })
            check((bool)installedState.Invoke(null, [new InstalledFixture { State = state }])! == (state is "Loaded" or "Unloaded"),
                state + " has the correct recommendation eligibility");
    }
}

internal sealed class InstallFixture
{
    public ManagerFixture Manager { get; } = new();
    public WindowFixture Window { get; }
    public UIFixture UI { get; }
    public InstallFixture() { Window = new(Manager); UI = new(Window); }
}
internal sealed class ManagerFixture
{
    public bool PluginsReady { get; set; } = true;
    public bool ReposReady { get; set; } = true;
    public bool SafeMode { get; set; }
    public bool Eligible = true;
    public bool TestingAllowed;
    public List<ManifestFixture> AvailablePlugins { get; } = [new()];
    public List<InstalledFixture> InstalledPlugins { get; } = [];
    public bool IsManifestEligible(ManifestFixture manifest) => Eligible;
    public bool CanUseTesting(ManifestFixture manifest) => TestingAllowed;
    public bool UseTesting(ManifestFixture manifest) => TestingAllowed;
}
internal sealed class ManifestFixture
{
    public string InternalName { get; set; } = "Target";
    public RepoFixture SourceRepo { get; set; } = new();
    public bool IsTestingExclusive { get; set; }
    public Version? AssemblyVersion { get; set; } = new(1, 0);
    public Version? TestingAssemblyVersion { get; set; } = new(1, 1);
}
internal sealed class RepoFixture
{
    public bool IsThirdParty { get; set; } = true;
    public string PluginMasterUrl { get; set; } = "https://example.com/master.json";
}
internal sealed class InstalledFixture
{
    public string InternalName { get; set; } = "Target";
    public string InstalledFromUrl { get; set; } = "https://example.com/master.json";
    public string State { get; set; } = "Loaded";
}
internal enum InstallStatus { Idle, InProgress }
internal sealed class WindowFixture(ManagerFixture manager)
{
    public volatile InstallStatus installStatus;
    public InstallStatus updateStatus;
    public InstallStatus enableDisableStatus = InstallStatus.Idle;
    public string? errorModalMessage;
    public bool Outdated, Incompatible, Deferred, UsedTesting;
    public string? Failure;
    public string ResultState = "Loaded";
    private ManifestFixture? selected;
    private bool IsAvailableManifestOutdated(ManifestFixture manifest, bool testing) => Outdated;
    private bool IsAvailableManifestIncompatible(ManifestFixture manifest) => Incompatible;
    public void StartInstall(ManifestFixture manifest, bool testing)
    {
        selected = manifest;
        UsedTesting = testing;
        installStatus = InstallStatus.InProgress;
        if (!Deferred) Finish();
    }
    public void Finish()
    {
        if (Failure is not null) errorModalMessage = Failure;
        else manager.InstalledPlugins.Add(new() { InternalName = selected!.InternalName, State = ResultState,
            InstalledFromUrl = selected.SourceRepo.IsThirdParty ? selected.SourceRepo.PluginMasterUrl : "OFFICIAL" });
        installStatus = InstallStatus.Idle;
    }
}
internal sealed class UIFixture(WindowFixture window)
{
    private readonly WindowFixture pluginWindow = window;
}
