using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FireGaze.UIText;

/// <summary>
///     公共译文库索引：哪些插件已经有现成译文包（<c>uit-packs/index.json</c>）。
/// </summary>
internal sealed class UITextLibraryIndex
{
    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("plugins")]
    public Dictionary<string, UITextLibraryIndexEntry> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>索引里一个插件包的元信息。</summary>
internal sealed class UITextLibraryIndexEntry
{
    /// <summary>包文件名（相对 <c>uit-packs/</c>）；空时按「内部名.json」推导。</summary>
    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; set; }

    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("entries")]
    public int Entries { get; set; }

    [JsonPropertyName("resources")]
    public int Resources { get; set; }

    [JsonPropertyName("attributes")]
    public int Attributes { get; set; }

    /// <summary>这个插件的官方中文资源覆盖情况（有 zh 卫星时为 true，库内条目只是补充）。</summary>
    [JsonPropertyName("hasOfficialZh")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasOfficialZh { get; set; }

    /// <summary>这个插件的全部可下载译文包（多来源）；空时按上面的单包字段回退。</summary>
    [JsonPropertyName("packs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<UITextLibraryPack>? Packs { get; set; }

    /// <summary>归一化：旧索引（单包字段）当「一个包」用。</summary>
    public List<UITextLibraryPack> EffectivePacks()
    {
        if (this.Packs is { Count: > 0 })
        {
            return this.Packs;
        }

        return
        [
            new UITextLibraryPack
            {
                ID = "library",
                File = this.File,
                Label = "基础包",
                Source = "library",
                Entries = this.Entries,
                Resources = this.Resources,
                Attributes = this.Attributes,
                UpdatedAt = this.UpdatedAt,
            },
        ];
    }
}

/// <summary>
///     一个可下载的译文包（一个插件可以有多个来源，由索引列出——详情里让用户自选）。
/// </summary>
internal sealed class UITextLibraryPack
{
    /// <summary>稳定标识（如 <c>library</c> / <c>user-xxx</c>）。</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ID { get; set; }

    /// <summary>包文件名（相对 <c>uit-packs/</c>）。</summary>
    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; set; }

    /// <summary>展示名（如「基础包」「社区包」）。</summary>
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    /// <summary>来源：<c>library</c>（自动构建）/ <c>user</c>（玩家投稿）等。</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    [JsonPropertyName("entries")]
    public int Entries { get; set; }

    [JsonPropertyName("resources")]
    public int Resources { get; set; }

    [JsonPropertyName("attributes")]
    public int Attributes { get; set; }

    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedAt { get; set; }

    /// <summary>下载量（中继统计；还没有统计时是 0，界面会显示「—」）。</summary>
    [JsonPropertyName("downloads")]
    public int Downloads { get; set; }
}

/// <summary>
///     公共译文库的下载端：索引 + 按插件的译文包，走 GitHub raw 与两个镜像（与简介词表同一套线路）。
/// </summary>
/// <remarks>
///     口径（2026-10-02 用户定）：
///       · **只下载数据，不打补丁**——补丁仍在本地由 <see cref="UITextPatchManager" /> 完成（备份/校验/回滚不变）；
///       · 包按插件、只在「这个插件有没翻的条目」时才拉；用不到插件的人永远不会下载；
///       · 合并走 <see cref="UITextPack.MergeLibrary" />：**玩家自己改过的译文永不被库顶掉**；
///       · 库只是「省一次翻译」的加速器，拉不到（墙/仓库还没有包）就照旧用自己的翻译通道。
/// </remarks>
internal sealed class UITextLibrary
{
    private const string Base = "https://raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/uit-packs/";
    private const string MirrorAtmoomen = "https://gh.atmoomen.top/raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/uit-packs/";
    private const string MirrorGhProxy = "https://gh-proxy.org/https://raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/uit-packs/";

    /// <summary>索引缓存有效期（本会话内）。索引很小，一次会话拉一次就够。</summary>
    private static readonly TimeSpan IndexTTL = TimeSpan.FromHours(6);

    /// <summary>本地包缓存的有效期：过期就重拉，拉不到继续用旧的。</summary>
    private static readonly TimeSpan PackTTL = TimeSpan.FromHours(24);

    private readonly Plugin plugin;
    private readonly string cacheDirectory;

    private UITextLibraryIndex? index;
    private DateTime indexFetchedAt = DateTime.MinValue;
    private DateTime unavailableUntil = DateTime.MinValue;
    private bool unavailableLogged;

    public UITextLibrary(Plugin plugin)
    {
        this.plugin = plugin;
        this.cacheDirectory = Path.Combine(plugin.ConfigDirectory, "uitrans", "library");
    }

    /// <summary>本会话里库不可用的原因（没配/404/网络不通），给日志用；正常时为空。</summary>
    public string? LastError { get; private set; }

    /// <summary>这个插件在库里有没有现成译文包（要在索引拉过之后问）。</summary>
    public bool HasPack(string internalName) =>
        this.index is not null && this.index.Plugins.ContainsKey(internalName);

    /// <summary>
    ///     给一个插件合并库里的译文。返回「写进去多少条」（0 = 没有更新 / 拉不到）。
    ///     失败不抛：库是加速器，不是依赖。
    /// </summary>
    public async Task<int> MergeIntoAsync(UITextPack pack, string internalName, CancellationToken token)
    {
        // 刚失败过（或仓库里还没有包）：先别反复拉，给用户的一键汉化省时间
        if (DateTime.Now < this.unavailableUntil)
        {
            return 0;
        }

        try
        {
            var libraryPack = await this.FetchPackAsync(internalName, token).ConfigureAwait(false);
            if (libraryPack is null)
            {
                return 0;
            }

            return pack.MergeLibrary(libraryPack);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            this.LastError = e.Message;
            Plugin.Log?.Debug(e, "[内部文本] 译文库合并出错（已忽略）");
            return 0;
        }
    }

    /// <summary>
    ///     拉取（或从缓存读）一个插件的译文包；仓库里没有这个包时返回 null。
    /// </summary>
    public async Task<UITextPack?> FetchPackAsync(string internalName, CancellationToken token)
    {
        var safeName = new string(internalName.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.').ToArray());
        if (safeName.Length == 0)
        {
            return null;
        }

        var cached = Path.Combine(this.cacheDirectory, safeName + ".json");
        var useCache = File.Exists(cached) && (DateTime.Now - File.GetLastWriteTime(cached)) < PackTTL;
        if (useCache)
        {
            var localPack = LoadPackFile(cached);
            if (localPack is not null)
            {
                return localPack;
            }
        }

        var index = await this.FetchIndexAsync(token).ConfigureAwait(false);
        if (index is null || !index.Plugins.TryGetValue(internalName, out var info))
        {
            return null;
        }

        // 有缓存但过期：先留着做兜底——网络上没有新包时继续用旧的
        UITextPack? stale = File.Exists(cached) ? LoadPackFile(cached) : null;
        var file = string.IsNullOrWhiteSpace(info.File) ? safeName + ".json" : info.File!;
        var text = await FetchTextAsync(file, token).ConfigureAwait(false);
        if (text is null)
        {
            return stale;
        }

        var pack = UITextPack.FromJSON(text, out var error);
        if (pack is null)
        {
            this.LastError = $"索引里的 {file} 读不出来：{error}";
            Plugin.Log?.Warning("[内部文本] 译文库包解析失败：" + this.LastError);
            return stale;
        }

        try
        {
            Directory.CreateDirectory(this.cacheDirectory);
            await File.WriteAllTextAsync(cached, text, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Plugin.Log?.Debug(e, "[内部文本] 译文库缓存写盘失败（不影响本次使用）");
        }

        return pack;
    }

    /// <summary>索引（本会话内缓存 6 小时）；详情里展示「云端译文」用这个只读快照。</summary>
    public UITextLibraryIndex? CachedIndex => this.index;

    /// <summary>
    ///     拉取指定文件的译文包并并进本机包（详情里「云端译文」的选择下载用）；返回补入条数。
    /// </summary>
    public async Task<int> MergePackFileAsync(UITextPack pack, string fileName, CancellationToken token)
    {
        try
        {
            var libraryPack = await this.FetchPackFileAsync(fileName, token).ConfigureAwait(false);
            if (libraryPack is null)
            {
                return 0;
            }

            return pack.MergeLibrary(libraryPack);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            this.LastError = e.Message;
            Plugin.Log?.Debug(e, "[内部文本] 指定译文包合并出错（已忽略）");
            return 0;
        }
    }

    /// <summary>拉取（或从缓存读）<c>uit-packs/</c> 下的一个具体文件。</summary>
    public async Task<UITextPack?> FetchPackFileAsync(string fileName, CancellationToken token)
    {
        var safe = new string(fileName.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.').ToArray());
        if (safe.Length == 0 || !safe.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cached = Path.Combine(this.cacheDirectory, safe);
        if (File.Exists(cached) && (DateTime.Now - File.GetLastWriteTime(cached)) < PackTTL)
        {
            var local = LoadPackFile(cached);
            if (local is not null)
            {
                return local;
            }
        }

        var text = await FetchTextAsync(safe, token).ConfigureAwait(false);
        if (text is null)
        {
            return File.Exists(cached) ? LoadPackFile(cached) : null;
        }

        var pack = UITextPack.FromJSON(text, out var error);
        if (pack is null)
        {
            this.LastError = $"{safe} 读不出来：{error}";
            return null;
        }

        try
        {
            Directory.CreateDirectory(this.cacheDirectory);
            await File.WriteAllTextAsync(cached, text, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Plugin.Log?.Debug(e, "[内部文本] 译文库缓存写盘失败（不影响本次使用）");
        }

        return pack;
    }

    /// <summary>索引（本会话内缓存 6 小时）。</summary>
    public async Task<UITextLibraryIndex?> FetchIndexAsync(CancellationToken token)
    {
        if (this.index is not null && (DateTime.Now - this.indexFetchedAt) < IndexTTL)
        {
            return this.index;
        }

        var text = await FetchTextAsync("index.json", token).ConfigureAwait(false);
        if (text is null)
        {
            return this.index; // 之前拉过就用旧的；没有就 null
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<UITextLibraryIndex>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (parsed is not null)
            {
                this.index = parsed;
                this.indexFetchedAt = DateTime.Now;
                this.LastError = null;
                return parsed;
            }
        }
        catch (Exception e)
        {
            this.LastError = "索引解析失败：" + e.Message;
            Plugin.Log?.Warning("[内部文本] 译文库索引解析失败：" + e.Message);
        }

        return this.index;
    }

    /// <summary>
    ///     依次尝试 raw + 两个镜像取一个文件；全部失败返回 null（只记一次日志，不刷屏）。
    /// </summary>
    private async Task<string?> FetchTextAsync(string fileName, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        // 整体限时：库只是加速器，不能把一键汉化拖住；超时就当拉不到（下次再试）
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var fetchToken = timeout.Token;

        var errors = new List<string>();
        foreach (var prefix in new[] { Base, MirrorAtmoomen, MirrorGhProxy })
        {
            var url = prefix + fileName;
            try
            {
                var response = await client.GetAsync(url, fetchToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // 仓库里还没有这个包（或整个库还没上线）：不算错误，静默跳过
                    this.unavailableUntil = DateTime.Now.AddMinutes(15);
                    return null;
                }

                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync(fetchToken).ConfigureAwait(false);
                if (text.Length > 0)
                {
                    return text;
                }

                errors.Add($"{url}：内容为空");
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    throw;
                }

                errors.Add($"{url}：超时");
                break;
            }
            catch (Exception e)
            {
                errors.Add($"{url}：{e.Message}");
            }
        }

        this.unavailableUntil = DateTime.Now.AddMinutes(15);
        this.LastError = string.Join("；", errors);
        if (!this.unavailableLogged)
        {
            this.unavailableLogged = true;
            Plugin.Log?.Information("[内部文本] 译文库暂时拉不到（配置目录/网络/仓库还没有包）：" + this.LastError);
        }

        return null;
    }

    private static UITextPack? LoadPackFile(string path)
    {
        try
        {
            return UITextPack.FromJSON(File.ReadAllText(path), out _);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
