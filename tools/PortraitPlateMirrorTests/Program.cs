#if !HAS_MIRROR
Console.Error.WriteLine("FAIL portrait mirror has no local texture ownership and restoration implementation");
return 1;
#else
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

var passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    passed++;
    Console.WriteLine("PASS " + description);
}
var identity = new PortraitIdentity(1, 2, 3, 4, 5);
var snapshot = new PortraitEditorSnapshot();
try
{
    var api = new FakeApi();
    using (var mirror = new PortraitPlateMirror(api))
    {
        api.Framework = false;
        Check(!mirror.Update(identity, snapshot, true) && api.Retains == 0, "off-thread update cannot read or change native texture state");
        api.Framework = true; api.EditorCurrent = false;
        Check(!mirror.Update(identity, snapshot, true) && api.Retains == 0, "a stale editor identity cannot acquire a display texture lease");
        api.EditorCurrent = true; api.Resolvable = false;
        Check(!mirror.Update(identity, snapshot, true) && api.Retains == 0, "closed foreign or ambiguous plate windows are left unchanged");
        api.Resolvable = true;
        Check(mirror.Update(identity, snapshot, true) && api.Retains == 1 && api.CurrentKernel == 100 && api.Releases.Count == 0,
            "initial mirror moves the old texture into a lease and retains the editor render texture once");
        Check(mirror.Update(identity, snapshot, true) && api.Retains == 1 && api.Writes == 1,
            "ordinary rendered frames do not repeatedly increment native reference counts");
        api.Framework = false;
        Check(!mirror.Close() && mirror.IsActive && api.Releases.Count == 0, "off-thread close preserves ownership for a later safe restoration");
        api.Framework = true;
        Check(mirror.Close() && !mirror.IsActive && api.CurrentKernel == 10 && api.Releases.SequenceEqual([100L]),
            "closing restores the exact original texture and releases only the temporary render reference");
        Check(mirror.Close() && api.Releases.Count == 1, "closing an already closed mirror cannot release a texture twice");
        api.SourceImage = new(11, 22, 256, 420, false);
        Check(mirror.Update(identity, snapshot, true) && api.CurrentImage == api.SourceImage,
            "a mirrored image copies its source crop and visibility without replacing parts-list ownership");
        Check(mirror.Close() && api.CurrentImage == new PortraitPlateImageState(0, 0, 256, 420, true),
            "closing restores original crop and visibility along with the original texture");
        api.SourceImage = new(0, 0, 256, 420, true);
        mirror.Update(identity, snapshot, true);
        api.Source = 101;
        Check(mirror.Update(identity, snapshot, true) && api.CurrentKernel == 101 && api.Retains == 4 && api.Releases.SequenceEqual([100L, 100L, 100L]),
            "a changed editor render target restores and reacquires ownership without leaking the old temporary target");
        Check(mirror.Update(identity, snapshot, false) && api.CurrentKernel == 10 && api.Releases.Last() == 101,
            "disabling sync restores the display in place");
    }
    api = new FakeApi();
    using (var mirror = new PortraitPlateMirror(api))
    {
        mirror.Update(identity, snapshot, true);
        api.Alive = false;
        Check(mirror.Close() && api.Releases.SequenceEqual([10L]) && api.StaleReads == 0,
            "a destroyed native owner releases only the retained original lease and never dereferences its old node");
    }
    api = new FakeApi();
    using (var mirror = new PortraitPlateMirror(api))
    {
        mirror.Update(identity, snapshot, true);
        api.ExternalReplace(200);
        Check(mirror.Close() && api.CurrentKernel == 200 && api.Releases.SequenceEqual([10L]),
            "another owner's replacement remains intact while our detached original reference is released");
    }
    api = new FakeApi { FailWrite = true };
    using (var mirror = new PortraitPlateMirror(api))
        Check(!mirror.Update(identity, snapshot, true) && !mirror.IsActive && api.CurrentKernel == 10 && api.Releases.SequenceEqual([100L]),
            "an install failure releases the acquired render reference and preserves the original native image");
    api = new FakeApi { InvalidateOnRetain = true };
    using (var mirror = new PortraitPlateMirror(api))
        Check(!mirror.Update(identity, snapshot, true) && !mirror.IsActive && api.Writes == 0 && api.Releases.SequenceEqual([100L]) && api.StaleReads == 0,
            "owner replacement during native retain is checked before the final image write");
    api = new FakeApi { InvalidateEditorOnResolve = true };
    using (var mirror = new PortraitPlateMirror(api))
        Check(!mirror.Update(identity, snapshot, true) && !mirror.IsActive && api.Retains == 0 && api.Writes == 0,
            "an editor destroyed while resolving the plate never retains its now stale render texture");
    api = new FakeApi();
    var disposed = new PortraitPlateMirror(api);
    disposed.Update(identity, snapshot, true);
    disposed.Dispose(); disposed.Dispose();
    Check(api.CurrentKernel == 10 && api.Releases.SequenceEqual([100L]) && !disposed.Update(identity, snapshot, true),
        "plugin unload restores the native image once and rejects later updates");
    var character = new FakeApi(); var frame = new FakeApi { Source = 200 }; var accent = new FakeApi { Source = 300 };
    using (var mirror = new PortraitPlateMirror(character, frame, accent))
    {
        Check(mirror.Update(identity, snapshot, true) && character.CurrentKernel == 100 && frame.CurrentKernel == 200 && accent.CurrentKernel == 300,
            "portrait character frame and accent are mirrored through separate retained image leases");
        Check(mirror.Close() && character.CurrentKernel == 10 && frame.CurrentKernel == 10 && accent.CurrentKernel == 10
            && character.Releases.SequenceEqual([100L]) && frame.Releases.SequenceEqual([200L]) && accent.Releases.SequenceEqual([300L]),
            "closing the composite mirror restores all three native layers and releases each once");
        accent.FailWrite = true;
        Check(!mirror.Update(identity, snapshot, true) && !mirror.IsActive && character.CurrentKernel == 10 && frame.CurrentKernel == 10 && accent.CurrentKernel == 10,
            "a later overlay install failure rolls back earlier layers instead of leaving a partial portrait mirror");
        accent.FailWrite = false;
        mirror.Update(identity, snapshot, true);
        character.Source = 0;
        Check(!mirror.Update(identity, snapshot, true) && !mirror.IsActive && frame.CurrentKernel == 10 && accent.CurrentKernel == 10,
            "a failed character-layer refresh also closes prior frame and accent leases");
    }
    api = new FakeApi();
    using (var mirror = new PortraitPlateMirror(api))
    {
        mirror.Update(identity, snapshot, true);
        api.SourceImage = new(4, 8, 256, 420, false);
        Check(mirror.Update(identity, snapshot, true) && api.Retains == 1 && api.CurrentImage == api.SourceImage,
            "an already leased image updates its crop and visibility without retaining another texture");
        api.CurrentImage = new(16, 32, 128, 210, true);
        Check(mirror.Close() && api.CurrentKernel == 10 && api.CurrentImage == new PortraitPlateImageState(16, 32, 128, 210, true),
            "closing preserves another owner's later crop changes while returning the borrowed original texture");
    }
    api = new FakeApi { FailImageWrite = true, SourceImage = new(10, 10, 256, 420, false) };
    using (var mirror = new PortraitPlateMirror(api))
        Check(!mirror.Update(identity, snapshot, true) && !mirror.IsActive && api.CurrentKernel == 10 && api.Releases.SequenceEqual([100L]),
            "an image-state install failure rolls the texture back and releases its temporary reference");
    api = new FakeApi { Source = 0, SourceImage = new(0, 0, 256, 420, false) };
    using (var mirror = new PortraitPlateMirror(api))
    {
        Check(mirror.Update(identity, snapshot, true) && !api.CurrentImage.Visible && api.Retains == 0 && mirror.IsActive,
            "a native hidden empty overlay mirrors visibility without inventing or retaining a texture");
        Check(mirror.Close() && api.CurrentImage.Visible && api.CurrentKernel == 10,
            "closing a hidden empty overlay restores its original image and visibility");
    }
    Console.WriteLine($"{passed}/{passed} portrait plate mirror checks passed");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

sealed unsafe class FakeApi : IPortraitPlateMirrorApi
{
    public bool Framework = true, EditorCurrent = true, Resolvable = true, Alive = true, FailWrite, FailImageWrite, InvalidateOnRetain, InvalidateEditorOnResolve;
    public nint Source = 100;
    public int Retains, Writes, StaleReads;
    public List<long> Releases = [];
    public PortraitPlateImageState SourceImage = new(0, 0, 256, 420, true);
    public PortraitPlateImageState CurrentImage = new(0, 0, 256, 420, true);
    private AtkTexture current = Make(10);
    public long CurrentKernel => (long)current.KernelTexture;
    public bool IsFrameworkThread => Framework;
    public bool IsEditorCurrent(PortraitIdentity identity) => EditorCurrent;
    public PortraitPlateImageState GetSourceImage(PortraitIdentity identity) => SourceImage;
    public PortraitPlateImageState ReadImage(PortraitPlateBinding binding) => CurrentImage;
    public bool TryWriteImage(PortraitPlateBinding binding, PortraitPlateImageState expected, PortraitPlateImageState replacement)
    { if (!Alive || CurrentImage != expected || FailImageWrite && CurrentImage != replacement) return false; CurrentImage = replacement; return true; }
    public nint GetSourceTexture(PortraitIdentity identity) => Source;
    public bool TryResolve(PortraitIdentity identity, out PortraitPlateBinding binding)
    { binding = new(11, 12, 13, 14, 15, 16, 17, 18, 19); if (InvalidateEditorOnResolve) EditorCurrent = false; return Alive && Resolvable && CurrentKernel == 10; }
    public bool IsCurrent(PortraitPlateBinding binding) => Alive;
    public AtkTexture ReadTexture(PortraitPlateBinding binding)
    { if (!Alive) { StaleReads++; throw new InvalidOperationException("stale node read"); } return current; }
    public AtkTexture CreateRetainedTexture(PortraitIdentity identity, nint source)
    { Retains++; if (InvalidateOnRetain) Alive = false; return Make(source); }
    public bool TryWriteTexture(PortraitPlateBinding binding, AtkTexture expected, AtkTexture replacement)
    { if (!Alive) { StaleReads++; return false; } if (FailWrite || current.KernelTexture != expected.KernelTexture) return false; current = replacement; Writes++; return true; }
    public void ReleaseTexture(ref AtkTexture texture) { Releases.Add((long)texture.KernelTexture); texture = default; }
    public void ExternalReplace(nint texture) => current = Make(texture);
    private static AtkTexture Make(nint texture) => new() { KernelTexture = (Texture*)texture, TextureType = TextureType.KernelTexture };
}
#endif
