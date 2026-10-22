using FireGaze.PortraitPreview;

var checks = 0;
var failures = new List<string>();
void Check(bool condition, string label) { checks++; if (!condition) failures.Add(label); }
#if HAS_LIGHTING_BINDING
var identity = new PortraitIdentity((nint)0x110, (nint)0x220, (nint)0x330, 7, 0x440);
var history = new PortraitHistoryKey(0x550, PortraitHistoryEntryPoint.GearsetPortrait, 3, "cn-7.57:race1:face2");
const string surface = "texture:12;crop:0,0,128,256;uv:0,0,1,1";
var ranges = Enumerable.Repeat(new PortraitNativeLightRange(0, 255, 1), 6).ToArray();
var look = new PortraitLook(Enumerable.Range(0, 13).Select(i => new PortraitPiece((uint)(100 + i), 2, 3)).ToArray(), true, true, false);
var customize = Enumerable.Range(0, 26).Select(i => (byte)(i + 1)).ToArray();
var snapshot = new PortraitEditorSnapshot
{
    GameBuild = "cn-7.57", EntryPoint = 1, Look = look, Customize = customize,
    Composition = new PortraitComposition
    {
        CameraPosition = new(1, 2, 3, 4), CameraTarget = new(5, 6, 7, 8),
        CameraYaw = 1, CameraPitch = 2, CameraDistance = 3, CameraZoomNormalized = .5f,
        ImageRotation = 4, CameraZoom = 5, BannerTimeline = 6, AnimationProgress = 7,
        Expression = 8, HeadDirection = new(9, 10), EyeDirection = new(11, 12),
        DirectionalRed = 11, DirectionalGreen = 12, DirectionalBlue = 13, DirectionalBrightness = 14,
        DirectionalVerticalAngle = 15, DirectionalHorizontalAngle = 16,
        AmbientRed = 21, AmbientGreen = 22, AmbientBlue = 23, AmbientBrightness = 24,
        Background = 25, Frame = 26, Decoration = 27, PoseClassJob = 28,
        HeadFacingCamera = true, EyesFacingCamera = true, Paused = true, CharacterVisible = true,
    },
};
PortraitLightingFitBinding Bind(PortraitEditorSnapshot? value = null, PortraitIdentity? owner = null,
    PortraitHistoryKey? key = null, string? generation = null, int width = 128, int height = 256,
    IReadOnlyList<PortraitNativeLightRange>? limits = null)
{
    if (!PortraitLightingFitBinding.TryCreate(owner ?? identity, key ?? history, value ?? snapshot,
            generation ?? surface, width, height, limits ?? ranges, out var binding) || binding is null)
        throw new InvalidOperationException("Valid binding fixture was refused.");
    return binding;
}
var baseline = Bind();
Check(baseline.State.Parameters.SequenceEqual(new[] { 11, 12, 13, 21, 22, 23 }), "six parameters follow directional RGB then ambient RGB");
Check(baseline.Identity == identity && baseline.HistoryKey == history && baseline.Snapshot.ToJSON() == snapshot.ToJSON(),
    "binding retains exact owner, specific source and full expected snapshot for narrow native write validation");
var same = Bind(snapshot.Copy());
Check(same.State.EditorIdentity == baseline.State.EditorIdentity && same.State.FixedStateKey == baseline.State.FixedStateKey,
    "equivalent detached snapshots produce the same binding");

// A changed RGB channel changes a parameter while keeping the immutable rendering contract.
var rgbChanges = new (string Name, Func<PortraitComposition, PortraitComposition> Change, int[] Want)[]
{
    ("directional red", x => x with { DirectionalRed = 31 }, [31,12,13,21,22,23]),
    ("directional green", x => x with { DirectionalGreen = 32 }, [11,32,13,21,22,23]),
    ("directional blue", x => x with { DirectionalBlue = 33 }, [11,12,33,21,22,23]),
    ("ambient red", x => x with { AmbientRed = 41 }, [11,12,13,41,22,23]),
    ("ambient green", x => x with { AmbientGreen = 42 }, [11,12,13,21,42,23]),
    ("ambient blue", x => x with { AmbientBlue = 43 }, [11,12,13,21,22,43]),
};
foreach (var change in rgbChanges)
{
    var binding = Bind(snapshot with { Composition = change.Change(snapshot.Composition) });
    Check(binding.State.EditorIdentity == baseline.State.EditorIdentity && binding.State.FixedStateKey == baseline.State.FixedStateKey
        && binding.State.Parameters.SequenceEqual(change.Want), change.Name + " is controlled without changing owner/fixed state");
}

// Omitting any owner/source field would let stale captures or compensation cross editors.
var ownerChanges = new (string Name, PortraitIdentity Value)[]
{
    ("editor", identity with { Editor = (nint)0x111 }), ("state", identity with { State = (nint)0x221 }),
    ("model", identity with { Model = (nint)0x331 }), ("generation", identity with { Generation = 8 }),
    ("character", identity with { Character = 0x441 }),
};
foreach (var change in ownerChanges)
{
    var binding = Bind(owner: change.Value);
    Check(binding.State.EditorIdentity != baseline.State.EditorIdentity && binding.State.FixedStateKey != baseline.State.FixedStateKey,
        change.Name + " replacement invalidates both owner and rendering contract");
}
var sourceChanges = new (string Name, PortraitHistoryKey Value)[]
{
    ("content ID", history with { ContentID = 0x551 }),
    ("entry", history with { EntryPoint = PortraitHistoryEntryPoint.AdventurerPlate }),
    ("source ID", history with { SourceID = 4 }), ("compatibility", history with { CompatToken = "cn-7.57:race1:face3" }),
};
foreach (var change in sourceChanges)
{
    var binding = Bind(key: change.Value);
    Check(binding.State.EditorIdentity != baseline.State.EditorIdentity && binding.State.FixedStateKey != baseline.State.FixedStateKey,
        change.Name + " replacement invalidates source ownership");
}

var fixedChanges = new (string Name, Func<PortraitComposition, PortraitComposition> Change)[]
{
    ("camera position", x => x with { CameraPosition = x.CameraPosition with { X = 9 } }),
    ("camera target", x => x with { CameraTarget = x.CameraTarget with { W = 9 } }),
    ("yaw", x => x with { CameraYaw = 9 }), ("pitch", x => x with { CameraPitch = 9 }),
    ("distance", x => x with { CameraDistance = 9 }), ("normalized zoom", x => x with { CameraZoomNormalized = .75f }),
    ("rotation", x => x with { ImageRotation = 9 }), ("zoom", x => x with { CameraZoom = 9 }),
    ("pose", x => x with { BannerTimeline = 9 }), ("time", x => x with { AnimationProgress = 9 }),
    ("expression", x => x with { Expression = 9 }), ("head direction", x => x with { HeadDirection = new(99, 10) }),
    ("eye direction", x => x with { EyeDirection = new(99, 12) }),
    ("main brightness", x => x with { DirectionalBrightness = 99 }),
    ("main vertical", x => x with { DirectionalVerticalAngle = 99 }),
    ("main horizontal", x => x with { DirectionalHorizontalAngle = 99 }),
    ("ambient brightness", x => x with { AmbientBrightness = 99 }),
    ("background", x => x with { Background = 99 }), ("frame", x => x with { Frame = 99 }),
    ("decoration", x => x with { Decoration = 99 }), ("pose job", x => x with { PoseClassJob = 99 }),
    ("head follow", x => x with { HeadFacingCamera = false }), ("eye follow", x => x with { EyesFacingCamera = false }),
    ("pause", x => x with { Paused = false }), ("character visibility", x => x with { CharacterVisible = false }),
};
foreach (var change in fixedChanges)
{
    var binding = Bind(snapshot with { Composition = change.Change(snapshot.Composition) });
    Check(binding.State.EditorIdentity == baseline.State.EditorIdentity && binding.State.FixedStateKey != baseline.State.FixedStateKey,
        change.Name + " changes the fixed rendering state without inventing a new editor");
}
// Unlike history deduplication, sampling must notice time advancing even while playing.
var playing = Bind(snapshot with { Composition = snapshot.Composition with { Paused = false } });
var playingLater = Bind(snapshot with { Composition = snapshot.Composition with { Paused = false, AnimationProgress = 8 } });
Check(playing.State.FixedStateKey != playingLater.State.FixedStateKey, "playing animation time remains part of the sampling contract");
var alternateLook = snapshot.Look.Copy();
alternateLook.Pieces[0] = new(999, 1, 2);
var beauty = (byte[])snapshot.Customize.Clone(); beauty[10] = 99;
foreach (var change in new[]
{
    snapshot with { Look = alternateLook }, snapshot with { Customize = beauty },
    snapshot with { Look = snapshot.Look with { HeadVisible = false } },
    snapshot with { Look = snapshot.Look with { WeaponVisible = false } },
    snapshot with { Look = snapshot.Look with { Visor = true } }, snapshot with { HideVieraEars = true },
    snapshot with { GameBuild = "cn-7.58" }, snapshot with { EntryPoint = 2 },
})
    Check(Bind(change).State.FixedStateKey != baseline.State.FixedStateKey, "appearance/build/native entry change invalidates fixed state");
foreach (var binding in new[] { Bind(generation: "texture:13;crop:0,0,128,256;uv:0,0,1,1"),
    Bind(generation: "texture:12;crop:1,0,128,256;uv:0,0,1,1"), Bind(width: 127), Bind(height: 255) })
    Check(binding.State.EditorIdentity == baseline.State.EditorIdentity && binding.State.FixedStateKey != baseline.State.FixedStateKey,
        "texture/crop/dimension change invalidates fixed state");
var changedRange = ranges.ToArray(); changedRange[0] = new(1, 255, 1);
var changedQuantum = ranges.ToArray(); changedQuantum[0] = new(1, 255, 2);
Check(Bind(limits: changedRange).State.FixedStateKey != baseline.State.FixedStateKey, "slider minimum/maximum affect fixed state");
Check(Bind(limits: changedQuantum).State.FixedStateKey != baseline.State.FixedStateKey, "native quantum affects fixed state");

// Captured state must not change when a caller later mutates its input arrays or a returned snapshot.
var mutableSnapshot = snapshot.Copy(); var mutableRanges = ranges.ToArray();
var detached = Bind(mutableSnapshot, limits: mutableRanges);
mutableSnapshot.Customize[10] = 200; mutableSnapshot.Look.Pieces[0] = new(9999, 0, 0); mutableRanges[0] = new(2, 254, 2);
var returned = detached.Snapshot; returned.Customize[10] = 201; returned.Look.Pieces[0] = new(9998, 0, 0);
Check(detached.Snapshot.Customize[10] == 11 && detached.Snapshot.Look.Pieces[0].ItemID == 100
    && detached.State.Ranges[0] == new PortraitNativeLightRange(0,255,1), "binding is isolated from caller and consumer array mutation");

bool Refused(PortraitIdentity id, PortraitHistoryKey key, PortraitEditorSnapshot value, string generation,
    int width, int height, IReadOnlyList<PortraitNativeLightRange> limits)
    => !PortraitLightingFitBinding.TryCreate(id, key, value, generation, width, height, limits, out var result) && result is null;
Check(Refused(identity with { Generation = 0 }, history, snapshot, surface, 128,256,ranges), "zero editor generation is refused");
Check(Refused(default, history, snapshot, surface,128,256,ranges), "invalid native identity is refused");
Check(Refused(identity, history with { ContentID = 0 }, snapshot, surface,128,256,ranges), "invalid source key is refused");
Check(Refused(identity, history, snapshot, " ",128,256,ranges), "missing capture surface generation is refused");
Check(Refused(identity, history, snapshot,surface,0,256,ranges), "empty capture dimensions are refused");
Check(Refused(identity, history, snapshot,surface,int.MaxValue,int.MaxValue,ranges), "oversize dimensions cannot overflow into acceptance");
Check(Refused(identity, history, snapshot with { Customize = [] },surface,128,256,ranges), "invalid snapshot is refused");
Check(Refused(identity, history, snapshot,surface,128,256,ranges.Take(5).ToArray()), "incomplete slider metadata is refused");
var invalidRanges = ranges.ToArray(); invalidRanges[0] = new(0,255,0);
Check(Refused(identity,history,snapshot,surface,128,256,invalidRanges), "invalid native quantum is refused");
var excluded = ranges.ToArray(); excluded[0] = new(20,255,1);
Check(Refused(identity,history,snapshot,surface,128,256,excluded), "RGB outside actual native slider range is refused");
#else
Check(false, "lighting-fit binding is absent; a sampling transaction cannot bind a specific editor/source/render state");
#endif
foreach (var failure in failures) Console.Error.WriteLine("FAIL " + failure);
Console.WriteLine($"PortraitLightingBindingTests: {checks - failures.Count}/{checks} passed");
return failures.Count == 0 ? 0 : 1;
