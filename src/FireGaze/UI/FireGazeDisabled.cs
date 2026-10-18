using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

internal enum FireGazeButtonStyle { Primary, Enable, Danger }

internal readonly record struct FireGazeButtonColors(Vector4 Fill, Vector4 Hover, Vector4 Active, Vector4 Text);

/// <summary>Only the native ImGui boundary is substituted during offline lifecycle tests.</summary>
internal interface IFireGazeDisabledStack
{
    void BeginDisabled(bool disabled);
    void EndDisabled();
    void PushColor(ImGuiCol role, Vector4 value);
    void PopColors(int count);
}

/// <summary>Tracks inherited disabled input and gives disabled controls an opaque neutral appearance.</summary>
internal sealed class FireGazeDisabledState(IFireGazeDisabledStack native)
{
    private static readonly ImGuiCol[] SurfaceRoles =
    [
        ImGuiCol.Button, ImGuiCol.ButtonHovered, ImGuiCol.ButtonActive,
        ImGuiCol.FrameBg, ImGuiCol.FrameBgHovered, ImGuiCol.FrameBgActive,
        ImGuiCol.Header, ImGuiCol.HeaderHovered, ImGuiCol.HeaderActive,
    ];
    private static readonly ImGuiCol[] ForegroundRoles =
    [ImGuiCol.Text, ImGuiCol.CheckMark, ImGuiCol.SliderGrab, ImGuiCol.SliderGrabActive];
    private readonly Stack<(bool Disabled, int Colors)> frames = new();

    internal bool IsDisabled => frames.TryPeek(out var frame) && frame.Disabled;

    internal void Begin(bool disabled = true)
    {
        var effective = disabled || IsDisabled;
        native.BeginDisabled(disabled);
        var pushed = 0;
        try
        {
            if (effective)
            {
                foreach (var role in SurfaceRoles)
                {
                    native.PushColor(role, FireGazePalette.Hover);
                    pushed++;
                }
                foreach (var role in ForegroundRoles)
                {
                    native.PushColor(role, FireGazePalette.Muted);
                    pushed++;
                }
                native.PushColor(ImGuiCol.Border, FireGazePalette.Border);
                pushed++;
            }
            frames.Push((effective, pushed));
        }
        catch
        {
            if (pushed > 0) native.PopColors(pushed);
            native.EndDisabled();
            throw;
        }
    }

    internal void End()
    {
        if (!frames.TryPop(out var frame))
            throw new InvalidOperationException("FireGaze disabled scope has no matching Begin.");
        if (frame.Colors > 0) native.PopColors(frame.Colors);
        native.EndDisabled();
    }

    internal IDisposable Push(bool disabled = true)
    {
        Begin(disabled);
        return new Scope(this);
    }

    private sealed class Scope(FireGazeDisabledState state) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            state.End();
            disposed = true;
        }
    }
}

internal static class FireGazeDisabled
{
    // ImGui is drawn on the UI thread; no disabled state is shared with another thread.
    [ThreadStatic] private static FireGazeDisabledState? current;
    private static FireGazeDisabledState Current => current ??= new(NativeStack.Instance);
    internal static bool IsDisabled => current?.IsDisabled ?? false;
    internal static void Begin(bool disabled = true) => Current.Begin(disabled);
    internal static void End() => Current.End();
    internal static IDisposable Push(bool disabled = true) => Current.Push(disabled);

    private sealed class NativeStack : IFireGazeDisabledStack
    {
        internal static readonly NativeStack Instance = new();
        public void BeginDisabled(bool disabled) => ImGui.BeginDisabled(disabled);
        public void EndDisabled() => ImGui.EndDisabled();
        public void PushColor(ImGuiCol role, Vector4 value) => ImGui.PushStyleColor(role, value);
        public void PopColors(int count) => ImGui.PopStyleColor(count);
    }
}
