using System.Runtime.InteropServices;

namespace FireGaze
{
    internal static class Plugin
    {
        internal static readonly OpeningFramework Framework = new();
    }
    internal sealed class OpeningFramework { internal bool IsInFrameworkUpdateThread = true; }
}

namespace FFXIVClientStructs.FFXIV.Client.UI.Agent
{
    // Test doubles for native dependencies, backed only by caller-owned fixture memory.
    // The production sampler itself is compiled unchanged into this executable.
    internal unsafe struct AgentBannerEditor
    {
        internal byte Active;
        internal static int GetterCalls;
        internal static Action? DuringGetter;
        internal bool IsAgentActive() { GetterCalls++; DuringGetter?.Invoke(); return Active != 0; }
    }
    internal unsafe struct AgentBannerEditorState
    {
        internal AgentBannerEditor* AgentBannerEditor;
        internal EditorOpenType OpenType;
        internal int OpenerEnabledGearsetIndex;
        internal enum EditorOpenType { Gearset = 1, AdventurerPlate = 2 }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CharaViewPortrait { internal nint Agent; }
}

namespace FFXIVClientStructs.FFXIV.Client.UI.Misc
{
    internal unsafe struct RaptureGearsetModule
    {
        internal int CurrentGearsetIndex;
        internal fixed int EnabledGearsetIndex2EntryIndex[100];
        internal static nint Address;
        internal static RaptureGearsetModule* Instance() => (RaptureGearsetModule*)Address;
    }
}

namespace FireGaze.PortraitPreview
{
    internal static class PortraitSafetySnapshotNative
    {
        internal static readonly Dictionary<nint, object> Reads = [];
        internal static bool TryRead<T>(nint address, out T value) where T : unmanaged
        {
            if (Reads.TryGetValue(address, out var stored) && stored is T typed) { value = typed; return true; }
            value = default; return false;
        }
    }

    internal sealed unsafe partial class PortraitPreviewFeature : IDisposable
    {
        private bool disposed;
        private readonly OpeningTarget target = new();
        private readonly OpeningSession session = new();
        private readonly OpeningDrag lightDrag = new();
        private readonly OpeningPhysical photos = new();
        private readonly OpeningPhysical liveImageService = new(), lightingFitCapture = new();
        private readonly OpeningLive nativeLiveController = new();
        private readonly OpeningWindow window = new();
        private bool SourceCensusBlocksOperations, NativeLiveEnabled, LightingFitMutationBlocked, plateMirrorClosePending;
        private string? plateMirrorCloseError;
        private readonly nint agent, state, view, gearsets;
        internal PortraitSafetyBoundary Before, After;
        internal OpeningCharacter? Character = new(77, 88);
        internal Action? DuringBoundary;
        internal int BoundaryReads;
        internal PortraitPreviewFeature()
        {
            agent = Marshal.AllocHGlobal(sizeof(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditor));
            state = Marshal.AllocHGlobal(64); view = Marshal.AllocHGlobal(64);
            gearsets = Marshal.AllocHGlobal(sizeof(FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule));
            *(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditor*)agent = default;
            var module = (FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule*)gearsets;
            *module = default; module->CurrentGearsetIndex = 3; module->EnabledGearsetIndex2EntryIndex[0] = 3;
            FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Address = gearsets;
            FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditor.GetterCalls = 0;
            FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditor.DuringGetter = null;
            Plugin.Framework.IsInFrameworkUpdateThread = true;
            Before = new(10, 42, 1, 100, 9, 200, 112, Flags(1),
                new(777, 178, 1, 2, 3, 18, 1000, false, false, false, 0), (long)agent, 0, 0);
            After = Before;
        }
        internal static int[] Flags(params int[] active) => Enumerable.Range(0, 112).Select(index => active.Contains(index) ? 1 : 0).ToArray();
        private OpeningCharacter? CurrentCharacter() => Character;
        internal PortraitEditorOpeningSample? Sample() { BoundaryReads = 0; return ReadEditorOpeningSample(); }
        internal IReadOnlyList<PortraitOpeningIssue> OpeningIssues => ReadEditorOpeningIssues();
        internal void SetRestoreFailure() { plateMirrorClosePending = true; plateMirrorCloseError = "fixture restore failure"; }
        internal void SetFocus(int? focus) { Before = Before with { FocusByte = focus }; After = Before; }
        internal void SetDisposed() => disposed = true;
        internal void SetBusy(string name)
        {
            switch (name)
            {
                case "sourceCensus": SourceCensusBlocksOperations = true; break;
                case "nativeLive": NativeLiveEnabled = true; break;
                case "nativeLivePhysical": nativeLiveController.IsPhysicallyIdle = false; break;
                case "lightingFit": LightingFitMutationBlocked = true; break;
                case "lightDrag": lightDrag.IsActive = true; break;
                case "photosBusy": photos.IsBusy = true; break;
                case "photosPhysical": photos.IsPhysicallyIdle = false; break;
                case "livePhysical": liveImageService.IsPhysicallyIdle = false; break;
                case "fitPhysical": lightingFitCapture.IsPhysicallyIdle = false; break;
                case "windowCensus": window.IsSourceCensusIdle = false; break;
                case "plateClose": plateMirrorClosePending = true; break;
                case "plateMirror": target.IsPlateMirrorActive = true; break;
            }
        }
        internal void SetOpened()
        {
            var nativeState = new FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditorState
            {
                AgentBannerEditor = (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditor*)agent,
                OpenType = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditorState.EditorOpenType.Gearset,
                OpenerEnabledGearsetIndex = 0,
            };
            PortraitSafetySnapshotNative.Reads[state] = nativeState;
            PortraitSafetySnapshotNative.Reads[view] = agent;
            target.Identity = new((long)agent, (long)state, (long)view, 7, 40);
            ((FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditor*)agent)->Active = 1;
            Before = Before with { EditorState = (long)state, EditorView = (long)view, Conditions = Flags(1, 100) };
            After = Before;
        }
        internal void ChangeGearset() => ((FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule*)gearsets)->CurrentGearsetIndex = 4;
        internal void SetPreviewActive() => session.IsActive = true;
        internal void SetSaveGuardUnavailable() => target.CanBlockSave = false;
        public void Dispose()
        {
            FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Address = 0;
            FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditor.DuringGetter = null;
            PortraitSafetySnapshotNative.Reads.Clear();
            foreach (var address in new[] { agent, state, view, gearsets }) Marshal.FreeHGlobal(address);
        }
        private sealed class SafetySnapshotSource(PortraitPreviewFeature feature)
        {
            internal PortraitSafetyBoundary ReadBoundary()
            {
                feature.DuringBoundary?.Invoke();
                return feature.BoundaryReads++ == 0 ? feature.Before : feature.After;
            }
        }
    }
    internal sealed record OpeningCharacter(ulong AccountID, ulong ContentID);
    internal sealed record OpeningIdentity(long Editor, long State, long Model, uint Generation, ulong Character);
    internal sealed class OpeningTarget
    {
        internal OpeningIdentity Identity = new(0, 0, 0, 0, 0);
        internal bool CanBlockSave = true, IsPlateMirrorActive;
        internal bool TryResolve(out OpeningIdentity identity, out object? bounds, out int kind)
        { identity = Identity; bounds = null; kind = 1; return identity.State > 0; }
        internal bool IsCurrent(OpeningIdentity identity) => identity == Identity;
    }
    internal sealed class OpeningSession { internal bool IsActive; }
    internal sealed class OpeningDrag { internal bool IsActive; }
    internal sealed class OpeningPhysical { internal bool IsBusy; internal bool IsPhysicallyIdle = true; }
    internal sealed class OpeningLive { internal bool IsPhysicallyIdle = true; }
    internal sealed class OpeningWindow { internal bool IsSourceCensusIdle = true; }
}
