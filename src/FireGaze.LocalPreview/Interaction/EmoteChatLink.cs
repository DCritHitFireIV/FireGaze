using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace FireGaze.LocalPreview.Interaction;

/// <summary>A plugin-scoped chat link owns the whole sentence and its immutable emote ID.</summary>
public sealed class EmoteChatLink : IDisposable
{
    private readonly IChatGui chat;
    private readonly Func<bool> allowed;
    private readonly Action<ushort> requestPreview;
    private readonly string pluginName;
    private bool disposed;

    public int ClicksObserved { get; private set; }
    public ushort LastClickedEmote { get; private set; }

    public EmoteChatLink(IChatGui chat, Func<bool> allowed, Action<ushort> requestPreview)
    {
        this.chat = chat;
        this.allowed = allowed;
        this.requestPreview = requestPreview;
        pluginName = chat.AddChatLinkHandler(NativeEmoteLinkBuilder.CommandId, OnClicked).Plugin;
    }

    public byte[] Wrap(ReadOnlySpan<byte> message, ushort emoteId)
        => disposed || !allowed() ? [] : NativeEmoteLinkBuilder.Wrap(message, emoteId, pluginName);

    public bool TryRead(ReadOnlySpan<byte> message, out ushort emoteId)
        => NativeEmoteLinkBuilder.TryReadEmoteId(message, out emoteId, pluginName);

    private void OnClicked(uint commandId, SeString message)
    {
        if (disposed || !allowed() || commandId != NativeEmoteLinkBuilder.CommandId
            || !TryRead(message.Encode(), out var emoteId)) return;
        ClicksObserved++;
        LastClickedEmote = emoteId;
        requestPreview(emoteId);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        chat.RemoveChatLinkHandler(NativeEmoteLinkBuilder.CommandId);
    }
}
