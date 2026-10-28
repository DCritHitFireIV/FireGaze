using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace FireGaze.LocalPreview;

/// <summary>Controls only nameplates belonging to independently owned companion actors.</summary>
internal sealed unsafe class CompanionNameplateController : IDisposable
{
    private readonly INamePlateGui gui;
    private readonly Func<nint, bool> ownsChild;
    private readonly Action<Exception>? reportFailure;
    private readonly Dictionary<(nint Child, nint Plate), Restore> pending = [];
    private bool showNames;
    private bool disposed;

    private sealed record Restore(SeString Name, nint RemovalMarker);

    public CompanionNameplateController(INamePlateGui gui, Func<nint, bool> ownsChild, bool showNames,
        Action<Exception>? reportFailure = null)
    {
        this.gui = gui;
        this.ownsChild = ownsChild;
        this.showNames = showNames;
        this.reportFailure = reportFailure;
        gui.OnDataUpdate += BeforeNativeUpdate;
        gui.OnPostDataUpdate += AfterNativeUpdate;
    }

    public void SetShowNames(bool value)
    {
        if (disposed || showNames == value) return;
        showNames = value;
        gui.RequestRedraw();
    }

    private void BeforeNativeUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        // No handler or actor pointer is retained. Pending contains detached strings and
        // numeric identities solely for this paired callback; a new pre callback discards
        // an incomplete old pair before examining any new frame.
        pending.Clear();
        if (disposed || showNames) return;
        foreach (var handler in handlers)
        {
            SeString? original = null;
            nint address = 0;
            bool removalAttempted = false;
            try
            {
                address = handler.GameObject?.Address ?? 0;
                if (address == 0 || !ownsChild(address)) continue;
                original = handler.Name ?? handler.InfoView.Name;
                var key = (address, handler.NamePlateInfoAddress);
                removalAttempted = true;
                handler.RemoveName();
                // This is the shared removed-string sentinel, never an actor pointer.
                pending[key] = new(original, (nint)handler.GetFieldAsPointer(NamePlateStringField.Name));
            }
            catch (Exception failure)
            {
                // A wrapper can throw after mutating its string field. The handler is
                // still valid in this callback, so undo only our partial mutation now.
                if (removalAttempted && original is not null)
                {
                    try { if (ownsChild(address)) handler.SetField(NamePlateStringField.Name, original); }
                    catch (Exception restoreFailure) { Report(restoreFailure); }
                }
                Report(failure);
            }
        }
    }

    private void AfterNativeUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        try
        {
            foreach (var handler in handlers)
            {
                try
                {
                    var address = handler.GameObject?.Address ?? 0;
                    if (address == 0 || !ownsChild(address)
                        || !pending.Remove((address, handler.NamePlateInfoAddress), out var restore)) continue;
                    // Native OnRequestedUpdate has copied the hidden field into the
                    // rendered node. Restore its backing string now, while this handler
                    // is valid, so enabling or unloading needs only a native redraw.
                    // A later plugin's replacement pointer wins over our restoration.
                    if ((nint)handler.GetFieldAsPointer(NamePlateStringField.Name) == restore.RemovalMarker)
                        handler.SetField(NamePlateStringField.Name, restore.Name);
                }
                catch (Exception failure) { Report(failure); }
            }
        }
        finally { pending.Clear(); }
    }

    private void Report(Exception failure)
    {
        try { reportFailure?.Invoke(failure); }
        catch { /* Nameplate callbacks must never interrupt the native render path. */ }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        gui.OnDataUpdate -= BeforeNativeUpdate;
        gui.OnPostDataUpdate -= AfterNativeUpdate;
        pending.Clear();
        gui.RequestRedraw();
    }
}
