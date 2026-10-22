using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Runtime;

public sealed unsafe partial class ActorPreviewRuntime
{
    // These are distinct, independently audited internal native boundaries. The
    // public SDK wrapper delegates remain unchanged and may remain disabled.
    // All pointers are borrowed for this synchronous call; row and parameter
    // are forwarded verbatim and are never cached, dereferenced, or released.
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool PlayResolvedTimelineNative(ActionTimelineSequencer* sequence, ushort id, void* row, void* parameter);
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool PlayResolvedActionTimelineNative(ActionTimelineSequencer* sequence, ushort adjustedIntro, ushort adjustedLoop, void* parameter);
    private Hook<PlayResolvedTimelineNative>? resolvedTimelinePlay;
    private Hook<PlayResolvedActionTimelineNative>? resolvedActionTimelinePlay;
    private volatile bool resolvedTimelineAdmitted, resolvedActionTimelineAdmitted;
    private string resolvedTimelineReason = "Not initialized.";
    private string resolvedActionTimelineReason = "Not initialized.";

    public OptionalNativeHookStatus ResolvedTimelinePlayStatus => !resolvedTimelineAdmitted
        ? new(false, disposed ? "Runtime disposed." : resolvedTimelineReason) : TimelineHookStatus(resolvedTimelinePlay, resolvedTimelineReason);
    public OptionalNativeHookStatus ResolvedActionTimelinePlayStatus => !resolvedActionTimelineAdmitted
        ? new(false, disposed ? "Runtime disposed." : resolvedActionTimelineReason) : TimelineHookStatus(resolvedActionTimelinePlay, resolvedActionTimelineReason);

    private void InstallResolvedTimelineBoundaries(IGameInteropProvider interop)
    {
        var process = new NativeTimelineCurrentProcess(interop);
        var qualifier = new NativeTimelineBoundaryQualifier(process);
        if (!qualifier.TryQualify(out var plan, out var reason) || plan is null)
        {
            resolvedTimelineReason = resolvedActionTimelineReason = reason;
            return;
        }
        var installed = InstallQualifiedTimelineBoundaries(qualifier, plan,
            () => CreateReviewedTimelineBoundary(interop, process, qualifier, plan, plan.Timeline,
                (PlayResolvedTimelineNative)ResolvedTimelinePlayDetour, hook => resolvedTimelinePlay = hook),
            hook => resolvedTimelinePlay = hook,
            () => CreateReviewedTimelineBoundary(interop, process, qualifier, plan, plan.ActionTimeline,
                (PlayResolvedActionTimelineNative)ResolvedActionTimelinePlayDetour, hook => resolvedActionTimelinePlay = hook),
            hook => resolvedActionTimelinePlay = hook);
        resolvedTimelineReason = installed.Item1.Reason;
        resolvedActionTimelineReason = installed.Item2.Reason;
        resolvedTimelineAdmitted = installed.Item1.Installed;
        resolvedActionTimelineAdmitted = installed.Item2.Installed;
    }

    private Hook<T> CreateReviewedTimelineBoundary<T>(IGameInteropProvider interop,
        NativeTimelineCurrentProcess process, NativeTimelineBoundaryQualifier qualifier, NativeTimelinePlan plan,
        NativeTimelineTarget target, T detour, Action<Hook<T>?> bind) where T : Delegate
    {
        if (!ReviewedSafetyHookApi<T>.TryBind(interop, this, detour, target.Address,
            () => qualifier.TryConfirmTarget(plan, target), process.ReadReviewedBootEvidence, out var api, out var reason))
            throw new InvalidOperationException(reason);
        return ReviewedDisabledHookFactory.Create(api!, target.Address, this, detour, installedHooks, bind, TryDisposeNativeHook);
    }

    private (OptionalNativeHookStatus, OptionalNativeHookStatus) InstallQualifiedTimelineBoundaries<T, TAction>(
        NativeTimelineBoundaryQualifier qualifier, NativeTimelinePlan plan,
        Func<Hook<T>> create, Action<Hook<T>?> bind,
        Func<Hook<TAction>> createAction, Action<Hook<TAction>?> bindAction)
        where T : Delegate where TAction : Delegate
    {
        var first = PrepareQualifiedTimelineBoundary(qualifier, plan, plan.Timeline, create, bind, out var firstReason);
        var second = PrepareQualifiedTimelineBoundary(qualifier, plan, plan.ActionTimeline, createAction, bindAction, out var secondReason);
        // A constructor must leave every reviewed byte unchanged. Verify both
        // prepared hooks before enabling either, so no installed peer has to be
        // trusted or excluded from the complete byte comparison.
        if (!qualifier.TryConfirmTarget(plan, plan.Timeline))
        {
            DiscardPreparedTimelineBoundary(first, bind);
            DiscardPreparedTimelineBoundary(second, bindAction);
            const string changed = "Loaded native timeline paths changed during disabled Hook preparation.";
            return (new(false, changed), new(false, changed));
        }
        var firstAdmitted = first is not null && EnablePreparedTimelineBoundary(first, bind, out firstReason);
        var secondAdmitted = second is not null && EnablePreparedTimelineBoundary(second, bindAction, out secondReason);
        return (new(firstAdmitted, firstReason), new(secondAdmitted, secondReason));
    }

    private Hook<T>? PrepareQualifiedTimelineBoundary<T>(NativeTimelineBoundaryQualifier qualifier, NativeTimelinePlan plan,
        NativeTimelineTarget target, Func<Hook<T>> create, Action<Hook<T>?> bind, out string reason) where T : Delegate
    {
        reason = "Loaded native timeline target changed before its Hook factory.";
        if (!qualifier.TryConfirmTarget(plan, target)) return null;
        Hook<T>? hook = null;
        try
        {
            hook = create();
            // Qualification admits only the current official disabled factory.
            // Publish both Original holders before either Enable can expose a
            // callback; an eager constructor is rejected before factory entry.
            bind(hook);
            if (!installedHooks.Contains(hook)) installedHooks.Add(hook);
            reason = "";
            return hook;
        }
        catch (Exception error)
        {
            DiscardPreparedTimelineBoundary(hook, bind);
            reason = "Qualified native timeline observer unavailable: " + error.Message;
            try { log.Warning(error, "[LocalPreview] Qualified timeline Hook unavailable; core input hooks retained."); } catch { }
            return null;
        }
    }

    private bool EnablePreparedTimelineBoundary<T>(Hook<T> hook, Action<Hook<T>?> bind, out string reason) where T : Delegate
    {
        try { hook.Enable(); reason = ""; return true; }
        catch (Exception error)
        {
            DiscardPreparedTimelineBoundary(hook, bind);
            reason = "Qualified native timeline observer unavailable: " + error.Message;
            try { log.Warning(error, "[LocalPreview] Qualified timeline Enable unavailable; core input hooks retained."); } catch { }
            return false;
        }
    }

    private void DiscardPreparedTimelineBoundary<T>(Hook<T>? hook, Action<Hook<T>?> bind) where T : Delegate
    {
        if (hook is null) return;
        if (TryDisposeNativeHook(hook)) { installedHooks.Remove(hook); bind(null); }
        else if (!installedHooks.Contains(hook)) installedHooks.Add(hook);
    }

    private readonly record struct TimelineSourceBinding(nint Address, uint Generation, long Revision);

    private bool TryPrepareResolvedTimeline(ActionTimelineSequencer* sequence, ushort intro, ushort loop,
        out TimelineSourceBinding binding, out ushort emote)
    {
        binding = default;
        emote = 0;
        if (Bypass || !BindingCurrent()) return false;
        var source = Bound();
        if (source == null || sequence != &source->Timeline.TimelineSequencer || (NativeCharacter*)Control.GetLocalPlayer() != source) return false;
        var introKnown = IsSourceEmoteTimeline(intro, out var introEmote);
        var loopKnown = IsSourceEmoteTimeline(loop, out var loopEmote);
        if (!introKnown && !loopKnown) return false;
        // A shared animation or conflicting pair is still a real native input,
        // but neither stale EmoteId nor one arbitrary half supplies its identity.
        emote = introKnown && loopKnown ? introEmote == loopEmote ? introEmote : (ushort)0
            : introKnown ? introEmote : loopEmote;
        binding = new((nint)source, *(uint*)((byte*)source + 0x74), revision);
        return true;
    }

    private bool ResolvedTimelineBindingCurrent(TimelineSourceBinding binding)
    {
        if (nativeClosing || disposed || revision != binding.Revision || !BindingCurrent()) return false;
        var source = Bound();
        return source != null && (nint)source == binding.Address && (NativeCharacter*)Control.GetLocalPlayer() == source
            && *(uint*)((byte*)source + 0x74) == binding.Generation;
    }

    private bool ResolvedTimelinePlayDetour(ActionTimelineSequencer* sequence, ushort id, void* row, void* parameter)
    {
        if (!resolvedTimelineAdmitted) return resolvedTimelinePlay!.Original(sequence, id, row, parameter);
        TimelineSourceBinding binding;
        ushort emote;
        bool eligible;
        try
        {
            eligible = TryPrepareResolvedTimeline(sequence, id, 0, out binding, out emote);
        }
        catch (Exception error)
        {
            ReportHandoff(error);
            return resolvedTimelinePlay!.Original(sequence, id, row, parameter);
        }
        if (!eligible) return resolvedTimelinePlay!.Original(sequence, id, row, parameter);
        var intent = new NativeIntent(PreviewSlot.Emote, emote);
        return RunNative(intent, () => {
            var result = resolvedTimelinePlay!.Original(sequence, id, row, parameter);
            var accepted = result && ResolvedTimelineBindingCurrent(binding);
            handoff.ReportDecision(intent, accepted);
            if (accepted && emote != 0) Raise(ExternalEmoteAccepted, emote);
            return result;
        }, false);
    }

    private bool ResolvedActionTimelinePlayDetour(ActionTimelineSequencer* sequence, ushort intro, ushort loop, void* parameter)
    {
        if (!resolvedActionTimelineAdmitted) return resolvedActionTimelinePlay!.Original(sequence, intro, loop, parameter);
        TimelineSourceBinding binding;
        ushort emote;
        bool eligible;
        try
        {
            eligible = TryPrepareResolvedTimeline(sequence, intro, loop, out binding, out emote);
        }
        catch (Exception error)
        {
            ReportHandoff(error);
            return resolvedActionTimelinePlay!.Original(sequence, intro, loop, parameter);
        }
        if (!eligible) return resolvedActionTimelinePlay!.Original(sequence, intro, loop, parameter);
        var intent = new NativeIntent(PreviewSlot.Emote, emote);
        return RunNative(intent, () => {
            var result = resolvedActionTimelinePlay!.Original(sequence, intro, loop, parameter);
            var accepted = result && ResolvedTimelineBindingCurrent(binding);
            handoff.ReportDecision(intent, accepted);
            if (accepted && emote != 0) Raise(ExternalEmoteAccepted, emote);
            return result;
        }, false);
    }
}
