using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitPlateBinding(nint Addon, ushort AddonID, nint Node, nint Asset,
    ushort PartID, nint Storage, ulong AccountID, ulong ContentID, long Generation, nint Component = 0);
internal readonly record struct PortraitPlateImageState(ushort U, ushort V, ushort Width, ushort Height, bool Visible);

internal unsafe interface IPortraitPlateMirrorApi
{
    bool IsFrameworkThread { get; }
    bool IsEditorCurrent(PortraitIdentity identity);
    nint GetSourceTexture(PortraitIdentity identity);
    PortraitPlateImageState GetSourceImage(PortraitIdentity identity);
    bool TryResolve(PortraitIdentity identity, out PortraitPlateBinding binding);
    bool IsCurrent(PortraitPlateBinding binding);
    AtkTexture ReadTexture(PortraitPlateBinding binding);
    PortraitPlateImageState ReadImage(PortraitPlateBinding binding);
    bool TryWriteImage(PortraitPlateBinding binding, PortraitPlateImageState expected, PortraitPlateImageState replacement);
    AtkTexture CreateRetainedTexture(PortraitIdentity identity, nint source);
    bool TryWriteTexture(PortraitPlateBinding binding, AtkTexture expected, AtkTexture replacement);
    void ReleaseTexture(ref AtkTexture texture);
    bool TryCloseOwner(PortraitPlateBinding binding);
}

/// <summary>
/// Moves a native image's original texture ownership into a temporary lease. Only its visible
/// AtkTexture is replaced; CharaCard storage, saved portrait data and the world character are untouched.
/// Native portrait image, frame and accent each receive an independent temporary texture lease.
/// </summary>
internal sealed unsafe partial class PortraitPlateMirror : IDisposable
{
    private readonly IPortraitPlateMirrorApi api;
    private PortraitPlateBinding? binding;
    private PortraitIdentity editor;
    private AtkTexture original;
    private AtkTexture temporary;
    private PortraitPlateImageState originalImage;
    private PortraitPlateImageState temporaryImage;
    private bool ownsTexture;
    private readonly PortraitPlateMirror[] overlays = [];
    private bool busy;
    private bool disposing;
    private bool disposeRequested;
    private bool disposed;
    public bool IsActive => binding is not null || overlays.Any(overlay => overlay.IsActive);
    public string? LastError { get; private set; }
    public bool DisposalRestored { get; private set; } = true;
    internal event Action<string>? DisposalFailed;

    internal PortraitPlateMirror(IPortraitPlateMirrorApi api, params IPortraitPlateMirrorApi[] overlayAPIs)
    {
        this.api = api;
        overlays = overlayAPIs.Select(overlay => new PortraitPlateMirror(overlay)).ToArray();
    }

    public bool Update(PortraitIdentity identity, PortraitEditorSnapshot snapshot, bool enabled)
    {
        if (disposed || disposing) return false;
        if (!enabled) return Close();
        if (!UpdateSingle(identity, snapshot)) { _ = Close(); return false; }
        if (disposed || disposing) return false;
        foreach (var overlay in overlays)
        {
            if (overlay.UpdateSingle(identity, snapshot)) continue;
            LastError = overlay.LastError ?? "肖像边框或装饰尚未准备好。";
            _ = Close();
            return false;
        }
        return true;
    }

    private bool UpdateSingle(PortraitIdentity identity, PortraitEditorSnapshot snapshot)
    {
        if (disposed || disposing || busy || !api.IsFrameworkThread) return false;
        if (!snapshot.IsValid) return false;
        busy = true;
        try
        {
            var source = api.IsEditorCurrent(identity) ? api.GetSourceTexture(identity) : 0;
            if (!api.IsEditorCurrent(identity)) return false;
            var sourceImage = api.GetSourceImage(identity);
            var hiddenEmpty = source == 0 && !sourceImage.Visible;
            if (binding is { } active)
            {
                if (editor == identity && api.IsCurrent(active)
                    && (ownsTexture && source != 0 && Same(api.ReadTexture(active), temporary) && (nint)temporary.KernelTexture == source
                        || !ownsTexture && hiddenEmpty))
                {
                    if (!api.IsEditorCurrent(identity) || !api.TryWriteImage(active, temporaryImage, sourceImage)) return false;
                    temporaryImage = sourceImage;
                    return true;
                }
                if (!CloseCore()) return false;
            }
            if (source == 0 && !hiddenEmpty || !api.IsEditorCurrent(identity) || !api.TryResolve(identity, out var resolved)) return false;
            if (!api.IsCurrent(resolved)) return false;
            var before = api.ReadTexture(resolved);
            var imageBefore = api.ReadImage(resolved);
            if (!api.IsEditorCurrent(identity) || api.GetSourceTexture(identity) != source || !api.IsCurrent(resolved)) return false;
            if (hiddenEmpty)
            {
                if (!api.TryWriteImage(resolved, imageBefore, sourceImage)) return false;
                originalImage = imageBefore; temporaryImage = sourceImage; ownsTexture = false; binding = resolved; editor = identity;
                return true;
            }
            var next = api.CreateRetainedTexture(identity, source);
            var installed = false;
            try
            {
                if (!api.IsEditorCurrent(identity) || api.GetSourceTexture(identity) != source || !api.IsCurrent(resolved)) return false;
                if (!api.TryWriteTexture(resolved, before, next)) return false;
                original = before; temporary = next; originalImage = imageBefore; temporaryImage = imageBefore;
                binding = resolved; editor = identity; ownsTexture = true;
                installed = true;
                if (!api.IsEditorCurrent(identity) || !api.TryWriteImage(resolved, imageBefore, sourceImage))
                { _ = CloseCore(); return false; }
                temporaryImage = sourceImage;
                LastError = null;
                return true;
            }
            finally { if (!installed) api.ReleaseTexture(ref next); }
        }
        catch (Exception ex) { LastError = "冒险者铭牌临时显示无法更新：" + ex.Message; return false; }
        finally { CompleteOperation(); }
    }

    public bool Close()
    {
        if (busy || !api.IsFrameworkThread) return false;
        foreach (var overlay in overlays) if (!overlay.Close()) return false;
        busy = true;
        try { return CloseCore(); }
        catch (Exception ex) { LastError = "冒险者铭牌临时显示无法还原：" + ex.Message; return false; }
        finally { CompleteOperation(); }
    }

    private void CompleteOperation()
    {
        busy = false;
        if (disposeRequested) Dispose();
    }

    private bool CloseCore()
    {
        if (binding is not { } active) return true;
        if (!ownsTexture)
        {
            if (api.IsCurrent(active) && api.ReadImage(active) == temporaryImage
                && !api.TryWriteImage(active, temporaryImage, originalImage)) return false;
            binding = null; editor = default;
            return true;
        }
        if (api.IsCurrent(active) && Same(api.ReadTexture(active), temporary))
        {
            // Do not overwrite another plugin's UV/visibility edits. Restore ours only if those
            // exact values still belong to this lease; the original texture ownership is separate.
            if (api.ReadImage(active) == temporaryImage && !api.TryWriteImage(active, temporaryImage, originalImage)) return false;
            if (!api.TryWriteTexture(active, temporary, original)) return false;
            // Ownership of the original texture has moved back to the asset. Detach before
            // native ReleaseTexture, whose downstream hooks can reenter managed code.
            var released = temporary;
            binding = null; original = default; temporary = default; editor = default; ownsTexture = false;
            api.ReleaseTexture(ref released);
        }
        else
        {
            // The native destructor or a proper native replacement owns release of the temporary
            // texture. Never inspect an old asset or decrement that reference a second time.
            var released = original;
            binding = null; original = default; temporary = default; editor = default; ownsTexture = false;
            api.ReleaseTexture(ref released);
        }
        return true;
    }

    internal static bool Same(AtkTexture left, AtkTexture right) => left.VirtualTable == right.VirtualTable
        && left.TextureType == right.TextureType && left.KernelTexture == right.KernelTexture;

    public void Dispose()
    {
        if (disposed || disposing || !api.IsFrameworkThread) return;
        if (busy) { disposeRequested = true; return; }
        disposing = true;
        disposeRequested = false;
        try
        {
            _ = Close();
            foreach (var overlay in overlays)
            {
                overlay.Dispose();
                if (!overlay.DisposalRestored)
                {
                    DisposalRestored = false;
                    LastError = overlay.LastError;
                }
            }
            // A failed compare can mean a native callback has just replaced the owner. Re-read
            // once before the unload fallback; ordinary Close keeps its retryable lease.
            if (Close()) return;
            busy = true;
            try
            {
                if (binding is { } active && api.IsCurrent(active)) _ = api.TryCloseOwner(active);
                if (CloseCore()) return;
            }
            catch (Exception ex) { LastError = "冒险者铭牌卸载收尾失败：" + ex.Message; }
            finally { busy = false; }
            // The native asset still owns the installed temporary reference. Never release it
            // here: native replacement/finalization will do so. Only our detached original is ours.
            DisposalRestored = false;
            LastError = "本地铭牌预览未能还原，请关闭后重新打开铭牌。" + (LastError is null ? "" : " " + LastError);
            var releaseOriginal = ownsTexture;
            var released = original;
            binding = null; original = default; temporary = default; editor = default; ownsTexture = false;
            if (releaseOriginal) api.ReleaseTexture(ref released);
        }
        finally
        {
            UnregisterNativeEvents();
            disposed = true;
            disposing = false;
            disposeRequested = false;
            // A native callback can request disposal while an operation is still busy. Its
            // caller may already have detached this mirror, so report the eventual result once.
            var notify = DisposalFailed;
            DisposalFailed = null;
            if (!DisposalRestored)
            {
                try { notify?.Invoke(LastError ?? "本地铭牌预览未能还原，请关闭后重新打开铭牌。"); }
                catch { /* Reporting failure must not retain a disposed native lease. */ }
            }
        }
    }

    partial void UnregisterNativeEvents();
}
