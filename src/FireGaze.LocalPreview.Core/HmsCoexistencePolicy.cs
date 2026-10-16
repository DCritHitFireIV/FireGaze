namespace FireGaze.LocalPreview.Core;

public readonly record struct HmsCoexistenceDecision(bool Allowed, string Reason);

public static class HmsCoexistencePolicy
{
    public static HmsCoexistenceDecision Decide(int loadedHmsCount, int? bridgeApiVersion)
    {
        if (loadedHmsCount == 0) return new(true, "");
        if (loadedHmsCount != 1)
            return new(false, "检测到多个 HMS 插件同时加载，请仅保留一个配套兼容版，以免重复控制角色。");
        if (bridgeApiVersion != 1)
            return new(false, "当前 HMS 缺少可用的试用兼容接口，请换用配套兼容版后再试。");
        return new(true, "");
    }
}
