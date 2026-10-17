using System.Buffers.Binary;
using System.Text;
using Lumina.Excel.Sheets;

namespace FireGaze.LocalPreview.Runtime;

public sealed unsafe partial class ActorPreviewRuntime
{
    private bool flightPoseEnabled;
    private long flightPoseRevision;
    private ushort flightPoseMountId;
    private NativePreviewActor? flightPoseDisplay;
    private readonly Dictionary<ushort, GroundedFlightPoseClips> flightPoseResources = new();

    /// <summary>A visual lease only; any newer preview revision invalidates the selection.</summary>
    public bool FlightPoseActive => flightPoseEnabled && !disposed && revision == flightPoseRevision
        && visualActor is { IsReady: true, IsDisposed: false } && visualActor == flightPoseDisplay
        && effects.TryGetValue(PreviewSlot.Mount, out var effect) && effect.Id == flightPoseMountId;

    /// <summary>Switches resident animation loops on the private display hierarchy.
    /// Never enables flight, requests takeoff, changes physics or writes to the genuine player.</summary>
    public bool TryToggleFlightPose()
    {
        if (!framework.IsInFrameworkUpdateThread || disposed || ownCall || nativeDepth != 0 || !BindingCurrent()
            || !effects.TryGetValue(PreviewSlot.Mount, out var effect)
            || !TryGetSpecialActionActor(true, effect.Id, out var mount)) return false;
        var expectedRevision = revision;
        var display = visualActor;
        if (display is null || !display.IsCurrentMountChild(mount)) return false;
        bool next = !FlightPoseActive;
        try
        {
            GroundedFlightPoseClips clips = default;
            if (next && !TryGetFlightPoseClips(effect.Id, out clips)) return false;
            // Resource loading and native identity helpers may re-enter plugin code.
            if (disposed || revision != expectedRevision || visualActor != display
                || !TryGetSpecialActionActor(true, effect.Id, out var current) || current != mount
                || !display.IsCurrentMountChild(current)) return false;
            flightPoseRevision = expectedRevision;
            flightPoseMountId = effect.Id;
            flightPoseDisplay = display;
            flightPoseEnabled = next;
            if (next) display.SetFlightPoseClips(clips);
            var actor = display.Actor;
            if (actor == null) { flightPoseEnabled = false; return false; }
            ApplyGroundedMotion(actor);
            return !disposed && revision == expectedRevision && visualActor == display
                && (!next || FlightPoseActive);
        }
        catch (Exception ex)
        {
            flightPoseEnabled = false;
            log?.Warning(ex, "[LocalPreview] The local flight appearance could not be selected.");
            return false;
        }
    }

    private bool TryGetFlightPoseClips(ushort mountId, out GroundedFlightPoseClips clips)
    {
        if (flightPoseResources.TryGetValue(mountId, out clips)) return clips.IsValid;
        // Cache negative results too. Resource resolution happens on user input,
        // never on the framework's per-frame motion update.
        flightPoseResources[mountId] = clips = default;
        var entry = data.GetExcelSheet<Mount>().GetRowOrDefault(mountId);
        if (entry is not { } mount || !mount.ModelChara.IsValid) return false;
        var model = mount.ModelChara.Value;
        if (model.Type is not (2 or 3) || model.Model == 0 || model.AnimationVariant == 0) return false;
        var path = model.Type == 3
            ? $"chara/monster/m{model.Model:D4}/animation/a{model.AnimationVariant:D4}/bt_common/resident/mount.pap"
            : $"chara/demihuman/d{model.Model:D4}/animation/a{model.AnimationVariant:D4}/bt_common/resident/mount.pap";
        var bytes = data.GameData.GetFile(path)?.Data;
        if (!TryReadFlightPoseClips(bytes, out clips)) return false;
        flightPoseResources[mountId] = clips;
        return true;
    }

    private static bool TryReadFlightPoseClips(byte[]? bytes, out GroundedFlightPoseClips clips)
    {
        clips = default;
        if (bytes is null || bytes.Length < 26 || !bytes.AsSpan(0, 4).SequenceEqual("pap "u8)) return false;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
        int info = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(14, 4));
        int hkx = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4));
        if (count is <= 0 or > 4096 || info < 26 || hkx > bytes.Length || hkx < info
            || (long)info + count * 40L != hkx) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var name = bytes.AsSpan(info + i * 40, 32);
            int end = name.IndexOf((byte)0);
            if (end < 0) return false;
            names.Add(Encoding.ASCII.GetString(name[..end]));
        }
        ushort idle = names.Contains("cbnm_fmt_id0") ? (ushort)4040 : names.Contains("cbnm_id0") ? (ushort)3 : (ushort)0;
        ushort forward = names.Contains("cbnm_fmt_01f_lp0") ? (ushort)4053 : names.Contains("cbnm_01f_lp0") ? (ushort)13 : (ushort)0;
        // A missing flying back loop uses the existing flying forward posture;
        // a mount with no distinct flying movement uses its normal back loop.
        ushort backward = names.Contains("cbnm_fmt_01b_lp0") ? (ushort)4046 : forward == 4053 ? forward
            : names.Contains("cbnm_01b_lp0") ? (ushort)16 : forward;
        ushort run = names.Contains("cbnm_fmt_02f_lp0") ? (ushort)4043 : names.Contains("cbnm_02f_lp0") ? (ushort)22 : (ushort)0;
        clips = new(idle, forward, backward, run);
        return clips.IsValid;
    }
}
