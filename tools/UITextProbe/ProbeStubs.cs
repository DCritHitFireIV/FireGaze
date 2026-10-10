// 探针专用存根：抽取器 / 补丁器源码里引用 Plugin.Log 的日志调用。
// 探针不加载 Dalamud，这里给一个什么都不做的替身，保证与插件共用同一份源码。
namespace FireGaze;

internal static class Plugin
{
    public static ProbeLog Log { get; } = new();
}

internal sealed class ProbeLog
{
    public void Debug(string message) { }

    public void Information(string message) { }

    public void Warning(string message) { }

    public void Warning(Exception exception, string message) { }

    public void Error(string message) { }

    public void Error(Exception exception, string message) { }
}
