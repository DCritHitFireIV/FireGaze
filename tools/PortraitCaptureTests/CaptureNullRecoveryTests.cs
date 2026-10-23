using FireGaze.PortraitPreview;

internal static class CaptureNullRecoveryTests
{
    // The service and gate remain real. Only the external image task is controlled.
    // These cases establish the service's Error / fresh Request contract.
    // CaptureFeatureSchedulerTests separately executes the real Feature partial.
    public static async Task<int> Run()
    {
        var checks = 0;
        var failures = new List<string>();
        void Check(bool value, string message)
        { checks++; if (!value) throw new InvalidOperationException(message); }
        async Task Case(string name, Func<Task> test)
        {
            try { await test(); }
            catch (Exception error) { failures.Add(name + ": " + error.Message); Console.Error.WriteLine("FAIL null recovery " + failures[^1]); }
        }

        // Break caught: a completed null leaves attempted=true and Error=null,
        // so the caller cannot schedule a fresh request for an unchanged source.
        await Case("current null signals recovery without spinning", () =>
        {
            var now = DateTimeOffset.UnixEpoch;
            var api = new FakeCaptureApi();
            using var service = new PortraitLiveImageService(api, () => now);
            service.Request(PortraitImageScope.Portrait, api.Sample.Viewport);
            service.Update(); now = now.AddMilliseconds(200); service.Update();
            api.Pending.SetResult(null); service.Update();
            Check(service.Error is { Length: > 0 } && !service.IsBusy && service.IsPhysicallyIdle,
                "a current empty result must expose a retry signal after releasing its physical task");
            for (var frame = 0; frame < 10; frame++)
            { now = now.AddSeconds(1); service.Update(); service.Draw(); }
            Check(api.Captures == 1 && api.Composes == 0 && service.AcquireCurrent() is null,
                "an unchanged request must not reset its gate or start repeated captures after an empty result");
            service.Request(PortraitImageScope.Portrait, api.Sample.Viewport); service.Update();
            Check(api.Captures == 1 && service.Error is null,
                "a fresh request clears its retry signal and observes its own settle interval");
            now = now.AddMilliseconds(200); service.Update();
            Check(api.Captures == 2, "a fresh settled request can retry the same unchanged source");
            api.Complete(new(576, 960, [new(new CountingCaptureTexture())]));
            service.Update(); service.Draw(); service.Update();
            using var image = service.AcquireCurrent();
            Check(image is { Width: 576, Height: 960 } && service.Error is null && api.Composes == 1,
                "the replacement request can publish a genuine image and clear the transient failure");
            return Task.CompletedTask;
        });

        // Break caught: signaling a stale null as the new request's failure can
        // repeatedly reset its gate or clear a replacement that is still settling.
        await Case("old null does not poison a replacement", () =>
        {
            var now = DateTimeOffset.UnixEpoch;
            var api = new FakeCaptureApi();
            using var service = new PortraitLiveImageService(api, () => now);
            service.Request(PortraitImageScope.Portrait, api.Sample.Viewport);
            service.Update(); now = now.AddMilliseconds(200); service.Update();
            var old = api.Pending;
            service.Request(PortraitImageScope.Portrait, api.Sample.Viewport); service.Update();
            now = now.AddSeconds(2); service.Update();
            Check(api.Captures == 1 && service.IsBusy && !service.IsPhysicallyIdle,
                "a replacement request cannot bypass an unfinished physical copy");
            old.SetResult(null); service.Update();
            Check(service.Error is null && api.Captures == 2 && service.IsBusy,
                "the obsolete null releases its barrier without assigning an error to the fresh request");
            api.Complete(new(576, 960, [new(new CountingCaptureTexture())]));
            service.Update(); service.Draw(); service.Update();
            using var image = service.AcquireCurrent();
            Check(image is not null && service.Error is null,
                "the unpolluted replacement finishes without another Request");
            return Task.CompletedTask;
        });

        // Break caught: a late null may not revive a closed request or fabricate
        // physical retirement while the provider task has not actually finished.
        await Case("closed and disposed null stays retired", async () =>
        {
            var now = DateTimeOffset.UnixEpoch;
            var api = new FakeCaptureApi();
            var service = new PortraitLiveImageService(api, () => now);
            service.Request(PortraitImageScope.Portrait, api.Sample.Viewport);
            service.Update(); now = now.AddMilliseconds(200); service.Update();
            service.Close();
            Check(service.IsBusy && !service.IsPhysicallyIdle, "logical close retains the pending physical task");
            api.Pending.SetResult(null); service.Update();
            now = now.AddSeconds(30); service.Update(); service.Draw();
            Check(service.Error is null && !service.IsBusy && service.IsPhysicallyIdle && api.Captures == 1,
                "a closed null result drains silently without resubmitting the old request");
            service.Request(PortraitImageScope.Portrait, api.Sample.Viewport);
            service.Update(); now = now.AddMilliseconds(200); service.Update();
            var pending = api.Pending; service.Dispose();
            var drain = service.DrainPhysicalAsync();
            Check(!drain.IsCompleted, "disposal waits asynchronously for the actual unfinished task");
            pending.SetResult(null); await drain.WaitAsync(TimeSpan.FromSeconds(5));
            service.Update(); service.Draw();
            Check(service.IsPhysicallyIdle && api.Captures == 2 && api.Composes == 0 && service.AcquireCurrent() is null,
                "late null after disposal releases the lease and cannot publish or restart work");
        });

        // Break caught: the new Request recovery path must not clear quarantine
        // or retry an ownership release whose physical completion is unknown.
        await Case("recovery cannot bypass cleanup quarantine", () =>
        {
            var now = DateTimeOffset.UnixEpoch;
            var api = new FakeCaptureApi();
            var service = new PortraitLiveImageService(api, () => now);
            service.Request(PortraitImageScope.Portrait, api.Sample.Viewport);
            service.Update(); now = now.AddMilliseconds(200); service.Update();
            api.Pending.SetResult(null); service.Update();
            service.Request(PortraitImageScope.Portrait, api.Sample.Viewport);
            service.Update(); now = now.AddMilliseconds(200); service.Update();
            var failed = new CountingCaptureTexture { OnDispose = () => throw new InvalidOperationException("unconfirmed cleanup") };
            api.Complete(new(576, 960, [new(failed)])); service.Update(); service.Draw(); service.Update();
            Check(service.Diagnostics.cleanupFailures == 1 && !service.IsPhysicallyIdle && failed.Disposes == 1,
                "cleanup failure retains the actual resource exactly once in quarantine");
            var samples = api.Samples;
            service.Request(PortraitImageScope.Portrait, api.Sample.Viewport);
            now = now.AddSeconds(30); service.Update(); service.Draw(); service.Update();
            Check(api.Captures == 2 && api.Samples == samples && service.AcquireCurrent() is null
                && service.Diagnostics.cleanupFailures == 1 && failed.Disposes == 1,
                "recovery requests cannot start a new task, sample native state, or retry quarantined cleanup");
            service.Dispose();
            Check(!service.DrainPhysicalAsync().IsCompleted, "quarantine continues to block provider retirement");
            return Task.CompletedTask;
        });

        Console.WriteLine($"CaptureNullRecoveryTests: {checks} checks; {failures.Count} failed.");
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
        return checks;
    }
}
