namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class NativePortraitTarget
{
    private readonly HashSet<PortraitIdentity> retiredEditorOwners = [];

    /// <summary>
    /// Positive proof only: a still-live editor has detached the old state, or a complete fresh
    /// resolution identifies a different native owner. Hidden/loading/unavailable is not proof.
    /// No pointer carried by the supplied old identity is ever dereferenced here.
    /// </summary>
    public bool IsEditorOwnerDestroyed(PortraitIdentity identity)
    {
        if (disposed || !api.IsFrameworkThread || !identity.IsValid) return false;
        if (retiredEditorOwners.Contains(identity)) return true;
        try
        {
            var editor = api.GetEditor();
            if (editor == null) return false;
            var currentState = (nint)editor->EditorState;
            if ((nint)editor == identity.Editor && currentState != identity.State
                && api.GetEditor() == editor && (nint)editor->EditorState == currentState)
            {
                retiredEditorOwners.Add(identity);
                return true;
            }
            if (!Resolve(out var current, out _, out _, requireReady: false) || current == identity) return false;
            retiredEditorOwners.Add(identity);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
