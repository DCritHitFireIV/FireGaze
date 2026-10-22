namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitOwnedSceneHooks
{
    private async Task ExpirePassiveAsync(TimeSpan duration)
    {
        // Timer retains the generation and closes it even if Framework polling
        // stops. A deadline revokes forwarding; it never fabricates native drain.
        try { await passiveDelay(duration, expiryCancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (!physicalRetired.Task.IsCompletedSuccessfully) TryDispose();
    }
    private async Task ExpirePassiveBaselineAsync(PortraitOwnedScenePassiveBaseline expected, TimeSpan duration)
    {
        try { await passiveDelay(duration, expiryCancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (ReferenceEquals(Volatile.Read(ref passiveBaseline), expected)) RevokePassiveBaseline("The future native source Return deadline expired.");
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
