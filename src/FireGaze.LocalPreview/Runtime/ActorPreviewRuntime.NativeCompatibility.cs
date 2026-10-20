using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using System.Reflection;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace FireGaze.LocalPreview.Runtime;

public sealed record OptionalNativeHookStatus(bool Installed, string Reason);

public sealed unsafe partial class ActorPreviewRuntime
{
    // Both current CN timeline functions return their native success in AL. The
    // installed ClientStructs declares them void; using those delegates for a
    // managed detour loses success even when Original already started the intro.
    // Retain the audited ABI for clients whose SDK agrees with it. A mismatch
    // disables these optional observers before asking Dalamud to create a hook;
    // the verifier in some Dalamud builds logs a mismatch but still installs it.
    // See docs/local-preview-timeline-abi-2026-10-17.md.
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool PlayTimelineNative(ActionTimelineSequencer* sequence, ushort id, void* param);
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool PlayActionTimelineNative(TimelineContainer* timeline, ushort intro, ushort loop, void* param);
    // Reviewed current CN BattleChara virtual Update and Character DisableDraw.
    // Update calls the RenderFlags consumer before drawing. These signatures must
    // resolve uniquely; no fixed runtime addresses or managed method rewriting.
    private const string CharacterUpdateSignature = "4C 8B DC 53 48 81 EC E0 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 B0 01 00 00 80 89 6C 23 00 00 40";
    private const string CharacterDisableSignature = "40 53 48 83 EC 20 80 B9 9A 00 00 00 00 48 8B D9 7D ?? 48 81 C1 80 1B 00 00";
    private Hook<GameObject.Delegates.Update>? characterUpdate;
    private Hook<GameObject.Delegates.DisableDraw>? characterDisable;
    private Hook<CompanionContainer.Delegates.SetupCompanion>? companionSetup;
    private Hook<OrnamentContainer.Delegates.SetupOrnament>? ornamentSetup;
    private Hook<PlayTimelineNative>? timelinePlay;
    private Hook<PlayActionTimelineNative>? actionTimelinePlay;
    private string timelinePlayReason = "Not initialized.";
    private string actionTimelinePlayReason = "Not initialized.";
    public OptionalNativeHookStatus SdkTimelinePlayStatus => TimelineHookStatus(timelinePlay, timelinePlayReason);
    public OptionalNativeHookStatus SdkActionTimelinePlayStatus => TimelineHookStatus(actionTimelinePlay, actionTimelinePlayReason);
    public OptionalNativeHookStatus TimelinePlayStatus => resolvedTimelineAdmitted ? ResolvedTimelinePlayStatus : SdkTimelinePlayStatus;
    public OptionalNativeHookStatus ActionTimelinePlayStatus => resolvedActionTimelineAdmitted ? ResolvedActionTimelinePlayStatus : SdkActionTimelinePlayStatus;
    private bool hmsVisibilityProtection;
    private long hmsVisibilityRepairs;
    private Dictionary<ushort, ushort>? emoteTimelineIdentities;
    public long HmsVisibilityRepairs => hmsVisibilityRepairs;
    public void SetHmsVisibilityProtection(bool enabled) => hmsVisibilityProtection = !disposed && enabled;

    private void InstallOriginalHmsNativeBoundaries(IGameInteropProvider interop)
    {
        characterUpdate = Install(interop.HookFromSignature<GameObject.Delegates.Update>(CharacterUpdateSignature, CharacterUpdateDetour));
        characterDisable = Install(interop.HookFromSignature<GameObject.Delegates.DisableDraw>(CharacterDisableSignature, CharacterDisableDetour));
        companionSetup = Install(interop.HookFromAddress<CompanionContainer.Delegates.SetupCompanion>(CompanionContainer.Addresses.SetupCompanion.Value, CompanionSetupDetour));
        ornamentSetup = Install(interop.HookFromAddress<OrnamentContainer.Delegates.SetupOrnament>(OrnamentContainer.Addresses.SetupOrnament.Value, OrnamentSetupDetour));
        timelinePlay = InstallOptionalTimelineHook<PlayTimelineNative, ActionTimelineSequencer.Delegates.PlayTimeline>(
            () => interop.HookFromAddress<PlayTimelineNative>(ActionTimelineSequencer.Addresses.PlayTimeline.Value, TimelinePlayDetour), out timelinePlayReason);
        actionTimelinePlay = InstallOptionalTimelineHook<PlayActionTimelineNative, TimelineContainer.Delegates.PlayActionTimeline>(
            () => interop.HookFromAddress<PlayActionTimelineNative>(TimelineContainer.Addresses.PlayActionTimeline.Value, ActionTimelinePlayDetour), out actionTimelinePlayReason);
        // This does not override the SDK mismatch decision above. It qualifies
        // different native function starts with complete independent contracts.
        if (timelinePlay is null && actionTimelinePlay is null) InstallResolvedTimelineBoundaries(interop);
    }

    private OptionalNativeHookStatus TimelineHookStatus<T>(Hook<T>? hook, string reason) where T : Delegate
        => disposed ? new(false, "Runtime disposed.")
            : hook is null ? new(false, reason)
            : hook.IsDisposed ? new(false, "Hook disposed.")
            : hook.IsEnabled ? new(true, "") : new(false, "Hook disabled.");

    private Hook<T>? InstallOptionalTimelineHook<T, TSdk>(Func<Hook<T>> create, out string reason)
        where T : Delegate where TSdk : Delegate
    {
        var actual = typeof(T).GetMethod("Invoke")!;
        var expected = typeof(TSdk).GetMethod("Invoke")!;
        var actualParameters = actual.GetParameters();
        var expectedParameters = expected.GetParameters();
        if (!NativeParameterMatches(actual.ReturnParameter, expected.ReturnParameter)
            || actualParameters.Length != expectedParameters.Length
            || !actualParameters.Zip(expectedParameters).All(pair => NativeParameterMatches(pair.First, pair.Second))
            || !NativeAttributeMatches<UnmanagedFunctionPointerAttribute>(typeof(T), typeof(TSdk)))
        {
            reason = $"SDK signature mismatch: {actual.ReturnType.Name} ({string.Join(", ", actualParameters.Select(p => p.ParameterType.Name))}) versus "
                + $"{expected.ReturnType.Name} ({string.Join(", ", expectedParameters.Select(p => p.ParameterType.Name))}); optional timeline observer unavailable.";
            log.Warning("[LocalPreview] {TimelineHook}: {Reason}", typeof(T).Name, reason);
            return null;
        }
        Hook<T>? hook = null;
        try
        {
            hook = create();
            hook.Enable();
            installedHooks.Add(hook);
            reason = "";
            return hook;
        }
        catch (Exception ex)
        {
            // A failed optional hook never enters runtime ownership or unwinds
            // the core input hooks. Release a partially enabled backend here.
            if (hook is not null)
                TryDisposeNativeHook(hook);
            reason = $"Optional timeline observer unavailable: {ex.Message}";
            log.Warning(ex, "[LocalPreview] Optional timeline hook setup failed; core input hooks retained.");
            return null;
        }
    }

    private static bool NativeParameterMatches(ParameterInfo actual, ParameterInfo expected)
        => actual.ParameterType == expected.ParameterType && actual.IsIn == expected.IsIn && actual.IsOut == expected.IsOut
            && NativeAttributeMatches<MarshalAsAttribute>(actual, expected);

    private static bool NativeAttributeMatches<T>(ICustomAttributeProvider actual, ICustomAttributeProvider expected) where T : Attribute
    {
        var left = actual.GetCustomAttributes(typeof(T), false).SingleOrDefault();
        var right = expected.GetCustomAttributes(typeof(T), false).SingleOrDefault();
        if (left is null || right is null) return left is null && right is null;
        return typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .All(field => Equals(field.GetValue(left), field.GetValue(right)))
            && typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .All(property => Equals(property.GetValue(left), property.GetValue(right)));
    }

    private void CharacterUpdateDetour(GameObject* obj)
    {
        if (nativeClosing || disposed) { characterUpdate!.Original(obj); return; }
        // The client can refresh the genuine actor after Framework.Update (for
        // example while a native special action is ending), which may restore its
        // draw bit. Reapply only the existing, identity-checked presentation lease
        // after Original; gameplay state and model setup remain untouched.
        var sourceUpdate = framework.IsInFrameworkUpdateThread && !disposed
            && visualActor?.IsCurrentSource((NativeCharacter*)obj) == true;
        // HMS writes bit2 directly. Ignore that bit exclusively on our current
        // private display allocation, immediately before native consumption.
        if (framework.IsInFrameworkUpdateThread && !disposed && hmsVisibilityProtection && visualActor?.IsCurrentAllocation((NativeCharacter*)obj) == true)
        {
            var flags = (ulong)obj->RenderFlags;
            if ((flags & 2) != 0) { obj->RenderFlags = (FFXIVClientStructs.FFXIV.Client.Game.Object.VisibilityFlags)(flags & ~2UL); hmsVisibilityRepairs++; }
        }
        characterUpdate!.Original(obj);
        if (sourceUpdate) visualActor?.ReassertSourceVisibility();
    }

    private void CharacterDisableDetour(GameObject* obj)
    {
        if (nativeClosing || disposed) { characterDisable!.Original(obj); return; }
        // HMS Revert has no public pre-event. Its synchronous source teardown
        // enters this native boundary before source/clone draw resources vanish.
        if (framework.IsInFrameworkUpdateThread && !disposed && !ownCall && HasActivePreview && obj != null && (NativeCharacter*)obj == Bound())
        {
            try { BeforeHmsLifecycle(); }
            catch (Exception ex) { ReportHandoff(ex); }
        }
        characterDisable!.Original(obj);
    }

    private void CompanionSetupDetour(CompanionContainer* container, short id, uint param)
    {
        if (Bypass || container == null || container->OwnerObject != Bound()) { companionSetup!.Original(container,id,param); return; }
        var source = Bound();
        if (source == null || (NativeCharacter*)Control.GetLocalPlayer() != source || container != &source->CompanionData) { companionSetup!.Original(container,id,param); return; }
        var sourceGeneration = *(uint*)((byte*)source + 0x74);
        var expectedRevision = revision;
        var previousId = container->CompanionId;
        var requestedId = unchecked((ushort)id);
        var intent = new NativeIntent(PreviewSlot.Minion,requestedId);
        RunNative(intent, () => {
            companionSetup!.Original(container,id,param);
            // Current CN can retain CompanionId == 0 after the actual minion exists.
            // Prefer the live child identity; a newly set positive canonical id is
            // accepted only while a child is still pending. A stale cached id must
            // not turn an unchanged unrelated child or no-op into native acceptance.
            bool accepted = false;
            if (!disposed && revision == expectedRevision && BindingCurrent() && Bound() == source && (NativeCharacter*)Control.GetLocalPlayer() == source
                && *(uint*)((byte*)source + 0x74) == sourceGeneration && container->OwnerObject == source)
            {
                var child = container->CompanionObject;
                accepted = requestedId == 0
                    ? child == null && container->CompanionId == 0
                    : child != null ? child->BaseId == requestedId
                    : container->CompanionId == requestedId && previousId != requestedId;
            }
            handoff.ReportDecision(intent,accepted);
            return true;
        }, false);
    }

    private void OrnamentSetupDetour(OrnamentContainer* container, short id, uint param)
    {
        if (Bypass || container == null || container->OwnerObject != Bound()) { ornamentSetup!.Original(container,id,param); return; }
        var intent = new NativeIntent(PreviewSlot.Accessory,unchecked((ushort)id));
        RunNative(intent, () => {
            ornamentSetup!.Original(container,id,param);
            handoff.ReportDecision(intent, container->OwnerObject == Bound() && container->OrnamentId == unchecked((ushort)id));
            return true;
        }, false);
    }

    private bool IsSourceEmoteTimeline(ushort timeline, out ushort emote)
    {
        emote = 0;
        if (Bypass || timeline is 0 or 1 or 3 or 34 or 65001) return false;
        var source = Bound();
        if (source == null) return false;
        {
            // Original HMS plays cosmetic timelines with absent or stale EmoteId.
            // Resolve known emote content without treating ordinary movement/idle
            // as input. Shared animation rows still end the trial but never invent
            // a unique emote identity for history or GPose.
            if (emoteTimelineIdentities is null)
            {
                emoteTimelineIdentities = new();
                void Register(uint timelineId, ushort emoteId)
                {
                    if (timelineId is 0 or 1 or 3 or 34 or 65001 || timelineId > ushort.MaxValue) return;
                    var key = (ushort)timelineId;
                    if (emoteTimelineIdentities.TryGetValue(key, out var previous) && previous != emoteId) emoteTimelineIdentities[key] = 0;
                    else if (!emoteTimelineIdentities.ContainsKey(key)) emoteTimelineIdentities[key] = emoteId;
                }
                var replacements = data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimelineReplace>();
                foreach (var entry in data.GetExcelSheet<Lumina.Excel.Sheets.Emote>())
                {
                    if (entry.RowId == 0 || entry.RowId > ushort.MaxValue) continue;
                    foreach (var variant in entry.ActionTimeline)
                    {
                        if (variant.RowId is 0 or 1 or 3 or 34 || variant.RowId > ushort.MaxValue) continue;
                        Register(variant.RowId, (ushort)entry.RowId);
                        // Current CN native action adjustment reads row+6 as an
                        // ActionTimelineReplace index (sheet 608), then chooses
                        // its first/second ushort for swimming/diving. Lumina's
                        // generated name for row+6 is KillUpper; the native route
                        // and table layout, not that label, establish this use.
                        // Both columns must exist, exactly as in the native test.
                        if (variant.Value.KillUpper is var replacementId && replacementId != 0
                            && replacements.GetRowOrDefault(replacementId) is { } replacement
                            && replacement.Old.RowId != 0 && replacement.New.RowId != 0)
                        {
                            Register(replacement.Old.RowId, (ushort)entry.RowId);
                            Register(replacement.New.RowId, (ushort)entry.RowId);
                        }
                    }
                }
            }
            return emoteTimelineIdentities.TryGetValue(timeline,out emote);
        }
    }

    private bool TimelinePlayDetour(ActionTimelineSequencer* sequence, ushort id, void* param)
    {
        if (nativeClosing || disposed) return timelinePlay!.Original(sequence,id,param);
        if (!framework.IsInFrameworkUpdateThread) return timelinePlay!.Original(sequence,id,param);
        var source = Bound();
        if (source == null || sequence != &source->Timeline.TimelineSequencer || !IsSourceEmoteTimeline(id,out var emote)) return timelinePlay!.Original(sequence,id,param);
        var intent = new NativeIntent(PreviewSlot.Emote,emote);
        return RunNative(intent, () => {
            bool accepted = timelinePlay!.Original(sequence,id,param);
            handoff.ReportDecision(intent,accepted);
            if (accepted && emote!=0) Raise(ExternalEmoteAccepted,emote);
            return accepted;
        },false);
    }

    private bool ActionTimelinePlayDetour(TimelineContainer* timeline, ushort intro, ushort loop, void* param)
    {
        if (nativeClosing || disposed) return actionTimelinePlay!.Original(timeline,intro,loop,param);
        if (!framework.IsInFrameworkUpdateThread) return actionTimelinePlay!.Original(timeline,intro,loop,param);
        var source = Bound();
        if (source == null || timeline != &source->Timeline || !(IsSourceEmoteTimeline(intro,out var emote) || IsSourceEmoteTimeline(loop,out emote))) return actionTimelinePlay!.Original(timeline,intro,loop,param);
        var intent = new NativeIntent(PreviewSlot.Emote,emote);
        return RunNative(intent, () => {
            bool accepted = actionTimelinePlay!.Original(timeline,intro,loop,param);
            handoff.ReportDecision(intent,accepted);
            if (accepted && emote!=0) Raise(ExternalEmoteAccepted,emote);
            return accepted;
        },false);
    }
}
