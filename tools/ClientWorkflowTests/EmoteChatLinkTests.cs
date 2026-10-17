using System.Reflection;
using System.Text;
using System.Text.Json;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FireGaze;

internal static class EmoteChatLinkTests
{
    private const uint HandlerId = 0x46470001;
    private const string Protocol = "FireGaze.LocalPreview.Emote.v1";
    private const string ScopedPlugin = "FireGaze-TestScope";
    private delegate byte[] WrapMessage(ReadOnlySpan<byte> message, ushort emoteId);

    public static void Run(Action<bool, string> check)
    {
        var type = typeof(Configuration).Assembly.GetType("FireGaze.LocalPreview.Interaction.EmoteChatLink");
        check(type is not null, "emote sentence links use a managed plugin-scoped chat-link handler");
        var chat = DispatchProxy.Create<IChatGui, EmoteChatGuiProxy>();
        var host = (EmoteChatGuiProxy)(object)chat;
        host.RegistrationPayload = Link(ScopedPlugin, HandlerId, 0, 0, "").Payloads.OfType<DalamudLinkPayload>().Single();
        var allowed = true;
        var requested = new List<ushort>();
        var instance = Activator.CreateInstance(type!, [chat, (Func<bool>)(() => allowed), (Action<ushort>)requested.Add])!;
        try
        {
            check(host.Registrations == 1 && host.RegisteredId == HandlerId && host.Handler is not null,
                "emote chat links register one fixed plugin-scoped handler");
            var wrap = (WrapMessage)type!.GetMethod("Wrap", [typeof(ReadOnlySpan<byte>), typeof(ushort)])!
                .CreateDelegate(typeof(WrapMessage), instance);
            var original = Encoding.UTF8.GetBytes("大吹打做出了抚摸的动作。");
            var first = SeString.Parse(wrap(original, 21));
            var firstLink = first.Payloads.OfType<DalamudLinkPayload>().Single();
            check(firstLink.Plugin == ScopedPlugin && firstLink.CommandId == HandlerId && firstLink.Extra1 == 21 &&
                  firstLink.Extra2 == 0 && firstLink.ExtraString == Protocol,
                "wrapped sentence carries its immutable action id and the registered plugin scope");
            check(first.TextValue == "大吹打做出了抚摸的动作。", "managed linking preserves the entire original sentence");
            var second = SeString.Parse(wrap(Encoding.UTF8.GetBytes("大吹打做出了另一个动作。"), 42));
            var handler = host.Handler!;
            handler(HandlerId, first);
            check(requested.SequenceEqual(new ushort[] { 21 }), "clicking the first sentence requests its own action exactly once");
            handler(HandlerId, second);
            handler(HandlerId, first);
            check(requested.SequenceEqual(new ushort[] { 21, 42, 21 }),
                "new messages cannot change earlier links and an earlier sentence remains reusable");
            check(host.Registrations == 1, "wrapping multiple sentences does not register per-message handlers");

            allowed = false;
            handler(HandlerId, first);
            check(requested.SequenceEqual(new ushort[] { 21, 42, 21 }), "disabling the category blocks clicks on existing links");
            allowed = true;
            handler(HandlerId, first);
            check(requested.SequenceEqual(new ushort[] { 21, 42, 21, 21 }), "reenabling the category permits an existing link again");

            foreach (var (callbackId, payload, name) in new (uint, SeString, string)[]
            {
                (HandlerId, Link("AnotherPlugin", HandlerId, 21), "another plugin"),
                (HandlerId, Link(ScopedPlugin, HandlerId + 1, 21), "another payload handler"),
                (HandlerId + 1, first, "another callback handler"),
                (HandlerId, Link(ScopedPlugin, HandlerId, 0), "zero action id"),
                (HandlerId, Link(ScopedPlugin, HandlerId, 65536), "out-of-range action id"),
                (HandlerId, Link(ScopedPlugin, HandlerId, -1), "negative action id"),
                (HandlerId, Link(ScopedPlugin, HandlerId, 21, extra: "AnotherProtocol"), "another protocol marker"),
                (HandlerId, Link(ScopedPlugin, HandlerId, 21, jsonOverride: "not-json"), "malformed composite payload"),
                (HandlerId, SeString.Parse(Encoding.UTF8.GetBytes("没有链接的普通文字")), "text without a payload"),
            })
            {
                handler(callbackId, payload);
                check(requested.SequenceEqual(new ushort[] { 21, 42, 21, 21 }), name + " cannot request a trial");
            }

            ((IDisposable)instance).Dispose();
            ((IDisposable)instance).Dispose();
            check(host.Removals == 1 && host.RemovedId == HandlerId,
                "disposing twice removes only the owned handler exactly once");
            handler(HandlerId, first);
            check(requested.SequenceEqual(new ushort[] { 21, 42, 21, 21 }),
                "a retained old callback cannot request a trial after disposal");
        }
        finally { ((IDisposable)instance).Dispose(); }
    }

    // Construct the real type-205 wire payload independently of the production
    // builder. SeString.Parse supplies DalamudLinkPayload instances to the real
    // registered callback, including malformed but parseable protocol input.
    private static SeString Link(string plugin, uint command, int id, int extra2 = 0,
        string extra = Protocol, string? jsonOverride = null)
    {
        var composite = Encoding.UTF8.GetBytes(jsonOverride ?? JsonSerializer.Serialize(new[] { plugin, extra }));
        var body = new List<byte>();
        body.AddRange(UIntExpression(205));
        body.AddRange(UIntExpression(command));
        body.AddRange(UIntExpression(unchecked((uint)id)));
        body.AddRange(UIntExpression(unchecked((uint)extra2)));
        body.Add(0xFF); // SeString string expression.
        body.AddRange(UIntExpression((uint)composite.Length));
        body.AddRange(composite);
        var wire = new List<byte> { 2, 0x27 };
        wire.AddRange(UIntExpression((uint)body.Count));
        wire.AddRange(body);
        wire.Add(3);
        wire.AddRange(Encoding.UTF8.GetBytes("动作提示"));
        wire.AddRange([2, 0x27, 2, 0xCF, 3]); // Balanced type-206 link terminator.
        return SeString.Parse(wire.ToArray());
    }

    private static byte[] UIntExpression(uint value)
    {
        if (value < 0xCF) return [(byte)(value + 1)];
        byte mask = 0xF0;
        var bytes = new List<byte>();
        for (var shift = 3; shift >= 0; shift--)
        {
            var part = (byte)(value >> (shift * 8));
            if (part == 0) continue;
            mask |= (byte)(1 << shift);
            bytes.Add(part);
        }
        bytes.Insert(0, (byte)(mask - 1));
        return bytes.ToArray();
    }
}

public class EmoteChatGuiProxy : DispatchProxy
{
    public DalamudLinkPayload RegistrationPayload = null!;
    public Action<uint, SeString>? Handler;
    public uint RegisteredId, RemovedId;
    public int Registrations, Removals;

    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        switch (method!.Name)
        {
            case "AddChatLinkHandler":
                RegisteredId = (uint)arguments![0]!;
                Handler = (Action<uint, SeString>)arguments[1]!;
                Registrations++;
                return RegistrationPayload;
            case "RemoveChatLinkHandler" when arguments is { Length: 1 }:
                RemovedId = (uint)arguments[0]!;
                Removals++;
                return null;
            default: throw new NotSupportedException(method.Name);
        }
    }
}
