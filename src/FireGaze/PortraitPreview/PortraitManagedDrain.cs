namespace FireGaze.PortraitPreview;

/// <summary>Combines managed close work after Framework-owned resources have been released.</summary>
internal static class PortraitManagedDrain
{
    public static Task Combine(params Task?[] tasks)
        => Task.WhenAll(tasks.Where(task => task is not null).Cast<Task>());
}
