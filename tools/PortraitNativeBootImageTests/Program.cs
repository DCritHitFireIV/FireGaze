using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

// This suite maps Boot with SEC_IMAGE_NO_EXECUTE, never LoadLibrary or a Boot
// entry point. Its export adapter is an explicit fixture; production uses actual
// GetModuleHandle/GetProcAddress, which still needs live game qualification.
internal static class Program
{
    private const string Expected = "862736DA018084B4A35EB149878D827D3AC651AE029915970365B686699B9A37";
    private static int checks;
    private static readonly string[] Names = ["BootSafetyHookInlineCreate", "BootSafetyHookInlineDestroy", "BootSafetyHookInlineDisable", "BootSafetyHookInlineEnable", "BootSafetyHookInlineGetTrampoline", "BootSafetyHookInlineIsEnabled"];
    private static readonly uint[] RVAs = [0x73690, 0x73800, 0x73860, 0x73930, 0x73A00, 0x73A10];
    public static int Main(string[] arguments)
    {
        try
        {
            if (arguments.Length != 1) throw new ArgumentException("Pass the audited Boot DLL path; no native code is executed.");
            var audited = File.ReadAllBytes(arguments[0]); Check(Hash(audited) == Expected, "audited real Boot disk identity");
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var root = Path.GetFullPath(Path.Combine(tempRoot, "FireGaze-BootImage-" + Guid.NewGuid().ToString("N")));
            if (!root.StartsWith(Path.TrimEndingDirectorySeparator(tempRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("FireGaze-BootImage-", StringComparison.Ordinal)) throw new InvalidOperationException("Temporary cleanup target escaped its intended directory.");
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "Dalamud.Boot.dll"); var old = (byte[])audited.Clone();
                // Boot's real Create sets r9d=1 (StartDisabled). Alter only its
                // immediate; all headers, RVAs, exports and metadata stay equal.
                var flag = Offset(audited, 0x736EE); Check(old[flag] == 1, "real StartDisabled immediate is one"); old[flag] = 0;
                File.WriteAllBytes(path, old);
                using var image = new ImageMapping(path);
                File.Move(path, Path.Combine(root, "old-loaded-Boot.dll")); File.WriteAllBytes(path, audited);
                Check(Hash(File.ReadAllBytes(path)) == Expected, "legacy disk-only qualification accepts replaced path");
                Check(image.ReadByte(0x736EE) == 0, "actual mapped old image still has changed StartDisabled flag");
                Console.WriteLine("Regression fixture: audited disk SHA matches while the OS-mapped StartDisabled immediate is zero; no native code executed.");
                var qualifier = Assembly.GetExecutingAssembly().GetType("FireGaze.PortraitPreview.PortraitNativeBootImageQualifier");
                Check(qualifier is not null, "loaded Boot code qualification is missing; disk-only gate accepts the stale mapped StartDisabled implementation");
                RunQualifiedTests(qualifier!, image, path, audited, root);
            }
            finally { Directory.Delete(root, true); }
            Console.WriteLine($"PortraitNativeBootImageTests: {checks} checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void RunQualifiedTests(Type qualifier, ImageMapping stale, string path, byte[] audited, string root)
    {
        var fixtureType = Assembly.GetExecutingAssembly().GetType("BootImageProcessFixture")!;
        var fixture = Activator.CreateInstance(fixtureType, stale, path)!;
        Check(!Qualify(qualifier, fixture, out var reason) && reason.Contains("loaded", StringComparison.OrdinalIgnoreCase), "changed mapped StartDisabled code rejected: " + reason);
        using var good = new ImageMapping(path); fixture = Activator.CreateInstance(fixtureType, good, path)!;
        Check(Qualify(qualifier, fixture, out reason), "complete non-executing audited image accepted by fixture boundary: " + reason);
        var concrete = fixtureType.GetField("ExportDelta")!; concrete.SetValue(fixture, 1);
        Check(!Qualify(qualifier, fixture, out _), "wrong actual export address rejected"); concrete.SetValue(fixture, 0);
        foreach (var name in Names)
        {
            fixtureType.GetField("MissingExport")!.SetValue(fixture, name);
            Check(!Qualify(qualifier, fixture, out _), "missing actual export rejected: " + name);
        }
        fixtureType.GetField("MissingExport")!.SetValue(fixture, null);
        foreach (var rva in new[] { 0x138, 0x195D30, 0x1B0000, 0x1F920 })
        {
            fixtureType.GetField("AlterRVA")!.SetValue(fixture, rva);
            Check(!Qualify(qualifier, fixture, out _), "mapped header/export/exception/helper byte drift rejected at " + rva.ToString("X"));
        }
        fixtureType.GetField("AlterRVA")!.SetValue(fixture, -1);
        fixtureType.GetField("UnreadableRVA")!.SetValue(fixture, 0x1000);
        Check(!Qualify(qualifier, fixture, out _), "unreadable executable section rejects without invocation");
        fixtureType.GetField("UnreadableRVA")!.SetValue(fixture, -1);
        fixtureType.GetField("ChangeOnSecondModule")!.SetValue(fixture, true);
        Check(!Qualify(qualifier, fixture, out _), "module identity drift between read rounds rejected");
        fixtureType.GetField("ChangeOnSecondModule")!.SetValue(fixture, false);
        fixtureType.GetField("AlterSecondCodeRead")!.SetValue(fixture, true);
        Check(!Qualify(qualifier, fixture, out _), "code drift between read rounds rejected");
        fixtureType.GetField("AlterSecondCodeRead")!.SetValue(fixture, false);
        var inspect = qualifier.GetMethod("TryInspectImage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var bad = (byte[])audited.Clone(); using (var pe = new PEReader(new MemoryStream(audited)))
        {
            var reloc = Offset(audited, (uint)pe.PEHeaders.PEHeader!.BaseRelocationTableDirectory.RelativeVirtualAddress);
            bad[reloc + 4] = 7; bad[reloc + 5] = bad[reloc + 6] = bad[reloc + 7] = 0;
        }
        Check(!Inspect(inspect, bad), "malformed relocation block rejected independently of disk SHA");
        bad = (byte[])audited.Clone(); using (var pe = new PEReader(new MemoryStream(audited)))
        {
            var reloc = Offset(audited, (uint)pe.PEHeaders.PEHeader!.BaseRelocationTableDirectory.RelativeVirtualAddress);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(reloc), 0x1000);
        }
        Check(!Inspect(inspect, bad), "executable relocation is fail-closed for this relocation-free audited text");
        bad = (byte[])audited.Clone(); using (var pe = new PEReader(new MemoryStream(audited)))
        {
            var exportsRVA = (uint)pe.PEHeaders.PEHeader!.ExportTableDirectory.RelativeVirtualAddress; var exports = Offset(audited, exportsRVA);
            var functions = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(audited.AsSpan(exports + 28));
            var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(audited.AsSpan(exports + 20));
            var functionTable = Offset(audited, functions);
            var create = Enumerable.Range(0, (int)count).Single(index => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(audited.AsSpan(functionTable + index * 4)) == 0x73690);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(functionTable + create * 4), exportsRVA + 40);
        }
        Check(!Inspect(inspect, bad), "forwarded SafetyHook export rejected independently of disk SHA");
        var wrongDisk = (byte[])audited.Clone(); wrongDisk[^1] ^= 1;
        var mappedGoodPath = Path.Combine(root, "audited-mapped-Boot.dll"); File.Move(path, mappedGoodPath); File.WriteAllBytes(path, wrongDisk);
        Check(!Qualify(qualifier, fixture, out _), "unqualified replacement disk image rejected while mapped code stays audited");
        File.Delete(path); File.Move(mappedGoodPath, path);
        var current = qualifier.GetMethod("TryQualifyCurrentProcess", BindingFlags.Public | BindingFlags.Static)!; object?[] args = [null, null];
        Check(!(bool)current.Invoke(null, args)!, "ordinary test process without loaded Boot rejects; production does not load it");
        Check(!ProcessHasBoot(), "qualification left Boot unloaded in the actual test process");
    }

    private static bool Qualify(Type qualifier, object fixture, out string reason)
    {
        fixture.GetType().GetMethod("Reset")!.Invoke(fixture, null);
        var instance = Activator.CreateInstance(qualifier, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [fixture], null)!;
        object?[] args = [null, null]; var result = (bool)qualifier.GetMethod("TryQualify")!.Invoke(instance, args)!; reason = (string)args[1]!; return result;
    }
    private static bool Inspect(MethodInfo inspect, byte[] bytes) { object?[] args = [bytes, null, null]; return (bool)inspect.Invoke(null, args)!; }
    private static bool ProcessHasBoot() { using var process = System.Diagnostics.Process.GetCurrentProcess(); return process.Modules.Cast<System.Diagnostics.ProcessModule>().Any(module => module.ModuleName.Equals("Dalamud.Boot.dll", StringComparison.OrdinalIgnoreCase)); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static int Offset(byte[] image, uint rva) { using var pe = new PEReader(new MemoryStream(image)); foreach (var section in pe.PEHeaders.SectionHeaders) if (rva >= section.VirtualAddress && rva < section.VirtualAddress + section.SizeOfRawData) return section.PointerToRawData + (int)rva - section.VirtualAddress; throw new ArgumentOutOfRangeException(nameof(rva)); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("FAIL: " + message); }

    public sealed unsafe class ImageMapping : IDisposable
    {
        private readonly FileStream file; private readonly nint mapping; public nint Base { get; }
        public ImageMapping(string path)
        {
            file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            mapping = CreateFileMappingW(file.SafeFileHandle.DangerousGetHandle(), 0, 0x11000002, 0, 0, null);
            if (mapping == 0) { file.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()); }
            Base = MapViewOfFile(mapping, 4, 0, 0, 0);
            if (Base == 0) { CloseHandle(mapping); file.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()); }
        }
        public byte ReadByte(int rva) => Marshal.ReadByte(Base + rva);
        public bool Read(nint address, Span<byte> output) { fixed (byte* destination = output) return ReadProcessMemory(-1, address, destination, (nuint)output.Length, out var read) && read == (nuint)output.Length; }
        public nint Export(string name) => Base + (nint)RVAs[Array.IndexOf(Names, name)];
        public void Dispose() { UnmapViewOfFile(Base); CloseHandle(mapping); file.Dispose(); }
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern nint CreateFileMappingW(nint file, nint attributes, uint protection, uint high, uint low, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern nint MapViewOfFile(nint mapping, uint access, uint high, uint low, nuint size);
        [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnmapViewOfFile(nint address);
        [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
        [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(nint process, nint address, void* output, nuint size, out nuint read);
    }
}
