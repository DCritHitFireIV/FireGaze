using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using InteropGenerator.Runtime;

internal static unsafe partial class Program
{
    private static int checks;
    private static string bootFile = "";
    public static int Main(string[] args)
    {
        try
        {
            var option = Array.IndexOf(args, "--game-exe");
            if (option < 0 || option + 1 >= args.Length) throw new ArgumentException("Required: --game-exe <current reviewed ffxiv_dx11.exe>");
            var bootOption = Array.IndexOf(args, "--boot-dll");
            if (bootOption < 0 || bootOption + 1 >= args.Length) throw new ArgumentException("Required: --boot-dll <current reviewed Dalamud.Boot.dll>");
            bootFile = args[bootOption + 1];
            var bytes = File.ReadAllBytes(args[option + 1]);
            using var pe = new PEReader(new MemoryStream(bytes));
            using var mapping = new NonExecutableImage(bytes, pe);
            FFXIVClientStructs.Interop.Generated.Addresses.Register();
            Resolver.GetInstance.Setup(mapping.Base, pe.PEHeaders.PEHeader!.SizeOfImage, 0, "offline-timeline-qualification", null);
            Resolver.GetInstance.Resolve();
            var addresses = typeof(ActionTimelineSequencer).Assembly.GetTypes().Where(type => type.Name == "Addresses")
                .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static)).Where(field => field.FieldType == typeof(Address))
                .Select(field => ((Address)field.GetValue(null)!).Value).ToArray();
            Check(addresses.Length == 2574, "current reviewed CN SDK address inventory must contain 2574 fields");
            var sdk = typeof(ActionTimelineSequencer).Assembly;
            var dalamud = typeof(Dalamud.Hooking.Hook<>).Assembly;
            var metadata = new NativeTimelineMetadata(File.ReadAllBytes(sdk.Location), sdk.ManifestModule.ModuleVersionId,
                File.ReadAllBytes(dalamud.Location), dalamud.ManifestModule.ModuleVersionId,
                ActionTimelineSequencer.Addresses.PlayTimeline.Value, TimelineContainer.Addresses.PlayActionTimeline.Value, addresses, true);
            var module = new NativeTimelineModule("ffxiv_dx11.exe", mapping.Base, pe.PEHeaders.PEHeader.SizeOfImage, bytes);
            var cases = new List<(string Name, System.Action Run)> {
                ("complete actual SDK-resolved PE qualifies without native execution", () => {
                    var process = new ProcessFixture(module, metadata, mapping);
                    Check(new NativeTimelineBoundaryQualifier(process).TryQualify(out var plan, out var reason), reason);
                    Check(plan is not null && plan.Timeline.Address == mapping.Base + 0x606B10 && plan.ActionTimeline.Address == mapping.Base + 0x606FD0,
                        "resolved native targets must be wrapper tail destinations");
                    Check(process.MemoryReads > 8, "qualification must inspect loaded helper paths in both rounds");
                }),
            };
            foreach (var scenario in new[] { "wrong name", "wrong disk", "wrong image size", "wrong SDK disk", "stale SDK MVID", "wrong Dalamud disk", "stale Dalamud MVID", "missing SDK address", "incomplete metadata", "SDK timeline changed", "SDK action changed", "metadata registers leaf", "module changed", "metadata changed", "unreadable", "loaded leaf changed", "loaded helper changed", "loaded native caller changed", "loaded second round changed", "eager Hook factory backend", "backend changed during qualification" })
            {
                var captured = scenario;
                cases.Add(($"qualification rejects {scenario} before any hook factory", () => {
                    var process = new ProcessFixture(module, metadata, mapping);
                    switch (captured)
                    {
                        case "wrong name": process.Module = module with { Name = "other.exe" }; break;
                        case "wrong disk": var changed = (byte[])bytes.Clone(); changed[^1] ^= 1; process.Module = module with { FileImage = changed }; break;
                        case "wrong image size": process.Module = module with { Size = module.Size - 1 }; break;
                        case "wrong SDK disk": process.Metadata = metadata with { SdkImage = [1] }; break;
                        case "stale SDK MVID": process.Metadata = metadata with { LoadedSdkMvid = Guid.NewGuid() }; break;
                        case "wrong Dalamud disk": process.Metadata = metadata with { DalamudImage = [1] }; break;
                        case "stale Dalamud MVID": process.Metadata = metadata with { LoadedDalamudMvid = Guid.NewGuid() }; break;
                        case "missing SDK address": var missing = addresses.ToArray(); missing[0] = 0; process.Metadata = metadata with { AllSdkAddresses = missing }; break;
                        case "incomplete metadata": process.Metadata = metadata with { AllSdkAddresses = addresses.Skip(1).ToArray() }; break;
                        case "SDK timeline changed": process.Metadata = metadata with { SdkTimeline = metadata.SdkTimeline + 1 }; break;
                        case "SDK action changed": process.Metadata = metadata with { SdkActionTimeline = metadata.SdkActionTimeline + 1 }; break;
                        case "metadata registers leaf": var conflict = addresses.ToArray(); conflict[0] = mapping.Base + 0x606B10; process.Metadata = metadata with { AllSdkAddresses = conflict }; break;
                        case "module changed": process.ChangeSecondModule = true; break;
                        case "metadata changed": process.ChangeSecondMetadata = true; break;
                        case "unreadable": process.Unreadable = true; break;
                        case "loaded leaf changed": process.AlterRva = 0x606B10; break;
                        case "loaded helper changed": process.AlterRva = 0x608C80; break;
                        case "loaded native caller changed": process.AlterRva = 0x618100; break;
                        case "loaded second round changed": process.AlterSecondRound = true; break;
                        case "eager Hook factory backend": process.Metadata = metadata with { UsesDeferredHookBackend = false }; break;
                        case "backend changed during qualification": process.ChangeSecondBackend = true; break;
                    }
                    Check(!new NativeTimelineBoundaryQualifier(process).TryQualify(out var plan, out _) && plan is null, "drift must deny and publish no target plan");
                }));
            }
            foreach (var scenario in new[] { "unchanged", "SDK timeline changed", "SDK action changed", "loaded wrapper changed", "loaded helper changed", "loaded target changed", "loaded native caller changed", "peer entry changed", "peer interior changed", "module changed", "metadata registers leaf", "stale SDK MVID", "backend changed" })
            {
                var captured = scenario;
                cases.Add(($"pre-factory confirmation handles {scenario}", () => {
                    var process = new ProcessFixture(module, metadata, mapping);
                    var qualifier = new NativeTimelineBoundaryQualifier(process);
                    Check(qualifier.TryQualify(out var plan, out var reason) && plan is not null, reason);
                    switch (captured)
                    {
                        case "SDK timeline changed": process.Metadata = metadata with { SdkTimeline = metadata.SdkTimeline + 1 }; break;
                        case "SDK action changed": process.Metadata = metadata with { SdkActionTimeline = metadata.SdkActionTimeline + 1 }; break;
                        case "loaded wrapper changed": process.AlterRva = 0x8BE410; break;
                        case "loaded helper changed": process.AlterRva = 0x608530; break;
                        case "loaded target changed": process.AlterRva = 0x606FD0; break;
                        case "loaded native caller changed": process.AlterRva = 0x618100; break;
                        case "peer entry changed": process.AlterRva = 0x606B10; break;
                        case "peer interior changed": process.AlterRva = 0x606C10; break;
                        case "module changed": process.Module = module with { BaseAddress = module.BaseAddress + 0x1000 }; break;
                        case "metadata registers leaf": var conflict = addresses.ToArray(); conflict[0] = mapping.Base + 0x606FD0; process.Metadata = metadata with { AllSdkAddresses = conflict }; break;
                        case "stale SDK MVID": process.Metadata = metadata with { LoadedSdkMvid = Guid.NewGuid() }; break;
                        case "backend changed": process.Metadata = metadata with { UsesDeferredHookBackend = false }; break;
                    }
                    Check(qualifier.TryConfirmTarget(plan!, plan!.ActionTimeline) == (captured == "unchanged"),
                        "confirm must recheck every reviewed span including peer entry and interior before either hook is enabled");
                }));
            }
            cases.AddRange(InstallationCases(module, metadata, mapping));
            cases.AddRange(BackendContractCases());
            cases.AddRange(ObserverDiagnosticCases());
            cases.AddRange(FailedHookLifetimeCases());
            var passed = 0;
            foreach (var (name, run) in cases)
            {
                try { run(); passed++; Console.WriteLine("PASS " + name); }
                catch (Exception error) { Console.WriteLine("FAIL " + name + ": " + error.Message); }
            }
            Console.WriteLine($"NativeTimelineQualificationTests: {passed}/{cases.Count} cases; {checks} assertions; actual resolved SDK addresses={addresses.Length}.");
            return passed == cases.Count ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool accepted, string reason) { checks++; if (!accepted) throw new InvalidOperationException(reason); }

    private sealed class ProcessFixture(NativeTimelineModule module, NativeTimelineMetadata metadata, NonExecutableImage mapping) : INativeTimelineProcess
    {
        public NativeTimelineModule Module = module;
        public NativeTimelineMetadata Metadata = metadata;
        public int MemoryReads;
        public int AlterRva = -1;
        public bool ChangeSecondModule, ChangeSecondMetadata, ChangeSecondBackend, Unreadable, AlterSecondRound;
        private int modules, metadataReads, firstEntryReads;
        public bool TryReadModule(out NativeTimelineModule result)
        { result = ChangeSecondModule && ++modules > 1 ? Module with { BaseAddress = Module.BaseAddress + 0x1000 } : Module; return true; }
        public bool TryReadMetadata(out NativeTimelineMetadata result)
        {
            metadataReads++;
            result = ChangeSecondMetadata && metadataReads > 1 ? Metadata with { SdkTimeline = Metadata.SdkTimeline + 1 }
                : ChangeSecondBackend && metadataReads > 1 ? Metadata with { UsesDeferredHookBackend = false } : Metadata;
            return true;
        }
        public bool TryReadMemory(nint address, Span<byte> destination)
        {
            MemoryReads++;
            if (Unreadable) return false;
            var rva = checked((int)(address - mapping.Base));
            mapping.Copy(address, destination);
            if (rva <= AlterRva && AlterRva < rva + destination.Length) destination[AlterRva - rva] ^= 1;
            if (rva == 0x606AB0 && ++firstEntryReads > 1 && AlterSecondRound) destination[0] ^= 1;
            return true;
        }
    }
    private sealed class NonExecutableImage : IDisposable
    {
        public nint Base { get; }
        public NonExecutableImage(byte[] bytes, PEReader pe)
        {
            Base = VirtualAlloc(0, (nuint)pe.PEHeaders.PEHeader!.SizeOfImage, 0x3000, 0x04);
            if (Base == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            Marshal.Copy(bytes, 0, Base, pe.PEHeaders.PEHeader.SizeOfHeaders);
            foreach (var section in pe.PEHeaders.SectionHeaders)
                if (section.SizeOfRawData > 0) Marshal.Copy(bytes, section.PointerToRawData, Base + section.VirtualAddress, section.SizeOfRawData);
        }
        public void Copy(nint address, Span<byte> destination) => new ReadOnlySpan<byte>((void*)address, destination.Length).CopyTo(destination);
        public void Dispose() => VirtualFree(Base, 0, 0x8000);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFree(nint address, nuint size, uint freeType);
    }
}
