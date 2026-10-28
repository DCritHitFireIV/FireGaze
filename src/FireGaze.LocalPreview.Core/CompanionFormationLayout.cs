using System.Numerics;

namespace FireGaze.LocalPreview.Core;

public static class CompanionFormationLayout
{
    public static float GetSpacing(float bodyRadius) => Math.Max(1.25f, bodyRadius * 2 + .2f);

    /// <summary>Stable four-wide slots behind the owner; Y is subsequently terrain projected by the host.</summary>
    public static Vector3 GetDestination(int slot, Vector3 ownerPosition, float ownerRotation, float spacing)
    {
        if (slot < 0 || !float.IsFinite(spacing) || spacing <= 0 || !float.IsFinite(ownerRotation))
            throw new ArgumentOutOfRangeException(nameof(slot));
        float lateral = ((slot % 4) - 1.5f) * spacing;
        float behind = (1 + slot / 4) * spacing;
        var right = new Vector3(MathF.Cos(ownerRotation), 0, -MathF.Sin(ownerRotation));
        var forward = new Vector3(MathF.Sin(ownerRotation), 0, MathF.Cos(ownerRotation));
        return ownerPosition + right * lateral - forward * behind;
    }
}
