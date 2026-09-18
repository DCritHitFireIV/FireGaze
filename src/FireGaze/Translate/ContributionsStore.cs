using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FireGaze.RepoAudit;

namespace FireGaze.Translate;

/// <summary>玩家提交（或自己编辑出来）的一条译文改动。</summary>
internal sealed class ContributionRecord
{
    public string Id { get; set; } = string.Empty;

    public DateTime TimeLocal { get; set; }

    /// <summary>插件内部名（词表键）。</summary>
    public string InternalName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Name / Punchline / Description。</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>提交时的原文（导入时用它跟词表对齐）。</summary>
    public string Original { get; set; } = string.Empty;

    /// <summary>玩家译文，空字符串 = 清除译文。</summary>
    public string Translated { get; set; } = string.Empty;

    /// <summary>词表里这原文有没有人动过（提交时对不上 = true）。</summary>
    public bool Stale { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// 待提交的翻译贡献：只存在本地（配置目录），不进词表、不联网。
/// 攒够了由玩家自己导出，在 GitHub 提一个 issue；确认后再由维护脚本写回词表。
/// </summary>
internal sealed class ContributionsStore
{
    /// <summary>GitHub issue 模板、导出文件、剪贴板共用的仓库地址。</summary>
    public const string RepoUrl = "https://github.com/DCritHitFireIV/FireGaze";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string configDirectory;
    private readonly List<ContributionRecord> records = [];

    public ContributionsStore(string configDirectory)
    {
        this.configDirectory = configDirectory;
        this.Load();
    }

    public string FilePath => Path.Combine(this.configDirectory, "contributions.json");

    public string ExportDirectory => Path.Combine(this.configDirectory, "contributions");

    public IReadOnlyList<ContributionRecord> Records => this.records;

    public int Count => this.records.Count;

    /// <summary>本次会话里新增、还没导出过的条数。</summary>
    public int UnsavedCount { get; private set; }

    public event Action? Changed;

    /// <summary>同一个插件 + 同一个字段 = 同一条：重复提交时覆盖（以最后一次为准）。</summary>
    public void AddOrReplace(ContributionRecord record)
    {
        var index = this.records.FindIndex(
            x => string.Equals(x.InternalName, record.InternalName, StringComparison.Ordinal) &&
                 string.Equals(x.Field, record.Field, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            this.records[index] = record;
        }
        else
        {
            this.records.Add(record);
        }

        this.Trim();
        this.UnsavedCount++;
        this.Save();
        this.Changed?.Invoke();
    }

    public void ClearAll()
    {
        this.records.Clear();
        this.UnsavedCount = 0;
        this.Save();
        this.Changed?.Invoke();
    }

    /// <summary>导出成维护脚本能直接吃的 JSON（与导出的文件内容一致）。</summary>
    public string BuildJson()
    {
        var payload = new
        {
            note = "FireGaze 翻译贡献：以下译文由玩家提交，请以 user 来源写入词表；机器翻译不得覆盖。",
            exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            count = this.records.Count,
            contributions = this.records,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>导出成 Markdown（人工阅读、贴进 issue 用）。</summary>
    public string BuildMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("### FireGaze 翻译贡献");
        builder.AppendLine();
        builder.AppendLine($"- 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine($"- 条数：{this.records.Count}");
        builder.AppendLine();
        builder.AppendLine("```json");
        builder.Append(this.BuildJson());
        builder.AppendLine();
        builder.AppendLine("```");
        return builder.ToString();
    }

    /// <summary>把导出文件写进配置目录，返回文件路径（写不出返回 null）。</summary>
    public string? SaveExportFile()
    {
        try
        {
            Directory.CreateDirectory(this.ExportDirectory);
            var name = $"contributions-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            var path = Path.Combine(this.ExportDirectory, name);
            File.WriteAllText(path, this.BuildJson() + Environment.NewLine, Encoding.UTF8);
            this.UnsavedCount = 0;
            this.Changed?.Invoke();
            return path;
        }
        catch (Exception e)
        {
            PluginLogFallback.Write("[FireGaze] 写出贡献文件失败：" + e.Message);
            return null;
        }
    }

    /// <summary>打开导出目录（资源管理器）。</summary>
    public void OpenExportDirectory()
    {
        try
        {
            Directory.CreateDirectory(this.ExportDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{this.ExportDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            PluginLogFallback.Write("[FireGaze] 打开贡献目录失败：" + e.Message);
        }
    }

    private void Trim()
    {
        const int max = 500;
        while (this.records.Count > max)
        {
            this.records.RemoveAt(0);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(this.FilePath))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<ContributionsFile>(File.ReadAllText(this.FilePath), JsonOptions);
            if (payload?.Contributions is { Count: > 0 })
            {
                this.records.AddRange(payload.Contributions);
                PluginLogFallback.Write($"[FireGaze] 已载入 {this.records.Count} 条待提交的翻译贡献");
            }
        }
        catch (Exception e)
        {
            PluginLogFallback.Write("[FireGaze] 读取贡献文件失败：" + e.Message);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(this.configDirectory);
            var payload = new
            {
                note = "FireGaze 待提交的翻译贡献（本地）。攒够后在「参与翻译」窗口导出，再在 GitHub 提 issue。",
                updatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                contributions = this.records,
            };

            File.WriteAllText(this.FilePath, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
        }
        catch (Exception e)
        {
            PluginLogFallback.Write("[FireGaze] 保存贡献文件失败：" + e.Message);
        }
    }

    private sealed class ContributionsFile
    {
        public List<ContributionRecord>? Contributions { get; set; }
    }
}
