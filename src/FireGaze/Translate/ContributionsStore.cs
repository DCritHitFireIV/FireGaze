using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FireGaze.RepoAudit;

namespace FireGaze.Translate;

/// <summary>玩家提交（或自己编辑出来）的一条译文改动。一个插件的一个字段只会有一条。</summary>
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

    /// <summary>改之前词表里的旧译文（删掉这条贡献时恢复回去）。</summary>
    public string Previous { get; set; } = string.Empty;

    /// <summary>改之前那份译文的来源（user / ai / 空）。</summary>
    public string? PreviousSource { get; set; }

    /// <summary>词表里这原文有没有人动过（提交时对不上 = true）。</summary>
    public bool Stale { get; set; }

    public string? Note { get; set; }

    /// <summary>给界面用的短标签。</summary>
    public string Describe()
    {
        var field = this.Field switch
        {
            "Name" => "插件名",
            "Punchline" => "一行简介",
            _ => "插件详情",
        };

        var text = this.Translated.Replace('\n', ' ');
        if (text.Length > 40)
        {
            text = text[..40] + "…";
        }

        return $"{this.DisplayName} · {field}：{text}";
    }
}

/// <summary>一次「一键提交」的快照（本地留档，供界面按日期回看）。</summary>
internal sealed class ContributionBatch
{
    public DateTime SubmittedLocal { get; set; }

    public List<ContributionRecord> Contributions { get; set; } = [];

    /// <summary>标签页用的日期，例如 2026-09-21。</summary>
    public string DateLabel => this.SubmittedLocal.ToString("yyyy-MM-dd");

    /// <summary>悬停时显示的具体时间。</summary>
    public string TimeLabel => this.SubmittedLocal.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// 本地翻译贡献：待提交（工作区）+ 已提交的历史快照。
/// 只存在玩家自己机器的配置目录里，不上传、不联网；一个插件一个字段只保留一条（不存历史版本）。
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
    private readonly List<ContributionBatch> history = [];

    /// <summary>删除 / 清空前的快照：只存**被拿掉的那几条**，撤销时按条目合并回来。</summary>
    private readonly List<List<ContributionRecord>> undoStack = [];

    private const int MaxUndoSteps = 10;
    private const int MaxHistoryBatches = 60;

    public ContributionsStore(string configDirectory)
    {
        this.configDirectory = configDirectory;
        this.Load();
        this.LoadHistory();
    }

    public string FilePath => Path.Combine(this.configDirectory, "contributions.json");

    public string HistoryFilePath => Path.Combine(this.configDirectory, "contributions-history.json");

    public string ExportDirectory => Path.Combine(this.configDirectory, "contributions");

    /// <summary>待提交（下面那一栏的工作区）。</summary>
    public IReadOnlyList<ContributionRecord> Records => this.records;

    /// <summary>已提交的历史快照（界面按日期分页签）。</summary>
    public IReadOnlyList<ContributionBatch> History => this.history;

    public int Count => this.records.Count;

    /// <summary>能不能撤回（删过 / 清空过）。</summary>
    public bool CanUndo => this.undoStack.Count > 0;

    public event Action? Changed;

    /// <summary>查某插件某字段已有的待提交译文（从上面打开时要进原来的那份改）。</summary>
    public ContributionRecord? Find(string internalName, string field)
        => this.records.FindLast(
            x => string.Equals(x.InternalName, internalName, StringComparison.Ordinal) &&
                 string.Equals(x.Field, field, StringComparison.OrdinalIgnoreCase));

    /// <summary>同一个插件 + 同一个字段 = 同一条：重复保存时覆盖（不存历史版本）。</summary>
    public void AddOrReplace(ContributionRecord record)
    {
        var index = this.records.FindIndex(
            x => string.Equals(x.InternalName, record.InternalName, StringComparison.Ordinal) &&
                 string.Equals(x.Field, record.Field, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            // 保留最初的「改之前是什么」，这样删掉这条时能恢复成原样
            if (string.IsNullOrEmpty(record.Previous))
            {
                record.Previous = this.records[index].Previous;
            }

            this.records[index] = record;
        }
        else
        {
            this.records.Add(record);
        }

        this.Trim();
        this.Save();
        this.Changed?.Invoke();
    }

    /// <summary>删掉一条待提交（调用方负责把词表里的值恢复成 record.Previous）。</summary>
    public ContributionRecord? Remove(string internalName, string field)
    {
        var index = this.records.FindIndex(
            x => string.Equals(x.InternalName, internalName, StringComparison.Ordinal) &&
                 string.Equals(x.Field, field, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var removed = this.records[index];
        this.PushUndo([removed]);
        this.records.RemoveAt(index);
        this.Save();
        this.Changed?.Invoke();
        return removed;
    }

    /// <summary>清空待提交（可撤回）。</summary>
    public void ClearAll()
    {
        if (this.records.Count == 0)
        {
            return;
        }

        this.PushUndo([.. this.records]);
        this.records.Clear();
        this.Save();
        this.Changed?.Invoke();
    }

    /// <summary>
    /// 撤回上一次删除 / 清空：只把当时拿掉的那几条加回来（不动撤销之后新增的），
    /// 返回真的恢复了哪些条目，调用方好把词表也同步回去。
    /// </summary>
    public IReadOnlyList<ContributionRecord> Undo(out string message)
    {
        if (this.undoStack.Count == 0)
        {
            message = "没有可撤回的操作（只有删除 / 清空能撤回）";
            return [];
        }

        var removed = this.undoStack[^1];
        this.undoStack.RemoveAt(this.undoStack.Count - 1);

        var restored = new List<ContributionRecord>();
        foreach (var record in removed)
        {
            var exists = this.records.Any(
                x => string.Equals(x.InternalName, record.InternalName, StringComparison.Ordinal) &&
                     string.Equals(x.Field, record.Field, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                continue;   // 之后又存了同一条，以新的为准
            }

            this.records.Add(record);
            restored.Add(record);
        }

        this.Save();
        this.Changed?.Invoke();
        message = restored.Count == 0
            ? "那几条后来又被你重存过，已经是最新的了"
            : $"已撤回：恢复 {restored.Count} 条待提交译文";
        return restored;
    }

    /// <summary>一键提交：把当前待提交存成一份历史快照（按日期分页签），然后清空工作区。</summary>
    public ContributionBatch? ArchiveSubmission()
    {
        if (this.records.Count == 0)
        {
            return null;
        }

        var batch = new ContributionBatch
        {
            SubmittedLocal = DateTime.Now,
            Contributions = [.. this.records],
        };

        this.history.Add(batch);
        while (this.history.Count > MaxHistoryBatches)
        {
            this.history.RemoveAt(0);
        }

        this.SaveHistory();
        this.records.Clear();
        this.undoStack.Clear();
        this.Save();
        this.Changed?.Invoke();
        return batch;
    }

    /// <summary>把这几条从本机历史留档里删掉（不影响词表、也不影响已经交出去的译文）。返回删掉的条数。</summary>
    public int RemoveFromHistory(IEnumerable<ContributionRecord> records)
    {
        var ids = new HashSet<string>(records.Select(x => x.Id), StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return 0;
        }

        var removed = 0;
        foreach (var batch in this.history)
        {
            removed += batch.Contributions.RemoveAll(x => ids.Contains(x.Id));
        }

        if (removed == 0)
        {
            return 0;
        }

        this.history.RemoveAll(x => x.Contributions.Count == 0);
        this.SaveHistory();
        this.Changed?.Invoke();
        return removed;
    }

    /// <summary>
    /// 把历史留档里的这几条**移回**待提交（不是复制：留档里不再保留它们），留档里被腾空的批次一并去掉。
    /// 返回真正加回待提交的条目。
    /// </summary>
    public IReadOnlyList<ContributionRecord> RecallFromHistory(IEnumerable<ContributionRecord> records)
    {
        var ids = new HashSet<string>(records.Select(x => x.Id), StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return [];
        }

        var recalled = new List<ContributionRecord>();
        foreach (var batch in this.history)
        {
            for (var i = batch.Contributions.Count - 1; i >= 0; i--)
            {
                var record = batch.Contributions[i];
                if (!ids.Contains(record.Id))
                {
                    continue;
                }

                recalled.Add(record);
                batch.Contributions.RemoveAt(i);
            }
        }

        if (recalled.Count == 0)
        {
            return [];
        }

        foreach (var record in recalled)
        {
            this.AddOrReplace(record);
        }

        this.history.RemoveAll(x => x.Contributions.Count == 0);
        this.SaveHistory();
        this.Changed?.Invoke();
        return recalled;
    }

    /// <summary>清空本机历史留档（不影响词表）。返回清掉的条数。</summary>
    public int ClearHistory()
    {
        var count = this.history.Sum(x => x.Contributions.Count);
        if (count == 0)
        {
            return 0;
        }

        this.history.Clear();
        this.SaveHistory();
        this.Changed?.Invoke();
        return count;
    }

    /// <summary>导出成维护脚本能直接吃的 JSON：**只含翻译本身**，不带任何玩家信息。</summary>
    public string BuildJson()
    {
        var payload = new
        {
            note = "FireGaze 翻译贡献：以下译文由玩家提交，请以 user 来源写入词表；机器翻译不得覆盖。",
            exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            count = this.records.Count,
            contributions = this.records.Select(x => new
            {
                x.InternalName,
                x.Field,
                x.Original,
                x.Translated,
            }),
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

    private void PushUndo(IEnumerable<ContributionRecord> removed)
    {
        this.undoStack.Add([.. removed]);
        while (this.undoStack.Count > MaxUndoSteps)
        {
            this.undoStack.RemoveAt(0);
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

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(this.HistoryFilePath))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(this.HistoryFilePath), JsonOptions);
            if (payload?.Batches is { Count: > 0 })
            {
                this.history.AddRange(payload.Batches);
                PluginLogFallback.Write($"[FireGaze] 已载入 {this.history.Count} 批历史提交记录");
            }
        }
        catch (Exception e)
        {
            PluginLogFallback.Write("[FireGaze] 读取历史提交记录失败：" + e.Message);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(this.configDirectory);
            var payload = new
            {
                note = "FireGaze 待提交的翻译贡献（本地）。下方的「一键提交」会把它们发到 GitHub，并在本地留一份历史记录。",
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

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(this.configDirectory);
            var payload = new
            {
                note = "FireGaze 历史提交记录（本地留档，按提交时间分页签展示）。",
                updatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                batches = this.history,
            };

            File.WriteAllText(this.HistoryFilePath, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
        }
        catch (Exception e)
        {
            PluginLogFallback.Write("[FireGaze] 保存历史提交记录失败：" + e.Message);
        }
    }

    private sealed class ContributionsFile
    {
        public List<ContributionRecord>? Contributions { get; set; }
    }

    private sealed class HistoryFile
    {
        public List<ContributionBatch>? Batches { get; set; }
    }
}
