namespace FireGaze.LocalPreview.Runtime;

/// <summary>Value-only optional IPC contract; never exposes native pointers across plugin contexts.</summary>
public sealed record CaptureProjection(int ApiVersion, bool Active, ulong ActorId, uint Territory,
    long Revision, PresentationSnapshot? Baseline);

public sealed record PresentationSnapshot(byte CharMode, byte CharModeParam, ushort EmoteId,
    ushort TimelineId, ushort BaseOverride, byte PoseType, byte CPoseState, bool WeaponDrawn,
    ushort MountId, ushort MountAnimTimeline, float MountPitch, ushort MinionId,
    byte MinionBehaviour, ushort MinionAnim, float MinionOffX, float MinionOffY, float MinionOffZ,
    float MinionRot, ushort OrnamentId, ushort OrnamentTimeline,
    float BodyDrawOffsetX, float BodyDrawOffsetY, float BodyDrawOffsetZ);
