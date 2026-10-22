using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using FireGaze.LocalPreview.Runtime;

internal static partial class Program
{
    private static IEnumerable<(string Name,Action Run)> BindingCases()
    {
        foreach(var variant in new[]{"valid","foreign provider","foreign plugin assembly","different target","zero address","target changed","Boot disk changed","Boot loaded changed"})
        {
            var captured=variant;
            yield return($"real reflected adapter binds metadata without constructing a native hook: {variant}",()=>{
                var cn=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"XIVLauncherCN/addon/Hooks/dev");
                var dalamud=Assembly.LoadFrom(Path.Combine(cn,"Dalamud.dll"));
                Check(ReviewedSafetyHookSchema.TryRead(dalamud,out var schema),"fixture must use exact real official members");
                var runtime=typeof(ActorPreviewRuntime).Assembly;
                var actorType=runtime.GetType("FireGaze.LocalPreview.Runtime.ActorPreviewRuntime",true)!;
                var owner=RuntimeHelpers.GetUninitializedObject(actorType);
                var target=captured=="different target"?RuntimeHelpers.GetUninitializedObject(actorType):owner;
                var delegateType=actorType.GetNestedType("PlayResolvedTimelineNative",BindingFlags.NonPublic)!;
                var detour=actorType.GetMethod("ResolvedTimelinePlayDetour",BindingFlags.NonPublic|BindingFlags.Instance)!.CreateDelegate(delegateType,target);
                var provider=RuntimeHelpers.GetUninitializedObject(schema!.Provider);
                var plugin=RuntimeHelpers.GetUninitializedObject(schema.Plugin.FieldType);
                schema.Plugin.FieldType.GetField("<Assembly>k__BackingField",BindingFlags.NonPublic|BindingFlags.Instance)!
                    .SetValue(plugin,captured=="foreign plugin assembly"?typeof(Program).Assembly:runtime);
                schema.Plugin.SetValue(provider,plugin);
                schema.Tracked.SetValue(provider,Activator.CreateInstance(schema.Tracked.FieldType));
                if(captured=="foreign provider")provider=new object();
                var boot=File.ReadAllBytes(Path.Combine(cn,"Dalamud.Boot.dll"));
                using var pe=new PEReader(new MemoryStream(boot,false));
                var disk=boot.ToArray();if(captured=="Boot disk changed")disk[^1]^=1;
                byte[]? Read(int rva,int size)
                {
                    var section=pe.PEHeaders.SectionHeaders.Single(s=>rva>=s.VirtualAddress&&(long)rva+size<=(long)s.VirtualAddress+s.SizeOfRawData);
                    var value=boot.AsSpan(section.PointerToRawData+rva-section.VirtualAddress,size).ToArray();
                    if(captured=="Boot loaded changed")value[0]^=1;
                    return value;
                }
                Func<ReviewedBootEvidence> readBoot=()=>new(disk,Read);
                Func<bool> confirm=()=>captured!="target changed";
                var apiType=typeof(ReviewedSafetyHookApi<>).MakeGenericType(delegateType);
                object?[] parameters=[provider,owner,detour,captured=="zero address"?(nint)0:(nint)0x606B10,confirm,readBoot,null,null];
                var accepted=(bool)apiType.GetMethod("TryBind",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,parameters)!;
                Check(accepted==(captured=="valid"),"only exact official scope, owner, ABI and disabled implementation can bind");
                Check((parameters[6] is not null)==accepted,"failed binding publishes no usable constructor adapter");
                if(!accepted)Check(parameters[7] is string reason&&reason.Length>0,"denial preserves technical diagnostic reason");
            });
        }
    }
}
