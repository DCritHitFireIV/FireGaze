using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace FireGaze
{
    internal static class Plugin
    {
        internal static readonly OpeningAddonLifecycle AddonLifecycle = new();
        internal static readonly OpeningClientState ClientState = new();
    }
    internal sealed class OpeningAddonLifecycle
    {
        internal readonly List<Action<AddonEvent, AddonArgs>> Listeners = [];
        internal void RegisterListener(AddonEvent type, string name, Action<AddonEvent, AddonArgs> callback) => Listeners.Add(callback);
        internal void UnregisterListener(AddonEvent type, string name, Action<AddonEvent, AddonArgs> callback) => Listeners.Remove(callback);
        internal void FinalizeEditor() { foreach (var listener in Listeners.ToArray()) listener(AddonEvent.PreFinalize, null!); }
    }
    internal sealed class OpeningClientState
    {
        internal event Action<int, int>? Logout;
        internal event Action<uint>? TerritoryChanged;
        internal void LogOut() => Logout?.Invoke(0, 0);
        internal void ChangeTerritory() => TerritoryChanged?.Invoke(2);
    }
}

namespace FireGaze.PortraitPreview
{
    // Compiles the real Feature lifecycle/command adapter. The fixture replaces only native reads
    // and the native open body; it cannot use an ACK, cached Diagnostics or a timer to create roots.
    internal sealed unsafe partial class PortraitPreviewFeature
    {
        internal PortraitEditorOpeningSample Current = Fixture.Closed();
        internal int Reads, Calls, Errors;
        internal double Now = 0;
        internal Action? DuringNative;
        internal PortraitPreviewFeature()
            => InitializeEditorOpening(new(ReadEditorOpeningSample, () => Now));
        private PortraitEditorOpeningSample? ReadEditorOpeningSample() { Reads++; return Current; }
        private void OpenEditorCore(string kind)
        {
            Calls++;
            Current = Current with { State = 20, View = 30, StateOwner = 10, ViewOwner = 10,
                OpenType = kind == "plate" ? 2 : 1, OpenerSourceID = kind == "plate" ? 0 : 3,
                Conditions = Fixture.Flags(1, 100) };
            DuringNative?.Invoke();
        }
        private void Error(string error) => Errors++;
        internal void Open(string kind, string nonce) => OpenEditorWithReceipt(kind, nonce);
        internal void Tick() => UpdateEditorOpening();
        internal PortraitEditorOpeningReceipt Receipt => ReadEditorOpeningReceipt();
        internal void Release() => DisposeEditorOpening();
    }
}
