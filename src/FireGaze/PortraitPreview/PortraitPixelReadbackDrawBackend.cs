using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

/// <summary>Native acquisition is admitted only within the host's public Draw callback.</summary>
internal interface IPortraitPixelReadbackDrawBackend : IPortraitPixelReadbackBackend
{
    IPortraitPixelReadbackSource CreateSource(IDalamudTextureWrap texture);
    IDisposable EnterDraw();
    void Invalidate();
}
