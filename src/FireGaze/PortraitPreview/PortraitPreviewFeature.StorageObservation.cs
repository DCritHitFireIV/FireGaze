namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewFeature
{
    private async Task DrainStorageObservation()
    {
        storageObservationClosing = true;
        // False means no recovery edge, not a failed durable write. The non-generic
        // async method preserves completion, failure and cancellation without exposing
        // this observation as a bool receipt to the managed durability drain.
        if (storageObservation is { } observation)
            await observation.ConfigureAwait(false);
    }
}
