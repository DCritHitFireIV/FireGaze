using System.Net.Http;

namespace FireGaze.UIText;

/// <summary>
///     按配置造一条翻译通道。
/// </summary>
internal static class UITextChannelFactory
{
    /// <summary>
    ///     现在能不能连上 Google 的免 key 端点（不能就连 MyMemory）。
    /// </summary>
    public static bool GoogleReachable
    {
        get
        {
            try
            {
                var proxy = HttpClient.DefaultProxy;
                return proxy is null || !proxy.IsBypassed(new Uri("https://translate.googleapis.com"));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    ///     当前配置对应的人话说明（界面上显示用）。
    /// </summary>
    public static string Describe(Configuration config) => config.UITextChannel switch
    {
        "google" => "免费：Google 免 key 端点（需要代理）",
        "mymemory" => "免费：MyMemory（免 key，质量一般）",
        "llm" => $"大模型：{config.UITextLLMModel}",
        "deepl" => "DeepL",
        _ => GoogleReachable ? "免费：自动（先 Google，失败换 MyMemory）" : "免费：自动（Google 连不上，用 MyMemory）",
    };

    /// <summary>
    ///     造通道；造不出来（缺 key / 地址不对）返回 null 并给出原因。
    /// </summary>
    public static IUITextChannel? Create(Configuration config, out string? error)
    {
        error = null;
        switch (config.UITextChannel)
        {
            case "google":
                return new FreeTranslationChannel(googleFirst: true);
            case "mymemory":
                return new FreeTranslationChannel(googleFirst: false);
            case "deepl":
            {
                var key = DPAPI.UnprotectFromBase64(config.UITextDeepLKeyProtected);
                if (string.IsNullOrWhiteSpace(key))
                {
                    error = "还没填 DeepL 的 API key。";
                    return null;
                }

                return new DeepLTranslationChannel(key);
            }

            case "llm":
            {
                var key = DPAPI.UnprotectFromBase64(config.UITextLLMKeyProtected);
                if (string.IsNullOrWhiteSpace(key))
                {
                    error = "还没填大模型的 API key。";
                    return null;
                }

                if (string.IsNullOrWhiteSpace(config.UITextLLMBaseURL) || string.IsNullOrWhiteSpace(config.UITextLLMModel))
                {
                    error = "大模型的地址或模型名是空的。";
                    return null;
                }

                return new LLMTranslationChannel(
                    config.UITextLLMBaseURL,
                    config.UITextLLMModel,
                    key,
                    deepSeek: string.Equals(config.UITextLLMProvider, "deepseek", StringComparison.OrdinalIgnoreCase));
            }

            default:
                return new FreeTranslationChannel(googleFirst: GoogleReachable);
        }
    }

    /// <summary>
    ///     把一批原文翻成中文（返回的键是原文）。
    /// </summary>
    public static async Task<UITextTranslateResult> TranslateAsync(
        Configuration config,
        IReadOnlyList<UITextTranslateItem> items,
        Action<int, int>? progress,
        CancellationToken token)
    {
        var result = new UITextTranslateResult();
        var channel = Create(config, out var error);
        if (channel is null)
        {
            result.Error = error;
            return result;
        }

        try
        {
            return await channel.TranslateAsync(items, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result.Error = "已取消";
            return result;
        }
        catch (Exception e)
        {
            result.Error = $"{e.GetType().Name}: {e.Message}";
            return result;
        }
    }
}
