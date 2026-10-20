using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using InteropGenerator.Runtime;

// This process reads a disk PE into non-executable private memory. Resolver and
// HookVerifier are the actual installed managed implementations; no native game
// entry point, Hook factory/backend, process attachment, or installed data write.
internal static unsafe class Program
{
    private const string ExpectedClient = "958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9";
    private const string ExpectedSdk = "5A7A8EA71E84D7410DBAD8A5391D4759C2A38B1A55F4F3BB745B22B556A6F778";
    private const string ExpectedDalamud = "CE0C3C8004CAC1E9AA96D0DC749463F5F1A32E9B49988419BF320545BF942C6A";
    [return: MarshalAs(UnmanagedType.U1)] private delegate bool SdkTimelineU1(ActionTimelineSequencer* sequence, ushort id, void* parameter);
    [return: MarshalAs(UnmanagedType.U1)] private delegate bool SdkActionU1(TimelineContainer* timeline, ushort intro, ushort loop, void* parameter);
    [return: MarshalAs(UnmanagedType.U1)] private delegate bool InternalTimelineU1(ActionTimelineSequencer* sequence, ushort id, void* borrowedResolvedRow, void* borrowedParameter);
    [return: MarshalAs(UnmanagedType.U1)] private delegate bool InternalActionU1(ActionTimelineSequencer* sequence, ushort adjustedIntro, ushort adjustedLoop, void* borrowedParameter);
    private static int checks;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--sheet-data") return TimelineDataAudit.Run(args[1], args[2]);
            if (args.Length != 2) throw new ArgumentException("Usage: TimelineAudit <audited-client-executable> <output-json>");
            var bytes = File.ReadAllBytes(args[0]);
            Check(Hash(bytes) == ExpectedClient, "client hash matches full reviewed PE");
            Check(Hash(File.ReadAllBytes(typeof(ActionTimelineSequencer).Assembly.Location)) == ExpectedSdk, "installed SDK identity");
            Check(Hash(File.ReadAllBytes(typeof(Dalamud.Hooking.Hook<>).Assembly.Location)) == ExpectedDalamud, "installed verifier identity");
            using var pe = new PEReader(new MemoryStream(bytes));
            var size = pe.PEHeaders.PEHeader!.SizeOfImage;
            using var image = new NonExecutableImage(bytes, pe);
            Check(image.MemoryProtection == 0x04, "private mapped PE has PAGE_READWRITE without execute");
            var sdk = typeof(ActionTimelineSequencer).Assembly;
            var allAddressFields = sdk.GetTypes().Where(type => type.Name == "Addresses")
                .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
                .Where(field => field.FieldType == typeof(Address)).ToArray();
            var allAddressObjects = allAddressFields.Select(field => (Field: field, Address: (Address)field.GetValue(null)!)).ToArray();
            Check(allAddressObjects.All(entry => entry.Address.Value == 0), "fresh isolated process begins with unbound SDK addresses");
            FFXIVClientStructs.Interop.Generated.Addresses.Register();
            var resolver = Resolver.GetInstance;
            var unregistered = allAddressObjects.Where(entry => !resolver.Addresses.Contains(entry.Address)).ToArray();
            Check(unregistered.Length == 0, "generated registration covers every public SDK Addresses field");
            resolver.Setup(image.Base, size, 0, "offline-timeline-audit", null);
            resolver.Resolve();
            Check(ActionTimelineSequencer.Addresses.PlayTimeline.Value - image.Base == 0x606AB0, "actual SDK resolver finds reviewed timeline wrapper");
            Check(TimelineContainer.Addresses.PlayActionTimeline.Value - image.Base == 0x8BE410, "actual SDK resolver finds reviewed action wrapper");
            var verifier = typeof(Dalamud.Hooking.Hook<>).Assembly.GetType("Dalamud.Hooking.Internal.Verification.HookVerifier", true)!;
            verifier.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            var inventory = (IEnumerable)verifier.GetField("allToVerify", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var verifiedEntries = new List<object>();
            var knownAddresses = new HashSet<nint>();
            foreach (var item in inventory)
            {
                var itemType = item.GetType(); var address = (nint)itemType.GetProperty("Key")!.GetValue(item)!;
                knownAddresses.Add(address);
                foreach (var entry in (IEnumerable)itemType.GetProperty("Value")!.GetValue(item)!)
                {
                    var type = entry.GetType();
                    verifiedEntries.Add(new {
                        Name = type.GetProperty("Name")!.GetValue(entry),
                        Rva = address == 0 ? null : "0x" + (address - image.Base).ToString("X"),
                        Signature = type.GetProperty("Signature")!.GetValue(entry),
                        Delegate = ((Type?)type.GetProperty("TargetDelegateType")!.GetValue(entry))?.FullName,
                    });
                }
            }
            Check(!knownAddresses.Contains(image.Base + 0x606B10), "actual complete HookVerifier inventory has no internal resolved-timeline target");
            Check(!knownAddresses.Contains(image.Base + 0x606FD0), "actual complete HookVerifier inventory has no internal resolved-action target");
            var rejectedTimeline = Verify(verifier, typeof(SdkTimelineU1), ActionTimelineSequencer.Addresses.PlayTimeline.Value);
            var rejectedAction = Verify(verifier, typeof(SdkActionU1), TimelineContainer.Addresses.PlayActionTimeline.Value);
            Check(!rejectedTimeline.Accepted && rejectedTimeline.Errors.Length > 0, "actual verifier rejects SDK wrapper Boolean versus Void");
            Check(!rejectedAction.Accepted && rejectedAction.Errors.Length > 0, "actual verifier rejects SDK action wrapper Boolean versus Void");
            var acceptedTimeline = Verify(verifier, typeof(ActionTimelineSequencer.Delegates.PlayTimeline), ActionTimelineSequencer.Addresses.PlayTimeline.Value);
            var acceptedAction = Verify(verifier, typeof(TimelineContainer.Delegates.PlayActionTimeline), TimelineContainer.Addresses.PlayActionTimeline.Value);
            Check(acceptedTimeline.Accepted && acceptedTimeline.Errors.Length == 0, "actual verifier accepts exact SDK timeline delegate");
            Check(acceptedAction.Accepted && acceptedAction.Errors.Length == 0, "actual verifier accepts exact SDK action delegate");
            var internalTimeline = Verify(verifier, typeof(InternalTimelineU1), image.Base + 0x606B10);
            var internalAction = Verify(verifier, typeof(InternalActionU1), image.Base + 0x606FD0);
            Check(internalTimeline.Accepted && internalTimeline.Errors.Length == 0, "actual verifier leaves independently audited unknown internal timeline address permitted");
            Check(internalAction.Accepted && internalAction.Errors.Length == 0, "actual verifier leaves independently audited unknown internal action address permitted");
            var entries = allAddressObjects.Select(entry => new {
                Field = entry.Field.DeclaringType!.FullName + "." + entry.Field.Name,
                entry.Address.Name, entry.Address.String, entry.Address.RelativeFollowOffsets,
                Rva = entry.Address.Value == 0 ? null : "0x" + (entry.Address.Value - image.Base).ToString("X"),
            }).ToArray();
            var document = new {
                Schema = 1, Checks = checks, ClientSha256 = ExpectedClient, SdkSha256 = ExpectedSdk, DalamudSha256 = ExpectedDalamud,
                RegisteredAddresses = resolver.Addresses.Count, AddressFields = allAddressObjects.Length,
                UnresolvedAddresses = resolver.Addresses.Count(address => address.Value == 0),
                NonExecutableProtection = "PAGE_READWRITE", OfficialVerifierEntries = verifiedEntries.Count,
                WrapperTimeline = rejectedTimeline, WrapperAction = rejectedAction, SdkTimeline = acceptedTimeline, SdkAction = acceptedAction,
                InternalTimeline = internalTimeline, InternalAction = internalAction,
                Limitation = "Unknown-address verifier success is not an independent ABI proof or authorization to install; production pre-factory gate stays closed until native contract/ownership and accepted-action identity audits are complete.",
                Addresses = entries, VerifierEntries = verifiedEntries,
            };
            File.WriteAllText(args[1], JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"TimelineAudit: {checks} checks passed; {entries.Length} address fields; {verifiedEntries.Count} actual official verifier entries; {document.UnresolvedAddresses} unresolved signatures.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static Verification Verify(Type verifier, Type delegateType, nint address)
    {
        object?[] parameters = [address, Assembly.GetExecutingAssembly(), null];
        var accepted = (bool)verifier.GetMethod("TryVerify", BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(delegateType).Invoke(null, parameters)!;
        var errors = ((IEnumerable)parameters[2]!).Cast<Exception>().Select(error => error.Message).ToArray();
        return new Verification(accepted, errors);
    }
    private sealed record Verification(bool Accepted, string[] Errors);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Check(bool accepted, string message) { checks++; if (!accepted) throw new InvalidOperationException("FAIL: " + message); }

    private sealed class NonExecutableImage : IDisposable
    {
        public nint Base { get; }
        public uint MemoryProtection { get; }
        public NonExecutableImage(byte[] bytes, PEReader pe)
        {
            Base = VirtualAlloc(0, (nuint)pe.PEHeaders.PEHeader!.SizeOfImage, 0x3000, 0x04);
            if (Base == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                Marshal.Copy(bytes, 0, Base, pe.PEHeaders.PEHeader.SizeOfHeaders);
                foreach (var section in pe.PEHeaders.SectionHeaders)
                    if (section.SizeOfRawData > 0) Marshal.Copy(bytes, section.PointerToRawData, Base + section.VirtualAddress, section.SizeOfRawData);
                if (VirtualQuery(Base, out var information, (nuint)sizeof(MemoryInformation)) == 0)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                MemoryProtection = information.Protect;
            }
            catch { VirtualFree(Base, 0, 0x8000); throw; }
        }
        public void Dispose() => VirtualFree(Base, 0, 0x8000);
        [StructLayout(LayoutKind.Sequential)] private struct MemoryInformation
        { public nint BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId; public nuint RegionSize; public uint State, Protect, Type; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFree(nint address, nuint size, uint freeType);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQuery(nint address, out MemoryInformation information, nuint length);
    }
}
