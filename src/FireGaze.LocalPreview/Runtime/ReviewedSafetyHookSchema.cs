using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace FireGaze.LocalPreview.Runtime;

internal static class ReviewedSafetyHookSchema
{
    internal const string DalamudHash = "AF15D754340C659F9F57C110C9914D8C68A068F588725CAAE50AC237B07DA1C4";
    internal static readonly Guid DalamudMvid = new("e8e67ae5-8265-4b32-a772-36d110056968");
    internal static bool MatchesIdentity(byte[] image, Guid loadedMvid)
    {
        if (loadedMvid != DalamudMvid || Convert.ToHexString(SHA256.HashData(image)) != DalamudHash) return false;
        using var pe = new PEReader(new MemoryStream(image,false));
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || !pe.HasMetadata) return false;
        var metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid) == loadedMvid
            && metadata.GetAssemblyDefinition().Version == new Version(15,0,3,9)
            && metadata.GetString(metadata.GetAssemblyDefinition().Name) == "Dalamud";
    }
    internal static bool MatchesMembers(Assembly assembly) => TryRead(assembly,out _);

    internal const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    internal sealed record Members(Assembly Assembly, Type Provider, FieldInfo Plugin, PropertyInfo PluginAssembly,
        FieldInfo Tracked, MethodInfo Add, MethodInfo Verify, MethodInfo Follow, Type SafetyDefinition);

    internal static bool TryRead(Assembly assembly,out Members? members)
    {
        members = null;
        try
        {
            if (assembly.ManifestModule.ModuleVersionId != DalamudMvid || assembly.GetName().Name != "Dalamud") return false;
            Type Type(string name) => assembly.GetType(name,true,false)!;
            var provider=Type("Dalamud.Hooking.Internal.GameInteropProviderPluginScoped");
            var local=Type("Dalamud.Plugin.Internal.Types.LocalPlugin");
            var hookInterface=Type("Dalamud.Hooking.IDalamudHook");
            var exception=Type("Dalamud.Hooking.Internal.Verification.HookVerificationException");
            var collection=Type("Dalamud.Utility.WeakConcurrentCollection`1").MakeGenericType(hookInterface);
            var plugin=provider.GetField("plugin",Declared)!;
            var tracked=provider.GetField("trackedHooks",Declared)!;
            var pluginAssembly=local.GetProperty("Assembly",Declared)!;
            if (!plugin.IsPrivate || plugin.IsStatic || !plugin.IsInitOnly || plugin.FieldType != local
                || !tracked.IsPrivate || tracked.IsStatic || !tracked.IsInitOnly || tracked.FieldType != collection
                || pluginAssembly.PropertyType != typeof(Assembly) || pluginAssembly.GetMethod is not { IsPublic:true, IsStatic:false }
                || pluginAssembly.GetIndexParameters().Length != 0) return false;
            var add=collection.GetMethods(Declared).Single(m=>m.Name=="Add");
            if (!Method(add,false,typeof(void),hookInterface) || !add.IsPublic || add.IsGenericMethod) return false;
            var verify=Type("Dalamud.Hooking.Internal.Verification.HookVerifier").GetMethods(Declared).Single(m=>m.Name=="TryVerify");
            if (!verify.IsPublic || !verify.IsGenericMethodDefinition || verify.GetGenericArguments().Length != 1
                || !verify.GetGenericArguments()[0].GetGenericParameterConstraints().SequenceEqual(new[]{typeof(Delegate)})
                || !Method(verify,true,typeof(bool),typeof(nint),typeof(Assembly),exception.MakeArrayType().MakeByRefType())
                || !verify.GetParameters()[2].IsOut) return false;
            var follow=Type("Dalamud.Hooking.Internal.HookManager").GetMethods(Declared).Single(m=>m.Name=="FollowJmp");
            if (!follow.IsAssembly || follow.IsGenericMethod || !Method(follow,true,typeof(nint),typeof(nint))) return false;
            var safety=Type("Dalamud.Hooking.Internal.SafetyHookHook`1");
            if (!safety.IsGenericTypeDefinition || safety.GetGenericArguments().Length != 1
                || !safety.GetGenericArguments()[0].GetGenericParameterConstraints().SequenceEqual(new[]{typeof(Delegate)})) return false;
            var closed=safety.MakeGenericType(typeof(Action));
            var constructor=closed.GetConstructors(Declared).Single();
            if (!constructor.IsAssembly || constructor.IsStatic || !constructor.GetParameters().Select(p=>p.ParameterType)
                .SequenceEqual(new[]{typeof(nint),typeof(Action),typeof(Assembly)})) return false;
            if (closed.BaseType != Type("Dalamud.Hooking.Hook`1").MakeGenericType(typeof(Action))) return false;
            foreach(var pair in new[]{("Original",typeof(Action)),("IsEnabled",typeof(bool)),("BackendName",typeof(string))})
            {
                var property=closed.GetProperty(pair.Item1,Declared)!;
                if(property.PropertyType != pair.Item2 || property.GetMethod is not { IsStatic:false, IsPublic:true }
                    || property.GetIndexParameters().Length != 0) return false;
            }
            members=new(assembly,provider,plugin,pluginAssembly,tracked,add,verify,follow,safety);
            return true;
        }
        catch(Exception error) when(error is not OutOfMemoryException) { return false; }
    }
    private static bool Method(MethodInfo method,bool isStatic,Type returns,params Type[] parameters)
        => method.IsStatic == isStatic && method.ReturnType == returns
            && method.GetParameters().Select(p=>p.ParameterType).SequenceEqual(parameters);
}
