using System.Runtime.InteropServices;
using FireGaze.PortraitPreview;

// This test compiles the production Feature.Editing partial unchanged. Only its host, input,
// native allocation and window boundaries are supplied here; history/session/catalog are real.
namespace FFXIVClientStructs.FFXIV.Client.UI.Agent
{
    internal struct AgentBannerEditorState
    {
        public enum EditorOpenType { Portrait, Gearset, AdventurerPlate }
        public int OpenerEnabledGearsetIndex;
    }
}

namespace FFXIVClientStructs.FFXIV.Client.UI.Misc
{
    internal unsafe struct RaptureGearsetModule
    {
        public fixed int EnabledGearsetIndex2EntryIndex[100];
        public static RaptureGearsetModule* Value;
        public static RaptureGearsetModule* Instance() => Value;
    }
}

namespace Dalamud.Bindings.ImGui
{
    // Rendering isn't invoked in these tests. The declarations let the real content method compile.
    internal static class ImGui
    {
        public static void TextWrapped(string value) { }
        public static void TextDisabled(string value) { }
        public static void BeginDisabled(bool value) { }
        public static void EndDisabled() { }
        public static bool Button(string value) => false;
        public static bool Checkbox(string label, ref bool value) => false;
        public static void SameLine() { }
    }
}

namespace FireGaze.UI
{
    // Only the unused drawing boundary is declared here; production editing state is exercised below.
    internal static class UiHelpers
    {
        public static IDisposable Disabled(bool value = true) => new DrawingScope();
        private sealed class DrawingScope : IDisposable { public void Dispose() { } }
    }
}

namespace FireGaze
{
    internal static class Plugin
    {
        public static IntegrationKeys Keys { get; } = new();
        public static Dalamud.Plugin.Services.IDataManager DataManager => throw new InvalidOperationException("Live data initialization is outside this integration boundary.");
    }
    internal sealed class IntegrationKeys
    {
        public bool Held;
        public bool IsVirtualKeyValid(int key) => key == 65;
        public bool this[int key] => key == 65 && Held;
    }
}

namespace FireGaze.PortraitPreview
{
    internal sealed record PortraitHistoryListItem(long ID, DateTimeOffset At, string Label, bool IsCurrent);
    internal sealed class PortraitPreviewWindow
    {
        public bool IsOpen;
        public static void DrawHistoryRows(PortraitPreviewFeature feature, bool allowed) { }
    }

    internal sealed unsafe class NativePortraitTarget : IPortraitPreviewTarget, IDisposable
    {
        public readonly FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditorState* State;
        public PortraitIdentity Identity;
        public PortraitEditorSnapshot Snapshot;
        public PortraitEditorSnapshot? Original;
        public bool Ready = true;
        public bool Current = true;
        public bool CanBlockSave = true;
        public bool RejectApply = false;
        public int CaptureCalls;
        public int ApplyCalls;
        public int MirrorUpdates;
        public int MirrorCloseCalls;
        public bool RejectMirrorClose;
        public bool IsPlateMirrorActive;
        public string? LastError = null;
        public string SnapshotGameBuild { get; set; } = "fixture";

        public NativePortraitTarget(PortraitEditorSnapshot snapshot)
        {
            State = (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditorState*)NativeMemory.AllocZeroed((nuint)sizeof(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditorState));
            Identity = new(1, (nint)State, 2, 3, 4);
            Snapshot = snapshot.Copy();
        }
        public bool IsCurrent(PortraitIdentity identity) => Current && identity == Identity;
        public bool TryResolve(out PortraitIdentity identity, out int bounds, out int openType)
        { identity = Identity; bounds = openType = 0; return Current && Ready; }
        public PortraitEditorSnapshot? CaptureEditorState(PortraitIdentity identity) => Ready && IsCurrent(identity) ? Snapshot.Copy() : null;
        public PortraitLook? Capture(PortraitIdentity identity)
        {
            if (!Ready || !IsCurrent(identity)) return null;
            CaptureCalls++;
            Original = Snapshot.Copy();
            return Snapshot.Look.Copy();
        }
        public bool Apply(PortraitIdentity identity, PortraitLook look)
        {
            if (!IsCurrent(identity) || RejectApply) return false;
            ApplyCalls++;
            Snapshot = Snapshot with { Look = look.Copy() };
            return true;
        }
        public bool RestoreOriginal(PortraitIdentity identity, PortraitLook look)
        {
            if (!IsCurrent(identity) || RejectApply || Original is null) return false;
            Snapshot = Snapshot with { Customize = (byte[])Original.Customize.Clone(), HideVieraEars = Original.HideVieraEars };
            return Apply(identity, look);
        }
        public bool ApplyEditorState(PortraitIdentity identity, PortraitEditorSnapshot snapshot)
        {
            if (!Ready || !IsCurrent(identity) || RejectApply || !snapshot.IsCompatibleWith(Snapshot)) return false;
            ApplyCalls++; Snapshot = snapshot.Copy(); return true;
        }
        public bool ClosePlateMirror()
        {
            MirrorCloseCalls++;
            if (RejectMirrorClose && IsPlateMirrorActive) return false;
            IsPlateMirrorActive = false;
            return true;
        }
        public PortraitEditorSnapshot? MirroredSnapshot;
        public bool UpdatePlateMirror(PortraitIdentity identity, PortraitEditorSnapshot snapshot, bool enabled)
        { if (!IsCurrent(identity)) return false; MirrorUpdates++; MirroredSnapshot = snapshot.Copy(); IsPlateMirrorActive = enabled; return enabled; }
        public void Dispose() => NativeMemory.Free(State);
    }

    internal sealed unsafe partial class PortraitPreviewFeature : IDisposable
    {
        private readonly NativePortraitTarget target;
        private readonly PortraitPreviewSession session;
        private readonly PortraitPreviewWindow window = new();
        private bool editorAvailable = true;
        private string status = "";
        private DateTimeOffset testClock = DateTimeOffset.Parse("2026-10-18T10:00:00+08:00");
        public OwnedCharacterKey? Owner = new(10,20);
        public string? LastError;
        public bool IsActive => session.IsActive;
        public string Status => plateMirrorCloseError ?? status;
        public PortraitHistoryStore Store => history;
        public NativePortraitTarget Native => target;
        public PortraitHistoryKey? Key => observedKey;
        public int MirrorUpdates => target.MirrorUpdates;
        public bool DeferredBaseline => deferredBaselineSync;
        public PortraitEditorSnapshot? ObservedSnapshot => observedEditor;
        public PortraitEditorSnapshot? MirroredSnapshot => target.MirroredSnapshot;

        public PortraitPreviewFeature(string file, int byteLimit = PortraitHistoryStore.MaximumBytes)
        {
            var customize = new byte[26]; customize[0]=1;customize[4]=1;customize[5]=1;customize[6]=1;
            target = new(new() {GameBuild="fixture",EntryPoint=2,Customize=customize,Composition=new() {BannerTimeline=1},Look=new(new PortraitPiece[13],true,true,false)});
            session = new(target);
            appearance = new([new(new(1,1,0,1),[new("hair","发型",6,255,0,[new(1,"发型1"),new(2,"发型2")])])]);
            editorValidation = new(new PortraitValidationData([], [0,1], [0,1], [0,1], [0,1], [0,1], [0,1], [0,1], [0,1]));
            history = new(file,()=>testClock,byteLimit);
            historyBatch = new(history,()=>testClock);
        }
        private OwnedCharacterKey? CurrentCharacter() => Owner;
        private void Mutate(System.Action action) => action();
        private void Error(string value) { LastError=status=value; }
        public void OpenPreview() { if (!target.CanBlockSave || !session.Open(target.Identity)) throw new InvalidOperationException("Test preview not ready."); window.IsOpen=true; }
        public void Sample(bool pointer=false, bool keyboard=false, int advanceMilliseconds=50)
        { testClock=testClock.AddMilliseconds(advanceMilliseconds);pointerHeld=pointer;Plugin.Keys.Held=keyboard;nextEditorSample=DateTime.MinValue;PollEditing(); }
        public void EditComposition(Func<PortraitComposition,PortraitComposition> change)
            => target.Snapshot=target.Snapshot with {Composition=change(target.Snapshot.Composition)};
        public void RollbackAndSync()
        { FlushEditing();if(!session.Close())throw new InvalidOperationException("Fixture rollback failed.");SynchronizeAfterClose(); }
        public void RecordDirect(string label="测试操作") => RecordDirectChange(label);
        public void RefreshRows() => RefreshHistoryRows();
        public void Advance(int milliseconds) => testClock=testClock.AddMilliseconds(milliseconds);
        public void OverwriteStatus(string value) => status = value;
        public void FlushOnly() => FlushEditing();
        public void PollThrottled() { nextEditorSample=DateTime.MaxValue; PollEditing(); }
        public bool SyncLatestLook() => SynchronizeCurrentLook();
        public bool SetNativeVisibility(bool head, bool weapon, bool visor) => session.SetVisibility(head, weapon, visor);
        public void Dispose() {Plugin.Keys.Held=false;history.Dispose();target.Dispose();}
    }
}
