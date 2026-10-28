using System.Numerics;

namespace FireGaze.PortraitPreview;

/// <summary>
/// A detached, identity-tagged image owned by the product workspace source.
/// The descriptor is deliberately independent of the window renderer so capture
/// and continuity tests can compile the same contract without pulling UI code.
/// </summary>
internal readonly record struct PortraitWorkspaceImage(
    ulong Handle, Vector2 UV0, Vector2 UV1, uint Tint, int PixelWidth, int PixelHeight,
    PortraitImageScope Scope, ulong Owner, string Editor, uint Generation, string VisualKey)
{
    public bool IsValid => Handle != 0 && PixelWidth > 0 && PixelHeight > 0
        && Scope == PortraitImageScope.VisibleAdventurerPlate
        && Owner != 0 && !string.IsNullOrWhiteSpace(Editor) && Generation != 0
        && !string.IsNullOrWhiteSpace(VisualKey);
}

internal sealed class PortraitWorkspaceImageBorrow(Func<PortraitWorkspaceImage?> read, IDisposable? lifetime = null) : IDisposable
{
    private Func<PortraitWorkspaceImage?>? read = read;
    private IDisposable? lifetime = lifetime;

    public bool TryGetImage(out PortraitWorkspaceImage image)
    {
        image = default;
        var getter = Volatile.Read(ref read);
        if (getter is null) return false;
        var candidate = getter();
        if (candidate is not { IsValid: true }) return false;
        image = candidate.Value;
        return true;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref read, null);
        Interlocked.Exchange(ref lifetime, null)?.Dispose();
    }
}
