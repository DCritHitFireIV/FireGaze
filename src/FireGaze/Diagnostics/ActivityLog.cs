using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FireGaze.Diagnostics;

/// <summary>日志级别（与 <see cref="ActivityLog" /> 的口径一一对应，见 docs/logging.md）。</summary>
internal enum ActivityLevel
{
    /// <summary>逐条细节，默认不导出。</summary>
    Trace = 0,

    /// <summary>阶段边界、计数、开关状态。</summary>
    Debug = 1,

    /// <summary>用户可感知的正常动作与结果。</summary>
    Info = 2,

    /// <summary>已恢复/可继续，但有损失或走了兜底。</summary>
    Warning = 3,

    /// <summary>本次动作失败，用户有感知。</summary>
    Error = 4,

    /// <summary>数据损坏 / 进程风险 / 不可恢复。</summary>
    Critical = 5,
}

/// <summary>一条日志。</summary>
internal sealed record ActivityEntry(
    DateTime Timestamp,
    ActivityLevel Level,
    string Scope,
    string Message,
    string? Exception);

/// <summary>
///     FireGaze 自己的诊断日志（内存环形缓冲 + 后台落盘 + 脱敏导出）。
/// </summary>
/// <remarks>
///     规范见 <c>~/.pi/agent/skills/logging-conventions/SKILL.md</c> 与 <c>docs/logging.md</c>：
///     · 绝不在渲染线程写文件——写入进队列，后台计时器每 2 秒刷一次盘；
///     · 日志系统自身故障静默降级，绝不拖垮插件；
///     · 导出前脱敏（用户目录、token、通知 URL）；
///     · 异常记完整调用链（<c>Exception.ToString()</c>）。
/// </remarks>
internal static class ActivityLog
{
    /// <summary>内存里保留的条数（反馈导出取最近 N 条）。</summary>
    public const int RetainedEntries = 1500;

    private static readonly object Gate = new();
    private static readonly List<ActivityEntry> Recent = new();
    private static readonly ConcurrentQueue<ActivityEntry> Pending = new();
    private static readonly Regex UserPath = new(@"[A-Za-z]:\\Users\\[^\\\s]+", RegexOptions.Compiled);
    private static readonly Regex UserPathUnix = new(@"/home/[^/\s]+", RegexOptions.Compiled);
    private static readonly Regex SecretToken = new(
        @"(ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9]{16,}|sctapi\.ftqq\.com/\S+)",
        RegexOptions.Compiled);

    private static string? logDirectory;
    private static Timer? flushTimer;
    private static int started;

    /// <summary>同步到卫月日志（由 <see cref="Plugin" /> 注入；可为空，用于测试）。</summary>
    public static Action<ActivityLevel, string>? Mirror { get; set; }

    /// <summary>日志目录（初始化后可用；测试时为 null）。</summary>
    public static string? Directory => logDirectory;

    /// <summary>启动日志系统（幂等）。初始化失败只影响日志本身。</summary>
    public static void Initialize(string configDirectory, Action<ActivityLevel, string>? mirror = null)
    {
        if (Interlocked.Exchange(ref started, 1) == 1)
        {
            return;
        }

        try
        {
            Mirror = mirror;
            logDirectory = Path.Combine(configDirectory, "logs");
            System.IO.Directory.CreateDirectory(logDirectory);
            flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
        catch
        {
            logDirectory = null;
        }
    }

    public static void Trace(string scope, string message) => Write(ActivityLevel.Trace, scope, message);

    public static void Debug(string scope, string message) => Write(ActivityLevel.Debug, scope, message);

    public static void Info(string scope, string message) => Write(ActivityLevel.Info, scope, message);

    public static void Warning(string scope, string message, Exception? error = null) =>
        Write(ActivityLevel.Warning, scope, message, error);

    public static void Error(string scope, string message, Exception? error = null) =>
        Write(ActivityLevel.Error, scope, message, error);

    public static void Critical(string scope, string message, Exception? error = null) =>
        Write(ActivityLevel.Critical, scope, message, error);

    /// <summary>写一条日志：进环形缓冲、进待落盘队列、同步一份到卫月日志。</summary>
    public static void Write(ActivityLevel level, string scope, string message, Exception? error = null)
    {
        ActivityEntry entry;
        try
        {
            entry = new ActivityEntry(DateTime.Now, level, scope, message, error?.ToString());
            lock (Gate)
            {
                Recent.Add(entry);
                if (Recent.Count > RetainedEntries)
                {
                    Recent.RemoveRange(0, Recent.Count - RetainedEntries);
                }
            }
        }
        catch
        {
            return;
        }

        Pending.Enqueue(entry);

        try
        {
            if (Mirror is { } mirror)
            {
                mirror(level, Format(entry, redact: false));
            }
        }
        catch
        {
            // 卫月日志不可用时忽略
        }
    }

    /// <summary>导出（默认脱敏）最近若干条：反馈上传与「查看将发送的内容」共用。</summary>
    public static string Snapshot(int maxEntries = 400, ActivityLevel minLevel = ActivityLevel.Info, bool redact = true)
    {
        try
        {
            List<ActivityEntry> copy;
            lock (Gate)
            {
                copy = Recent.Where(e => e.Level >= minLevel).ToList();
            }

            if (copy.Count == 0)
            {
                return "（还没有可导出的日志）";
            }

            if (copy.Count > maxEntries)
            {
                copy = copy.Skip(copy.Count - maxEntries).ToList();
            }

            var builder = new StringBuilder(copy.Count * 96);
            foreach (var entry in copy)
            {
                builder.AppendLine(Format(entry, redact));
            }

            return builder.ToString().TrimEnd();
        }
        catch
        {
            return "（日志导出失败）";
        }
    }

    /// <summary>脱敏：用户目录 / token / 通知 URL。</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        try
        {
            var result = UserPath.Replace(text, @"C:\Users\%USER%");
            result = UserPathUnix.Replace(result, "/home/%USER%");
            result = SecretToken.Replace(result, "%SECRET%");
            return result;
        }
        catch
        {
            return text;
        }
    }

    private static string Format(ActivityEntry entry, bool redact)
    {
        var prefix = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                     + " [" + entry.Level.ToString().ToUpperInvariant().PadRight(8) + "] "
                     + "[" + entry.Scope + "] ";
        var text = prefix + entry.Message;
        if (!string.IsNullOrEmpty(entry.Exception))
        {
            text += Environment.NewLine + "    " + entry.Exception.Replace(
                Environment.NewLine,
                Environment.NewLine + "    ", StringComparison.Ordinal);
        }

        return redact ? Redact(text) : text;
    }

    /// <summary>后台刷盘（计时器线程；绝不从渲染线程调用）。</summary>
    private static void Flush()
    {
        var directory = logDirectory;
        if (string.IsNullOrEmpty(directory))
        {
            Pending.Clear();
            return;
        }

        try
        {
            if (Pending.IsEmpty)
            {
                return;
            }

            var path = Path.Combine(directory, $"firegaze-{DateTime.Now:yyyyMMdd}.log");
            var builder = new StringBuilder();
            while (Pending.TryDequeue(out var entry))
            {
                builder.AppendLine(Format(entry, redact: false));
                if (builder.Length > 200_000)
                {
                    break;
                }
            }

            if (builder.Length > 0)
            {
                File.AppendAllText(path, builder.ToString(), new UTF8Encoding(false));
            }

            Prune(directory);
        }
        catch
        {
            // 写盘失败静默（磁盘满/权限），内存里仍保留
            Pending.Clear();
        }
    }

    private static void Prune(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles("firegaze-*.log");
            if (files.Length <= 10)
            {
                return;
            }

            foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(10))
            {
                file.Delete();
            }
        }
        catch
        {
            // 清理失败不影响记录
        }
    }
}
