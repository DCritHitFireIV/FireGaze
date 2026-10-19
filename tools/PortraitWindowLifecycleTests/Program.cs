using System.Reflection;
using FireGaze.PortraitPreview;

// The production Window partials run unchanged. Only its game-facing view model is detached;
// no native renderer, input injection, plugin registration or editor model is used.
var passed = 0;
var failed = 0;
void Check(bool condition, string name)
{
    Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
    if (condition) passed++; else failed++;
}

using var window = new PortraitPreviewWindow(new Model(), null!, null!);
window.IsOpen = true;
window.OpenHistory(true);
window.OpenFavorites();
var auxiliaries = new[] { "showHistory", "showFavorites", "showGlamourPlates", "showAnalysis", "showLight", "showSourceGuide" };
foreach (var field in auxiliaries) typeof(PortraitPreviewWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
window.OnClose();
foreach (var field in auxiliaries)
    Check(!(bool)typeof(PortraitPreviewWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!,
        "closing preview retires auxiliary window: " + field);
Check(!window.HistoryWindowOpen, "closed preview reports history closed");
Check(window.HistoryPhotosView, "closing preview preserves the selected photo view");
window.IsOpen = true;
Check(!window.HistoryWindowOpen, "reopening preview does not automatically reopen history");
window.OpenHistory(false);
window.OnClose();
Check(!window.HistoryPhotosView && !window.HistoryWindowOpen, "closing list view preserves list preference without leaving history open");
window.OnClose();
Check(!window.HistoryWindowOpen, "repeated close stays closed");
var blockedModel = new Model { RejectClose = true };
using (var blocked = new PortraitPreviewWindow(blockedModel, null!, null!))
{
    blocked.IsOpen = true;
    blocked.OpenHistory(true);
    blocked.OnClose();
    Check(blocked.HistoryWindowOpen && blockedModel.IsActive,
        "a refused appearance restoration retains the active auxiliary window intent");
    blockedModel.RejectClose = false;
    blocked.OnClose();
    Check(!blocked.HistoryWindowOpen && blocked.HistoryPhotosView,
        "a later successful close retires windows while preserving photo preference");
}
var bind=typeof(PortraitPreviewWindow).GetMethod("BindLightingSubject",BindingFlags.Instance|BindingFlags.NonPublic)!;
var drag=typeof(PortraitPreviewWindow).GetField("lightingRegionStart",BindingFlags.Instance|BindingFlags.NonPublic)!;
var source=new PortraitLightingFitView(false,true,"",PortraitLightingFitSessionPhase.Idle,PortraitLightingFitSessionOutcome.None,400,500,"source","owner","fixed",false,false,"sample");
bind.Invoke(window,[source]);drag.SetValue(window,new System.Numerics.Vector2(.1f,.2f));
bind.Invoke(window,[source]);
Check(drag.GetValue(window) is not null,"unchanged validated source preserves an ongoing subject selection drag");
bind.Invoke(window,[source with{FixedStateKey="new-pose"}]);
Check(drag.GetValue(window) is null,"a changed fixed rendering state discards the old image selection gesture");
drag.SetValue(window,new System.Numerics.Vector2(.1f,.2f));bind.Invoke(window,[null]);
Check(drag.GetValue(window) is null,"unavailable lighting source discards an ongoing subject selection gesture");
bind.Invoke(window,[source]);drag.SetValue(window,new System.Numerics.Vector2(.1f,.2f));window.CloseAuxiliaryWindows();
Check(drag.GetValue(window) is null,"closing preview also clears any ongoing subject selection gesture");
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class Model : IPortraitPreviewViewModel
{
    public bool IsActive { get; private set; } = true;
    public bool RejectClose;
    public PortraitLook? Current => new(new PortraitPiece[13], true, true, false);
    public string Status => "";
    public IReadOnlyList<string> SourceSummary => [];
    public IReadOnlyList<PortraitDye> Dyes => [];
    public IReadOnlyList<PortraitBrowserItem> GetItems(PortraitSlot slot) => [];
    public PortraitBrowserItem? GetItem(PortraitSlot slot, uint id) => null;
    public void Equip(PortraitSlot slot, uint id) { }
    public void Clear(PortraitSlot slot) { }
    public void Dye(PortraitSlot slot, int channel, byte stain) { }
    public void SetVisibility(bool head, bool weapon, bool visor) { }
    public void ClosePreview() { if (!RejectClose) IsActive = false; }
    public bool SyncPlate => false;
    public void SetSyncPlate(bool enabled) { }
    public bool CanUndo => false;
    public bool CanRedo => false;
    public void Undo() { }
    public void Redo() { }
    public IReadOnlyList<PortraitHistoryListItem> History => [];
    public string HistoryStatus => "";
    public void RestoreHistory(long id) { }
    public IReadOnlyList<PortraitAppearanceGroup> AppearanceGroups => [];
    public void ChangeAppearance(string groupID, int value) { }
}
