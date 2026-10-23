using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.PortraitPreview;

internal static class CaptureSurfaceBindingTests
{
    // Keep the production service, gate and capture API. Only native acquisition and
    // public GPU work are controlled; an obsolete native surface must never publish.
    public static async Task<int> Run()
    {
        var checks = 0;
        var failures = new List<string>();
        async Task Case(string name, Func<Task> run)
        {
            try { await run(); checks++; }
            catch (Exception error) { failures.Add(name + ": " + error.Message); Console.Error.WriteLine("FAIL surface binding " + failures[^1]); }
        }
        void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }

        await Case("published SRV replacement", async () =>
        {
            using var fixture = new Fixture();
            await fixture.Publish();
            fixture.Source.Surface = new("replacement-srv", 2, 1);
            fixture.Service.Update();
            using var image = fixture.Service.AcquireCurrent();
            Check(image is null && fixture.Textures.Flattened[0].Shares[0].Disposes == 1,
                "replacing the actual source without changing editor state must retire the published old image");
        });

        await Case("late old copy", async () =>
        {
            using var fixture = new Fixture();
            fixture.Start();
            fixture.Source.Surface = new("new-crop", 2, 1);
            fixture.Service.Update();
            fixture.Textures.CompleteCopies();
            await Until(() => fixture.Textures.Flattened.Count != 0 && !fixture.Service.IsBusy,
                () => fixture.Service.Update());
            fixture.Service.Update(); fixture.Service.Draw(); fixture.Service.Update();
            using var image = fixture.Service.AcquireCurrent();
            Check(image is null && fixture.Textures.Flattened[0].Disposes == 1
                && fixture.Textures.Flattened[0].Shares.Count == 0,
                "an old independently owned copy must be released without composition or publication");
        });

        await Case("surface changes during acquisition", async () =>
        {
            using var fixture = new Fixture();
            fixture.Source.OnAcquire = () => fixture.Source.Surface = new("changed-during-acquire", 2, 1);
            var pending = fixture.Api.CaptureAsync(fixture.Source.Sample, fixture.Source.Surface);
            fixture.Textures.CompleteCopies();
            using var result = await pending;
            Check(result is null && fixture.Textures.Copies.Count == 0 && fixture.Source.Retained.All(x => x.Disposes == 1),
                "a fresh descriptor mismatch after acquisition must reject and release inputs before physical copy");
        });

        await Case("descriptor unavailable", async () =>
        {
            using var fixture = new Fixture();
            await fixture.Publish();
            fixture.Source.Describable = false;
            fixture.Service.Update();
            using var image = fixture.Service.AcquireCurrent();
            Check(image is null, "a failed current source description cannot keep an image qualified");
        });

        await Case("descriptor cleanup failure", async () =>
        {
            using var fixture = new Fixture();
            var failed = new CountingCaptureTexture
            { OnDispose = () => throw new InvalidOperationException("unconfirmed descriptor release") };
            fixture.Source.OnDescribe = () => PortraitCaptureCleanupException.DisposeAll(failed);
            fixture.Start();
            await Task.CompletedTask;
            fixture.Service.Dispose();
            var drain = fixture.Service.DrainPhysicalAsync();
            Check(fixture.Service.Diagnostics.cleanupFailures == 1 && fixture.Textures.Copies.Count == 0
                && !fixture.Service.IsPhysicallyIdle && !drain.IsCompleted && failed.Disposes == 1,
                "descriptor cleanup failure must quarantine the actual resource and prohibit capture");
        });

        await Case("surface changes before publication", async () =>
        {
            using var fixture = new Fixture();
            fixture.Start(); fixture.Textures.CompleteCopies(); await fixture.WaitForCopy();
            fixture.Service.Update(); fixture.Service.Draw();
            fixture.Source.Surface = new("new-size-or-tint", 2, 1);
            fixture.Service.Update();
            using var image = fixture.Service.AcquireCurrent();
            Check(image is null && fixture.Textures.Flattened[0].Shares.Single().Disposes == 1,
                "a composed candidate must be discarded when its source changed before Framework publication");
        });

        await Case("draw reads no native descriptor", async () =>
        {
            using var fixture = new Fixture();
            fixture.Start(); fixture.Textures.CompleteCopies(); await fixture.WaitForCopy();
            fixture.Service.Update();
            fixture.Source.OnDescribe = () => throw new InvalidOperationException("Draw inspected native source");
            fixture.Service.Draw();
            fixture.Source.OnDescribe = null;
            fixture.Service.Update();
            using var image = fixture.Service.AcquireCurrent();
            Check(image is not null, "Draw must compose only owned references using Framework-qualified detached state");
        });

        await Case("visible plate uses original binding", async () =>
        {
            using var fixture = new Fixture();
            fixture.Source.Sample = fixture.Source.Sample with
            {
                Scope = PortraitImageScope.VisibleAdventurerPlate,
                Plate = new(1, 2, 3, 4, 1, 2, 5, new(10, 10, 2, 1), "card")
            };
            fixture.Source.Describable = false;
            fixture.Start(); fixture.Textures.CompleteViewport();
            await Until(() => fixture.Textures.Copies.Count == 1);
            fixture.Textures.CompleteCopies();
            await Until(() => fixture.Service.Diagnostics.phase == "awaiting-composition", () => fixture.Service.Update());
            fixture.Service.Draw(); fixture.Service.Update();
            using var image = fixture.Service.AcquireCurrent();
            Check(image is { Sample.Scope: PortraitImageScope.VisibleAdventurerPlate }
                && fixture.Source.Describes == 0, "visible plate capture must not require a portrait surface descriptor");
        });

        await Case("descriptor request reentry", async () =>
        {
            using var fixture = new Fixture();
            fixture.Service.Request(PortraitImageScope.Portrait, fixture.Source.Sample.Viewport);
            fixture.Source.OnDescribe = fixture.Service.Close;
            fixture.Service.Update();
            fixture.Source.OnDescribe = null;
            fixture.Advance(); fixture.Service.Update();
            await Task.CompletedTask;
            Check(fixture.Service.Diagnostics.phase == "closed" && fixture.Textures.Copies.Count == 0,
                "closing during source description must revoke the same request before capture starts");
        });

        await Case("descriptor unload retains physical prefix", async () =>
        {
            using var fixture = new Fixture();
            var heldInPrefix = false;
            Task? drain = null;
            fixture.Source.OnDescribe = () =>
            {
                fixture.Service.Dispose();
                drain = fixture.Service.DrainPhysicalAsync();
                heldInPrefix = !drain.IsCompleted;
            };
            fixture.Service.Request(PortraitImageScope.Portrait, fixture.Source.Sample.Viewport);
            fixture.Service.Update();
            await Task.CompletedTask;
            Check(heldInPrefix && drain is { IsCompletedSuccessfully: true } && fixture.Textures.Copies.Count == 0,
                "physical retirement must remain pending throughout a reentrant descriptor prefix and settle after it returns");
        });

        await Case("same descriptor request reentry restarts settling", async () =>
        {
            using var fixture = new Fixture();
            fixture.Service.Request(PortraitImageScope.Portrait, fixture.Source.Sample.Viewport);
            fixture.Source.OnDescribe = () => fixture.Service.Request(PortraitImageScope.Portrait, fixture.Source.Sample.Viewport);
            fixture.Service.Update();
            fixture.Source.OnDescribe = null;
            fixture.Advance(); fixture.Service.Update();
            Check(fixture.Textures.Copies.Count == 0, "the old description cannot qualify an identically valued replacement request");
            fixture.Advance(); fixture.Service.Update();
            Check(fixture.Textures.Copies.Count == 1, "the replacement request can capture only after its own stable interval");
            fixture.Textures.CompleteCopies(); await fixture.WaitForCopy();
        });

        if (failures.Count != 0) throw new InvalidOperationException($"Surface binding: {failures.Count} failed; " + string.Join("; ", failures));
        return checks;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Source Source = new();
        public readonly Textures Textures = new();
        public readonly PortraitLiveCaptureApi Api;
        public readonly PortraitLiveImageService Service;
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public Fixture() { Api = new(Source, Textures); Service = new(Api, () => now); }
        public void Advance() => now = now.AddMilliseconds(250);
        public void Start()
        {
            Service.Request(Source.Sample.Scope, Source.Sample.Viewport);
            Service.Update(); Advance(); Service.Update();
        }
        public async Task WaitForCopy()
            => await Until(() => Textures.Flattened.Count != 0
                && Service.Diagnostics.phase == "awaiting-composition", () => Service.Update());
        public async Task Publish()
        {
            Start(); Textures.CompleteCopies(); await WaitForCopy(); Service.Draw(); Service.Update();
            using var image = Service.AcquireCurrent();
            if (image is null) throw new InvalidOperationException("fixture did not publish its initial current image");
        }
        public void Dispose() { Service.Dispose(); Api.Dispose(); }
    }

    private sealed class Source : IPortraitLiveCaptureSource
    {
        public PortraitCaptureSample Sample = new(new(1, 2), new(3, 4, 5, 6, 7), "editor-unchanged",
            PortraitImageScope.Portrait, new(7, Vector2.Zero, new(1920, 1080)));
        public PortraitCaptureSurface Surface = new("original-srv", 2, 1);
        public bool Describable = true;
        public int Describes;
        public Action? OnAcquire, OnDescribe;
        public readonly List<CountingCaptureTexture> Retained = [];
        public bool IsFrameworkThread => true;
        public bool TrySample(PortraitImageScope scope, PortraitCaptureViewport viewport, out PortraitCaptureSample? sample)
        { sample = Sample with { Scope = scope }; return true; }
        public bool TryDescribePortrait(PortraitCaptureSample sample, out PortraitCaptureSurface? surface)
        { Describes++; OnDescribe?.Invoke(); surface = Describable ? Surface : null; return surface is not null; }
        public bool TryAcquirePortrait(PortraitCaptureSample sample, out PortraitLiveCaptureInputs? inputs)
        {
            var retained = new CountingCaptureTexture(2, 1); Retained.Add(retained);
            inputs = new(new(retained, new() { NewWidth = 2, NewHeight = 1 }), [], new(2, 1), Surface);
            OnAcquire?.Invoke(); return true;
        }
        public void Dispose() { }
    }

    private sealed class Textures : IPortraitCaptureTextures
    {
        public readonly List<TaskCompletionSource<IDalamudTextureWrap>> Copies = [];
        public readonly List<CountingCaptureTexture> Flattened = [];
        private readonly TaskCompletionSource<IDalamudTextureWrap> viewport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IDalamudTextureWrap> CopyAsync(IDalamudTextureWrap source, TextureModificationArgs args)
        {
            var pending = new TaskCompletionSource<IDalamudTextureWrap>(TaskCreationOptions.RunContinuationsAsynchronously);
            Copies.Add(pending); return pending.Task;
        }
        public Task<IDalamudTextureWrap> CaptureViewportAsync(ImGuiViewportTextureArgs args) => viewport.Task;
        public Task<IDalamudTextureWrap> ComposeAsync(PortraitCaptureLayers layers)
        {
            var flattened = new CountingCaptureTexture(layers.Width, layers.Height);
            Flattened.Add(flattened); return Task.FromResult<IDalamudTextureWrap>(flattened);
        }
        public void CompleteCopies()
        { foreach (var copy in Copies) copy.TrySetResult(new CountingCaptureTexture(2, 1)); }
        public void CompleteViewport() => viewport.TrySetResult(new CountingCaptureTexture(2, 1));
    }

    private static async Task Until(Func<bool> condition, Action? tick = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("controlled physical capture did not settle");
            tick?.Invoke(); await Task.Delay(1);
        }
    }
}
