using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    private unsafe void SetDevelopmentPhoto(bool enter)
    {
        if (disposed || !Framework.IsInFrameworkUpdateThread || !ClientState.IsLoggedIn
            || ClientState.IsGPosing == enter) return;
        var ui=FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
        if (ui==null) return;
        if (enter) ui->EnterGPose();
        else ui->ExitGPose();
    }
    // Development plugin only. Uses the same client API as native collection buttons,
    // on Dalamud's command/framework thread. It never bypasses unlocks or game status.
    private unsafe void ExecuteDevelopmentNative(string[] parts)
    {
        if (disposed || !Framework.IsInFrameworkUpdateThread || parts.Length != 3
            || !ushort.TryParse(parts[2], out var id) || !ClientState.IsLoggedIn) return;
        if (parts[1] == "agentsit" && id is 50 or 52)
        {
            // Acceptance probe for the genuine emote-menu entry. This void API
            // reports dispatch only; successful sitting must be observed separately.
            var agent = AgentEmote.Instance();
            if (agent == null) return;
            agent->ExecuteEmote(id);
            lastMessage = $"原生坐姿菜单验证 #{id}：已调用；以角色实际坐姿为准";
            return;
        }
        if (parts[1] == "builtin")
        {
            // Only these genuine game commands are admitted by the development probe.
            // Never substitute a public chat channel or desktop keyboard input.
            string? text = id switch { 1 => "/sit", 2 => "/groundsit", 3 => "/battlemode", _ => null };
            var ui = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
            if (text is null || ui == null) return;
            var message = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromString(text);
            try { ui->ProcessChatBoxEntry(message); }
            finally { message->Dtor(true); }
            return;
        }
        bool result;
        if(parts[1] == "petslot")
        {
            // The native dispatcher is reached only through a currently leased slot.
            // No unrelated normal, cross or genuine pet hotbar slot is callable here.
            if(id>6 || specialHotbarView?.NativeActive!=true)return;
            result=specialHotbarView.ExecuteNativeSlot(id);
        }
        else if (parts[1] == "emote")
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
