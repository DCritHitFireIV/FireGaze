using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace FireGaze.LocalPreview.Runtime;

public sealed unsafe partial class NativeCompanionDisplayActor
{
    private Companion* demoChild;
    private long demoBindingRevision;
    private ushort demoOverride;
    private ushort priorDemoOverride;

    internal bool HasInteractionDemo(ushort timeline)
        => !IsDisposed && demoChild == child && demoBindingRevision == bindingRevision
            && demoOverride == timeline && IsCurrentCompanion(child)
            && child->Timeline.BaseOverride == timeline;

    internal bool TryStartInteractionDemo(ushort timeline)
    {
        if (timeline is not (4 or 198) || !IsReady || IsDisposed
            || !IsCurrentCompanion(child) || ActionTimelineSequencer.Addresses.PlayTimeline.Value == 0)
            return false;
        if (timeline == 4 && child->Special != 11) return false;
        if (demoChild == child && demoBindingRevision == bindingRevision
            && demoOverride == timeline && child->Timeline.BaseOverride == timeline) return true;

        StopInteractionDemo();
        if (!IsReady || IsDisposed || !IsCurrentCompanion(child)) return false;
        var current = child;
        priorDemoOverride = current->Timeline.BaseOverride;
        demoChild = current;
        demoBindingRevision = bindingRevision;
        demoOverride = timeline;
        // The native timeline consumer prefers this override to the behavior's
        // default base. Pin it once; replaying it each frame restarts the motion.
        current->Timeline.BaseOverride = timeline;
        current->Timeline.TimelineSequencer.PlayTimeline(timeline);
        return !IsDisposed && IsCurrentCompanion(current) && child == current
            && demoBindingRevision == bindingRevision && current->Timeline.BaseOverride == timeline;
    }

    internal void StopInteractionDemo()
    {
        var current = demoChild;
        var revision = demoBindingRevision;
        var ownedOverride = demoOverride;
        var restore = priorDemoOverride;
        demoChild = null;
        demoOverride = 0;
        demoBindingRevision = 0;
        if (current == null || IsDisposed || revision != bindingRevision
            || !IsCurrentCompanion(current) || current->Timeline.BaseOverride != ownedOverride) return;
        current->Timeline.BaseOverride = restore;
        // Zero hands selection back to the native default resolver. It can be
        // walking, swimming or perching; forcing idle here would corrupt that state.
    }
}
