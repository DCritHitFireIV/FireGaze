using System.Numerics;

namespace FireGaze.PortraitPreview;

/// <summary>Immutable metadata used to decide whether a captured image can be shown in the space viewport.</summary>
internal readonly record struct PortraitSpaceLiveImageInfo(PortraitImageScope Scope, int Width, int Height);

/// <summary>Guards the space viewport from mixing scopes or drawing invalid capture dimensions.</summary>
internal static class PortraitLightingSpaceImage
{
    public static bool TryFit(
        PortraitSpaceLiveImageInfo? image,
        PortraitImageScope requiredScope,
        Vector2 available,
        out PortraitComparisonRect fit)
    {
        fit = default;
        if (image is not { } value || value.Scope != requiredScope)
            return false;
        return PortraitComparisonLayout.TryFit(available, new(value.Width, value.Height), out fit);
    }
}
