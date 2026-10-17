using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

// Inspect the built bytes: source greps cannot establish whether a compiler embedded a local debug path.
var passed = 0;
var failed = 0;
var artifactDirectory = args.Length == 0 ? Path.Combine(FindRepositoryRoot(), "src", "FireGaze", "bin", "Release") : Path.GetFullPath(args[0]);
var profilePath = new Regex(@"(?i)\b[A-Z]:[\\/]+Users[\\/]+[\p{L}\p{N}_][\p{L}\p{N}_. -]{0,79}(?=[\\/\x00\r\n\t\""<>]|$)", RegexOptions.CultureInvariant);
void Check(bool condition, string description)
{
    if (condition) passed++;
    else { failed++; Console.WriteLine("FAIL " + description); }
}
Check(Directory.Exists(artifactDirectory), "built artifact directory exists");
foreach (var fileName in new[] { "FireGaze.dll", "FireGaze.LocalPreview.Core.dll", "dnlib.dll" })
{
    var path = Path.Combine(artifactDirectory, fileName);
    Check(File.Exists(path), fileName + " is present");
    if (!File.Exists(path)) continue;
    var bytes = File.ReadAllBytes(path);
    try
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        Check(pe.HasMetadata, fileName + " is a readable managed PE");
        var codeViewEntries = pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.CodeView).ToArray();
        // DebugType=None may have no CodeView entries. That is a valid portable artifact.
        var paths = codeViewEntries.Select(entry => pe.ReadCodeViewDebugDirectoryData(entry).Path).ToArray();
        // A vendor CI PDB location is not a private user profile or a runtime dependency.
        // Owned assemblies must omit machine-specific debug paths entirely.
        var ownedAssembly = fileName != "dnlib.dll";
        Check(paths.All(value => !profilePath.IsMatch(value) && (!ownedAssembly || !Path.IsPathRooted(value))),
            fileName + (ownedAssembly ? " has no absolute CodeView/PDB path" : " CodeView has no Windows user-profile path"));
    }
    catch (BadImageFormatException)
    {
        Check(false, fileName + " is a readable managed PE");
    }
    Check(!profilePath.IsMatch(Encoding.UTF8.GetString(bytes)), fileName + " UTF8 has no literal Windows user-profile path");
    Check(!profilePath.IsMatch(Encoding.Unicode.GetString(bytes.AsSpan(0, bytes.Length & ~1))), fileName + " UTF16 even alignment has no literal Windows user-profile path");
    Check(!profilePath.IsMatch(Encoding.Unicode.GetString(bytes.AsSpan(1, (bytes.Length - 1) & ~1))), fileName + " UTF16 odd alignment has no literal Windows user-profile path");
}
Console.WriteLine($"ArtifactPortabilityTests: {passed} passed, {failed} failed");
Environment.ExitCode = failed == 0 ? 0 : 1;
return;

static string FindRepositoryRoot()
{
    for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        if (File.Exists(Path.Combine(current.FullName, "src", "FireGaze", "FireGaze.csproj"))) return current.FullName;
    throw new DirectoryNotFoundException("Cannot locate the source root for the built artifact check.");
}
