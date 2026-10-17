using Dalamud.Plugin.Services;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitAppearanceCatalog
{
    public PortraitAppearanceCatalog(IDataManager data) : this(data.GameData) { }
}
