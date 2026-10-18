using System.Numerics;

namespace FireGaze.PortraitPreview;

/// <summary>UI-owned image reference. Setting a reference never restores or modifies editor state.</summary>
internal sealed class PortraitComparisonReference<T> : IDisposable where T : class, IDisposable
{
    private T? current;
    private string? scope;
    private bool disposed;

    public T? For(string currentScope) => !disposed && scope == currentScope ? current : null;

    // Ownership always transfers, including a late result after the comparison window closes.
    public void Set(string currentScope, T owned)
    {
        ArgumentNullException.ThrowIfNull(owned);
        if (ReferenceEquals(current, owned)) return;
        if (disposed || string.IsNullOrEmpty(currentScope)) { owned.Dispose(); return; }
        var previous = current;
        current = owned;
        scope = currentScope;
        previous?.Dispose();
    }

    public void Clear()
    {
        var previous = current;
        current = null;
        scope = null;
        previous?.Dispose();
    }

    public void Dispose() { if (disposed) return; disposed = true; Clear(); }
}

internal readonly record struct PortraitComparisonRect(Vector2 Offset, Vector2 Size);
internal readonly record struct PortraitComparisonWipe(float Cut, float UvCut);

internal static class PortraitComparisonLayout
{
    public static bool TryFit(Vector2 available, Vector2 source, out PortraitComparisonRect rectangle)
    {
        rectangle = default;
        if (!Valid(available) || !Valid(source)) return false;
        var scale = Math.Min((double)available.X / source.X, (double)available.Y / source.Y);
        var size = new Vector2((float)(source.X * scale), (float)(source.Y * scale));
        rectangle = new((available - size) / 2, size);
        return true;
    }

    public static bool TryWipe(Vector2 size, float fraction, out PortraitComparisonWipe wipe)
    {
        wipe = default;
        if (!Valid(size) || !float.IsFinite(fraction)) return false;
        var clamped = Math.Clamp(fraction, 0, 1);
        wipe = new(size.X * clamped, clamped);
        return true;
    }

    private static bool Valid(Vector2 size) => float.IsFinite(size.X) && float.IsFinite(size.Y)
        && size.X > 0 && size.Y > 0;
}
