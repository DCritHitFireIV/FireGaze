namespace FireGaze.PortraitPreview;

/// <summary>
/// Combines managed close work after Framework-owned resources have been released. A boolean
/// writer result represents durable persistence; false must not become a successful host close.
/// </summary>
internal static class PortraitManagedDrain
{
    public static Task Combine(params Task?[] tasks)
        => Combine(null, tasks);

    public static async Task Combine(Action<Exception>? reportFailure, params Task?[] tasks)
    {
        var accepted = tasks.Where(task => task is not null).Cast<Task>().ToArray();
        try
        {
            // WhenAll settles every accepted writer even when one has already failed.
            await Task.WhenAll(accepted).ConfigureAwait(false);
            var failures = accepted.Count(task => task is Task<bool> { Result: false });
            if (failures != 0) throw new IOException($"{failures} 项托管文件写入未能保存最终记录。");
        }
        catch (Exception error)
        {
            try { reportFailure?.Invoke(error); }
            catch (Exception logError) { throw new AggregateException(error, logError); }
            throw;
        }
    }
}
