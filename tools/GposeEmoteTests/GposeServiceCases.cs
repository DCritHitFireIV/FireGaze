using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Runtime;
using Lumina.Excel.Sheets;
using NativePlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

unsafe static class GposeServiceCases
{
    public static void Run(Lumina.GameData game, Action<bool, string> check)
    {
        var player = (NativePlayerState*)NativeMemory.AllocZeroed((nuint)sizeof(NativePlayerState));
        nint previous = NativePlayerState.Addresses.Instance.Value;
        bool inFramework = true, loggedIn = true, gposing = false, enabled = true;
        var framework = FixtureProxy.Make<IFramework>((method, _) => method.Name == "get_IsInFrameworkUpdateThread" ? inFramework : null);
        var client = FixtureProxy.Make<IClientState>((method, _) => method.Name switch
        {
            "get_IsLoggedIn" => loggedIn,
            "get_IsGPosing" => gposing,
            _ => null,
        });
        var data = new GameDataFixture(game);
        var objects = FixtureProxy.Make<IObjectTable>((_, _) => null);
        var interop = FixtureProxy.Make<IGameInteropProvider>((_, _) => null);
        var log = FixtureProxy.Make<IPluginLog>((_, _) => null);
        GposeEmoteService? service = null;
        try
        {
            NativePlayerState.Addresses.Instance.Value = (nint)player;
            player->ContentId = 100;
            service = new(framework, client, objects, data, interop, log, () => { }, () => enabled);
            service.RecordPreview(21, 3);
            check(service.CurrentEmoteId == 21, "the service remembers a real started preview using current native ContentId");
            service.RecordNative(50);
            service.RecordNative(52);
            service.RecordNative(96);
            check(service.CurrentEmoteId == 0, "the service clears old ordinary memory for CN chair ground and hidden cpose sitting resets");
            service.RecordNative(157);
            check(service.CurrentEmoteId == 157, "sit-ups remain a replayable emote despite sitting text in the name");
            service.RecordPreview(306);
            check(service.CurrentEmoteId == 306, "the green-tea seated emote is remembered as a normal action");
            service.RecordNative(ushort.MaxValue);
            service.RecordNative(0);
            check(service.CurrentEmoteId == 306, "missing-sheet and idle emote observations leave the latest known action intact");
            inFramework = false;
            service.RecordPreview(1);
            check(service.CurrentEmoteId == 306, "a worker-thread preview notification cannot mutate GPose action memory");
            inFramework = true; gposing = true;
            service.RecordNative(1);
            check(service.CurrentEmoteId == 306, "local GPose replay and posing-tool changes cannot rewrite world action memory");
            gposing = false; enabled = false;
            service.RecordPreview(1);
            check(service.CurrentEmoteId == 306, "a disabled feature ignores preview notifications");
            enabled = true; loggedIn = false;
            service.RecordNative(1);
            check(service.CurrentEmoteId == 306, "logged-out callbacks cannot inject a new local action");
            loggedIn = true; player->ContentId = 0;
            service.RecordPreview(1);
            check(service.CurrentEmoteId == 306, "a transient zero ContentId cannot attribute a preview to an unknown character");
            player->ContentId = 200;
            service.RecordNative(1);
            check(service.CurrentEmoteId == 1, "a new character's accepted action replaces the previous session safely");
            service.Dispose();
            service.RecordPreview(21);
            check(service.CurrentEmoteId == 0, "dispose clears memory and rejects later callbacks");
        }
        finally
        {
            service?.Dispose();
            NativePlayerState.Addresses.Instance.Value = previous;
            NativeMemory.Free(player);
        }
    }
}

public class FixtureProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    public static T Make<T>(Func<MethodInfo, object?[]?, object?> callback) where T : class
    {
        var value = Create<T, FixtureProxy>();
        ((FixtureProxy)(object)value).Handler = callback;
        return value;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}

sealed class GameDataFixture(Lumina.GameData game) : IDataManager
{
    public Dalamud.Game.ClientLanguage Language => Dalamud.Game.ClientLanguage.ChineseSimplified;
    public Lumina.GameData GameData => game;
    public Lumina.Excel.ExcelModule Excel => game.Excel;
    public bool HasModifiedGameDataFiles => false;
    public Lumina.Excel.ExcelSheet<T> GetExcelSheet<T>(Dalamud.Game.ClientLanguage? language = null, string? name = null)
        where T : struct, Lumina.Excel.IExcelRow<T> => game.GetExcelSheet<T>(name: name)!;
    public Lumina.Excel.SubrowExcelSheet<T> GetSubrowExcelSheet<T>(Dalamud.Game.ClientLanguage? language = null, string? name = null)
        where T : struct, Lumina.Excel.IExcelSubrow<T> => throw new NotSupportedException();
    public Lumina.Data.FileResource? GetFile(string path) => throw new NotSupportedException();
    public T? GetFile<T>(string path) where T : Lumina.Data.FileResource => throw new NotSupportedException();
    public Task<T> GetFileAsync<T>(string path, CancellationToken token) where T : Lumina.Data.FileResource => throw new NotSupportedException();
    public bool FileExists(string path) => throw new NotSupportedException();
}
