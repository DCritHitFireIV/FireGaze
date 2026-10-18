using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal static class PhotoEncodingBoundaryTests
{
    internal static async Task<int> RunAsync(string root, PortraitHistoryStore history, string historyPath,
        PortraitHistoryKey key, PortraitHistoryRecord record, PortraitEditorSnapshot snapshot, PortraitIdentity identity)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++; Console.WriteLine("PASS " + name);
        }
        var historyBefore = File.ReadAllBytes(historyPath);
        var countBefore = history.Count;
        var cursorBefore = history.CurrentRecordID(key);
        var records = history.GetRecords(key);
        var encode = typeof(PortraitHistoryPhotos).GetMethod("Encode", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var captureField = typeof(PortraitHistoryPhotos).GetField("capture", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var encodingField = typeof(PortraitHistoryPhotos).GetField("encoding", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var textureProvider = DispatchProxy.Create<ITextureProvider, NoGpuProxy>();

        foreach (var opaque in new[] { false, true })
        {
            var name = opaque ? "opaque-black.bin" : "transparent-first-frame.bin";
            var path = Path.Combine(root, name);
            var readback = DispatchProxy.Create<ITextureReadbackProvider, EncodingReadbackProxy>();
            var probe = (EncodingReadbackProxy)(object)readback;
            probe.Raw = new byte[160 * 256 * 4];
            if (opaque) probe.Raw[3] = 255; // All RGB bytes stay black.
            var source = new PortraitThumbnailSource();
            using var photos = new PortraitHistoryPhotos(path, textureProvider, readback, source, history.Bytes);
            photos.SetRecords(records, history.Bytes);
            photos.Poll("encoding-owner", identity, record.ID, snapshot, true);
            await Task.Delay(250);
            photos.Poll("encoding-owner", identity, record.ID, snapshot, true);
            Check(source.Calls == 1, "Encode probe begins with a real service capture ticket");

            // Reproduce only Draw's managed completion handoff. No ImGui, native or GPU method runs.
            var unusedLayer = new FakeTexture();
            source.Complete(new(unusedLayer));
            var copied = (Task<PortraitThumbnailLayers?>)captureField.GetValue(photos)!;
            (await copied)?.Dispose();
            captureField.SetValue(photos, null);
            var composed = new SharingTexture();
            var character = new SharingTexture();
            probe.ExpectedCharacter = character;
            probe.ExpectedComposed = composed;
            // The historical fixture predates the source-readback parameter; exercising it proves RED.
            var args = encode.GetParameters().Length == 3
                ? new object[] { composed, character, CancellationToken.None }
                : new object[] { composed, CancellationToken.None };
            var encoded = (Task<byte[]>)encode.Invoke(photos, args)!;
            encodingField.SetValue(photos, encoded);
            var bytes = await encoded;

            if (!opaque)
            {
                Check(bytes.Length == 0 && probe.SaveCalls == 0,
                    "a transparent first render is rejected before entering the PNG encoder");
                photos.Poll("encoding-owner", identity, record.ID, snapshot, true);
                Check(!File.Exists(path) && Number(photos, "count") == 0 && Number(photos, "discarded") == 1,
                    "the real completion pipeline rejects empty pixels without creating a cache hit");
            }
            else
            {
                Check(bytes.SequenceEqual(probe.Png) && probe.SaveCalls == 1,
                    "opaque black enters the PNG encoder and keeps its encoded output");
                for (var retry = 0; retry < 100 && Number(photos, "saved") == 0; retry++)
                {
                    photos.Poll("encoding-owner", identity, record.ID, snapshot, true);
                    await Task.Delay(10);
                }
                var cache = new PortraitThumbnailCache(path, PortraitThumbnailCache.MaximumBytes - history.Bytes);
                Check(File.Exists(path) && cache.Count == 1 && Number(photos, "saved") == 1,
                    "visible pixels pass the actual service completion and managed cache writer");
                Check(probe.EncodedExpectedComposed && probe.PngGuid == new Guid("1b7cfaf4-713f-473c-bbcd-6137425faeaf")
                    && probe.SaveLeavesWrapOpen && probe.SaveLeavesStreamOpen,
                    "PNG encoding reads the composed surface with explicit ownership flags");
            }
            Check(probe.RawCalls == 1 && probe.ReadExpectedCharacter && probe.RawLeavesWrapOpen,
                "visibility checks read the retained character copy through the official readback interface");
            Check(character.Shared.Count == 1 && character.Shared[0].Disposes == 1 && character.Disposes == 0
                && composed.Disposes == 1 && unusedLayer.Disposes == 1,
                "Encode releases its owned composition and shared character reference without releasing the source owner");
            character.Dispose();
            Check(File.ReadAllBytes(historyPath).SequenceEqual(historyBefore) && history.Count == countBefore
                && history.CurrentRecordID(key) == cursorBefore && history.TryGetUndo(key, out _),
                "photo encoding acceptance or rejection preserves real history bytes records cursor and undo");
        }
        return checks;
    }

    private static long Number(PortraitHistoryPhotos photos, string name)
        => Convert.ToInt64(photos.Diagnostics().GetType().GetProperty(name)!.GetValue(photos.Diagnostics()));
}

public class EncodingReadbackProxy : DispatchProxy
{
    public byte[] Raw = [];
    public byte[] Png = new byte[33];
    public int RawCalls, SaveCalls;
    public SharingTexture? ExpectedCharacter, ExpectedComposed;
    public bool ReadExpectedCharacter, EncodedExpectedComposed, RawLeavesWrapOpen, SaveLeavesWrapOpen, SaveLeavesStreamOpen;
    public Guid PngGuid;

    public EncodingReadbackProxy()
        => new byte[] { 137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,160,0,0,1,0,8,6 }.CopyTo(Png, 0);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(ITextureReadbackProvider.GetRawImageAsync))
        {
            RawCalls++;
            ReadExpectedCharacter = ExpectedCharacter?.Shared.Count == 1
                && ReferenceEquals(args![0], ExpectedCharacter.Shared[0]);
            RawLeavesWrapOpen = args![2] is true;
            return Task.FromResult((new RawImageSpecification(160, 256, 28, 160 * 4), Raw));
        }
        if (targetMethod?.Name == nameof(ITextureReadbackProvider.SaveToStreamAsync))
        {
            SaveCalls++;
            EncodedExpectedComposed = ReferenceEquals(args![0], ExpectedComposed);
            PngGuid = (Guid)args[1]!;
            SaveLeavesWrapOpen = args[4] is true;
            SaveLeavesStreamOpen = args[5] is true;
            ((Stream)args[2]!).Write(Png);
            return Task.CompletedTask;
        }
        throw new InvalidOperationException("Encoding boundary must not call " + targetMethod?.Name);
    }
}

public sealed class SharingTexture : IDalamudTextureWrap
{
    public List<SharingTexture> Shared { get; } = [];
    public int Disposes;
    public ImTextureID Handle => throw new InvalidOperationException("The managed Encode test must not access a native handle.");
    public int Width => 160;
    public int Height => 256;
    public IDalamudTextureWrap CreateWrapSharingLowLevelResource()
    {
        var shared = new SharingTexture(); Shared.Add(shared); return shared;
    }
    public void Dispose() => Disposes++;
}
