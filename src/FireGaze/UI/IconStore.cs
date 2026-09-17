using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>
/// 图标落盘缓存的「界面侧」：把缓存文件变成纹理、并把它塞回卫月的图标缓存。
/// </summary>
/// <remarks>
/// 两个目的：
/// <list type="number">
/// <item>体检页显示图标（第一次下载后，重开游戏直接从本地出图，不再重下）；</item>
/// <item>把纹理注入卫月 <c>PluginImageCache.pluginIconMap</c>（只补我们没有、它也没有的键），
/// 这样**插件安装器**里那些插件也能立刻用上本地缓存，不必等它自己重新下载。</item>
/// </list>
/// 注入的条目在 <see cref="Dispose"/> 时按「还是不是我们放进去的那个对象」逐个撤回，
/// 免得插件卸载后安装器还指向已经销毁的纹理。
/// </remarks>
internal sealed class IconStore : IDisposable
{
    private readonly IconCache cache;

    /// <summary>正在加载（或已加载）的共享纹理：InternalName → 纹理。</summary>
    private readonly Dictionary<string, ISharedImmediateTexture> textures = new(StringComparer.Ordinal);

    /// <summary>已经拿到句柄、可以直接画的：InternalName → 句柄。</summary>
    private readonly Dictionary<string, ImTextureID> handles = new(StringComparer.Ordinal);

    /// <summary>我们注入进卫月缓存的对象（撤回时要按对象比对）：键 → LoadedIcon 实例。</summary>
    private readonly Dictionary<string, object> injected = new(StringComparer.Ordinal);

    private bool disposed;

    private DateTime nextFlush = DateTime.MinValue;

    public IconStore(string configDirectory) => this.cache = new IconCache(configDirectory);

    /// <summary>缓存目录（写进界面提示，方便用户自己清）。</summary>
    public string CacheDirectory => this.cache.Directory;

    /// <summary>缓存里有多少个插件的图标。</summary>
    public int CachedCount => this.cache.Count;

    /// <summary>本地有货（内存里或盘上）吗——「检查缺图标」用它判断。</summary>
    public bool Has(InstalledPluginEntry entry)
    {
        if (this.handles.ContainsKey(entry.InternalName) || this.textures.ContainsKey(entry.InternalName))
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
        if (this.disposed)
        {
            return false;
        }

        if (this.handles.TryGetValue(entry.InternalName, out handle))
        {
            return true;
        }

        if (!this.textures.TryGetValue(entry.InternalName, out var shared))
        {
            if (!this.EnsureTexture(entry) || !this.textures.TryGetValue(entry.InternalName, out shared))
            {
                return false;
            }
        }

        if (!shared.TryGetWrap(out var wrap, out _) || wrap is null || wrap.Handle.IsNull)
        {
            return false;
        }

        handle = wrap.Handle;
        this.handles[entry.InternalName] = handle;
        this.TryInject(entry, wrap);
        return true;
    }

    /// <summary>有落盘文件就建立共享纹理（界面线程调用；实际解码由卫月在后台做）。</summary>
    public bool EnsureTexture(InstalledPluginEntry entry)
    {
        if (this.disposed
            || this.textures.ContainsKey(entry.InternalName)
            || string.IsNullOrWhiteSpace(entry.IconUrl)
            || !this.cache.TryGetPath(entry.InternalName, entry.IconUrl!, out var path))
        {
            return false;
        }

        try
        {
            // 我们的路径一定是全路径（配置目录在 %APPDATA% 下），所以用 GetFromFileAbsolute
            this.textures[entry.InternalName] = Plugin.Textures.GetFromFileAbsolute(path);
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
        if (this.disposed || string.IsNullOrWhiteSpace(entry.IconUrl))
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

        // 把注入进卫月缓存的条目撤回（只撤我们自己放进去的那些）
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
        this.handles.Clear();
        this.textures.Clear();
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
