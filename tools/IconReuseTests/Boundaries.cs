using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace Dalamud.Bindings.ImGui
{
    internal readonly record struct ImTextureID(nint Value)
    {
        public static ImTextureID Null => new(0);
        public bool IsNull => Value == 0;
    }
}
namespace Dalamud.Interface.Textures { internal interface ITextureProvider { } }
namespace Dalamud.Interface.Textures.TextureWraps
{
    internal interface IDalamudTextureWrap : IDisposable { ImTextureID Handle { get; } }
}
namespace Dalamud.Plugin { internal interface IDalamudPluginInterface { } }
namespace FireGaze
{
    internal static class Plugin
    {
        public static TestLog Log { get; } = new();
        public static TextureService Textures { get; } = new();
    }
    internal static class PluginLogFallback { public static void Write(string text) { } }
    internal sealed class TestLog
    {
        public void Debug(string text) { }
        public void Information(string text) { }
        public void Warning(Exception exception, string text) { }
    }
    // Decoding and native texture ownership belong to the external texture provider.
    internal sealed class TextureService
    {
        public TestWrap Next { get; set; } = new(1);
        public int Decodes { get; private set; }
        public Task<IDalamudTextureWrap>? Pending { get; set; }
        public Task<IDalamudTextureWrap> CreateFromImageAsync(byte[] bytes, string name, CancellationToken cancellation)
        { Decodes++; return Pending ?? Task.FromResult<IDalamudTextureWrap>(Next); }
    }
    internal sealed class TestWrap(nint value) : IDalamudTextureWrap
    {
        public bool Available = true;
        public ImTextureID Handle => new(Available ? value : 0);
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
    internal sealed class TestImageCache
    {
        public readonly Dictionary<string, object?> pluginIconMap = new(StringComparer.Ordinal);
        public sealed class LoadedIcon(IDalamudTextureWrap texture, DateTime at)
        {
            public IDalamudTextureWrap Texture { get; } = texture;
            public DateTime At { get; } = at;
        }
    }
    internal sealed class TestInstalledPlugin(Guid id)
    {
        public Guid EffectiveWorkingPluginId { get; } = id;
    }
}
