using System.Text.Json;

namespace FireGaze.PortraitPreview;

/// <summary>Only portable values; native pointers and persistent banner records never enter history.</summary>
internal sealed record PortraitEditorSnapshot
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public string GameBuild { get; init; } = "";
    public byte EntryPoint { get; init; }
    public PortraitLook Look { get; init; } = new(new PortraitPiece[13], true, true, false);
    public byte[] Customize { get; init; } = new byte[26];
    public bool HideVieraEars { get; init; }
    public PortraitComposition Composition { get; init; } = new();

    public PortraitEditorSnapshot Copy() => this with { Look = Look.Copy(), Customize = (byte[])Customize.Clone() };
    public string ToJSON() => JsonSerializer.Serialize(this);
    public static PortraitEditorSnapshot? FromJSON(string snapshotJSON)
    {
        try
        {
            var value = JsonSerializer.Deserialize<PortraitEditorSnapshot>(snapshotJSON);
            return value?.IsValid == true ? value.Copy() : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { return null; }
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Version == CurrentVersion && GameBuild is { Length: <= 128 }
        && Look?.Pieces is { Length: 13 } && Look.Pieces[12].ItemID <= ushort.MaxValue
        && Customize is { Length: 26 } && Composition is not null && Composition.IsValid;
    public bool IsCompatibleWith(PortraitEditorSnapshot current) => IsValid && current.IsValid
        && GameBuild == current.GameBuild && EntryPoint == current.EntryPoint
        && Customize[0] == current.Customize[0] && Customize[1] == current.Customize[1]
        && Customize[4] == current.Customize[4];
    public bool SameForHistory(PortraitEditorSnapshot other)
    {
        if (!IsCompatibleWith(other) || !Customize.SequenceEqual(other.Customize) || HideVieraEars != other.HideVieraEars
            || Look.HeadVisible != other.Look.HeadVisible || Look.WeaponVisible != other.Look.WeaponVisible
            || Look.Visor != other.Look.Visor || !Look.Pieces.SequenceEqual(other.Look.Pieces)) return false;
        var a = Composition; var b = other.Composition;
        // Playback advances without user edits. A paused timeline/slider position remains meaningful.
        if (!a.Paused && !b.Paused && a.BannerTimeline == b.BannerTimeline)
        { a = a with { AnimationProgress = 0 }; b = b with { AnimationProgress = 0 }; }
        return a == b;
    }
}

internal readonly record struct PortraitVector4(float X, float Y, float Z, float W)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) && float.IsFinite(W);
}
internal readonly record struct PortraitVector2(float X, float Y)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y);
}
internal sealed record PortraitComposition
{
    public PortraitVector4 CameraPosition { get; init; }
    public PortraitVector4 CameraTarget { get; init; }
    public float CameraYaw { get; init; }
    public float CameraPitch { get; init; }
    public float CameraDistance { get; init; }
    public float CameraZoomNormalized { get; init; }
    public short ImageRotation { get; init; }
    public byte CameraZoom { get; init; }
    public ushort BannerTimeline { get; init; }
    public float AnimationProgress { get; init; }
    public byte Expression { get; init; }
    public PortraitVector2 HeadDirection { get; init; }
    public PortraitVector2 EyeDirection { get; init; }
    public byte DirectionalRed { get; init; }
    public byte DirectionalGreen { get; init; }
    public byte DirectionalBlue { get; init; }
    public byte DirectionalBrightness { get; init; }
    public short DirectionalVerticalAngle { get; init; }
    public short DirectionalHorizontalAngle { get; init; }
    public byte AmbientRed { get; init; }
    public byte AmbientGreen { get; init; }
    public byte AmbientBlue { get; init; }
    public byte AmbientBrightness { get; init; }
    public ushort Background { get; init; }
    public ushort Frame { get; init; }
    public ushort Decoration { get; init; }
    public short PoseClassJob { get; init; }
    public bool HeadFacingCamera { get; init; }
    public bool EyesFacingCamera { get; init; }
    public bool Paused { get; init; }
    public bool CharacterVisible { get; init; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => CameraPosition.IsFinite && CameraTarget.IsFinite && HeadDirection.IsFinite && EyeDirection.IsFinite
        && float.IsFinite(CameraYaw) && float.IsFinite(CameraPitch) && float.IsFinite(CameraDistance)
        && float.IsFinite(CameraZoomNormalized) && float.IsFinite(AnimationProgress) && AnimationProgress >= 0;
}
