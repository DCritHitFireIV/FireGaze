using System.Reflection;
using System.Reflection.PortableExecutable;

internal static unsafe partial class Program
{
    private static IEnumerable<(string Name, System.Action Run)> BackendContractCases()
    {
        foreach (var scenario in new[] { "exact reviewed disabled factory", "eager selected backend", "changed Boot disk", "unreadable loaded code", "changed Create flag", "changed flags branch", "changed intermediate helper" })
        {
            var captured = scenario;
            yield return ($"native factory contract: {scenario}", () => {
                var image = File.ReadAllBytes(bootFile);
                using var pe = new PEReader(new MemoryStream(image));
                var assembly = typeof(FireGaze.LocalPreview.Runtime.ActorPreviewRuntime).Assembly;
                var contract = assembly.GetType("FireGaze.LocalPreview.Runtime.NativeTimelineBackendContract");
                Check(contract is not null, "disabled factory admission must bind current native Boot identity and loaded implementation");
                var method = contract!.GetMethod("Matches", BindingFlags.NonPublic | BindingFlags.Static)!;
                var disk = image.ToArray();
                if (captured == "changed Boot disk") disk[^1] ^= 1;
                Func<int, int, byte[]?> read = (rva, size) => {
                    if (captured == "unreadable loaded code") return null;
                    var section = pe.PEHeaders.SectionHeaders.Single(s => rva >= s.VirtualAddress && rva + size <= s.VirtualAddress + s.SizeOfRawData);
                    var loaded = image.AsSpan(section.PointerToRawData + rva - section.VirtualAddress, size).ToArray();
                    var changed = captured switch { "changed Create flag" => 0x736EC, "changed flags branch" => 0x1F859, "changed intermediate helper" => 0x1F944, _ => -1 };
                    if (changed >= rva && changed < rva + size) loaded[changed - rva] ^= 1;
                    return loaded;
                };
                Check((bool)method.Invoke(null, [captured != "eager selected backend", disk, read])! == (captured == "exact reviewed disabled factory"),
                    "selected backend, current Boot disk and all loaded disabled-factory implementation spans must match");
            });
        }
    }
}
