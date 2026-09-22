using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>
/// 图标落盘缓存的「界面侧」：把缓存文件变成**我们自己的**纹理、并把它塞回卫月的图标缓存。
/// </summary>
/// <remarks>
/// 两个目的：
/// <list type="number">
/// <item>体检页显示图标（第一次下载后，重开游戏直接从本地出图，不再重下）；</item>
/// <item>把纹理注入卫月 <c>PluginImageCache.pluginIconMap</c>（只补我们没有、它也没有的键），
/// 这样**插件安装器**里那些插件也能立刻用上本地缓存。</item>
/// </list>
/// <para>
/// <b>2026-09-18 血教训（渲染器崩溃）</b>：第一版用 <c>GetFromFileAbsolute</c>（共享纹理）并且只留了
/// <c>wrap.Handle</c>（一个裸指针）——共享纹理会被纹理管理器的清理线程释放，之后画这个裸句柄
/// 直接把 DX11 渲染线程带崩（CLR 未处理异常 + <c>Dx11Renderer.RenderDrawDataInternal</c>，转储
/// <c>dalamud_appcrash_20260918_030142</c>）。现在改成：用 <c>CreateFromImageAsync</c> **自己创建并持有
/// <see cref="IDalamudTextureWrap"/> 对象**（保住引用；文档也要求「用后自行 Dispose」），
/// 画的时候现取 <c>Handle</c>，卸载时先撤回注入、再逐个 Dispose。
/// </para>
/// </remarks>
internal sealed class IconStore : IDisposable
{
    private readonly IconCache cache;

    /// <summary>「本地缓存图标」开关（用户在体检页可关；关掉后回到只用卫月内存缓存）。</summary>
    private readonly Func<bool> enabled;

    /// <summary>正在创建的纹理（后台由卫月解码）：InternalName → 任务。</summary>
    private readonly Dictionary<string, Task<IDalamudTextureWrap>> loading = new(StringComparer.Ordinal);

    /// <summary>已经拿到、**由我们持有**的纹理：InternalName → 纹理。</summary>
    private readonly Dictionary<string, IDalamudTextureWrap> wraps = new(StringComparer.Ordinal);

    /// <summary>我们注入进卫月缓存的对象（撤回时要按对象比对）：键 → LoadedIcon 实例。</summary>
    private readonly Dictionary<string, object> injected = new(StringComparer.Ordinal);

    private bool disposed;
    private DateTime nextFlush = DateTime.MinValue;

    /// <summary>安装器打开时的预热清单（本会话只排一次）。</summary>
    private List<InstalledPluginEntry>? warmUpSource;

    /// <summary>本会话是否已经排过预热（空清单也算，否则会每帧重建索引）。</summary>
    private bool warmUpQueued;

    /// <summary>已建纹理、还没拿到句柄的（拿到那一刻会顺手注入卫月缓存）。</summary>
    private readonly List<(InstalledPluginEntry Entry, DateTime Since)> warmUpPending = [];

    private bool loggedProvider;

    private int warmUpCursor;

    /// <summary>预热里等得太久的丢弃（文件坏了 / 解不出来，不要每帧白试）。</summary>
    private static readonly TimeSpan WarmUpGiveUp = TimeSpan.FromSeconds(15);

    public IconStore(string configDirectory, Func<bool> enabled)
    {
        this.cache = new IconCache(configDirectory);
        this.enabled = enabled;
    }

    /// <summary>缓存目录（写进界面提示，方便用户自己清）。</summary>
    public string CacheDirectory => this.cache.Directory;

    /// <summary>缓存里有多少个插件的图标。</summary>
    public int CachedCount => this.cache.Count;

    /// <summary>本地有货（内存里或盘上）吗——「检查缺图标」用它判断。</summary>
    public bool Has(InstalledPluginEntry entry)
    {
        if (!this.enabled())
        {
            return false;
        }

        if (this.wraps.ContainsKey(entry.InternalName) || this.loading.ContainsKey(entry.InternalName))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(entry.IconUrl)
               && this.cache.TryGetPath(entry.InternalName, entry.IconUrl!, out _);
    }

    /// <summary>拿句柄；没有就从盘上建纹理（解码是异步的，可能这一帧还没有）。</summary>
    public bool TryGetHandle(InstalledPluginEntry entry, out ImTextureID handle)
    {
        handle = ImTextureID.Null;
        if (this.disposed || !this.enabled())
        {
            return false;
        }

        if (this.wraps.TryGetValue(entry.InternalName, out var ready))
        {
            handle = ready.Handle;
            return !handle.IsNull;
        }

        if (!this.loading.TryGetValue(entry.InternalName, out var task) && !this.EnsureTexture(entry))
        {
            return false;
        }

        if (!this.loading.TryGetValue(entry.InternalName, out task) || !task.IsCompleted)
        {
            return false;
        }

        this.loading.Remove(entry.InternalName);

        if (task.Status != TaskStatus.RanToCompletion || task.Result is not { } wrap || wrap.Handle.IsNull)
        {
            Plugin.Log.Debug($"[FireGaze] 图标纹理解码失败：{entry.InternalName}");
            return false;
        }

        this.wraps[entry.InternalName] = wrap;
        handle = wrap.Handle;
        this.TryInject(entry, wrap);
        return true;
    }

    /// <summary>有落盘文件就开始建纹理（界面线程调用；解码在卫月内部异步做）。</summary>
    public bool EnsureTexture(InstalledPluginEntry entry)
    {
        if (this.disposed
            || !this.enabled()
            || this.wraps.ContainsKey(entry.InternalName)
            || this.loading.ContainsKey(entry.InternalName)
            || string.IsNullOrWhiteSpace(entry.IconUrl)
            || !this.cache.TryGetPath(entry.InternalName, entry.IconUrl!, out var path))
        {
            return false;
        }

        try
        {
            if (!this.loggedProvider)
            {
                this.loggedProvider = true;
                Plugin.Log.Debug($"[FireGaze] 图标纹理提供者：{Plugin.Textures.GetType().FullName}");
            }

            var bytes = File.ReadAllBytes(path);
            var task = Plugin.Textures.CreateFromImageAsync(
                bytes,
                $"FireGaze:{entry.InternalName}",
                CancellationToken.None);

            this.loading[entry.InternalName] = task;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.Debug($"[FireGaze] 图标纹理创建失败（{entry.InternalName}）：{e.Message}");
            return false;
        }
    }

    /// <summary>下载线程拿到字节后调它：写进落盘缓存（线程安全）。</summary>
    public bool SaveDownloaded(InstalledPluginEntry entry, byte[] bytes, string? contentType)
    {
        if (this.disposed || !this.enabled() || string.IsNullOrWhiteSpace(entry.IconUrl))
        {
            return false;
        }

        var ok = this.cache.Save(
            entry.InternalName,
            entry.IconUrl!,
            bytes,
            IconCache.ExtensionFor(contentType, entry.IconUrl!));

        this.cache.Flush();
        return ok;
    }

    /// <summary>把缓存索引写盘（界面线程在下载推进时调；最多每秒一次）。</summary>
    public void FlushIndex()
    {
        var now = DateTime.UtcNow;
        if (now < this.nextFlush)
        {
            return;
        }

        this.nextFlush = now.AddSeconds(1);
        this.cache.Flush();
    }

    /// <summary>
    /// 插件安装器打开时调：把「盘上有图」的已装插件排进预热队列。
    /// 之后的 <see cref="WarmUpStep"/> 会分批建纹理——纹理就绪时自动注入卫月缓存，
    /// 安装器画到这些插件就直接用本地图，不再自己重新下载。
    /// </summary>
    public void ScheduleWarmUp(IReadOnlyList<InstalledPluginEntry> installed)
    {
        if (this.disposed || !this.enabled() || this.warmUpQueued)
        {
            return;
        }

        this.warmUpQueued = true;

        var list = new List<InstalledPluginEntry>();
        foreach (var entry in installed)
        {
            if (string.IsNullOrWhiteSpace(entry.IconUrl))
            {
                continue;
            }

            if (this.cache.TryGetPath(entry.InternalName, entry.IconUrl!, out _))
            {
                list.Add(entry);
            }
        }

        this.warmUpSource = list;
        this.warmUpCursor = 0;

        // 只在真的有东西要挂时写 Information（空缓存时不要刷屏）
        if (list.Count > 0)
        {
            Plugin.Log.Information($"[FireGaze] 插件安装器已打开：本地缓存的 {list.Count} 个图标将分批挂上（不重新下载）");
        }
        else
        {
            Plugin.Log.Debug("[FireGaze] 图标预热：本地缓存里暂时没有已装插件的图标");
        }
    }

    /// <summary>每帧推进预热（界面线程；<paramref name="max"/> = 本帧最多处理几个）。</summary>
    public void WarmUpStep(int max)
    {
        if (this.disposed || max <= 0 || this.warmUpSource is null)
        {
            return;
        }

        // 1) 已经把纹理建上了的：拿到句柄（= 注入完成）就出队
        var now = DateTime.Now;
        for (var i = this.warmUpPending.Count - 1; i >= 0 && max > 0; i--)
        {
            var (entry, since) = this.warmUpPending[i];

            if (this.TryGetHandle(entry, out _))
            {
                this.warmUpPending.RemoveAt(i);
                max--;
            }
            else if (now - since > WarmUpGiveUp)
            {
                this.warmUpPending.RemoveAt(i);   // 解不出来（文件坏 / 格式不支持）：别再等
            }
        }

        // 2) 从清单里取新的开始建纹理
        while (max > 0 && this.warmUpCursor < this.warmUpSource.Count)
        {
            var entry = this.warmUpSource[this.warmUpCursor++];
            if (this.EnsureTexture(entry))
            {
                this.warmUpPending.Add((entry, now));
            }

            max--;
        }

        if (this.warmUpCursor >= this.warmUpSource.Count && this.warmUpPending.Count == 0)
        {
            this.warmUpSource = null;   // 排完了，收工（但 warmUpQueued 仍为 true，不再重建索引）
        }
    }

    public void Dispose()
    {
        this.disposed = true;

        try
        {
            this.cache.Flush();
        }
        catch
        {
            // ignore
        }

        // ① 先把注入进卫月缓存的条目撤回（只撤我们自己放进去的那些）
        foreach (var (key, instance) in this.injected)
        {
            try
            {
                PluginIconLookup.TryRemoveInjected(key, instance);
            }
            catch
            {
                // 撤回失败不影响卸载
            }
        }

        this.injected.Clear();

        // 我们的纹理归卫月的「插件作用域」管（TextureManagerPluginScoped 在卸载时会统一释放）：
        // 这里只丢掉引用，**不自己 Dispose**——直接 Dispose 会和渲染线程抢，也容易双重释放。
        this.wraps.Clear();
        this.loading.Clear();
        this.warmUpSource = null;
        this.warmUpQueued = true;
        this.warmUpPending.Clear();
    }

    private void TryInject(InstalledPluginEntry entry, IDalamudTextureWrap wrap)
    {
        try
        {
            if (PluginIconLookup.TryInject(entry, wrap, out var key, out var instance) && instance is not null)
            {
                this.injected[key] = instance;
            }
        }
        catch
        {
            // 注入失败只是安装器用不上缓存，不影响我们自己的显示
        }
    }
}
