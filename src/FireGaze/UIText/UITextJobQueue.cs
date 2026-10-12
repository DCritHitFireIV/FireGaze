using System.Text.Json;
using FireGaze.Internal;

namespace FireGaze.UIText;

internal enum UITextJobMode { Translate, CheckUpdates, ApplyUpdates, UpgradeTranslations, RetranslateNonHuman, RestoreTranslations }
internal enum UITextJobState { Pending, Running, Done, Failed, Skipped }

internal sealed class UITextJob
{
    public string InternalName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public UITextJobMode Mode { get; set; }
    public UITextJobState State { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime? RetryAt { get; set; }
    public int RecoveryAttempts { get; set; }
    public UITextRewritePlan? Rewrite { get; set; }
}

internal sealed class UITextJobQueue
{
    private readonly string path;
    private bool loadFailed;
    private string? invalidBackup;
    public List<UITextJob> Items { get; } = [];
    public bool Paused { get; private set; } = true;
    public string? Error { get; private set; }
    public UITextJob? Active => Items.FirstOrDefault(j => j.State == UITextJobState.Running);

    public UITextJobQueue(string path)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<List<UITextJob>>(File.ReadAllText(path))
                ?? throw new IOException("队列内容为空或无效。");
            if (saved.Any(j => j is null || string.IsNullOrWhiteSpace(j.InternalName) || !Enum.IsDefined(j.Mode)
                || !Enum.IsDefined(j.State) || (j.State is UITextJobState.Pending or UITextJobState.Running
                    && j.Mode is UITextJobMode.UpgradeTranslations or UITextJobMode.RetranslateNonHuman or UITextJobMode.RestoreTranslations
                    && j.Rewrite is null)))
                throw new IOException("队列包含无法识别的任务，已暂停；旧记录保留。");
            Items.AddRange(saved);
            foreach (var job in Items)
            {
                job.DisplayName ??= job.InternalName;
                job.Message ??= string.Empty;
            }
            foreach (var job in Items.Where(j => j.State == UITextJobState.Running))
            {
                job.State = UITextJobState.Pending;
                job.Message = "上次中断，等待继续";
            }
        }
        catch (Exception e) { Items.Clear(); loadFailed = true; Error = "读取队列失败：" + e.Message; }
    }

    public void Enqueue(string name, string displayName, UITextJobMode mode)
    {
        if (Items.Any(j => j.InternalName == name && j.Mode == mode
            && j.State is UITextJobState.Pending or UITextJobState.Running)) return;
        Items.Add(new UITextJob { InternalName = name, DisplayName = displayName, Mode = mode });
        Save();
    }

    public void SetPaused(bool paused) { Paused = paused; }

    public bool Request(string name, string displayName, UITextJobMode mode)
        => RequestCore(name, displayName, mode, null);

    public bool RequestRewrite(string name, string displayName, UITextJobMode mode, UITextRewritePlan rewrite)
    {
        if (Items.Any(j => j.InternalName == name && j.Mode != UITextJobMode.CheckUpdates
            && j.State is UITextJobState.Pending or UITextJobState.Running)) return false;
        return RequestCore(name, displayName, mode, rewrite);
    }

    private bool RequestCore(string name, string displayName, UITextJobMode mode, UITextRewritePlan? rewrite)
    {
        if (Items.Any(j => j.InternalName == name && (j.Mode == mode || j.Mode != UITextJobMode.CheckUpdates && mode != UITextJobMode.CheckUpdates)
            && j.State is UITextJobState.Pending or UITextJobState.Running)) return false;
        Items.Add(new UITextJob { InternalName = name, DisplayName = displayName, Mode = mode, Rewrite = rewrite });
        Save();
        Paused = Error is not null;
        return Error is null;
    }

    public UITextJob? BeginNext()
    {
        if (Paused || Active is not null) return null;
        var job = Items.FirstOrDefault(j => j.State == UITextJobState.Pending && (j.RetryAt is null || j.RetryAt <= DateTime.UtcNow));
        if (job is null) return null;
        job.State = UITextJobState.Running;
        job.RetryAt = null;
        if (Save()) return job;
        job.State = UITextJobState.Pending;
        Paused = true;
        return null;
    }

    public void Complete(string name, bool ok, string message)
    {
        var job = Active;
        if (job?.InternalName != name) return;
        job.State = ok ? UITextJobState.Done : UITextJobState.Failed;
        job.Message = message;
        if (!Save()) Paused = true;
    }

    public void DeferRecovery(string name, DateTime retryAt, string message)
    {
        if (Active is not { } job || job.InternalName != name || job.Mode != UITextJobMode.CheckUpdates) return;
        job.RecoveryAttempts++;
        if (job.RecoveryAttempts >= 4)
        {
            job.State = UITextJobState.Failed;
            job.RetryAt = null;
            job.Message = "原始包暂不可用，已暂停重复检查以减少资源占用；现有译文和插件保留。";
            if (!Save()) Paused = true;
            return;
        }
        job.State = UITextJobState.Pending;
        job.RetryAt = retryAt;
        job.Message = message;
        if (!Save()) Paused = true;
    }

    public void SkipActive(string name)
    {
        if (Active is not { } job || job.InternalName != name) return;
        job.State = UITextJobState.Skipped;
        job.Message = "已跳过，已保存的译文保留";
        if (!Save()) Paused = true;
    }

    public int RetryFailures()
    {
        var failed = Items.Where(j => j.State == UITextJobState.Failed).ToList();
        foreach (var job in failed) { job.State = UITextJobState.Pending; job.Message = string.Empty; }
        Save();
        return failed.Count;
    }

    public void ClearFinished()
    {
        Items.RemoveAll(j => j.State is UITextJobState.Done or UITextJobState.Skipped);
        Save();
    }

    public int CancelWaiting()
    {
        var waiting = Items.Where(j => j.State == UITextJobState.Pending).ToList();
        foreach (var job in waiting) { job.State = UITextJobState.Skipped; job.Message = "已取消排队"; }
        Save();
        return waiting.Count;
    }

    public void Cancel()
    {
        Paused = true;
        CancelWaiting();
    }

    private bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (loadFailed && File.Exists(path) && invalidBackup is null)
            {
                var backup = path + ".invalid-" + Guid.NewGuid().ToString("N");
                File.Copy(path, backup);
                invalidBackup = backup;
            }
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Items));
            loadFailed = false;
            Error = null;
            return true;
        }
        catch (Exception e) { Error = "保存队列失败：" + e.Message; return false; }
    }
}
