using System.Reflection;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal static class PhotoPhysicalDrainCases
{
    internal static async Task<int> RunAsync(string root, ITextureProvider textures, ITextureReadbackProvider readback)
    {
        var source = new PortraitThumbnailSource();
        var photos = new PortraitHistoryPhotos(Path.Combine(root, "physical-drain.bin"), textures, readback, source, 0);
        var snapshot = new PortraitEditorSnapshot { GameBuild = "physical-drain", EntryPoint = 1 };
        var key = new PortraitHistoryKey(10, PortraitHistoryEntryPoint.GearsetPortrait, 0, "physical-drain");
        var record = new PortraitHistoryRecord(1, key, DateTimeOffset.UtcNow, null, PortraitHistoryRecordKind.Edit, snapshot.ToJSON());
        photos.SetRecords([record], 0);
        var identity = new PortraitIdentity(1, 2, 3, 4, 5);
        for (var retry = 0; retry < 100 && source.Calls == 0; retry++)
        { photos.Poll("owner", identity, 1, snapshot, true); await Task.Delay(10); }
        if (source.Calls != 1) throw new InvalidOperationException("physical fixture never started an actual capture");
        var managed = photos.BeginDispose();
        await managed.WaitAsync(TimeSpan.FromSeconds(5));
        if (photos.IsPhysicallyIdle) throw new InvalidOperationException("census physical-idle admission escaped a detached photo copy");
        // Old production has no host physical drain: model exactly its completed host boundary.
        var physical = PhysicalDrain(photos);
        var pendingUntilCompletion = !physical.IsCompleted;
        var providerNotLogicallyCancelled = !source.RequestedCancellation.CanBeCanceled;
        var texture = new FakeTexture();
        source.Complete(new(texture));
        await physical.WaitAsync(TimeSpan.FromSeconds(5));
        if (!photos.IsPhysicallyIdle) throw new InvalidOperationException("confirmed photo release did not restore detached physical idle");
        for (var retry = 0; retry < 100 && texture.Disposes == 0; retry++) await Task.Delay(1);
        if (!pendingUntilCompletion) throw new InvalidOperationException("host physical drain completed while an actual provider copy was unfinished");
        if (!providerNotLogicallyCancelled) throw new InvalidOperationException("provider task can be logically cancelled before its physical work retires");
        if (texture.Disposes != 1) throw new InvalidOperationException("physical drain completed before the late owned texture was released once");
        Console.WriteLine("PASS host physical drain holds provider lifetime until late capture resource release");
        Console.WriteLine("PASS provider copy never receives a logical cancellation token");
        var checks = 3;
        foreach (var prefix in new[] { "capture", "retain" })
        {
            var reentrantSource = new PortraitThumbnailSource();
            var reentrant = new PortraitHistoryPhotos(Path.Combine(root, prefix + "-prefix.bin"), textures, readback, reentrantSource, 0);
            reentrant.SetRecords([record], 0);
            var heldInPrefix = false;
            Action close = () => { _ = reentrant.BeginDispose(); heldInPrefix = !PhysicalDrain(reentrant).IsCompleted; };
            if (prefix == "capture") reentrantSource.OnCapture = close;
            for (var retry = 0; retry < 100 && reentrantSource.Calls == 0; retry++)
            { reentrant.Poll("owner", identity, 1, snapshot, true); await Task.Delay(10); }
            var late = new FakeTexture();
            if (prefix == "retain") late.OnShare = close;
            reentrantSource.Complete(new(late));
            if (prefix == "retain") { reentrant.Poll("owner", identity, 1, snapshot, true); reentrant.Draw(); }
            await PhysicalDrain(reentrant).WaitAsync(TimeSpan.FromSeconds(5));
            if (!heldInPrefix || late.Disposes != 1 || late.Shares.Any(value => value.Disposes != 1))
                throw new InvalidOperationException(prefix + " reentrancy allowed physical retirement before owned texture release");
            Console.WriteLine("PASS physical photo retirement owns synchronous " + prefix + " prefix before reentrant close");
            checks++;
        }

        var blockedProvider = DispatchProxy.Create<ITextureReadbackProvider, BlockedPhysicalReadback>();
        var blocked = (BlockedPhysicalReadback)(object)blockedProvider;
        var blockedSource = new PortraitThumbnailSource();
        var reading = new PortraitHistoryPhotos(Path.Combine(root, "blocked-readback.bin"), textures, blockedProvider, blockedSource, 0);
        reading.SetRecords([record], 0);
        for (var retry = 0; retry < 100 && blockedSource.Calls == 0; retry++)
        { reading.Poll("owner", identity, 1, snapshot, true); await Task.Delay(10); }
        var input = new FakeTexture(); blockedSource.Complete(new(input));
        reading.Poll("owner", identity, 1, snapshot, true); reading.Draw();
        if (!blocked.Entered.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("readback prefix not reached");
        var release = reading.BeginDispose();
        await release.WaitAsync(TimeSpan.FromSeconds(5));
        var physicalRead = PhysicalDrain(reading);
        var heldBeforeProviderTask = !physicalRead.IsCompleted;
        blocked.Release.Set();
        await physicalRead.WaitAsync(TimeSpan.FromSeconds(5));
        if (!heldBeforeProviderTask || blocked.Cancellable || input.Shares.Any(value => value.Disposes != 1))
            throw new InvalidOperationException("readback synchronous prefix escaped physical lifetime or received logical cancellation");
        Console.WriteLine("PASS actual compositor blocked provider prefix retires outside Framework with retained inputs");
        var failedSource = new PortraitThumbnailSource();
        var failedPhotos = new PortraitHistoryPhotos(Path.Combine(root, "failed-release.bin"), textures, readback, failedSource, 0);
        failedPhotos.SetRecords([record], 0);
        for (var retry = 0; retry < 100 && failedSource.Calls == 0; retry++)
        { failedPhotos.Poll("owner", identity, 1, snapshot, true); await Task.Delay(10); }
        await failedPhotos.BeginDispose().WaitAsync(TimeSpan.FromSeconds(5));
        var failedRetirement = PhysicalDrain(failedPhotos);
        var failedTexture = new FakeTexture { OnDispose = () => throw new InvalidOperationException("unconfirmed release") };
        failedSource.Complete(new(failedTexture));
        for (var retry = 0; retry < 100 && failedTexture.Disposes == 0; retry++) await Task.Delay(1);
        await Task.Delay(20);
        if (failedRetirement.IsCompleted || failedTexture.Disposes != 1)
            throw new InvalidOperationException("late photo release failure falsely retired provider ownership");
        Console.WriteLine("PASS late photo release failure retains provider ownership in quarantine");
        return checks + 2;
    }

    internal static Task PhysicalDrain(object owner) => owner.GetType().GetMethod("DrainPhysicalAsync",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(owner, null) as Task ?? Task.CompletedTask;
}

public class BlockedPhysicalReadback : DispatchProxy
{
    public ManualResetEventSlim Entered = new(), Release = new();
    public bool Cancellable;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name != nameof(ITextureReadbackProvider.GetRawImageAsync))
            throw new InvalidOperationException("Unexpected operation after cancellation: " + method?.Name);
        Cancellable |= args!.OfType<CancellationToken>().Any(value => value.CanBeCanceled);
        Entered.Set();
        if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("blocked readback prefix not released");
        return Task.FromResult((new RawImageSpecification(160, 256, 28, 640), new byte[160 * 256 * 4]));
    }
}
