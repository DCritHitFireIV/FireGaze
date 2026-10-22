namespace FireGaze.Internal;

internal static class PreviewFeatureNotice
{
    public const string Message = "FireGaze: 上线了坐骑试用和肖像同步预览的功能，现在可以试用坐骑、预览铭牌效果了";

    public static bool TryAnnounce(bool loggedIn, bool featuresReady, global::FireGaze.Configuration config,
        Action<string> print, Action save)
    {
        if (!loggedIn || !featuresReady || config.PreviewFeaturesAnnounced) return false;
        print(Message);
        config.PreviewFeaturesAnnounced = true;
        save();
        return true;
    }
}
