using FireGaze.PortraitPreview;

internal static class CaptureQuarantineAdmissionTests
{
    // An old copy can finish after a new request has already settled. Its actual
    // release must remain authoritative for admission later in that same Update.
    public static async Task<int> Run()
    {
        var checks = 0;
        foreach (var cleanupFails in new[] { true, false })
        {
            var now = DateTimeOffset.UnixEpoch;
            var api = new FakeCaptureApi();
            using var service = new PortraitLiveImageService(api, () => now);
            void Check(bool value, string why)
            { checks++; if (!value) throw new InvalidOperationException(why); }

            service.Request(PortraitImageScope.Portrait, api.Sample.Viewport);
            service.Update(); now = now.AddMilliseconds(250); service.Update();
            Check(api.Captures == 1, "fixture has one genuinely pending old capture");

            service.Request(PortraitImageScope.Portrait, api.Sample.Viewport);
            service.Update(); now = now.AddMilliseconds(250); service.Update();
            Check(api.Captures == 1, "a settled replacement cannot overlap the old physical copy");

            var old = new CountingCaptureTexture();
            if (cleanupFails) old.OnDispose = () => throw new InvalidOperationException("old source release was not confirmed");
            var other = new CountingCaptureTexture();
            api.Complete(new(576, 960, [new(old), new(other)]));
            service.Update();
            var captureCountAfterRelease = api.Captures;
            Check(old.Disposes == 1 && other.Disposes == 1,
                "stale completion cleanup attempts every old layer exactly once");

            if (cleanupFails)
            {
                // Let a broken implementation's extra physical operation settle,
                // so this test does not abandon an artificial provider task.
                if (api.Captures > 1) api.Complete(new(576, 960, [new(new CountingCaptureTexture())]));
                service.Update(); service.Draw(); service.Update();
                service.Request(PortraitImageScope.Portrait, api.Sample.Viewport);
                now = now.AddSeconds(1); service.Update();
                service.Dispose();
                var drain = service.DrainPhysicalAsync();
                Check(service.Diagnostics.cleanupFailures == 1 && !service.IsPhysicallyIdle && !drain.IsCompleted,
                    "unconfirmed stale release remains quarantined and keeps physical retirement pending");
                Check(old.Disposes == 1 && other.Disposes == 1,
                    "later update and unload never retry a stale layer's non-idempotent release");
                Check(captureCountAfterRelease == 1 && api.Captures == 1,
                    "same-Update stale cleanup quarantine must block the already settled replacement capture");
            }
            else
            {
                Check(captureCountAfterRelease == 2,
                    "confirmed old cleanup still starts the already settled replacement in the same Update");
                api.Complete(new(576, 960, [new(new CountingCaptureTexture())]));
                service.Update(); service.Draw(); service.Update();
                using (var image = service.AcquireCurrent())
                    Check(image is not null, "the normal replacement publishes its own independent current image");
                service.Dispose();
                await service.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(service.IsPhysicallyIdle, "successful replacement releases all physical ownership on close");
            }
        }
        return checks;
    }
}
