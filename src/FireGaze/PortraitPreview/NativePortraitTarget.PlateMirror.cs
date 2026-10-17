namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class NativePortraitTarget
{
    public bool IsPlateMirrorActive => plateMirror?.IsActive ?? false;
    public string? PlateMirrorError => plateMirror?.LastError;
    public bool UpdatePlateMirror(PortraitIdentity identity, PortraitEditorSnapshot snapshot, bool enabled)
    {
        if (disposed || !api.IsFrameworkThread) return false;
        if (!enabled) return ClosePlateMirror();
        if (plateMirror is null || !IsCurrent(identity)) return false;
        var success=plateMirror.Update(identity,snapshot,true);
        if(!success && plateMirror.LastError is not null) LastError=plateMirror.LastError;
        return success;
    }
    public bool ClosePlateMirror()
    {
        if (disposed || !api.IsFrameworkThread) return false;
        var success=plateMirror?.Close() ?? true;
        if(!success && plateMirror?.LastError is not null) LastError=plateMirror.LastError;
        return success;
    }
}
