using FireGaze.PortraitPreview;
using System.Text.Json;

internal static class ConsumerObservationIntegrationCases
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException("FAIL consumer integration " + message); }

    internal static int Run()
    {
        // Break caught: stopping at census publication prevents the outer native
        // Render finally from appending its scalar event, so no later refresh can correlate it.
        using (var b = new JointDiagnosticBoundary())
        {
            Check(b.Controller.TryStart(out var reason), reason);
            var refreshedBeforeAppend = false;
            b.OnClock = () =>
            {
                if (refreshedBeforeAppend || !b.HasCensusDetached) return;
                refreshedBeforeAppend = true;
                b.Controller.Update();
            };
            b.Source(0);
            b.OnClock = null;
            Check(refreshedBeforeAppend, "fixture refresh occurs after actual census publication and before passive append");
            Check(b.Controller.BlocksOperations && b.Controller.ConsumerObservation is null,
                "incomplete passive append keeps the bounded request live for a later ordinary refresh");
            var reads = b.Memory.Memory.Reads;
            b.Controller.Update();
            Check(b.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "later refresh physically retires the completed request");
            Check(b.Controller.ConsumerObservation is { } observed
                && !observed.Issues.Contains(PortraitConsumerObservationIssue.SourceRenderNotAppended)
                && observed.OpaqueReceivers.SelectMany(receiver => receiver.Calls)
                    .Any(call => call.TargetKind == PortraitOwnedSceneNativeKind.Render && call.Argument0 == 0),
                "later refresh observes the real outer Render append with actual frame zero");
            Check(b.Memory.Memory.Reads == reads, "retry correlation performs no additional native slot reads");
        }
        // Break caught: directly serializing a detached identity still exposes
        // IntPtr-valued DTO fields, so diagnostics fail after evidence is collected.
        using (var b = new JointFeatureBoundary())
        {
            b.Feature.JointCommand("native-joint", "start");
            b.Joint.Source(0);
            b.Feature.CensusTick();
            Check(b.Joint.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "JSON fixture drains the physical batch");
            using var json = JsonDocument.Parse(b.Feature.JointJSON());
            var consumers = json.RootElement.GetProperty("consumers");
            Check(consumers.ValueKind == JsonValueKind.Object
                && consumers.GetProperty("initialCoverage").GetString() == "Unknown"
                && !consumers.GetProperty("productionAdmission").GetBoolean(),
                "actual Feature JSON serializes collected consumers while retaining denied admission");
        }
        // Break caught: the associated-source JSON branch contains further native
        // address tokens in its phase, roots, slot and resource evidence.
        using (var b = new JointFeatureBoundary())
        {
            b.Joint.ClearIdleRetainedRenderState(6);
            b.Feature.JointCommand("native-joint", "start");
            b.Joint.RecreateResource();
            b.Joint.Source(0);
            var sourceCensus = b.Joint.Controller.Snapshot.Census;
            b.Feature.CensusTick();
            Check(b.Joint.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "associated JSON fixture drains");
            Check(b.Joint.Controller.ConsumerObservation?.SourceObservation is not null,
                "real typed resource return and source Render produce a historical source association; issues="
                    + string.Join(",", b.Joint.Controller.ConsumerObservation?.Issues ?? []) + "; rows="
                    + string.Join("|", sourceCensus?.Slots.Rows.Select(row => row.ToString()) ?? []));
            using var json = JsonDocument.Parse(b.Feature.JointJSON());
            var consumers = json.RootElement.GetProperty("consumers");
            var source = consumers.GetProperty("sourceObservation");
            Check(source.ValueKind == JsonValueKind.Object
                && source.GetProperty("render").GetProperty("Receiver").GetInt64() == (long)b.Joint.Context.Source.Model
                && !consumers.GetProperty("productionAdmission").GetBoolean(),
                "associated source and opaque address values serialize while admission remains denied");
            Check(source.GetProperty("Context").GetProperty("Source").GetProperty("Editor").GetInt64() == (long)b.Joint.Context.Source.Editor
                && source.GetProperty("SourceReturn").GetProperty("source").GetProperty("view").GetInt64() == (long)b.Joint.Context.Source.Model
                && source.GetProperty("Phase").GetProperty("Roots").GetProperty("RenderTargets").GetInt64() == (long)b.Joint.Memory.Phase.Roots.RenderTargets,
                "scalar JSON retains exact editor, source-return and phase-root identities");
            Check(source.GetProperty("SourceSlot").GetProperty("Character").GetInt64() == (long)b.Joint.Context.Source.Character
                && source.GetProperty("Resource").GetProperty("Receiver").GetInt64() == (long)b.Joint.Memory.Phase.Roots.RenderTargets
                && !source.GetProperty("Resource").GetProperty("Ready").GetBoolean(),
                "scalar JSON retains slot/resource identity without making the resource Ready");
        }
        // Break caught: pending correlation must use the physical host's existing
        // deadline; retrying must never prolong its native interception lifetime.
        using (var b = new JointDiagnosticBoundary())
        {
            StartWithPendingAppend(b);
            b.Now = 2001;
            b.Controller.Update();
            Check(b.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && !b.Controller.BlocksOperations,
                "pending correlation expires at the original two-second deadline and physically retires");
            Check(b.Controller.ConsumerObservation is null,
                "expired request cannot publish its late source append");
        }
        // Break caught: retrying after a session/source owner change must not
        // reuse the earlier detached census or publish its opaque addresses.
        using (var b = new JointDiagnosticBoundary())
        {
            StartWithPendingAppend(b);
            b.Context = b.Context with { SessionEpoch = 9 };
            b.Controller.Update();
            Check(b.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "changed pending request physically retires");
            Check(b.Controller.ConsumerObservation is null,
                "context change cannot publish pending consumer evidence from the prior request");
        }
        // Break caught: a new request must not inherit the previous installation's
        // observation while waiting for its own source callback.
        using (var b = new JointDiagnosticBoundary())
        {
            Check(b.Controller.TryStart(out var reason), reason);
            b.Source(0); b.Controller.Update();
            Check(b.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "first observation physically drains");
            var old = b.Controller.ConsumerObservation;
            Check(old is not null, "first request retains its detached result after automatic retirement");
            Check(b.Controller.TryStart(out reason), reason);
            Check(b.Controller.ConsumerObservation is null, "replacement clears old detached observation before any new callback");
            b.Source(1); b.Controller.Update();
            Check(b.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "replacement observation physically drains");
            Check(b.Controller.ConsumerObservation is { } current && old is not null
                && current.Context.Installation != old.Context.Installation
                && current.Context.RequestEpoch > old.Context.RequestEpoch
                && !current.ProductionAdmission && current.InitialCoverage == PortraitConsumerInitialCoverage.Unknown,
                "replacement result binds the new installation and request with authority still denied");
        }
        // Break caught: a cached completed diagnostic must be revoked by every
        // actual Feature lifecycle path, including one that arrives after drain.
        foreach (var revoke in new[] { "finalize", "mutation", "logout", "territory", "stop", "dispose" })
        {
            using var b = new JointFeatureBoundary();
            b.Feature.JointCommand("native-joint", "start");
            b.Joint.Source(0); b.Feature.CensusTick();
            Check(b.Joint.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5))
                && b.Joint.Controller.ConsumerObservation is not null, revoke + " fixture has a completed cached observation");
            switch (revoke)
            {
                case "finalize": b.Feature.CensusFinalize(); break;
                case "mutation": b.Feature.CensusMutation(); break;
                case "logout": b.Feature.CensusLogout(); break;
                case "territory": b.Feature.CensusTerritory(); break;
                case "stop": b.Feature.JointCommand("native-joint", "stop"); break;
                case "dispose": b.Feature.CensusDispose(); break;
            }
            b.Feature.CensusTick();
            Check(b.Joint.Controller.ConsumerObservation is null,
                revoke + " clears the old consumer observation instead of exposing prior addresses");
            using var json = JsonDocument.Parse(b.Feature.JointJSON());
            Check(json.RootElement.GetProperty("consumers").ValueKind == JsonValueKind.Null,
                revoke + " JSON contains no revoked consumer payload");
        }
        Console.WriteLine($"ConsumerObservationIntegrationCases: {checks} checks passed.");
        return 0;
    }

    private static void StartWithPendingAppend(JointDiagnosticBoundary b)
    {
        Check(b.Controller.TryStart(out var reason), reason);
        var refreshed = false;
        b.OnClock = () =>
        {
            if (refreshed || !b.HasCensusDetached) return;
            refreshed = true;
            b.Controller.Update();
        };
        b.Source(0);
        b.OnClock = null;
        Check(refreshed && b.Controller.BlocksOperations && b.Controller.ConsumerObservation is null,
            "pending fixture refreshes before the outer append without stopping its bounded request");
    }
}
