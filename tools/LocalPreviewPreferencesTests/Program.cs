using FireGaze;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Interaction;

var passed = 0;
var failed = 0;
void Check(bool condition, string name)
{
    Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
    if (condition) passed++; else failed++;
}

var fresh = new Configuration();
Check(!fresh.LocalPreviewEnabled, "fresh installs cannot enable trial menus before consent");
var type = typeof(Configuration).Assembly.GetType("FireGaze.LocalPreview.LocalPreviewPreferences");
Check(type is not null, "preview preference flow owns confirmation and chat consent");
if (type is not null)
{
    object Preferences(Configuration config) => Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        null, [config], null)!;
    object? Call(object preferences, string method, params object[] args) => type.GetMethod(method)!.Invoke(preferences, args);
    bool Read(object preferences, string property) => (bool)type.GetProperty(property)!.GetValue(preferences)!;
    var freshPrefs = Preferences(fresh);
    Check(!Read(freshPrefs, "CanLinkEmoteMessages"), "fresh installs leave emote chat sentences unchanged");
    Check((bool)Call(freshPrefs, "Migrate")!, "first load records the opt-in settings revision");
    Call(freshPrefs, "RequestEnable");
    Check(Read(freshPrefs, "ConfirmationPending") && !fresh.LocalPreviewEnabled,
        "asking to enable leaves menus disabled while confirmation is open");
    Check(!(bool)Call(freshPrefs, "CompleteEnable", false)! && !fresh.LocalPreviewEnabled && !Read(freshPrefs, "ConfirmationPending"),
        "cancel closes confirmation without enabling preview");
    Check(!(bool)Call(freshPrefs, "CompleteEnable", true)! && !fresh.LocalPreviewEnabled,
        "a stale confirm callback cannot enable after cancellation");
    Call(freshPrefs, "RequestEnable");
    Check((bool)Call(freshPrefs, "CompleteEnable", true)! && fresh.LocalPreviewEnabled,
        "accepting a pending confirmation enables preview");
    Check(!Read(freshPrefs, "CanLinkEmoteMessages"), "enabling menus alone cannot change chat sentences");
    Call(freshPrefs, "SetChatLinksEnabled", true);
    Check(Read(freshPrefs, "CanLinkEmoteMessages"), "chat links become available only after both opt-ins");
    fresh.LocalPreviewEnabled = false;
    Check(!Read(freshPrefs, "CanLinkEmoteMessages"), "closing the master switch revokes new and retained chat-link execution");
    fresh.LocalPreviewEnabled = true;
    Call(freshPrefs, "SetChatLinksEnabled", false);
    Check(!Read(freshPrefs, "CanLinkEmoteMessages"), "closing only chat preview revokes links while menus remain enabled");
    Check(fresh.LocalPreviewEnabled, "chat opt-out keeps ordinary item and character previews available");

    var old = JsonConvert.DeserializeObject<Configuration>("{\"LocalPreviewEnabled\":true,\"LocalPreviewMountEnabled\":false,\"LocalPreviewEmoteEnabled\":false}")!;
    var oldPrefs = Preferences(old);
    Check((bool)Call(oldPrefs, "Migrate")! && !old.LocalPreviewEnabled && !Read(oldPrefs, "CanLinkEmoteMessages"),
        "older default-on trial builds require fresh opt-in once");
    Check(old.LocalPreviewMountEnabled && old.LocalPreviewMinionEnabled && old.LocalPreviewAccessoryEnabled && old.LocalPreviewEmoteEnabled && old.LocalPreviewItemMenusEnabled,
        "legacy individual opt-outs cannot leave hidden product categories unavailable");
    Call(oldPrefs, "RequestEnable");
    Call(oldPrefs, "CompleteEnable", true);
    Call(oldPrefs, "SetChatLinksEnabled", true);
    var saved = JsonConvert.SerializeObject(old);
    var savedObject = JObject.Parse(saved);
    Check(new[] { "LocalPreviewMountEnabled", "LocalPreviewMinionEnabled", "LocalPreviewAccessoryEnabled", "LocalPreviewEmoteEnabled", "LocalPreviewItemMenusEnabled" }
        .All(key => !savedObject.ContainsKey(key)), "saved product configuration exposes no separate collection preferences");
    var restored = JsonConvert.DeserializeObject<Configuration>(saved)!;
    var restoredPrefs = Preferences(restored);
    Check(!(bool)Call(restoredPrefs, "Migrate")! && restored.LocalPreviewEnabled && Read(restoredPrefs, "CanLinkEmoteMessages"),
        "ordinary reload retains explicit user consent instead of resetting it repeatedly");

    var chat = DispatchProxy.Create<IChatGui, PreferenceChatProxy>();
    var host = (PreferenceChatProxy)(object)chat;
    host.Registration = SeString.Parse(NativeEmoteLinkBuilder.Wrap(Encoding.UTF8.GetBytes("范围"), 21, "Settings-Test"))
        .Payloads.OfType<DalamudLinkPayload>().Single();
    var trials = new List<ushort>();
    using var links = new EmoteChatLink(chat, () => Read(restoredPrefs, "CanLinkEmoteMessages"), trials.Add);
    var sentence = Encoding.UTF8.GetBytes("光之战士表演起了默剧。");
    var earlierLink = SeString.Parse(links.Wrap(sentence, 21));
    host.Handler!(NativeEmoteLinkBuilder.CommandId, earlierLink);
    Check(trials.SequenceEqual(new ushort[] { 21 }), "both opt-ins let the real registered chat callback start its recorded emote");
    Call(restoredPrefs, "SetChatLinksEnabled", false);
    Check(links.Wrap(sentence, 21).Length == 0, "chat opt-out leaves the native formatter without replacement bytes");
    host.Handler(NativeEmoteLinkBuilder.CommandId, earlierLink);
    Check(trials.Count == 1, "chat opt-out blocks a retained registered callback for an existing sentence");
    Call(restoredPrefs, "SetChatLinksEnabled", true);
    restored.LocalPreviewEnabled = false;
    Check(links.Wrap(sentence, 21).Length == 0, "master opt-out leaves subsequent native sentences unchanged");
    host.Handler(NativeEmoteLinkBuilder.CommandId, earlierLink);
    Check(trials.Count == 1, "master opt-out blocks old clickable sentences even with chat preference retained");
}
Console.WriteLine($"LocalPreviewPreferencesTests: {passed}/{passed + failed} passed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

public class PreferenceChatProxy : DispatchProxy
{
    public DalamudLinkPayload Registration = null!;
    public Action<uint, SeString>? Handler;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "AddChatLinkHandler")
        {
            Handler = (Action<uint, SeString>)args![1]!;
            return Registration;
        }
        if (method.Name == "RemoveChatLinkHandler") return null;
        throw new NotSupportedException(method.Name);
    }
}
