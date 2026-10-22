using System.Reflection;
using FireGaze.Translate;
using Newtonsoft.Json;

namespace FireGaze
{
    // Keep real configuration, table parsing and manifest rewriting. Only the
    // Dalamud host and remote download boundary are replaced in this harness.
    internal sealed class Plugin
    {
        internal Plugin(string directory)
        {
            ConfigPath = Path.Combine(directory, "config.json");
            Table = new TranslationTable(directory, directory);
            File.WriteAllText(Path.Combine(directory, "translations.json"), """
                {"_meta":{"updatedAt":"2026-10-22"},"Sample":{
                  "Name":{"Original":"Sample","Translated":"示例"},
                  "Punchline":{"Original":"Original summary","Translated":"中文简介"},
                  "Description":{"Original":"Original detail","Translated":"中文详情"}}}
                """);
            if (!Table.Load(out var error)) throw new InvalidOperationException(error);
            Patcher = new ManifestPatcher(() => Config, Table, _ => { });
            var type = typeof(ManifestPatcher);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            type.GetField("pluginManager", flags)!.SetValue(Patcher, Manager);
            type.GetField("propAvailable", flags)!.SetValue(Patcher, typeof(ManifestManager).GetProperty(nameof(ManifestManager.AvailablePlugins)));
            type.GetField("propInstalled", flags)!.SetValue(Patcher, typeof(ManifestManager).GetProperty(nameof(ManifestManager.InstalledPlugins)));
        }

        public Configuration Config { get; } = new();
        public TranslationTable Table { get; }
        public ManifestPatcher Patcher { get; }
        public int LastTranslatedCount { get; private set; }
        internal string ConfigPath { get; }
        internal ManifestManager Manager { get; } = new();
        internal UI.InstallerListScroll InstallerFeatures { get; } = new();
        internal Func<Task<(bool Ok, string Message)>> Download { get; set; } = () => Task.FromResult((true, "词表已更新"));
        public void SaveConfig() => File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Config));
        public int ApplyTranslations() => LastTranslatedCount = Patcher.ApplyAll();
        public int RestoreTranslations() { var count = Patcher.RestoreAll(); LastTranslatedCount = 0; return count; }
        public async Task<(bool Ok, string Message)> UpdateTranslationTableAsync()
        {
            var result = await Download();
            if (result.Ok) { Config.LastTableUpdateLocal = DateTime.Now; SaveConfig(); Patcher.ApplyAll(); }
            return result;
        }
    }

    internal sealed class ManifestManager
    {
        public List<Manifest> AvailablePlugins { get; } = [new()];
        public object[] InstalledPlugins { get; } = [];
    }

    internal sealed class Manifest
    {
        public string InternalName { get; } = "Sample";
        public string Name { get; } = "Sample";
        public string Punchline { get; } = "Original summary";
        public string Description { get; } = "Original detail";
    }
}

namespace FireGaze.RepoAudit
{
    internal sealed class InstalledPluginEntry
    {
        public required string InternalName { get; init; }
        public required string DisplayName { get; init; }
        public required object RawPlugin { get; init; }
        public bool IsLoaded { get; set; }
    }

    internal sealed class InstalledPluginsIndex
    {
        public bool Available { get; init; } = true;
        public IReadOnlyList<InstalledPluginEntry> All { get; init; } = [];
        internal static InstalledPluginsIndex Next { get; set; } = new();
        internal static int Builds;
        public static InstalledPluginsIndex Build() { Builds++; return Next; }
    }
}

namespace FireGaze.UI
{
    internal sealed class InstallerListScroll
    {
        public bool IsOpen { get; set; }
        internal InstallerListScrollSnapshot Snapshot { get; } = new(true, true, true, true, "Done", false, 900, 6, 3, new DateTime(2026, 10, 22), true);
    }

    internal static class UiHelpers
    {
        public static System.Numerics.Vector4 Muted => default;
        public static void ColoredWrapped(System.Numerics.Vector4 color, string message) { }
        public static IDisposable Disabled() => new Scope();
        private sealed class Scope : IDisposable { public void Dispose() { } }
    }
}

namespace Dalamud.Bindings.ImGui
{
    [Flags] internal enum ImGuiTableFlags { SizingFixedFit = 1, PadOuterX = 2 }
    [Flags] internal enum ImGuiTableColumnFlags { WidthFixed = 1 }
    internal static class ImGui
    {
        internal static string? Click;
        private static bool Take(string label) { if (Click != label) return false; Click = null; return true; }
        public static bool Button(string label) => Take(label);
        public static bool SmallButton(string label) => Take(label);
        public static bool Checkbox(string label, ref bool value) { if (!Take(label)) return false; value = !value; return true; }
        public static bool RadioButton(string label, bool active) => Take(label);
        public static void SameLine() { }
        public static void TextDisabled(string message) { }
        public static void TextWrapped(string message) { }
        public static void Text(string message) { }
        public static void TextUnformatted(string message) { }
        public static void Separator() { }
        public static bool BeginTable(string label, int count, ImGuiTableFlags flags) => true;
        public static void TableSetupColumn(string label, ImGuiTableColumnFlags flags, float width) { }
        public static void TableHeadersRow() { }
        public static void TableNextRow() { }
        public static void TableNextColumn() { }
        public static void EndTable() { }
    }
}
