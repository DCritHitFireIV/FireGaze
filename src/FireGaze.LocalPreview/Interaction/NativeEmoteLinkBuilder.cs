using Lumina.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace FireGaze.LocalPreview.Interaction;

public static class NativeEmoteLinkBuilder
{
    public static bool TryReadEmoteId(ReadOnlySpan<byte> message, out ushort emoteId)
    {
        emoteId = 0;
        try
        {
            foreach (var payload in new ReadOnlySeStringSpan(message))
            {
                if (payload.Type != ReadOnlySePayloadType.Macro || payload.MacroCode != MacroCode.Link) continue;
                if (payload.TryGetExpression(out var type, out var arg)
                    && type.TryGetUInt(out var linkType) && linkType == (uint)LinkMacroPayloadType.Emote
                    && arg.TryGetUInt(out var id) && id is > 0 and <= ushort.MaxValue)
                {
                    emoteId = (ushort)id;
                    return true;
                }
                return false;
            }
        }
        catch (Exception) { /* malformed or truncated chat payload is not an actionable link */ }
        return false;
    }

    public static byte[] Wrap(ReadOnlySpan<byte> message, ushort emoteId)
    {
        if (emoteId == 0 || message.IsEmpty) return [];
        var builder = new SeStringBuilder().PushLink(LinkMacroPayloadType.Emote, emoteId, 0, 0);
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
