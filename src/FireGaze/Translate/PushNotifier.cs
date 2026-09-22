using System.Net;
using System.Text;
using System.Text.Json;

namespace FireGaze.Translate;

/// <summary>
/// 把玩家攒好的译文推给维护者审核（server3 酱 / Server 酱³ 接口）。
///
/// 玩家点「一键提交」→ 直接把内容 POST 到这个推送地址 → 维护者手机上收到一条消息
/// （带 markdown 清单 + 一份可复制的 JSON）。没有服务端、没有 issue 流程。
/// 失败了不丢数据：待提交原样留着，调用方会顺手导出到本地文件。
/// </summary>
internal static class PushNotifier
{
    /// <summary>默认推送地址（server3 酱；uid + sendkey）。可在设置里改空以关闭推送。</summary>
    public const string DefaultUrl = "https://27428.push.ft07.com/send/sctp27428tpxdmoyz4mcs6qdmehhhjek.send";

    /// <summary>推送正文够长了就只发摘要（推送服务对正文长度有限制）。</summary>
    private const int MaxMarkdownChars = 12000;

    /// <summary>发一条推送。返回 (是否成功, 给界面看的结果说明)。</summary>
    public static async Task<(bool Ok, string Message)> SendAsync(
        string url,
        string title,
        string markdown,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, "没有配置推送地址");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return (false, "推送地址不是一个 http(s) 地址");
        }

        var body = markdown.Length > MaxMarkdownChars
            ? markdown[..MaxMarkdownChars] + "\n\n（正文过长已截断，完整内容见随附的导出文件）"
            : markdown;

        var form = new StringBuilder();
        form.Append("title=").Append(Uri.EscapeDataString(title));
        form.Append("&desp=").Append(Uri.EscapeDataString(body));
        form.Append("&tags=").Append(Uri.EscapeDataString("FireGaze|翻译"));

        try
        {
            using var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            using var content = new StringContent(form.ToString(), Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await client.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"推送失败：HTTP {(int)response.StatusCode}");
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                var code = doc.RootElement.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : -1;
                if (code == 0)
                {
                    var pushId = doc.RootElement.TryGetProperty("data", out var data) &&
                                 data.TryGetProperty("pushid", out var id)
                        ? id.ToString()
                        : "?";
                    return (true, $"已推送（pushid {pushId}）");
                }

                var message = doc.RootElement.TryGetProperty("message", out var msg) ? msg.GetString() : text[..Math.Min(120, text.Length)];
                return (false, $"推送被拒绝：{message}");
            }
            catch (JsonException)
            {
                return (false, "推送返回的不是预期格式：" + text[..Math.Min(120, text.Length)]);
            }
        }
        catch (Exception e)
        {
            return (false, "推送出错：" + e.Message);
        }
    }
}
