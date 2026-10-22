using Lumina.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using System.Text.Json;

namespace FireGaze.LocalPreview.Interaction;

public static class NativeEmoteLinkBuilder
{
    public const uint CommandId = 0x46470001;
    public const string Marker = "FireGaze.LocalPreview.Emote.v1";

    public static bool TryReadEmoteId(ReadOnlySpan<byte> message, out ushort emoteId, string pluginName = "FireGaze")
    {
        emoteId = 0;
        try
        {
            ushort candidate = 0;
            foreach (var payload in new ReadOnlySeStringSpan(message))
            {
                if (payload.Type != ReadOnlySePayloadType.Macro || payload.MacroCode != MacroCode.Link)
                {
                    if (candidate == 0) return false;
                    continue;
                }
                if (candidate != 0)
                {
                    if (!payload.TryGetExpression(out var end) || !end.TryGetUInt(out var terminator) || terminator != 206)
                        return false;
                    emoteId = candidate;
                    return true;
                }
                if (!payload.TryGetExpression(out var type, out var command, out var arg, out var reserved, out var route)
                    || !type.TryGetUInt(out var linkType) || linkType != 205
                    || !command.TryGetUInt(out var commandId) || commandId != CommandId
                    || !arg.TryGetUInt(out var id) || id is 0 or > ushort.MaxValue
                    || !reserved.TryGetUInt(out var unused) || unused != 0 || !route.TryGetString(out var metadata)) return false;
                var identity = JsonSerializer.Deserialize<string[]>(metadata.ExtractText());
                if (identity is not { Length: 2 } || identity[0] != pluginName || identity[1] != Marker) return false;
                candidate = (ushort)id;
            }
        }
        catch (Exception) { /* malformed or truncated chat payload is not an actionable link */ }
        return false;
    }

    public static byte[] Wrap(ReadOnlySpan<byte> message, ushort emoteId, string pluginName = "FireGaze")
    {
        if (emoteId == 0 || message.IsEmpty || string.IsNullOrWhiteSpace(pluginName)) return [];
        var builder = new SeStringBuilder().PushLink((LinkMacroPayloadType)205, CommandId, emoteId, 0,
            JsonSerializer.Serialize(new[] { pluginName, Marker }));
        foreach (var payload in new ReadOnlySeStringSpan(message))
        {
            // A native message can already contain player links. Strip their delimiters while
            // retaining all text, colors and icons so the complete sentence has one link owner.
            if (payload.Type == ReadOnlySePayloadType.Macro && payload.MacroCode == MacroCode.Link) continue;
            builder.Append(payload);
        }
        return builder.PopLink().ToArray();
    }
}
