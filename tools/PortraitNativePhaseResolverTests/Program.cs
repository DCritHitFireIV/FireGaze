#if !PHASE_PROCESS_RESOLVER
Console.Error.WriteLine("FAIL actual process/image resolver is missing; detached caller evidence cannot qualify live targets.");
return 1;
#else
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

var fixture = FrozenSdkFixture.Load(args, typeof(CharaView).Assembly);
var image = File.ReadAllBytes(fixture.GameExecutable);
var sdk = fixture.Image;
var moduleBase = unchecked((nint)0x140000000L);
var roots = new uint[] { 0x2BA030, 0x2B9A40, 0x421D60, 0x9CD4D0 };
var prefixes = new[] {
    "405357415441554883EC5865488B0425580000004C8BE98B1573689602488B1D",
    "40564883EC70488BF1440F295424208B894C830300440F28D183F9FF744C8BC1",
    "4C8BDC49895B104989731849897B2055498DAB28FFFFFF4881ECD0010000488B",
    "48897C242041564883EC2083790800448BF2488BF90F840A0300008B510C85D2"
}.Select(Convert.FromHexString).ToArray();
using var pe = new PEReader(new MemoryStream(image, false));
var size = pe.PEHeaders.PEHeader!.SizeOfImage;
var mvid = fixture.MVID;
var passed = 0; var failed = 0;
void Check(bool valid, string message)
{
    if (valid) { passed++; Console.WriteLine("PASS " + message); }
    else { failed++; Console.Error.WriteLine("FAIL " + message); }
}
FakeProcess Fresh() => new(image, sdk, size, mvid, moduleBase, roots, prefixes);
bool Resolve(FakeProcess process, out PortraitNativePhaseResolvedPlan? plan)
    => new PortraitNativePhaseObservationResolver(process).TryResolve(out plan, out _);
bool ResolveWithReason(FakeProcess process, out PortraitNativePhaseResolvedPlan? plan, out string reason)
    => new PortraitNativePhaseObservationResolver(process).TryResolve(out plan, out reason);
void Reject(FakeProcess process, string name)
{
    Check(!Resolve(process, out var plan) && plan is null, name);
}

if (fixture.CurrentUnsupported)
{
    var unsupported = Fresh(); unsupported.SDKImage = fixture.CurrentImage; unsupported.SDKMVID = fixture.CurrentMVID;
    Check(!Resolve(unsupported, out var unsupportedPlan) && unsupportedPlan is null && unsupported.MemoryReads == 0,
        "unsupported current SDK refuses all phase targets before native reads or any installation plan");
}
var valid = Fresh();
Check(Resolve(valid, out var resolved) && resolved is { Targets.Count: 4 }
    && resolved.Targets[0].Address == moduleBase + 0x2BA030
    && resolved.Targets[1].Phase == PortraitNativeObservedPhase.ManagerUpdateCandidate
    && resolved.Targets[2].Address == moduleBase + 0x421D60
    && resolved.Targets[3].Address == moduleBase + 0x9CD4D0,
    "real disk EXE, historical SDK metadata and two fixture-prefix rounds resolve four inert targets");
Check(valid.MemoryReads == 8 && valid.RootReads == 2 && valid.SDKReads == 2,
    "qualification rechecks module, loaded SDK identity and every runtime entry without scene access");
Check(resolved is not null && resolved.SDKMVID == mvid && resolved.ModuleBase == moduleBase,
    "plan binds historical fixture SDK MVID and ASLR module identity");
Check(!PortraitNativePhaseObservationResolver.TryResolveCurrentProcess(static _ => false, out _, out _),
    "real adapter rejects the test process before reading targets or installing hooks");

var wrong = Fresh(); wrong.ModuleAvailable = false; Reject(wrong, "missing current module refuses");
wrong = Fresh(); wrong.Name = "another.exe"; Reject(wrong, "another process name refuses even with the same disk bytes");
wrong = Fresh(); wrong.ModuleBase = 0; Reject(wrong, "zero module base refuses");
wrong = Fresh(); wrong.ModuleBase = nint.MaxValue - 8; Reject(wrong, "module address overflow refuses");
wrong = Fresh(); wrong.ImageSize = size - 1; Reject(wrong, "OS module size and PE image size must agree");
wrong = Fresh(); wrong.Executable = (byte[])image.Clone(); wrong.Executable[^1] ^= 1; Reject(wrong, "EXE file fingerprint drift refuses");
wrong = Fresh(); wrong.SDKAvailable = false; Reject(wrong, "missing loaded SDK refuses");
wrong = Fresh(); wrong.SDKImage = (byte[])sdk.Clone(); wrong.SDKImage[^1] ^= 1; Reject(wrong, "SDK file fingerprint drift refuses");
wrong = Fresh(); wrong.SDKMVID = Guid.NewGuid(); Reject(wrong, "loaded SDK and on-disk module MVID mismatch refuses");
wrong = Fresh(); wrong.ManagerRender++; Reject(wrong, "SDK manager Render resolves to the exact audited entry");
wrong = Fresh(); wrong.SourceRender++; Reject(wrong, "SDK source Render resolves to the exact audited entry");
wrong = Fresh(); wrong.ChangedPrefix = 2;
Check(!ResolveWithReason(wrong, out _, out var prefixReason) && prefixReason.Contains("RVA 0x421D60", StringComparison.Ordinal)
    && prefixReason.Contains("runtime prefix", StringComparison.OrdinalIgnoreCase),
    "runtime prefix rejection reports its phase RVA and failed check");
for (var index = 0; index < 4; index++)
{
    wrong = Fresh(); wrong.Unreadable = index; Reject(wrong, "runtime unreadable prefix " + index + " refuses");
    wrong = Fresh(); wrong.ChangedPrefix = index; Reject(wrong, "runtime hook/jump or changed prefix " + index + " refuses");
    wrong = Fresh(); wrong.Reserved = index; Reject(wrong, "same-plugin address reservation " + index + " refuses");
    wrong = Fresh(); wrong.ChangeSecondRound = index; Reject(wrong, "late prefix replacement " + index + " refuses");
}
wrong = Fresh(); wrong.ChangeRoot = true; Reject(wrong, "ASLR root changes during qualification refuse old targets");
wrong = Fresh(); wrong.ChangeSDKMVID = true; Reject(wrong, "SDK identity changes during qualification refuse old targets");
wrong = Fresh(); wrong.ReserveLate = true; Reject(wrong, "new address reservation during qualification refuses");
wrong = Fresh(); wrong.ReserveAfterRead = 8; Reject(wrong, "reservation acquired inside the final prefix read refuses an obsolete plan");
wrong = Fresh(); wrong.ThrowMemoryRead = true; Reject(wrong, "memory read exception cannot leak a partial plan");

Check(PortraitNativePhaseObservationResolver.TryInspectExecutableImage(image, size, out var inspected, out _)
    && inspected.Count == 4 && inspected.Select(entry => entry.FunctionRootRVA).SequenceEqual(roots),
    "disk parser independently verifies actual .pdata function roots");
var badImage = (byte[])image.Clone(); badImage[0] = 0;
Check(!PortraitNativePhaseObservationResolver.TryInspectExecutableImage(badImage, size, out _, out _), "invalid PE cannot produce entry evidence");
badImage = [.. image, .. prefixes[0]];
Check(!PortraitNativePhaseObservationResolver.TryInspectExecutableImage(badImage, size, out _, out _), "duplicate frozen prefix anywhere in the file refuses");
badImage = (byte[])image.Clone();
var text = pe.PEHeaders.SectionHeaders.First(section => (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0);
var sectionTable = pe.PEHeaders.PEHeaderStartOffset + pe.PEHeaders.CoffHeader.SizeOfOptionalHeader;
var textIndex = pe.PEHeaders.SectionHeaders.IndexOf(text);
var flagsOffset = sectionTable + textIndex * 40 + 36;
var flags = BitConverter.ToUInt32(badImage, flagsOffset) & ~(uint)SectionCharacteristics.MemExecute;
BitConverter.GetBytes(flags).CopyTo(badImage, flagsOffset);
Check(!PortraitNativePhaseObservationResolver.TryInspectExecutableImage(badImage, size, out _, out _), "matching prefixes outside executable sections refuse");
var exception = pe.PEHeaders.PEHeader.ExceptionTableDirectory;
var pdataSection = pe.PEHeaders.SectionHeaders.First(section => exception.RelativeVirtualAddress >= section.VirtualAddress
    && exception.RelativeVirtualAddress < section.VirtualAddress + section.SizeOfRawData);
var pdataOffset = pdataSection.PointerToRawData + exception.RelativeVirtualAddress - pdataSection.VirtualAddress;
badImage = (byte[])image.Clone();
for (var entry = pdataOffset; entry < pdataOffset + exception.Size; entry += 12)
{
    if (BitConverter.ToUInt32(badImage, entry) != 0x2BA030) continue;
    BitConverter.GetBytes(0x2BA02Fu).CopyTo(badImage, entry); break;
}
Check(!PortraitNativePhaseObservationResolver.TryInspectExecutableImage(badImage, size, out _, out _),
    "an entry inside a function cannot masquerade as its unwind root");
Check(PortraitNativePhaseObservationResolver.TryResolveCurrentProcess(static _ => false, out var absent, out var reason) == false
    && absent is null && !string.IsNullOrWhiteSpace(reason), "real wrong-process refusal has a bounded explanation and no partial result");
Console.WriteLine($"PortraitNativePhaseResolverTests: {passed} passed, {failed} failed; disk/current-test-process reads only; game calls/hooks/scene operations: 0");
return failed == 0 ? 0 : 1;

sealed class FakeProcess(byte[] executable, byte[] sdk, int size, Guid mvid, nint moduleBase, uint[] roots, byte[][] prefixes)
    : IPortraitNativePhaseProcess
{
    public byte[] Executable = executable, SDKImage = sdk;
    public nint ModuleBase = moduleBase;
    public int ImageSize = size;
    public Guid SDKMVID = mvid;
    public string Name = "ffxiv_dx11.exe";
    public bool ModuleAvailable = true, SDKAvailable = true, ChangeRoot, ChangeSDKMVID, ReserveLate, ThrowMemoryRead;
    public int RootReads, SDKReads, MemoryReads, ReserveAfterRead = int.MaxValue;
    public int Unreadable = -1, ChangedPrefix = -1, Reserved = -1, ChangeSecondRound = -1;
    public nint ManagerRender = moduleBase + 0x2BA030, SourceRender = moduleBase + 0x9CD4D0;
    public bool TryReadModule(out PortraitNativePhaseProcessModule module)
    {
        RootReads++;
        module = new(Name, ModuleBase + (ChangeRoot && RootReads > 1 ? 1 : 0), ImageSize, Executable);
        return ModuleAvailable;
    }
    public bool TryReadSDK(out PortraitNativePhaseSDKImage image)
    {
        SDKReads++;
        image = new(SDKImage, ChangeSDKMVID && SDKReads > 1 ? Guid.NewGuid() : SDKMVID);
        return SDKAvailable;
    }
    public nint SDKManagerRender => ManagerRender;
    public nint SDKSourceRender => SourceRender;
    public bool IsAddressReserved(nint address)
        => Reserved >= 0 && address == ModuleBase + (nint)roots[Reserved] || ReserveLate && MemoryReads >= 4
            || MemoryReads >= ReserveAfterRead;
    public bool TryReadMemory(nint address, Span<byte> destination)
    {
        if (ThrowMemoryRead) throw new IOException("controlled external read failure");
        var index = Array.FindIndex(roots, rva => address == ModuleBase + (nint)rva);
        MemoryReads++;
        if (index < 0 || index == Unreadable || destination.Length != prefixes[index].Length) return false;
        prefixes[index].CopyTo(destination);
        if (index == ChangedPrefix || index == ChangeSecondRound && MemoryReads > 4) destination[0] ^= 1;
        return true;
    }
}
#endif
