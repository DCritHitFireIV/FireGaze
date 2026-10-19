using System.Text;

namespace FireGaze.PortraitPreview;

/// <summary>Immediately queues changed observations; one shared worker performs durable atomic writes.</summary>
internal sealed class PortraitGlamourPlateCache : IDisposable
{
    public const string FileName = "portrait-glamour-plates-v1.json";
    private readonly string path;
    private readonly OwnedCacheWriter writer;
    private bool writable = true;

    public PortraitGlamourPlateCache(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        path = Path.Combine(Path.GetFullPath(configDirectory), FileName);
        writer = new(Write);
    }

    public int CompletedWrites => writer.CompletedWrites;
    public int FailedWrites => writer.FailedWrites;
    public bool IsReadOnly => !writable;
    public long SavedRevision => writer.SavedRevision;
    public bool? LastWriteSucceeded => writer.LastWriteSucceeded;

    public bool Load(PortraitGlamourPlateCatalog catalog, out string reason)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try
        {
            if (!File.Exists(path)) { reason = "尚未保存投影模板记录"; return false; }
            if (new FileInfo(path).Length > PortraitGlamourPlateCatalog.MaximumFileBytes
                || !catalog.ImportJSON(File.ReadAllText(path)))
            {
                writable = false;
                reason = "投影模板记录无法读取，已保留原文件";
                return false;
            }
            reason = "已读取投影模板记录";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            writable = false;
            reason = "投影模板记录当前无法读取，已保留原文件";
            return false;
        }
    }

    public bool QueueIfNeeded(PortraitGlamourPlateCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var revision = catalog.ContentRevision;
        return writable && writer.NeedsRevision(revision) && writer.Queue(revision, catalog.ExportJSON());
    }

    public Task<bool> FlushAsync() => writer.FlushAsync();
    /// <summary>Stops admissions and returns the managed writer drain for Framework-external awaiting.</summary>
    public Task<bool> BeginDispose() => writer.BeginDispose();
    public void Dispose() => writer.Dispose();

    private bool Write(string snapshot)
    {
        var bytes = Encoding.UTF8.GetBytes(snapshot);
        if (bytes.Length > PortraitGlamourPlateCatalog.MaximumFileBytes) return false;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".portrait-glamour-plates-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
