using System.Reflection;
using Dalamud.Bindings.ImGui;
using FireGaze;
using FireGaze.RepoAudit;
using FireGaze.UI;
using Newtonsoft.Json;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
    Console.WriteLine("PASS " + message);
}

const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var root = Path.Combine(Path.GetTempPath(), "firegaze-installer-settings-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    var tabType = typeof(TranslateTab);
    object? Invoke(TranslateTab tab, string name, params object?[] arguments)
    {
        var method = tabType.GetMethod(name, instance);
        Check(method is not null, "production action is available through the plugin object: " + name);
        return method!.Invoke(tab, arguments);
    }
    var snapshotProperty = tabType.GetProperty("Snapshot", instance);
    Check(snapshotProperty is not null, "translation and installer state have a read-only snapshot");
    Check(!snapshotProperty!.CanWrite, "snapshot cannot replace production state");
    object Snapshot(TranslateTab tab) => snapshotProperty.GetValue(tab)!;
    T Read<T>(object snapshot, string name) => (T)snapshot.GetType().GetProperty(name)!.GetValue(snapshot)!;
    (Plugin Plugin, TranslateTab Tab) Fixture(string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var plugin = new Plugin(directory);
        return (plugin, new TranslateTab(plugin));
    }
    async Task WaitUpdate(TranslateTab tab)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (Read<bool>(Snapshot(tab), "UpdateInFlight"))
        {
            if (DateTime.UtcNow > timeout) throw new TimeoutException("translation update did not finish");
            await Task.Delay(5);
        }
    }

    // Removing either the host action's persistence or its real manifest rewrite
    // must break these checks. Configuration and Patcher are the production types.
    var direct = Fixture("direct");
    Invoke(direct.Tab, "SetEnabled", true);
    Check(direct.Plugin.Config.TranslateEnabled && direct.Plugin.Manager.AvailablePlugins[0].Punchline == "中文简介",
        "enabling through the production action changes both preference and native manifest data");
    Check(JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(direct.Plugin.ConfigPath))!.TranslateEnabled,
        "enabling persists the existing TranslateEnabled key immediately");
    Invoke(direct.Tab, "SetEnabled", false);
    Check(!direct.Plugin.Config.TranslateEnabled && direct.Plugin.Manager.AvailablePlugins[0].Punchline == "Original summary",
        "disabling through the production action restores original manifest data");

    var clicked = Fixture("clicked");
    ImGui.Click = "启用简介汉化###TranslateEnabled";
    clicked.Tab.Draw();
    Check(clicked.Plugin.Config.TranslateEnabled && clicked.Plugin.Manager.AvailablePlugins[0].Punchline == "中文简介",
        "the actual checkbox uses the same enable-and-apply path");

    foreach (var row in new[] { ("NameMode", "Name", "示例"), ("PunchlineMode", "Punchline", "中文简介"), ("DescriptionMode", "Description", "中文详情") })
    {
        var byMethod = Fixture("method-" + row.Item1);
        Invoke(byMethod.Tab, "SetEnabled", true);
        Check((bool)Invoke(byMethod.Tab, "SetDisplayMode", row.Item1, 1)!, "known field accepts a defined display mode: " + row.Item1);
        Check((string)typeof(Manifest).GetProperty(row.Item2)!.GetValue(byMethod.Plugin.Manager.AvailablePlugins[0])! == row.Item3,
            "mode action rewrites the selected real manifest field: " + row.Item1);
        var saved = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(byMethod.Plugin.ConfigPath))!;
        Check((DisplayMode)typeof(Configuration).GetProperty(row.Item1)!.GetValue(saved)! == DisplayMode.Chinese,
            "display mode survives configuration readback: " + row.Item1);

        var byButton = Fixture("button-" + row.Item1);
        Invoke(byButton.Tab, "SetEnabled", true);
        ImGui.Click = "中文###" + row.Item1 + "-Chinese";
        byButton.Tab.Draw();
        Check((DisplayMode)typeof(Configuration).GetProperty(row.Item1)!.GetValue(byButton.Plugin.Config)! == DisplayMode.Chinese
            && (string)typeof(Manifest).GetProperty(row.Item2)!.GetValue(byButton.Plugin.Manager.AvailablePlugins[0])! == row.Item3,
            "the actual mode radio uses the same validated production path: " + row.Item1);
    }

    foreach (var choice in new[]
    {
        ("NameMode", "Name", 0, "原版", "Sample"),
        ("NameMode", "Name", 1, "中文", "示例"),
        ("NameMode", "Name", 2, "双语", "示例 (Sample)"),
        ("PunchlineMode", "Punchline", 0, "原版", "Original summary"),
        ("PunchlineMode", "Punchline", 1, "中文", "中文简介"),
        ("PunchlineMode", "Punchline", 2, "双语", "中文简介\n\nOriginal summary"),
        ("DescriptionMode", "Description", 0, "原版", "Original detail"),
        ("DescriptionMode", "Description", 1, "中文", "中文详情"),
        ("DescriptionMode", "Description", 2, "双语", "中文详情\n\nOriginal detail"),
    })
    {
        var fixture = Fixture("matrix-" + choice.Item1 + choice.Item3);
        Invoke(fixture.Tab, "SetEnabled", true);
        Check((bool)Invoke(fixture.Tab, "SetDisplayMode", choice.Item1, choice.Item3)!, "mode action accepts the field/mode combination: " + choice.Item1 + choice.Item3);
        Check((string)typeof(Manifest).GetProperty(choice.Item2)!.GetValue(fixture.Plugin.Manager.AvailablePlugins[0])! == choice.Item5,
            "mode action produces the expected original/chinese/bilingual text: " + choice.Item1 + choice.Item3);
        var current = (DisplayMode)typeof(Configuration).GetProperty(choice.Item1)!.GetValue(fixture.Plugin.Config)!;
        var saved = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(fixture.Plugin.ConfigPath))!;
        Check(current == (DisplayMode)choice.Item3 && (DisplayMode)typeof(Configuration).GetProperty(choice.Item1)!.GetValue(saved)! == current,
            "every field/mode combination survives readback: " + choice.Item1 + choice.Item3);
    }

    var unchanged = File.ReadAllText(direct.Plugin.ConfigPath);
    foreach (var invalid in new (string? Field, int Mode)[] { ("Other", 1), (null, 1), ("NameMode", -1), ("PunchlineMode", 3), ("DescriptionMode", int.MaxValue) })
    {
        Check(!(bool)Invoke(direct.Tab, "SetDisplayMode", invalid.Field, invalid.Mode)!, "invalid field or mode is refused");
        Check(File.ReadAllText(direct.Plugin.ConfigPath) == unchanged, "invalid display action does not write or alter persisted configuration");
    }

    var update = Fixture("update");
    var completion = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
    update.Plugin.Download = () => completion.Task;
    Check((bool)Invoke(update.Tab, "StartTableUpdate")!, "update action accepts the first download");
    Check(Read<bool>(Snapshot(update.Tab), "UpdateInFlight"), "accepted update immediately exposes in-flight state");
    Check(!(bool)Invoke(update.Tab, "StartTableUpdate")!, "an in-flight update cannot start another download");
    completion.SetResult((true, "词表测试更新完成"));
    await WaitUpdate(update.Tab);
    Check(Read<string>(Snapshot(update.Tab), "UpdateMessage") == "词表测试更新完成", "successful update publishes the actual result");
    Check(JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(update.Plugin.ConfigPath))!.LastTableUpdateLocal != default,
        "the shared update path retains host update persistence");

    update.Plugin.Download = () => throw new IOException("fixture remote failure");
    Check((bool)Invoke(update.Tab, "StartTableUpdate")!, "a completed update permits another request");
    await WaitUpdate(update.Tab);
    Check(Read<string>(Snapshot(update.Tab), "UpdateMessage") == "更新出错：IOException", "update exception is observable and releases the in-flight guard");
    update.Plugin.Download = () => Task.FromResult((false, "网络暂不可用"));
    ImGui.Click = "从 GitHub 更新词表###UpdateTable";
    update.Tab.Draw();
    await WaitUpdate(update.Tab);
    Check(Read<string>(Snapshot(update.Tab), "UpdateMessage") == "网络暂不可用", "the actual update button uses the recoverable update action");

    var conflict = Fixture("conflict");
    var raw = new NativePlugin();
    typeof(TranslateTab).GetField("fastDalamudEntry", instance)!.SetValue(conflict.Tab,
        new InstalledPluginEntry { InternalName = "FuckDalamudCN", DisplayName = "FastDalamudCN", RawPlugin = raw, IsLoaded = true });
    typeof(TranslateTab).GetField("fastDalamudInstalled", instance)!.SetValue(conflict.Tab, true);
    Check((string)Invoke(conflict.Tab, "OpenConflictSettings")! == "已打开 FastDalamudCN 的设置窗口。" && raw.DalamudInterface.LocalUiBuilder.Opens == 1,
        "conflict action invokes the real native configuration-opening reflection path");
    ImGui.Click = "打开 FastDalamudCN 设置###OpenFastDalamudCN";
    conflict.Tab.Draw();
    Check(raw.DalamudInterface.LocalUiBuilder.Opens == 2, "the conflict settings button uses the same opening action");

    var detected = Fixture("detected-conflict");
    InstalledPluginsIndex.Next = new InstalledPluginsIndex { All = [new InstalledPluginEntry { InternalName = "FuckDalamudCN", DisplayName = "FastDalamudCN", RawPlugin = raw, IsLoaded = true }] };
    Invoke(detected.Tab, "RefreshConflictStatus");
    var detectionDeadline = DateTime.UtcNow.AddSeconds(5);
    while (Read<bool>(Snapshot(detected.Tab), "ConflictCheckInFlight"))
    {
        if (DateTime.UtcNow > detectionDeadline) throw new TimeoutException("conflict detection did not finish");
        await Task.Delay(5);
    }
    Invoke(detected.Tab, "RefreshConflictStatus");
    Check(Read<bool>(Snapshot(detected.Tab), "ConflictInstalled") && Read<bool>(Snapshot(detected.Tab), "ConflictLoaded"),
        "the shared conflict refresh publishes native installation and load state");

    var beforeBuilds = InstalledPluginsIndex.Builds;
    var beforeSnapshot = File.ReadAllText(update.Plugin.ConfigPath);
    var state = Snapshot(update.Tab);
    Check(Read<int>(state, "TableCount") == 1 && Read<string>(state, "TableUpdateDescription").Contains("2026-10-22"),
        "snapshot exposes loaded table facts without leaking its filesystem path");
    Check(state.GetType().GetProperty("Installer")!.GetValue(state) is not null, "snapshot includes current installer state");
    Check(InstalledPluginsIndex.Builds == beforeBuilds, "reading a snapshot does not refresh the plugin index");
    Check(File.ReadAllText(update.Plugin.ConfigPath) == beforeSnapshot, "reading diagnostics never persists configuration");
    Console.WriteLine($"InstallerSettingsTests: {passed} passed, 0 failed");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
finally { Directory.Delete(root, true); }

internal sealed class NativePlugin { public NativeInterface DalamudInterface { get; } = new(); }
internal sealed class NativeInterface { public NativeUiBuilder LocalUiBuilder { get; } = new(); }
internal sealed class NativeUiBuilder { public int Opens; public void OpenConfig() => Opens++; }
