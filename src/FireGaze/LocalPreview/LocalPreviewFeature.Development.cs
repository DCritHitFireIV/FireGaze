using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    // Development plugin only. Uses the same client API as native collection buttons,
    // on Dalamud's command/framework thread. It never bypasses unlocks or game status.
    private unsafe void ExecuteDevelopmentNative(string[] parts)
    {
        if (parts.Length != 3 || !ushort.TryParse(parts[2], out var id) || !ClientState.IsLoggedIn) return;
        bool result;
        if (parts[1] == "emote")
        {
            var manager = EmoteManager.Instance();
            if (manager == null) return;
            result = manager->ExecuteEmote(id);
        }
        else
        {
            var type = parts[1] switch
            {
                "mount" => ActionType.Mount, "minion" => ActionType.Companion,
                "accessory" => ActionType.Ornament, "general" => ActionType.GeneralAction, _ => ActionType.None,
            };
            var manager = ActionManager.Instance();
            if (type == ActionType.None || manager == null) return;
            result = manager->UseAction(type, id);
        }
        lastMessage = $"原生验证 {parts[1]} #{id}：客户端返回 {result}";
        Log.Information("[LocalPreview] " + lastMessage);
    }
}
