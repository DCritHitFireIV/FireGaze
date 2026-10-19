using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using InteropGenerator.Runtime;

// Metadata only: no interop initialization, native calls, hooks, or game process.
static class TimelineAbiMetadata
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    static object Describe(string field, Type sdkDelegate, Address address)
    {
        var production = typeof(ActorPreviewRuntime).GetField(field, Private)!.FieldType.GetGenericArguments()[0];
        var actual = production.GetMethod("Invoke")!;
        var sdk = sdkDelegate.GetMethod("Invoke")!;
        var argumentsMatch = actual.GetParameters().Select(p => p.ParameterType)
            .SequenceEqual(sdk.GetParameters().Select(p => p.ParameterType));
        var marshalAs = actual.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()?.Value;
        if (actual.ReturnType != typeof(bool) || marshalAs != UnmanagedType.U1 || !argumentsMatch)
            throw new InvalidOperationException($"{address.Name}: compiled native delegate must preserve the audited U1 return and exact SDK arguments");
        return new {
            Name = address.Name, Signature = address.String, RelativeFollowOffsets = address.RelativeFollowOffsets,
            SdkReturn = sdk.ReturnType.Name, ProductionReturn = actual.ReturnType.Name,
            ProductionMarshalAs = marshalAs.ToString(), ArgumentsMatch = argumentsMatch,
        };
    }

    static object[] Functions() => [
        Describe("timelinePlay", typeof(ActionTimelineSequencer.Delegates.PlayTimeline), ActionTimelineSequencer.Addresses.PlayTimeline),
        Describe("actionTimelinePlay", typeof(TimelineContainer.Delegates.PlayActionTimeline), TimelineContainer.Addresses.PlayActionTimeline),
    ];

    public static void VerifyProduction() => _ = Functions();

    public static int Write()
    {
        var sdkPath = typeof(ActionTimelineSequencer).Assembly.Location;
        var version = FileVersionInfo.GetVersionInfo(sdkPath);
        var document = new {
            Schema = 1, ClientStructsSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sdkPath))),
            ClientStructsVersion = version.FileVersion, ClientStructsProductVersion = version.ProductVersion,
            Functions = Functions(),
        };
        Console.WriteLine(JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
