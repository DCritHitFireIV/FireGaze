namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitOwnedSceneHooks
{
    private async Task ExpirePassiveAsync(TimeSpan duration)
    {
        // Timer retains the generation and closes it even if Framework polling
        // stops. A deadline revokes forwarding; it never fabricates native drain.
        await Task.Delay(duration).ConfigureAwait(false);
        if (!physicalRetired.Task.IsCompletedSuccessfully) TryDispose();
    }
    private async Task DriveRetirementAsync()
    {
        while (!physicalRetired.Task.IsCompletedSuccessfully)
        {
            if (unknownCreation || Volatile.Read(ref publishedHooks).OfType<IPortraitOwnedScenePhysicalHook>().Any(hook => hook.RetirementFaulted))
            { await physicalRetired.Task.ConfigureAwait(false); return; }
            await Task.Delay(5).ConfigureAwait(false);
            if (DisposeHooks()) return;
        }
    }
}
