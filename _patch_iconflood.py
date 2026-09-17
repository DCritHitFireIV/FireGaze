import io
p = r'C:\everyone\FireGaze\src\FireGaze\UI\RepoAuditTab.cs'
s = open(p, encoding='utf-8').read()

def rep(old, new, label, count=1):
    global s
    n = s.count(old)
    print(f'  [{label}] hits={n}')
    assert n == count, label
    s = s.replace(old, new)

# 1) 删掉预取队列字段
rep('''    private readonly Dictionary<string, ImTextureID> iconHandles = new(StringComparer.Ordinal);
    private readonly List<InstalledPluginEntry> iconPending = [];
    private int iconCursor;''',
'''    private readonly Dictionary<string, ImTextureID> iconHandles = new(StringComparer.Ordinal);''',
'drop-queue-fields')

# 2) 不再建预取队列
rep('''        this.iconHandles.Clear();
        this.iconPending.Clear();
        this.iconCursor = 0;

        if (this.installedIndex.Available)
        {
            // 预取队列：所有有来源地址的已装插件（不限可见行），每帧取一小批
            foreach (var entry in this.installedIndex.All)
            {
                if (!string.IsNullOrWhiteSpace(entry.RepositoryUrl))
                {
                    this.iconPending.Add(entry);
                }
            }
        }''',
'''        this.iconHandles.Clear();''',
'drop-queue-build')

# 3) 删掉按帧撒下载的 PumpIconLookups，换成「只读 peek 可见行」
start = s.index('    /// <summary>\n    /// 图标预取：每帧最多试')
end = s.index('    /// <summary>图标条：有图标的在前、缺图标的最后（首字母占位），组内保持原顺序。')
new_method = '''    /// <summary>
    /// 只读地把可见行里已在缓存中的图标取出来显示。
    /// </summary>
    /// <remarks>
    /// <b>这里绝不能触发下载</b>：曾经写过"每帧 12 个"的自动预取，185 个插件十几帧内全撒出去，
    /// 又经 FastDalamudCN 三线路竞速 → 一秒上千行失败日志、网络栈被拖死、游戏未响应。
    /// 下载一律走「下载图标」按钮那条限速通道。
    /// </remarks>
    private void PeekVisibleIcons(RepoAuditItem item)
    {
        if (!this.plugin.Config.ShowInstalledIcons || item.InstalledCount <= 0)
        {
            return;
        }

        foreach (var entry in item.InstalledPlugins)
        {
            if (this.iconHandles.ContainsKey(entry.InternalName))
            {
                continue;
            }

            if (PluginIconLookup.TryPeekHandle(entry, out var handle) && !handle.IsNull)
            {
                this.iconHandles[entry.InternalName] = handle;
            }
        }
    }

'''
s = s[:start] + new_method + s[end:]

# 4) 调用点：改到行循环里（只处理看得见的行）
rep('''        // ---------------- 结果表 ----------------
        this.TickIconDownload();

        if (this.plugin.Config.ShowInstalledIcons)
        {
            this.PumpIconLookups();
        }''',
'''        // ---------------- 结果表 ----------------
        this.TickIconDownload();''', 'drop-pump-call')

rep('''                for (var rowIndex = clipper.DisplayStart; rowIndex < clipper.DisplayEnd; rowIndex++)
                {
                    var item = snapshot[rowIndex];
                    ImGui.TableNextRow();''',
'''                for (var rowIndex = clipper.DisplayStart; rowIndex < clipper.DisplayEnd; rowIndex++)
                {
                    var item = snapshot[rowIndex];

                    // 只对看得见的行做只读检查（不下载）
                    this.PeekVisibleIcons(item);

                    ImGui.TableNextRow();''', 'peek-in-loop')

# 5) 下载流程里不再维护预取队列
rep('''                this.iconHandles[entry.InternalName] = handle;
                this.iconPending.RemoveAll(x => x.InternalName == entry.InternalName);
                this.iconDead.Remove(entry.InternalName);''',
'''                this.iconHandles[entry.InternalName] = handle;
                this.iconDead.Remove(entry.InternalName);''', 'download-no-queue')

open(p, 'w', encoding='utf-8', newline='\n').write(s)
print('patched')

# 版本 1.1.0.3
p2 = r'C:\everyone\FireGaze\src\FireGaze\FireGaze.csproj'
s2 = open(p2, encoding='utf-8').read().replace('<Version>1.1.0.2</Version>', '<Version>1.1.0.3</Version>')
open(p2, 'w', encoding='utf-8', newline='\n').write(s2)
print('version -> 1.1.0.3')
