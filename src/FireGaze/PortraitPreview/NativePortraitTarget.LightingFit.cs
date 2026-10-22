using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitNativeLightColors(byte DirectionalRed, byte DirectionalGreen, byte DirectionalBlue,
    byte AmbientRed, byte AmbientGreen, byte AmbientBlue)
{
    public int[] Parameters => [DirectionalRed, DirectionalGreen, DirectionalBlue, AmbientRed, AmbientGreen, AmbientBlue];
    public static PortraitNativeLightColors From(PortraitComposition value) => new(value.DirectionalRed, value.DirectionalGreen,
        value.DirectionalBlue, value.AmbientRed, value.AmbientGreen, value.AmbientBlue);
    public static bool TryCreate(IReadOnlyList<int> parameters, out PortraitNativeLightColors colors)
    {
        colors = default;
        if (parameters is not { Count: 6 } || parameters.Any(value => value is < 0 or > 255)) return false;
        colors = new((byte)parameters[0], (byte)parameters[1], (byte)parameters[2], (byte)parameters[3], (byte)parameters[4], (byte)parameters[5]);
        return true;
    }
    public PortraitComposition Apply(PortraitComposition current) => current with
    {
        DirectionalRed = DirectionalRed, DirectionalGreen = DirectionalGreen, DirectionalBlue = DirectionalBlue,
        AmbientRed = AmbientRed, AmbientGreen = AmbientGreen, AmbientBlue = AmbientBlue,
    };
}

/// <summary>Raw slider metadata. Steps is deliberately not interpreted as a quantization quantum.</summary>
internal readonly record struct PortraitNativeLightControl(int Minimum, int Maximum, int Value, int Steps);
internal sealed record PortraitNativeLightWriteResult(bool Applied, PortraitEditorSnapshot? Actual, PortraitNativeLightColors? Owned);
internal unsafe interface IPortraitLightingControlsNativeApi
{
    PortraitNativeLightControl[]? ReadLightControls(AgentBannerEditorState* state);
    void SyncLightColors(AgentBannerEditorState* state, PortraitNativeLightColors colors);
}

internal sealed unsafe partial class NativePortraitTarget
{
    public PortraitNativeLightControl[]? ReadLightControls(PortraitIdentity identity)
    {
        if (api is not IPortraitLightingControlsNativeApi controls || !TryResolve(out var current, out _, out _) || current != identity) return null;
        var result = controls.ReadLightControls((AgentBannerEditorState*)identity.State);
        return IsCurrent(identity) && result is { Length: 6 } ? result : null;
    }

    /// <summary>
    /// Compare the entire current snapshot, apply only two RGB triplets, then read back. A failed
    /// native call can be partial: Owned reports attempted values for conditional compensation;
    /// it never reports false/no-mutation when a previous triplet may already have been changed.
    /// </summary>
    public PortraitNativeLightWriteResult WriteLightColors(PortraitIdentity identity, PortraitEditorSnapshot expected,
        PortraitNativeLightColors desired)
    {
        if (expected is null || !expected.IsValid || api is not IPortraitEditorNativeApi native
            || CaptureEditorState(identity) is not { } current || current.ToJSON() != expected.ToJSON()) return new(false, null, null);
        var originalColors = PortraitNativeLightColors.From(current.Composition);
        if (originalColors == desired) return new(true, current, desired);
        PortraitNativeLightColors? attempted = null;
        try
        {
            var view = (CharaViewPortrait*)identity.Model;
            var intermediate = originalColors with
            { DirectionalRed = desired.DirectionalRed, DirectionalGreen = desired.DirectionalGreen, DirectionalBlue = desired.DirectionalBlue };
            if (intermediate != originalColors)
            {
                attempted = intermediate;
                native.SetDirectionalColor(view, desired.DirectionalRed, desired.DirectionalGreen, desired.DirectionalBlue);
                if (!Matches(identity, current with { Composition = intermediate.Apply(current.Composition) }, out _)) return Partial();
            }
            if (intermediate != desired)
            {
                attempted = desired;
                native.SetAmbientColor(view, desired.AmbientRed, desired.AmbientGreen, desired.AmbientBlue);
            }
            if (!Matches(identity, current with { Composition = desired.Apply(current.Composition) }, out var after)) return Partial();
            var state = (AgentBannerEditorState*)identity.State;
            // These are editor-local fields, not a persisted BannerModule entry or world actor.
            state->BannerEntry.DirectionalLightingColorRed = desired.DirectionalRed;
            state->BannerEntry.DirectionalLightingColorGreen = desired.DirectionalGreen;
            state->BannerEntry.DirectionalLightingColorBlue = desired.DirectionalBlue;
            state->BannerEntry.AmbientLightingColorRed = desired.AmbientRed;
            state->BannerEntry.AmbientLightingColorGreen = desired.AmbientGreen;
            state->BannerEntry.AmbientLightingColorBlue = desired.AmbientBlue;
            if (api is IPortraitLightingControlsNativeApi controls && IsCurrent(identity)) controls.SyncLightColors(state, desired);
            return Matches(identity, current with { Composition = desired.Apply(current.Composition) }, out after)
                ? new(true, after, desired) : Partial();
        }
        catch (Exception exception)
        {
            LastError = "灯光颜色暂时无法调整：" + exception.Message;
            return Partial();
        }

        PortraitNativeLightWriteResult Partial()
        {
            // Never dereference the stored pointer after allocation/generation replacement.
            PortraitEditorSnapshot? actual = null;
            try { if (IsCurrent(identity)) actual = CaptureEditorState(identity); } catch { }
            // Temporary unreadability is not proof of destruction. Retain the opaque owner's
            // attempted values; the adapter's lifecycle check decides when that lease is gone.
            return new(false, actual, attempted);
        }
    }

    private bool Matches(PortraitIdentity identity, PortraitEditorSnapshot expected, out PortraitEditorSnapshot? actual)
    {
        actual = CaptureEditorState(identity);
        return actual is not null && expected.ToJSON() == actual.ToJSON();
    }

    private sealed partial class LiveApi : IPortraitLightingControlsNativeApi
    {
        public PortraitNativeLightControl[]? ReadLightControls(AgentBannerEditorState* state)
        {
            var result = new PortraitNativeLightControl[6];
            for (var axis = 0; axis < 6; axis++)
            {
                var addon = CurrentAddon(state);
                if (addon == null) return null;
                var slider = axis switch
                {
                    0 => addon->DirectionalLightingColorRedSlider, 1 => addon->DirectionalLightingColorGreenSlider,
                    2 => addon->DirectionalLightingColorBlueSlider, 3 => addon->AmbientLightingColorRedSlider,
                    4 => addon->AmbientLightingColorGreenSlider, _ => addon->AmbientLightingColorBlueSlider,
                };
                if (slider == null) return null;
                result[axis] = new(slider->MinValue, slider->MaxValue, slider->Value, slider->Steps);
            }
            return CurrentAddon(state) != null ? result : null;
        }

        public void SyncLightColors(AgentBannerEditorState* state, PortraitNativeLightColors colors)
        {
            var values = colors.Parameters;
            for (var axis = 0; axis < 6; axis++)
            {
                var addon = CurrentAddon(state);
                if (addon == null) return;
                var slider = axis switch
                {
                    0 => addon->DirectionalLightingColorRedSlider, 1 => addon->DirectionalLightingColorGreenSlider,
                    2 => addon->DirectionalLightingColorBlueSlider, 3 => addon->AmbientLightingColorRedSlider,
                    4 => addon->AmbientLightingColorGreenSlider, _ => addon->AmbientLightingColorBlueSlider,
                };
                if (slider != null) slider->SetValue(values[axis], false);
            }
            if (CurrentAddon(state) != null) state->SetHasChanged(true);
        }
    }
}
