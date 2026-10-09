using System.Text.Json;
using FireGaze.Internal;

namespace FireGaze.UIText;

internal enum UITextJobMode { Translate, CheckUpdates, ApplyUpdates }
internal enum UITextJobState { Pending, Running, Done, Failed, Skipped }

internal sealed class UITextJob
{
    public string InternalName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public UITextJobMode Mode { get; set; }
    public UITextJobState State { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal sealed class UITextJobQueue
{
    private readonly string path;
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
            Items.AddRange(JsonSerializer.Deserialize<List<UITextJob>>(File.ReadAllText(path)) ?? []);
            foreach (var job in Items.Where(j => j.State == UITextJobState.Running))
            {
                job.State = UITextJobState.Pending;
                job.Message = "上次中断，等待继续";
            }
        }
        catch (Exception e) { Error = "读取队列失败：" + e.Message; }
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
    {
        if (Items.Any(j => j.InternalName == name && j.Mode == mode
            && j.State is UITextJobState.Pending or UITextJobState.Running)) return false;
        Enqueue(name, displayName, mode);
        Paused = Error is not null;
        return Error is null;
    }

    public UITextJob? BeginNext()
    {
        if (Paused || Active is not null) return null;
        var job = Items.FirstOrDefault(j => j.State == UITextJobState.Pending);
        if (job is null) return null;
        job.State = UITextJobState.Running;
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
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Items));
            Error = null;
            return true;
        }
        catch (Exception e) { Error = "保存队列失败：" + e.Message; return false; }
    }
}
