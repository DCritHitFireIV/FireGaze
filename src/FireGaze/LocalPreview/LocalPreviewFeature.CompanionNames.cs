using Dalamud.Plugin.Services;

namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    private readonly INamePlateGui? namePlateGUI;
    private CompanionNameplateController? companionNames;

    private void InitializeCompanionNames()
    {
        if (namePlateGUI is null) return;
        companionNames = new(namePlateGUI, address => companions?.OwnsChild(address) == true,
            config.LocalPreviewShowCompanionNames,
            failure => Log.Debug(failure, "[LocalPreview] 宠物名称显示回调未完成"));
    }

    private void RefreshCompanionNames()
        => companionNames?.SetShowNames(config.LocalPreviewShowCompanionNames);
}
