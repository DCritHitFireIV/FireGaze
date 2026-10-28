namespace FireGaze.LocalPreview.Core;

/// <summary>
/// Pure admission policy for independent companions.  The native runtime supplies
/// the two observed mount signals; this policy keeps the safety decision testable
/// without dereferencing a live game object.
/// </summary>
public static class CompanionMountPolicy
{
    public const string PlayerFacingReason = "请先下坐骑，再召唤本地宠物。";

    public static bool IsNativeMountActive(uint mountId, bool mountObjectPresent)
        => mountId != 0 || mountObjectPresent;
}
