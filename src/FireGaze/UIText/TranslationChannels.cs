using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FireGaze.UIText;

/// <summary>
///     一条待翻的文本（<paramref name="Context" /> 是代码位置，给大模型当上下文用）。
/// </summary>
internal sealed record UITextTranslateItem(string Text, string? Context);

/// <summary>
///     一批翻译的结果：成功的进 <see cref="Translated" />（键 = 原文），失败的进 <see cref="Failed" />。
/// </summary>
internal sealed class UITextTranslateResult
{
    public Dictionary<string, string> Translated { get; } = new(StringComparer.Ordinal);

    public List<string> Failed { get; } = [];

    /// <summary>
    ///     整条通道级的错误（比如没配 key / 连不上）；有它时 <see cref="Failed" /> 通常也有一堆。
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
///     翻译通道：发出去一批文本，拿回一批译文。
/// </summary>
internal interface IUITextChannel
{
    /// <summary>
    ///     短名（写进译文来源：<c>ai:google</c> 之类）。
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     界面上显示的一句话说明。
    /// </summary>
    string Description { get; }

    Task<UITextTranslateResult> TranslateAsync(
        IReadOnlyList<UITextTranslateItem> items,
        Action<int, int>? progress,
        CancellationToken token);
}

/// <summary>
///     免费、免 key 通道：优先 Google 免 key 端点（需要能连上 Google，通常是挂了代理），
///     连不上就整批退回 MyMemory（国内直连可用，质量一般）。
/// </summary>
internal sealed class FreeTranslationChannel : IUITextChannel
{
    private static readonly HttpClient Client = CreateClient();

    private readonly bool googleFirst;
    private volatile bool googleUnavailable;

    public FreeTranslationChannel(bool googleFirst) => this.googleFirst = googleFirst;

    public string Name => this.googleFirst && !this.googleUnavailable ? "google" : "mymemory";

    public string Description => this.googleFirst
        ? "免费通道：先试 Google 免 key 端点（需要代理），连不上自动换 MyMemory"
        : "免费通道：MyMemory（免 key、国内直连，质量一般）";

    public async Task<UITextTranslateResult> TranslateAsync(
        IReadOnlyList<UITextTranslateItem> items,
        Action<int, int>? progress,
        CancellationToken token)
    {
        var result = new UITextTranslateResult();
        var done = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 3,
            CancellationToken = token,
        };

        try
        {
            await Parallel.ForEachAsync(items, options, async (item, ct) =>
            {
                var translated = await this.TranslateOneAsync(item.Text, ct).ConfigureAwait(false);
                if (translated is null)
                {
                    lock (result.Failed)
                    {
                        result.Failed.Add(item.Text);
                    }
                }
                else
                {
                    lock (result.Translated)
                    {
                        result.Translated[item.Text] = translated;
                    }
                }

                progress?.Invoke(Interlocked.Increment(ref done), items.Count);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result.Error = "已取消";
        }
        catch (Exception e)
        {
            result.Error = $"{e.GetType().Name}: {e.Message}";
        }

        return result;
    }

    private async Task<string?> TranslateOneAsync(string original, CancellationToken token)
    {
        var text = UITextText.ForTranslation(original);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (this.googleFirst && !this.googleUnavailable)
        {
            try
            {
                return await TranslateWithGoogleAsync(text, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                this.googleUnavailable = true;
            }
            catch (HttpRequestException)
            {
                this.googleUnavailable = true;
            }
        }

        try
        {
            return await TranslateWithMyMemoryAsync(text, token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<string?> TranslateWithGoogleAsync(string text, CancellationToken token)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=zh-CN&dt=t&q="
                  + Uri.EscapeDataString(text);
        var json = await Client.GetStringAsync(url, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var chunk in document.RootElement[0].EnumerateArray())
        {
            if (chunk.ValueKind == JsonValueKind.Array && chunk.GetArrayLength() > 0 && chunk[0].ValueKind == JsonValueKind.String)
            {
                builder.Append(chunk[0].GetString());
            }
        }

        var translated = builder.ToString();
        return translated.Length == 0 ? null : translated;
    }

    private static async Task<string?> TranslateWithMyMemoryAsync(string text, CancellationToken token)
    {
        var url = "https://api.mymemory.translated.net/get?langpair=en|zh-CN&q=" + Uri.EscapeDataString(text);
        var json = await Client.GetStringAsync(url, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("responseData", out var data)
            || !data.TryGetProperty("translatedText", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var translated = value.GetString();
        return string.IsNullOrWhiteSpace(translated) ? null : translated;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FireGaze/1.0 (+https://github.com/DCritHitFireIV/FireGaze)");
        return client;
    }
}

/// <summary>
///     用户自填 key 的大模型通道（OpenAI 兼容的 <c>/chat/completions</c>）：
///     DeepSeek 官方、以及任何兼容服务（火山 / 硅基流动 / Kimi / 智谱 / 通义 / Groq / OpenRouter …）。
/// </summary>
internal sealed class LLMTranslationChannel : IUITextChannel
{
    private const string SystemPrompt =
        "你是《最终幻想14》插件界面的汉化译者。把输入的英文界面文本翻成简体中文。\n" +
        "要求：\n" +
        "1. 只输出 JSON，不要任何解释；格式 {\"translations\":[{\"i\":0,\"t\":\"译文\"}]}，i 与输入一一对应、数量一致。\n" +
        "2. 保留占位符原样：{0}、{1}、{name}、%s、\\n、\\t 等不要改、不要翻译。\n" +
        "3. 界面词要短：设置 / 显示 / 关闭 / 启用 / 保存 / 应用 / 重置 这类词优先。\n" +
        "4. 术语用国服常见译名（如 Treasure Map=藏宝图、Duty=任务、FATE 保留原文）。\n" +
        "5. 已经是中文的原样返回；纯符号、版本号、命令名也原样返回。";

    private static readonly HttpClient Client = CreateClient();

    private readonly string baseURL;
    private readonly string model;
    private readonly string apiKey;
    private readonly bool deepSeek;

    public LLMTranslationChannel(string baseURL, string model, string apiKey, bool deepSeek)
    {
        this.baseURL = baseURL.TrimEnd('/');
        this.model = model;
        this.apiKey = apiKey;
        this.deepSeek = deepSeek;
    }

    public string Name => this.deepSeek ? "deepseek" : "llm";

    public string Description => $"大模型：{this.model} @ {this.baseURL}";

    /// <summary>
    ///     每请求多少条（太大容易被截断，太小费 token）。
    /// </summary>
    public int BatchSize { get; set; } = 40;

    public async Task<UITextTranslateResult> TranslateAsync(
        IReadOnlyList<UITextTranslateItem> items,
        Action<int, int>? progress,
        CancellationToken token)
    {
        var result = new UITextTranslateResult();
        var done = 0;
        for (var offset = 0; offset < items.Count; offset += this.BatchSize)
        {
            token.ThrowIfCancellationRequested();
            var chunk = items.Skip(offset).Take(this.BatchSize).ToList();
            var translated = await this.TranslateChunkAsync(chunk, token).ConfigureAwait(false);
            if (translated is null)
            {
                lock (result.Failed)
                {
                    foreach (var item in chunk)
                    {
                        result.Failed.Add(item.Text);
                    }
                }
            }
            else
            {
                for (var i = 0; i < chunk.Count; i++)
                {
                    if (translated.TryGetValue(i, out var value) && value.Length > 0)
                    {
                        result.Translated[chunk[i].Text] = value;
                    }
                    else
                    {
                        result.Failed.Add(chunk[i].Text);
                    }
                }
            }

            done += chunk.Count;
            progress?.Invoke(done, items.Count);
        }

        return result;
    }

    /// <summary>
    ///     单块重试一次（模型偶尔会吐坏 JSON）。
    /// </summary>
    private async Task<Dictionary<int, string>?> TranslateChunkAsync(List<UITextTranslateItem> chunk, CancellationToken token)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var parsed = await this.TryChunkOnceAsync(chunk, token).ConfigureAwait(false);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    private async Task<Dictionary<int, string>?> TryChunkOnceAsync(List<UITextTranslateItem> chunk, CancellationToken token)
    {
        var payload = new StringBuilder();
        payload.Append('[');
        for (var i = 0; i < chunk.Count; i++)
        {
            if (i > 0)
            {
                payload.Append(',');
            }

            var item = chunk[i];
            payload.Append("{\"i\":").Append(i)
                .Append(",\"text\":").Append(JsonSerializer.Serialize(UITextText.ForTranslation(item.Text)));
            if (!string.IsNullOrEmpty(item.Context))
            {
                payload.Append(",\"ctx\":").Append(JsonSerializer.Serialize(item.Context));
            }

            payload.Append('}');
        }

        payload.Append(']');

        var body = new Dictionary<string, object?>
        {
            ["model"] = this.model,
            ["temperature"] = 0,
            ["stream"] = false,
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = SystemPrompt },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = payload.ToString() },
            },
        };
        if (this.deepSeek)
        {
            // DeepSeek v4 系列：带上就关思考（省 token、更快）；其它兼容服务不认这个字段，不能发
            body["thinking"] = new Dictionary<string, string> { ["type"] = "disabled" };
            body["max_tokens"] = 4096;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, this.baseURL + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.apiKey);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Plugin.Log?.Warning($"[内部文本] 大模型返回 {(int)response.StatusCode}：{UITextText.OneLine(text, 200)}");
            return null;
        }

        var content = ExtractContent(text);
        if (content is null)
        {
            return null;
        }

        var json = UITextText.ExtractJSON(content);
        if (json is null)
        {
            return null;
        }

        return ParseTranslations(json);
    }

    private static string? ExtractContent(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            if (document.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }
        catch (JsonException)
        {
            // 下面统一返回 null
        }

        return null;
    }

    private static Dictionary<int, string>? ParseTranslations(string json)
    {
        var result = new Dictionary<int, string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement array;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                array = document.RootElement;
            }
            else if (document.RootElement.TryGetProperty("translations", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                array = nested;
            }
            else
            {
                return null;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var index = GetInt(item, "i") ?? GetInt(item, "index");
                var text = GetString(item, "t") ?? GetString(item, "text") ?? GetString(item, "translation");
                if (index is null || text is null)
                {
                    continue;
                }

                result[index.Value] = UITextText.CleanTranslated(text);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return result.Count == 0 ? null : result;
    }

    private static int? GetInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FireGaze/1.0");
        return client;
    }
}

/// <summary>
///     DeepL（免费版 key 以 <c>:fx</c> 结尾走 api-free）。
/// </summary>
internal sealed class DeepLTranslationChannel : IUITextChannel
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string apiKey;
    private readonly string endpoint;

    public DeepLTranslationChannel(string apiKey)
    {
        this.apiKey = apiKey;
        this.endpoint = apiKey.Trim().EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";
    }

    public string Name => "deepl";

    public string Description => "DeepL" + (this.endpoint.Contains("api-free", StringComparison.Ordinal) ? "（免费版）" : string.Empty);

    public async Task<UITextTranslateResult> TranslateAsync(
        IReadOnlyList<UITextTranslateItem> items,
        Action<int, int>? progress,
        CancellationToken token)
    {
        var result = new UITextTranslateResult();
        const int batch = 40;
        var done = 0;
        for (var offset = 0; offset < items.Count; offset += batch)
        {
            token.ThrowIfCancellationRequested();
            var chunk = items.Skip(offset).Take(batch).ToList();
            var fields = new List<KeyValuePair<string, string>>
            {
                new("target_lang", "ZH-HANS"),
            };
            foreach (var item in chunk)
            {
                fields.Add(new KeyValuePair<string, string>("text", UITextText.ForTranslation(item.Text)));
            }

            try
            {
                using var content = new FormUrlEncodedContent(fields);
                using var request = new HttpRequestMessage(HttpMethod.Post, this.endpoint) { Content = content };
                request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + this.apiKey);
                using var response = await Client.SendAsync(request, token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    result.Error = $"DeepL {(int)response.StatusCode}";
                    result.Failed.AddRange(chunk.Select(x => x.Text));
                    continue;
                }

                using var document = JsonDocument.Parse(text);
                var translations = document.RootElement.GetProperty("translations");
                var index = 0;
                foreach (var item in translations.EnumerateArray())
                {
                    if (index >= chunk.Count)
                    {
                        break;
                    }

                    var value = item.GetProperty("text").GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Translated[chunk[index].Text] = UITextText.CleanTranslated(value);
                    }

                    index++;
                }
            }
            catch (Exception e)
            {
                result.Error = $"{e.GetType().Name}: {e.Message}";
                result.Failed.AddRange(chunk.Select(x => x.Text));
            }

            done += chunk.Count;
            progress?.Invoke(done, items.Count);
        }

        return result;
    }
}
