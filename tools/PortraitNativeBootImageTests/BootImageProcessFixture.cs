using System.Reflection.PortableExecutable;
using FireGaze.PortraitPreview;

// Export addresses intentionally model the boundary; SEC_IMAGE_NO_EXECUTE maps
// do not register a loader module and cannot prove real GetProcAddress routing.
internal sealed class BootImageProcessFixture(Program.ImageMapping mapping, string path) : IPortraitNativeBootProcess
{
    public int ExportDelta = 0;
    public int UnreadableRVA = -1;
    public bool ChangeOnSecondModule = false;
    public bool AlterSecondCodeRead = false;
    public string? MissingExport = null;
    public int AlterRVA = -1;
    private int modules, firstCodeReads;
    public void Reset() { modules = firstCodeReads = 0; }
    public bool TryReadModule(out PortraitNativeBootModule module)
    {
        modules++; var bytes = File.ReadAllBytes(path); using var pe = new PEReader(new MemoryStream(bytes));
        module = new("Dalamud.Boot.dll", mapping.Base + (ChangeOnSecondModule && modules == 2 ? 4096 : 0), pe.PEHeaders.PEHeader!.SizeOfImage, bytes); return true;
    }
    public bool TryReadMemory(nint address, Span<byte> destination)
    {
        var rva = address - mapping.Base;
        if (UnreadableRVA >= 0 && rva <= UnreadableRVA && UnreadableRVA < rva + destination.Length) return false;
        if (!mapping.Read(address, destination)) return false;
        if (AlterRVA >= rva && AlterRVA < rva + destination.Length) destination[(int)(AlterRVA - rva)] ^= 1;
        if (rva == 0x1000 && ++firstCodeReads == 2 && AlterSecondCodeRead) destination[0] ^= 1;
        return true;
    }
    public nint GetExport(nint module, string name) => name == MissingExport ? 0 : mapping.Export(name) + ExportDelta;
}
