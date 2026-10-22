using System.Numerics;
using FireGaze.PortraitPreview;

var passed = 0;
var failed = 0;
void Check(bool value, string name)
{
    Console.WriteLine((value ? "PASS " : "FAIL ") + name);
    if (value) passed++; else failed++;
}

var owner = new PortraitSpatialSceneOwner(Guid.Parse("9e8eb98d-52d2-4b09-9f3b-5b3bba50fc52"), 17, 4);
var broker = new PortraitSpatialSceneBroker();

Check(broker.State == PortraitSpatialSceneState.Off, "broker starts off");
Check(broker.TryQualify(owner), "qualification accepts first owner");
Check(broker.State == PortraitSpatialSceneState.Qualifying, "qualification enters qualifying");
Check(broker.TryBeginLoad(owner), "qualified owner may begin loading");
Check(broker.State == PortraitSpatialSceneState.Loading, "load enters loading");
Check(broker.TryReady(owner), "matching owner may become ready");
Check(broker.State == PortraitSpatialSceneState.Ready, "ready state is observable");
Check(broker.TryBeginRetire(owner), "ready owner may retire");
Check(broker.State == PortraitSpatialSceneState.Retiring, "retire enters retiring");
Check(broker.TryCompleteRetire(owner), "retirement completes");
Check(broker.State == PortraitSpatialSceneState.Off, "completed retirement returns off");

var stale = owner with { Generation = 5 };
Check(!broker.TryBeginLoad(stale), "stale generation cannot load after retirement");
Check(!broker.TryReady(stale), "stale ready callback cannot revive scene");
Check(broker.State == PortraitSpatialSceneState.Off, "late callbacks leave retired scene off");

Check(broker.TryQualify(owner) && broker.TryBeginLoad(owner), "owner can qualify again with a new session");
Check(!broker.TryReady(stale), "old callback cannot claim a new session");
Check(broker.State == PortraitSpatialSceneState.Loading, "old callback leaves new session loading");
Check(broker.TryReady(owner), "new session can become ready");
Check(!broker.TryQualify(owner), "ready session cannot be requalified reentrantly");

var other = owner with { Owner = Guid.NewGuid(), Generation = 6 };
Check(!broker.TryQualify(other), "second owner cannot steal active scene");
Check(broker.TryBeginRetire(owner) && broker.TryCompleteRetire(owner), "active owner can still retire after rejected takeover");

Check(broker.TryQualify(owner), "a fresh qualification can start after normal retirement");
Check(broker.TryBeginLoad(owner) && broker.TryReady(owner), "fresh session reaches ready before stop");
Check(broker.TryStop(owner), "owner stop is accepted from ready");
Check(broker.State == PortraitSpatialSceneState.Stopped, "stop enters terminal stopped state");
Check(!broker.TryReady(owner), "stopped session rejects late ready callback");
Check(broker.TryQualify(other), "new owner must explicitly qualify after stop");
Check(broker.State == PortraitSpatialSceneState.Qualifying, "stop does not auto-resume the old session");
Check(broker.TryStop(other), "qualifying session can be stopped safely");

Check(!broker.TryQualify(default), "default owner is rejected");
Check(!broker.TryQualify(owner with { Revision = 0 }), "zero revision is rejected");
Check(!broker.TryQualify(owner with { Generation = 0 }), "zero generation is rejected");

var valid = new PortraitSpatialLayerSnapshot(
    new PortraitSpatialLayer(1, 2, 3, 4, 0.25f, 0.1f, 0.75f, 0.9f, true),
    new PortraitSpatialLayer(5, 6, 7, 8, 0, 0, 1, 1, true),
    new PortraitSpatialLayer(9, 10, 11, 12, 0.1f, 0.2f, 0.9f, 0.8f, false));
Check(valid.IsValid, "detached background frame decoration layers validate");
Check(!new PortraitSpatialLayerSnapshot(
    valid.Background with { X = float.NaN }, valid.Frame, valid.Decoration).IsValid,
    "nonfinite layer geometry is rejected");
Check(!new PortraitSpatialLayerSnapshot(
    valid.Background with { U0 = .8f, U1 = .2f }, valid.Frame, valid.Decoration).IsValid,
    "reversed layer UVs are rejected");
Check(!new PortraitSpatialLayerSnapshot(
    valid.Background with { V0 = .9f, V1 = .1f }, valid.Frame, valid.Decoration).IsValid,
    "reversed vertical layer UVs are rejected");
Check(!new PortraitSpatialLayerSnapshot(
    valid.Background, valid.Frame, valid.Decoration with { Width = -1 }).IsValid,
    "negative layer dimensions are rejected");

var replayEvents = new[]
{
    new PortraitSpatialSceneReplayEvent(PortraitSpatialSceneEventKind.Qualify, owner),
    new PortraitSpatialSceneReplayEvent(PortraitSpatialSceneEventKind.BeginLoad, owner),
    new PortraitSpatialSceneReplayEvent(PortraitSpatialSceneEventKind.Ready, owner),
    new PortraitSpatialSceneReplayEvent(PortraitSpatialSceneEventKind.Ready, stale),
    new PortraitSpatialSceneReplayEvent(PortraitSpatialSceneEventKind.BeginRetire, owner),
    new PortraitSpatialSceneReplayEvent(PortraitSpatialSceneEventKind.CompleteRetire, owner),
};
var replay = PortraitSpatialSceneReplay.Run(replayEvents);
Check(replay.AcceptedEvents == 5, "replay accepts only the five valid transitions");
Check(replay.FinalState == PortraitSpatialSceneState.Off, "replay ends at off after valid retirement");
Check(replay.RejectedEvents == 1, "replay records stale callback rejection");
Check(replay.Trace.Count == replayEvents.Length, "replay keeps one outcome per input event");
Check(replay.Trace[3].State == PortraitSpatialSceneState.Ready && !replay.Trace[3].Accepted,
    "replay trace makes stale ready callback auditable");

Console.WriteLine($"PortraitSpatialSceneTests: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
