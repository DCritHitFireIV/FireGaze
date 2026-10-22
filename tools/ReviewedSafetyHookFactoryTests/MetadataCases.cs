using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using FireGaze.LocalPreview.Runtime;

internal static partial class Program
{
    private static IEnumerable<(string Name,Action Run)> MetadataCases()
    {
        var cn=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"XIVLauncherCN/addon/Hooks/dev");
        AssemblyLoadContext.Default.Resolving += (_,name) => File.Exists(Path.Combine(cn,name.Name+".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(cn,name.Name+".dll")) : null;
        var file=Path.Combine(cn,"Dalamud.dll");
        var image=File.ReadAllBytes(file);
        var boot=File.ReadAllBytes(Path.Combine(cn,"Dalamud.Boot.dll"));
        yield return ("current real Dalamud PE identity and MVID qualify offline",()=>{
            Check(ReviewedSafetyHookSchema.MatchesIdentity(image,new Guid("e8e67ae5-8265-4b32-a772-36d110056968")),"current exact CN image must qualify");
        });
        yield return ("changed Dalamud image rejected before reflection or native constructor",()=>{
            var changed=image.ToArray(); changed[^1]^=1;
            Check(!ReviewedSafetyHookSchema.MatchesIdentity(changed,new Guid("e8e67ae5-8265-4b32-a772-36d110056968")),"changed bytes deny");
        });
        yield return ("mismatched loaded MVID rejects identical disk",()=>{
            Check(!ReviewedSafetyHookSchema.MatchesIdentity(image,Guid.NewGuid()),"loaded and disk identities cannot drift");
        });
        yield return ("exact official verifier scope constructor and tracking member metadata qualify without invoking hooks",()=>{
            Check(ReviewedSafetyHookSchema.MatchesMembers(Assembly.LoadFrom(file)),"current real member signatures must qualify");
        });
        yield return ("lookalike assembly cannot supply official reflected members",()=>{
            Check(!ReviewedSafetyHookSchema.MatchesMembers(typeof(Program).Assembly),"same-named substitutes cannot become the official factory");
        });
        foreach(var variant in new[]{"valid disabled implementation", "changed disk", "unreadable", "changed create", "changed flags", "changed helper"})
        {
            var captured=variant;
            yield return ($"Boot implementation independent of global selection: {variant}",()=>{
                var disk=boot.ToArray(); if(captured=="changed disk")disk[^1]^=1;
                using var pe=new PEReader(new MemoryStream(boot,false));
                byte[]? Read(int rva,int size)
                {
                    if(captured=="unreadable")return null;
                    var section=pe.PEHeaders.SectionHeaders.Single(s=>rva>=s.VirtualAddress && (long)rva+size<=(long)s.VirtualAddress+s.SizeOfRawData);
                    var bytes=boot.AsSpan(section.PointerToRawData+rva-section.VirtualAddress,size).ToArray();
                    var changed=captured switch{"changed create"=>0x736EC,"changed flags"=>0x1F859,"changed helper"=>0x1F944,_=>-1};
                    if(changed>=rva && changed<rva+size)bytes[changed-rva]^=1;
                    return bytes;
                }
                Check(NativeTimelineBackendContract.Matches(true,disk,Read)==(captured=="valid disabled implementation"),"all reviewed executable spans must match; global setting is not an input");
            });
        }
    }
}
