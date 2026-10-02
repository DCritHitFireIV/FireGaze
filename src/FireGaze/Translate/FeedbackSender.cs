using System.Reflection;
using System.Text;
using FireGaze.Diagnostics;
using FireGaze.UIText;

namespace FireGaze.Translate;

/// <summary>
///     用户反馈：把「内容 + 可选诊断日志」打包成反馈投稿，走与译文投稿同一条中继
///     （<see cref="ContributeRelay" />，客户端不持有任何凭据），由服务端机器人建 issue。
/// </summary>
internal static class FeedbackSender
{
    /// <summary>随反馈附带的日志条数上限（Info 及以上、已脱敏）。</summary>
    public const int LogEntries = 400;

    /// <summary>组装反馈正文（预览与发送共用同一份）。</summary>
    public static string BuildBody(string category, string text, bool attachLog)
    {
        var builder = new StringBuilder();
        builder.AppendLine("### FireGaze 反馈");
        builder.AppendLine();
        builder.AppendLine($"- 分类：{category}");
        builder.AppendLine($"- 版本：{VersionLine()}");
        builder.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine("### 内容");
        builder.AppendLine();
        builder.AppendLine(text.Trim());
        builder.AppendLine();

        if (attachLog)
        {
            builder.AppendLine("<details><summary>诊断日志（已脱敏，最近 " + LogEntries + " 行）</summary>");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(ActivityLog.Snapshot(LogEntries, ActivityLevel.Info, redact: true));
            builder.AppendLine("```");
            builder.AppendLine("</details>");
        }

        return builder.ToString();
    }

    /// <summary>发送（后台线程调用）。返回（是否成功，issue 地址或失败原因）。</summary>
    public static async Task<(bool Ok, string Message)> SendAsync(string category, string text, bool attachLog)
    {
        var body = BuildBody(category, text, attachLog);
        var title = "[反馈] " + UITextText.OneLine(text.Trim(), 48);
        return await ContributeRelay.TrySubmitAsync(title, body, "feedback").ConfigureAwait(false);
    }

    private static string VersionLine()
    {
        var firegaze = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        string dalamud;
        try
        {
            dalamud = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly.GetName().Version?.ToString() ?? "?";
        }
        catch
        {
            dalamud = "?";
        }

        return $"FireGaze {firegaze} · 卫月 {dalamud} · {Environment.OSVersion.VersionString}";
    }
}
