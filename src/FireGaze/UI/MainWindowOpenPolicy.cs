using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>
/// Keeps explicit navigation usable when ImGui persisted the main window in its
/// collapsed title-bar state. This is an action boundary, never a per-frame rule.
/// </summary>
internal static class MainWindowOpenPolicy
{
    private static readonly HashSet<Window> Pending = new();

    /// <summary>
    ///     Request one expansion at an explicit navigation boundary. The
    ///     condition is intentionally Always for the next host draw only; it
    ///     must be released afterwards or ImGui will reopen a user-collapsed
    ///     title bar every frame.
    /// </summary>
    public static void ExpandForExplicitOpen(Window window)
    {
        window.Collapsed = false;
        window.CollapsedCondition = ImGuiCond.Always;
        Pending.Add(window);
    }

    /// <summary>
    ///     Called from <see cref="Window.PostDraw"/> after WindowHost has
    ///     applied the one-shot expansion condition.
    /// </summary>
    public static void AfterExplicitOpenDraw(Window window)
    {
        if (!Pending.Remove(window)) return;
        window.Collapsed = null;
        window.CollapsedCondition = ImGuiCond.None;
    }
}
