using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using Lumina;

internal static class ExitSlotTests
{
    public static void Run(GameData game, Action<bool, string> check)
    {
        Console.WriteLine("Current client special bindings: " + string.Join(", ", Enum.GetNames<InputId>()
            .Where(name => name.StartsWith("HOTBAR_EX", StringComparison.Ordinal))));
        check(TrialSpecialHotbarModel.ExitSlot == 11, "trial exit occupies the twelfth slot in the first row");
        check((int)InputId.HOTBAR_EX_B == (int)InputId.HOTBAR_EX_1 + 11,
            "the twelfth trial slot uses the player's native special-bar binding twelve");
        var catalog = new TrialSpecialHotbarCatalog(game.Excel);
        foreach (var mount in new[] { false, true })
        {
            var model = new TrialSpecialHotbarModel(catalog);
            var plays = 0; var exits = 0; var toggles = 0;
            Func<uint, bool> play = _ => { plays++; return true; };
            Action exit = () => exits++;
            Func<bool> toggle = () => { toggles++; return true; };
            model.SetPreview(mount, mount ? (ushort)223 : (ushort)56, 70);
            model.BeginFrame(100);
            for (var slot = 6; slot < 11; slot++)
                check(model.ActivateSlot(slot, 70, play, exit, toggle) && plays == 0 && exits == 0 && toggles == 0,
                    $"{(mount ? "mount" : "accessory")} empty slot {slot + 1} is consumed without exiting or running an action");
            check(!model.ActivateSlot(11, 69, play, exit, toggle) && exits == 0,
                "stale twelfth-slot input cannot end the current trial");
            check(model.ActivateSlot(11, 70, play, exit, toggle) && exits == 1 && plays == 0 && toggles == 0,
                "the twelfth slot invokes only the shared exit callback");
            check(model.ActivateSlot(11, 70, play, exit, toggle) && exits == 1,
                "native and fallback routes cannot exit twice for the same input frame");
            check(!model.ActivateSlot(12, 70, play, exit, toggle)
                && !model.ActivateSlot(-1, 70, play, exit, toggle) && exits == 1,
                "inputs outside the twelve-slot row remain unclaimed");
            model.Clear();
            check(!model.ActivateSlot(11, 70, play, exit, toggle) && exits == 1,
                "a cleared trial releases the twelfth-slot input");
        }
    }
}
