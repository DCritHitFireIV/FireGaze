using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

internal sealed partial class ContributeWindow : Window
{
    private void SetStatus(string message, bool isError)
    {
        statusMessage = message;
        statusIsError = isError;
    }

    private static string FieldLabel(string field) => field switch
    {
        "Name" => "插件名",
        "Punchline" => "一行简介",
        _ => "插件详情",
    };

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    /// <summary>
    ///     库链地址的短名（GitHub raw 地址显示成 owner/repo）。
    /// </summary>
    private static string RepoShort(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.Trim('/');
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && uri.Host.Contains("githubusercontent", StringComparison.OrdinalIgnoreCase))
            {
                var take = Math.Min(2, segments.Length);
                var start = Math.Max(0, segments.Length - take - 1);
                return string.Join("/", segments.Skip(start).Take(take));
            }

            return uri.Host;
        }
        catch
        {
            return UiHelpers.Shorten(url, 24);
        }
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }
}
