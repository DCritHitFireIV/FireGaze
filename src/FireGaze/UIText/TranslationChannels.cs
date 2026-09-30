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
    ///     整条通道级的错误（比如没配 key / 两条免费通道都被限流）；有它时通常也有一堆失败条目。
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    ///     给人看的诊断（最多几条）：比如「Google：HTTP 429 × 120」「MyMemory：HTTP 429 × 120」。
    ///     免费接口失败时**必须**留下原因，不然用户只看到「失败 N 条」完全猜不到发生了什么。
    /// </summary>
    public List<string> Notes { get; } = [];

    public void Note(string text)
    {
        if (this.Notes.Count >= 5 || this.Notes.Contains(text, StringComparer.Ordinal))
        {
            return;
        }

        this.Notes.Add(text);
    }
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
///     连不上或**被限流**就换 MyMemory。
/// </summary>
/// <remarks>
///     免费接口是按 IP 限流的（Google 免 key 端点会 429、MyMemory 匿名额度只有几千字符/天），
///     所以这里**串行 + 每条之间礼让 800ms**，遇到 429/5xx 退避重试，连续被限流就把那条通道冷却；
///     两条都冷却时整批停下并说清原因——宁可不翻，也不要静默地刷出一堆失败。
/// </remarks>
internal sealed class FreeTranslationChannel : IUITextChannel
{
    private static readonly HttpClient Client = CreateClient();

    private readonly bool googleFirst;
    private readonly ProviderState google = new("Google");
    private readonly ProviderState myMemory = new("MyMemory");

    public FreeTranslationChannel(bool googleFirst) => this.googleFirst = googleFirst;

    public string Name => this.googleFirst && !this.google.Blocked ? "google" : "mymemory";

    public string Description => this.googleFirst
        ? "免费：先试 Google 免 key 端点（需要代理），失败/限流换 MyMemory"
        : "免费：MyMemory（免 key、国内直连，额度很小）";

    public async Task<UITextTranslateResult> TranslateAsync(
        IReadOnlyList<UITextTranslateItem> items,
        Action<int, int>? progress,
        CancellationToken token)
    {
        var result = new UITextTranslateResult();
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var done = 0;

        foreach (var item in items)
        {
            if (token.IsCancellationRequested)
            {
                result.Error = "已取消";
                break;
            }

            var order = this.ProviderOrder();
            if (order.All(p => p.CoolingDown))
            {
                result.Error = TranslationThrottle.BothProvidersBlocked;
                break;
            }

            var (text, notes) = await this.TranslateOneAsync(item.Text, token).ConfigureAwait(false);
            if (text is null)
            {
                result.Failed.Add(item.Text);
                foreach (var note in notes)
                {
                    reasons[note] = reasons.GetValueOrDefault(note) + 1;
                }
            }
            else
            {
                result.Translated[item.Text] = text;
            }

            progress?.Invoke(++done, items.Count);

            if (done < items.Count)
            {
                try
                {
                    await Task.Delay(TranslationThrottle.PerRequestDelayMilliseconds, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    result.Error = "已取消";
                    break;
                }
            }
        }

        foreach (var (reason, count) in reasons.OrderByDescending(x => x.Value).Take(3))
        {
            result.Note($"{reason} × {count}");
        }

        return result;
    }

    private List<ProviderState> ProviderOrder() => this.googleFirst
        ? [this.google, this.myMemory]
        : [this.myMemory, this.google];

    private async Task<(string? Text, List<string> Notes)> TranslateOneAsync(string original, CancellationToken token)
    {
        var text = UITextText.ForTranslation(original);
        var notes = new List<string>();
        if (text.Length == 0)
        {
            return (string.Empty, notes);
        }

        foreach (var provider in this.ProviderOrder())
        {
            if (provider.CoolingDown)
            {
                notes.Add($"{provider.Label}：冷却中");
                continue;
            }

            var (value, failure, rateLimited) = await CallWithRetriesAsync(provider, text, token).ConfigureAwait(false);
            if (value is not null)
            {
                provider.OnSuccess();
                return (value, notes);
            }

            if (rateLimited)
            {
                provider.OnRateLimited();
            }

            notes.Add($"{provider.Label}：{failure}");
            if (token.IsCancellationRequested)
            {
                break;
            }
        }

        return (null, notes);
    }

    private static async Task<(string? Text, string Failure, bool RateLimited)> CallWithRetriesAsync(
        ProviderState provider,
        string text,
        CancellationToken token)
    {
        var lastFailure = "未知错误";
        var rateLimited = false;
        for (var attempt = 0; attempt < TranslationThrottle.MaxAttempts; attempt++)
        {
            if (token.IsCancellationRequested)
            {
                return (null, "已取消", false);
            }

            try
            {
                var value = provider.Label == "Google"
                    ? await TranslateWithGoogleAsync(text, token).ConfigureAwait(false)
                    : await TranslateWithMyMemoryAsync(text, token).ConfigureAwait(false);

                if (value is not null)
                {
                    return (value, string.Empty, false);
                }

                lastFailure = "返回内容为空";
            }
            catch (HttpRequestException e)
            {
                lastFailure = DescribeStatus(e);
                rateLimited = IsRateLimit(e);
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                lastFailure = "请求超时";
            }
            catch (JsonException)
            {
                lastFailure = "返回不是合法 JSON（可能被代理/网关拦了）";
            }
            catch (Exception e)
            {
                lastFailure = $"{e.GetType().Name}: {e.Message}";
            }

            if (attempt < TranslationThrottle.MaxAttempts - 1)
            {
                try
                {
                    await Task.Delay(TranslationThrottle.RetryDelayMilliseconds(attempt), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return (null, "已取消", false);
                }
            }
        }

        return (null, lastFailure, rateLimited);
    }

    private static bool IsRateLimit(HttpRequestException e)
        => e.StatusCode is not null && TranslationThrottle.IsRetryableStatus((int)e.StatusCode.Value);

    private static string DescribeStatus(HttpRequestException e)
    {
        if (e.StatusCode is null)
        {
            return "连不上（" + e.Message + "）";
        }

        var code = (int)e.StatusCode.Value;
        return code == 429 ? "HTTP 429 被限流" : $"HTTP {code}";
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
        if (string.IsNullOrWhiteSpace(translated))
        {
            return null;
        }

        // MyMemory 额度用尽时会把提示语塞进 translatedText
        if (translated.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)
            || translated.Contains("QUOTA", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("额度用尽（MYMEMORY WARNING）", null, System.Net.HttpStatusCode.TooManyRequests);
        }

        return translated;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FireGaze/1.0 (+https://github.com/DCritHitFireIV/FireGaze)");
        return client;
    }

    /// <summary>
    ///     一条免费通道的健康状况：连续被限流几次、冷却到什么时候。
    /// </summary>
    private sealed class ProviderState
    {
        public ProviderState(string label) => this.Label = label;

        public string Label { get; }

        public int RateLimitStreak { get; private set; }

        public DateTime CooldownUntil { get; private set; }

        public bool CoolingDown => DateTime.Now < this.CooldownUntil;

        /// <summary>
        ///     连续被限流超过阈值就冷却一段时间（免费接口的 429 一般要等几分钟才会恢复）。
        /// </summary>
        public bool Blocked => this.CoolingDown;

        public void OnSuccess() => this.RateLimitStreak = 0;

        public void OnRateLimited()
        {
            this.RateLimitStreak++;
            var minutes = TranslationThrottle.CooldownMinutes(this.RateLimitStreak);
            if (minutes > 0)
            {
                this.CooldownUntil = DateTime.Now.AddMinutes(minutes);
            }
        }
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
            var (translated, failure) = await this.TranslateChunkAsync(chunk, token).ConfigureAwait(false);
            if (translated is null)
            {
                lock (result.Failed)
                {
                    foreach (var item in chunk)
                    {
                        result.Failed.Add(item.Text);
                    }
                }

                if (failure is not null)
                {
                    result.Note(failure);
                    result.Error ??= failure;
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
    ///     单块重试一次（模型偶尔会吐坏 JSON）；失败时把原因带回来。
    /// </summary>
    private async Task<(Dictionary<int, string>? Result, string? Failure)> TranslateChunkAsync(
        List<UITextTranslateItem> chunk,
        CancellationToken token)
    {
        string? lastFailure = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (parsed, failure) = await this.TryChunkOnceAsync(chunk, token).ConfigureAwait(false);
            if (parsed is not null)
            {
                return (parsed, null);
            }

            lastFailure = failure;
        }

        return (null, lastFailure);
    }

    private async Task<(Dictionary<int, string>? Result, string? Failure)> TryChunkOnceAsync(
        List<UITextTranslateItem> chunk,
        CancellationToken token)
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

        string text;
        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var hint = code switch
                {
                    401 => "HTTP 401：key 不对或没权限",
                    402 => "HTTP 402：账户余额不足",
                    429 => "HTTP 429：请求太频繁",
                    _ => $"HTTP {code}",
                };
                Plugin.Log?.Warning($"[内部文本] 大模型返回 {code}：{UITextText.OneLine(text, 200)}");
                return (null, $"大模型 {hint}");
            }
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            return (null, "大模型请求超时");
        }
        catch (HttpRequestException e)
        {
            return (null, "大模型连不上：" + e.Message);
        }

        var content = ExtractContent(text);
        if (content is null)
        {
            return (null, "大模型返回里没有 content（可能被截断）");
        }

        var json = UITextText.ExtractJSON(content);
        if (json is null)
        {
            return (null, "大模型没有按 JSON 回答");
        }

        var parsed = ParseTranslations(json);
        return parsed is null ? (null, "大模型返回的 JSON 结构不对") : (parsed, null);
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
                    var message = $"DeepL HTTP {(int)response.StatusCode}";
                    result.Error = message;
                    result.Note(message);
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
                var message = $"DeepL {e.GetType().Name}: {e.Message}";
                result.Error = message;
                result.Note(message);
                result.Failed.AddRange(chunk.Select(x => x.Text));
            }

            done += chunk.Count;
            progress?.Invoke(done, items.Count);
        }

        return result;
    }
}
