using System.Numerics;
using Dalamud.Interface.Textures.TextureWraps;

// The production Analysis and Comparison partials run unchanged. Only the drawing, texture
// readback, and feature boundaries are detached; no game model, GPU or native input is invoked.
namespace Dalamud.Interface.Textures.TextureWraps
{
    internal interface IDalamudTextureWrap : IDisposable
    {
        nint Handle { get; }
        int Width { get; }
        int Height { get; }
        IDalamudTextureWrap CreateWrapSharingLowLevelResource();
    }
}
namespace Dalamud.Interface.Textures
{
    internal struct TextureModificationArgs { public Vector2 Uv1; }
}
namespace Dalamud.Bindings.ImGui
{
    internal enum ImGuiCond { FirstUseEver }
    internal enum ImDrawFlags { None }
    internal sealed class DrawList
    {
        public void AddRectFilled(Vector2 a, Vector2 b, uint color) { }
        public void AddRect(Vector2 a, Vector2 b, uint color, float radius, ImDrawFlags flags, float thickness) { }
        public void AddImage(nint handle, Vector2 a, Vector2 b) { ImGui.LastDrawImageSize = b - a; ImGui.LastImage = handle; }
        public void AddImage(nint handle, Vector2 a, Vector2 b, Vector2 uv0, Vector2 uv1) { ImGui.LastDrawImageSize = b - a; ImGui.LastImage = handle; }
        public void AddLine(Vector2 a, Vector2 b, uint color, float thickness) { }
    }
    internal static class ImGui
    {
        public static string? Click;
        public static List<string> Texts = [];
        public static Vector2 LastImageSize;
        public static Vector2 LastDrawImageSize;
        public static nint LastImage;
        public static Vector2 GetContentRegionAvail() => new(500, 800);
        public static Vector2 GetCursorScreenPos() => Vector2.Zero;
        public static Vector2 CalcTextSize(string text) => new(text.Length * 8, 16);
        public static Style GetStyle() => new();
        public static void SetNextWindowSize(Vector2 size, ImGuiCond condition) { }
        public static bool Begin(string name, ref bool open) => true;
        public static void End() { }
        public static void TextWrapped(string text) => Texts.Add(text);
        public static void TextDisabled(string text) => Texts.Add(text);
        public static void TextUnformatted(string text) => Texts.Add(text);
        public static void Separator() { }
        public static void SameLine() { }
        public static bool RadioButton(string text, bool selected) => Consume(text);
        public static bool Button(string text) => Consume(text);
        public static bool CollapsingHeader(string text) => true;
        public static void SetNextItemWidth(float width) { }
        public static bool SliderFloat(string id, ref float value, float min, float max, string format) => false;
        public static DrawList GetWindowDrawList() => new();
        public static uint GetColorU32(Vector4 color) => 0;
        public static void Dummy(Vector2 size) { }
        public static void Image(nint handle, Vector2 size) { LastImage = handle; LastImageSize = size; }
        private static bool Consume(string value) { if (Click != value) return false; Click = null; return true; }
        internal sealed class Style { public Vector2 FramePadding => new(4); public Vector2 ItemSpacing => new(8); }
    }
}
namespace FireGaze.UI
{
    internal static class FireGazePalette
    {
        public static Vector4 Text => Vector4.One;
        public static Vector4 Panel => Vector4.One;
        public static Vector4 Warn => Vector4.One;
        public static Vector4 Focus => Vector4.One;
    }
}
namespace FireGaze
{
    internal static class Plugin { public static Readback TextureReadback { get; } = new(); }
    internal sealed class Readback
    {
        public int Calls;
        public Task<RawImage> GetRawImageAsync(IDalamudTextureWrap texture, Dalamud.Interface.Textures.TextureModificationArgs args,
            bool leaveWrapOpen, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new RawImage(new(28, texture.Width, texture.Height, texture.Width * 4),
                Enumerable.Repeat(new byte[] { 100, 100, 100, 255 }, texture.Width * texture.Height).SelectMany(x => x).ToArray()));
        }
    }
    internal sealed record RawImage(RawSpecification Specification, byte[] RawData);
    internal sealed record RawSpecification(int DxgiFormat, int Width, int Height, int Pitch);
}
namespace FireGaze.PortraitPreview
{
    internal enum PortraitImageScope { Portrait, VisibleAdventurerPlate }
    internal readonly record struct PortraitCaptureOwner(ulong AccountID, ulong ContentID) { public bool IsValid => AccountID != 0 && ContentID != 0; }
    internal readonly record struct PortraitIdentity(nint Editor, nint State, nint Model, long Generation, ulong Character)
    { public bool IsValid => Editor != 0 && State != 0 && Model != 0 && Generation != 0; }
    internal readonly record struct PortraitCaptureViewport(uint ID, Vector2 Position, Vector2 Size);
    internal readonly record struct PortraitCaptureRect(float X, float Y, float Width, float Height);
    internal sealed record PortraitPlateCaptureBinding(nint Addon, ushort AddonID, nint Agent, nint Storage,
        ulong AccountID, ulong ContentID, long Generation, PortraitCaptureRect Rect, string VisualKey);
    internal sealed record PortraitCaptureSample(PortraitCaptureOwner Owner, PortraitIdentity Editor, string StateKey,
        PortraitImageScope Scope, PortraitCaptureViewport Viewport, PortraitPlateCaptureBinding? Plate = null)
    { public bool IsValid => Owner.IsValid && Editor.IsValid && !string.IsNullOrWhiteSpace(StateKey); }
    internal sealed class PortraitCapturedImage(IDalamudTextureWrap texture, PortraitCaptureSample sample) : IDisposable
    {
        public IDalamudTextureWrap Texture { get; } = texture;
        public PortraitCaptureSample Sample { get; } = sample;
        public int Width => Texture.Width;
        public int Height => Texture.Height;
        public void Dispose() => Texture.Dispose();
    }
    internal sealed record PortraitHistoryListItem(long ID, DateTimeOffset At, string Label, bool IsCurrent);
    internal sealed class Model
    {
        public IReadOnlyList<PortraitHistoryListItem> History { get; set; } = [new(1, DateTimeOffset.UnixEpoch, "记录", true)];
        public PortraitImageScope LiveCaptureScope { get; set; }
        public PortraitCaptureSample? Sample;
        public Texture? LiveTexture;
        public string SourceGuideKey { get; set; } = "character-a";
        public void SetLiveCaptureScope(PortraitImageScope scope) => LiveCaptureScope = scope;
        public PortraitCapturedImage? AcquireCurrentLiveImage() => Sample is null || LiveTexture is null ? null :
            new(LiveTexture.CreateWrapSharingLowLevelResource(), Sample);
    }
    internal sealed class PortraitHistoryPhotos
    {
        public Texture? Photo;
        public IDalamudTextureWrap? Get(long record) => Photo;
    }
    internal sealed partial class PortraitPreviewWindow : IDisposable
    {
        private const float Scale = 1;
        private readonly Model feature;
        private readonly PortraitHistoryPhotos? photos;
        public PortraitPreviewWindow(Model model, PortraitHistoryPhotos? photos = null) { feature = model; this.photos = photos; showAnalysis = true; }
        private static void ContinueToolbar(float right, float width) { }
        public void Dispose() { analysis.Dispose(); analysisPreview?.Dispose(); comparisonReference.Dispose(); }
    }
    internal sealed class Texture(int width, int height, nint handle) : IDalamudTextureWrap
    {
        public int Disposes;
        public List<Texture> Shares = [];
        public nint Handle => handle;
        public int Width => width;
        public int Height => height;
        public IDalamudTextureWrap CreateWrapSharingLowLevelResource() { var copy = new Texture(width, height, handle); Shares.Add(copy); return copy; }
        public void Dispose() => Disposes++;
    }
}
