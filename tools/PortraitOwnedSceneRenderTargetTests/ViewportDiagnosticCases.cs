using System.Text.Json;
using FireGaze.PortraitPreview;

internal static class ViewportDiagnosticCases
{
    internal static async Task Run()
    {
        using var fixture = new OwnedExperimentBrokerCases.Fixture(true);
        using var adapter = new PortraitOwnedSceneRenderTargetAdapter(new ReadApi(new Runtime()), new TextureApi(), () => false);
        using var session = new PortraitOwnedSceneSession((_, _) => fixture.Broker, () => fixture.Runtime, adapter);
        var feature = new PortraitPreviewFeature();
        feature.DiagnosticScene(session, fixture.Broker);
        Check(feature.DiagnosticJSON(), "Off");
        if (!session.TryStart(fixture.SourceSnapshot, 3)) throw new Exception("diagnostic fixture start rejected");
        Check(feature.DiagnosticJSON(), "Requested");
        fixture.Render(1); fixture.Render(2);
        var ready = Check(feature.DiagnosticJSON(), "Ready");
        var snapshot = fixture.Host.Snapshot();
        if (snapshot.SourceReturns.Count == 0 || snapshot.Observation.Records.Count == 0)
            throw new Exception("diagnostic test needs actual detached source returns and phase records");
        var host = ready.GetProperty("host");
        if (host.GetProperty("resource").GetProperty("receiver").ValueKind != JsonValueKind.Number
            || host.GetProperty("sourceReturns")[0].GetProperty("source").GetProperty("model").GetInt64() != 103
            || host.GetProperty("observation").GetProperty("records")[0].GetProperty("receiver").ValueKind != JsonValueKind.Number)
            throw new Exception("detached native identities were not encoded as numeric evidence");

        session.Stop(); session.Dispose(); feature.DiagnosticClosing();
        Check(feature.DiagnosticJSON(), "Retiring");
        fixture.Render(3);
        await session.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var closed = Check(feature.DiagnosticJSON(), "Off");
        if (!closed.GetProperty("physicalDrainCompleted").GetBoolean()
            || closed.GetProperty("host").GetProperty("hooks").GetInt32() != 0
            || closed.GetProperty("productionAdmission").GetBoolean())
            throw new Exception("retired diagnostic omitted complete physical close or granted production admission");
        Console.WriteLine("PASS actual owned viewport diagnostic JSON before start, requested, ready, retiring and disposed/drained");
    }

    private static JsonElement Check(string json, string state)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("state").GetString() != state)
            throw new Exception("owned diagnostic state mismatch: " + json);
        return document.RootElement.Clone();
    }
}

namespace FireGaze.PortraitPreview
{
    internal sealed unsafe partial class PortraitPreviewFeature
    {
        private PortraitOwnedSceneSession? ownedViewport;
        private PortraitOwnedSceneExperimentBroker? ownedViewportBroker;
        private PortraitOwnedSceneRenderTargetComReader? ownedViewportCom = null;
        private PortraitOwnedSceneFirstFrameProof? ownedViewportProof = null;
        private PortraitOwnedSceneObserverCameraSnapshot? ownedObserver = null;
        private bool ownedViewportClosing;
        private long ownedViewportAttempt = 1;
        private bool OwnedViewportBlocksOperations => ownedViewport is not null
            && (!ownedViewportClosing || !ownedViewportDrain.IsCompletedSuccessfully);
        private string OwnedViewportStatus => "offline diagnostics fixture";
        internal void DiagnosticScene(PortraitOwnedSceneSession scene, PortraitOwnedSceneExperimentBroker broker)
        { ownedViewport = scene; ownedViewportBroker = broker; }
        internal void DiagnosticClosing()
        { ownedViewportClosing = true; ownedViewportDrain = ownedViewport!.DrainPhysicalAsync(); }
        internal string DiagnosticJSON() => JsonSerializer.Serialize(OwnedViewportDiagnostics());
    }
}
