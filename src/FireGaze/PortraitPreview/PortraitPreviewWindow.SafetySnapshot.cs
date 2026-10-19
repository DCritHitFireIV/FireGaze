namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitSafetyWindowLight(bool ShowLight, long PointerToken, long Serial);

internal sealed partial class PortraitPreviewWindow
{
    // Detached managed values only; never reserve a token or begin/cancel a gesture while sampling.
    internal PortraitSafetyWindowLight ReadSafetyLightState() => new(showLight, lightPointerToken, lightTokenSerial);
}
