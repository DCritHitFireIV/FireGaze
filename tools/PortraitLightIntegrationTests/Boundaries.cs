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
        public static IntegrationFramework Framework { get; } = new();
        public static IntegrationObjects Objects { get; } = new();
        public static Dalamud.Plugin.Services.IDataManager DataManager => throw new InvalidOperationException("Live data initialization is outside this integration boundary.");
    }
    internal sealed class IntegrationFramework { public bool IsInFrameworkUpdateThread = true; }
    internal sealed class IntegrationObjects { public IntegrationPlayer? LocalPlayer { get; set; } = new(); }
    internal sealed class IntegrationPlayer { public IntegrationName Name { get; } = new(); }
    internal sealed class IntegrationName { public string TextValue { get; set; } = "收藏来源角色"; }
    internal sealed class IntegrationKeys
    {
        public bool Held;
        public bool IsVirtualKeyValid(int key) => key == 65;
        public bool this[int key] => key == 65 && Held;
    }
}

namespace FireGaze.PortraitPreview
{
    // Actual saved-template native memory layout is exercised in PortraitGlamourPlateTests.
    // This boundary supplies detached observations to the unchanged production Feature partial.
    internal sealed class PortraitGlamourPlateReader(Func<bool> framework, Func<OwnedCharacterKey?> owner)
    {
        public PortraitGlamourPlateCapture? Next;
        public int Calls;
        public PortraitGlamourPlateCapture Capture() { Calls++; return Next ?? new(owner(), null, "fixture unloaded"); }
    }
    internal sealed record PortraitHistoryListItem(long ID, DateTimeOffset At, string Label, bool IsCurrent);
    internal sealed class PortraitPreviewWindow
    {
        public bool IsOpen;
        public void CloseAuxiliaryWindows() { }
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
        public bool RejectLight;
        public int LightWrites;
        public bool SetDirectionalLightAngles(PortraitIdentity identity,PortraitNativeLightAngles expected,PortraitNativeLightAngles desired)
        {
            if(!Ready || !IsCurrent(identity) || RejectLight || !desired.IsValid || PortraitNativeLightAngles.From(Snapshot.Composition)!=expected)return false;
            if(expected==desired)return true;
            Snapshot=Snapshot with {Composition=Snapshot.Composition with {DirectionalVerticalAngle=desired.Vertical,DirectionalHorizontalAngle=desired.Horizontal}};LightWrites++;return true;
        }
        public int CaptureCalls;
        public int EditorCaptureCalls;
        public int ApplyCalls;
        public int MirrorUpdates;
        public int MirrorCloseCalls;
        public bool RejectMirrorClose;
        public bool IsPlateMirrorActive;
        public string? LastError = null;
        public string SnapshotGameBuild { get; set; } = "fixture";
        public Action? AfterEditorApply;

        public NativePortraitTarget(PortraitEditorSnapshot snapshot)
        {
            State = (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditorState*)NativeMemory.AllocZeroed((nuint)sizeof(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditorState));
            Identity = new(1, (nint)State, 2, 3, 4);
            Snapshot = snapshot.Copy();
        }
        public bool IsCurrent(PortraitIdentity identity) => Current && identity == Identity;
        public bool TryResolve(out PortraitIdentity identity, out int bounds, out int openType)
        { identity = Identity; bounds = openType = 0; return Current && Ready; }
        public PortraitEditorSnapshot? CaptureEditorState(PortraitIdentity identity)
        {EditorCaptureCalls++;return Ready && IsCurrent(identity) ? Snapshot.Copy() : null;}
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
            ApplyCalls++; Snapshot = snapshot.Copy();AfterEditorApply?.Invoke(); return true;
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
        private OwnedEquipmentView ownedView = new(null, [], []);
        private Dictionary<PortraitSlot, PortraitBrowserItem[]> browser = [];
        private readonly OwnedEquipmentCatalog owned;
        private readonly OwnedCacheWriter cacheWriter;
        private readonly OwnedCacheSavePolicy ownedSavePolicy;
        private bool editorAvailable = true;
        private string status = "";
        private DateTimeOffset testClock = DateTimeOffset.Parse("2026-10-18T10:00:00+08:00");
        public OwnedCharacterKey? Owner = new(10,20);
        public string? LastError;
        public bool IsActive => session.IsActive;
        private bool LightingFitHistorySuppressed => false;
        private bool CancelLightingFitForEdit() => true;
        public string Status => plateMirrorCloseError ?? cachePersistenceWarning ?? status;
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
            owned = new(id=>id);
            var cacheFile=new OwnedEquipmentCacheFile(Path.GetDirectoryName(file)!);cacheFile.Load(owned,out _);
            cacheWriter=new(json=>cacheFile.SaveJSON(json,out _));ownedSavePolicy=new(cacheWriter);
            InitializeLibrary(Path.GetDirectoryName(file)!);
            contentID = Owner!.Value.ContentID;
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
        public IReadOnlyList<PortraitBrowserItem> GetItems(PortraitSlot slot) => browser.GetValueOrDefault(slot) ?? [];
        public void SetOwnedItems(OwnedEquipmentView view,PortraitSlot slot,PortraitBrowserItem[] items) {ownedView=view;browser[slot]=items;}
        public void SetRetainerName(ulong id,string name) => retainerDisplayNames[id]=name;
        public void SetLibraryValidation(IReadOnlyList<PortraitValidationItem>? items = null) => editorValidation = new(new PortraitValidationData(items ?? [], [0,1], [0,1], [0,1], [0,1], [0,1], [0,1], [0,1], [0,1]));
        public void SetAppearance(PortraitAppearanceCatalog catalog) => appearance = catalog;
        public void SetPlateObservations(PortraitGlamourPlate[] plates) { glamourPlates.ActivateCharacter(Owner!.Value);glamourPlates.ApplyRead(Owner.Value,testClock,plates);glamourPlateView=glamourPlates.BuildView(testClock); }
        public PortraitGlamourPlateReader PlateReader => glamourPlateReader;
        public void SampleLibrary() => PollLibrary();
        public void SetPlateNext(PortraitGlamourPlate[] plates) => glamourPlateReader.Next = new(Owner,plates,"已读取");
        public void MakePlatePollDue() => nextGlamourPlateRead = DateTime.MinValue;
        public void Equip(PortraitSlot slot,uint id) => throw new InvalidOperationException("Native equip is outside this test boundary.");
        public void ObserveOwned(OwnedSourceKey source,OwnedItemRecord[] records) {owned.ActivateCharacter(Owner!.Value);owned.ApplyRead(Owner.Value,source,testClock,records);QueueOwnedCacheIfNeeded();}
        public bool FlushOwned() => cacheWriter.FlushAsync().GetAwaiter().GetResult();
        public int OwnedWrites => cacheWriter.CompletedWrites;
        public void Dispose() {Plugin.Keys.Held=false;DisposeLibrary();cacheWriter.Dispose();history.Dispose();target.Dispose();}
    }
}
