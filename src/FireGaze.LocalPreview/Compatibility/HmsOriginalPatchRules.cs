using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace FireGaze.LocalPreview.Compatibility;

/// <summary>Validated transformations of original HMS IL. No HMS implementation is copied.</summary>
public static class HmsOriginalPatchRules
{
    private static bool Address(CodeInstruction instruction) => instruction.opcode == OpCodes.Callvirt
        && instruction.operand is MethodInfo { Name: "get_Address", DeclaringType.FullName: "Dalamud.Game.ClientState.Objects.Types.IGameObject" };

    public static IEnumerable<CodeInstruction> ReplaceCaptureAddresses(IEnumerable<CodeInstruction> instructions, MethodInfo bridge, bool detector)
    {
        var result = instructions.Select(i => new CodeInstruction(i)).ToList();
        var addresses = result.Where(Address).ToList();
        if (addresses.Count != (detector ? 1 : 2)) throw new InvalidOperationException("HMS capture address pattern changed");
        // The second capture address also writes the live facing-camera flag. Preserve it.
        addresses[0].opcode = OpCodes.Call;
        addresses[0].operand = bridge;
        return result;
    }

    public static IEnumerable<CodeInstruction> MarkEmoteDispatches(IEnumerable<CodeInstruction> instructions, MethodInfo marker)
    {
        var source = instructions.Select(i => new CodeInstruction(i)).ToList();
        var timeline = source.Where(i => IsCall(i,"ActionTimelineSequencer","PlayTimeline")).ToList();
        var variant = source.Where(i => IsCall(i,"TimelineContainer","PlayActionTimeline")).ToList();
        if (timeline.Count != 4 || variant.Count != 1) throw new InvalidOperationException("HMS emote dispatch pattern changed");
        // First two PlayTimeline calls only draw the weapon; the last two dispatch the requested emote.
        var selected = new HashSet<CodeInstruction> { timeline[2], timeline[3], variant[0] };
        var result = new List<CodeInstruction>();
        foreach(var instruction in source)
        {
            result.Add(instruction);
            if (selected.Contains(instruction)) result.Add(new CodeInstruction(OpCodes.Call,marker));
        }
        return result;
    }

    public static IEnumerable<CodeInstruction> MarkEmoteStop(IEnumerable<CodeInstruction> instructions, MethodInfo marker)
    {
        var source = instructions.Select(i => new CodeInstruction(i)).ToList();
        if (source.Count(i=>IsCall(i,"Character","SetMode")) != 1) throw new InvalidOperationException("HMS emote stop pattern changed");
        var result = new List<CodeInstruction>();
        foreach(var instruction in source)
        {
            result.Add(instruction);
            if (IsCall(instruction,"Character","SetMode")) result.Add(new CodeInstruction(OpCodes.Call,marker));
        }
        return result;
    }

    public static IEnumerable<CodeInstruction> MarkPresentationDispatches(IEnumerable<CodeInstruction> instructions, MethodInfo marker, string methodName)
    {
        var native = methodName switch { "MountSelf" => "CreateAndSetupMount", "SummonMinionSelf" => "SetupCompanion", "SummonOrnamentSelf" => "SetupOrnament", _ => throw new InvalidOperationException("Unknown HMS presentation entry") };
        var owner = methodName switch { "MountSelf" => "MountContainer", "SummonMinionSelf" => "CompanionContainer", _ => "OrnamentContainer" };
        var source=instructions.Select(i=>new CodeInstruction(i)).ToList();
        bool Selected(CodeInstruction i) => IsCall(i,owner,native);
        if(source.Count(Selected)!=2) throw new InvalidOperationException("HMS self presentation dispatch pattern changed");
        var result = new List<CodeInstruction>();
        foreach(var instruction in source) {
            result.Add(instruction);
            if(Selected(instruction)) result.Add(new CodeInstruction(OpCodes.Call,marker));
        }
        return result;
    }

    public static IEnumerable<CodeInstruction> ReplaceSemanticReads(IEnumerable<CodeInstruction> instructions,MethodInfo bridge,string methodName)
    {
        string getter=methodName switch { "OnCommand"=>"CurrentMountId","DoMinion"=>"CurrentMinionId","DoAccessory"=>"CurrentOrnamentId",_=>throw new InvalidOperationException("Unknown HMS semantic caller") };
        var source=instructions.Select(i=>new CodeInstruction(i)).ToList();
        var matches=source.Where(i=>i.opcode==OpCodes.Call && i.operand is MethodInfo m && m.Name==getter && m.DeclaringType?.FullName=="HMSync.HMSyncPlugin").ToList();
        if(matches.Count!=1) throw new InvalidOperationException("HMS toggle comparison pattern changed");
        matches[0].opcode=OpCodes.Call;
        matches[0].operand=bridge;
        return source;
    }

    private static bool IsCall(CodeInstruction instruction,string type,string name) => instruction.opcode == OpCodes.Call
        && instruction.operand is MethodInfo method && method.Name==name
        && method.DeclaringType?.FullName == "FFXIVClientStructs.FFXIV.Client.Game." + (type == "ActionTimelineSequencer" ? "Control." : "Character.")+type;
}
