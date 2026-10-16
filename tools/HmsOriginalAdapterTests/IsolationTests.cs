using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;

internal static class HmsIsolationTests
{
    private static Harmony? active;
    private static MethodBase[] methods = [];
    private static int otherCalls;
    private static Type? Isolation => typeof(HmsIsolationTests).Assembly.GetType("FireGaze.LocalPreview.Compatibility.HmsHarmonyIsolation");

    public static void DisposeDuringDetach() => AcrossVersions(true, false, false);
    public static void DetachBeforeDispose() => AcrossVersions(false, true, false);
    public static void GenericMembers() => AcrossVersions(false, false, true);
    public static bool HasNativeDetour(MethodBase method)
    {
        var detours=Detours();
        lock(detours) return detours.Contains(method);
    }
    private static void AcrossVersions(bool insideDispose, bool earlyDetach, bool members)
    {
        foreach(var label in new[]{"Dev", "Release", "Release"})
        {
            var context = new AssemblyLoadContext("HMS-isolation-"+Guid.NewGuid().ToString("N"), true);
            try
            {
                using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory,"Fixture"+label,"HMSync.dll"));
                var assembly = context.LoadFromStream(stream);
                var type = assembly.GetType("HMSync.HMSyncPlugin",true)!;
                var instance = Activator.CreateInstance(type)!;
                active = new Harmony("FireGaze.LocalPreview.HmsIsolation.Test."+Guid.NewGuid().ToString("N"));
                methods = members ? new[]{type.GetMethod("GenericMembers")!,type.GetMethod("NullableMembers")!} : new[]{type.GetMethod("Dispose")!};
                try
                {
                    foreach(var method in methods)
                        active.Patch(method, new HarmonyMethod(typeof(HmsIsolationTests).GetMethod(insideDispose?nameof(DetachPrefix):nameof(Prefix))!),
                            transpiler:new HarmonyMethod(typeof(HmsIsolationTests).GetMethod(nameof(SafeTranspiler))!));
                    if(earlyDetach) Detach();
                    if(members)
                    {
                        Equal(methods[0].Invoke(instance,null),8);
                        Equal(methods[1].Invoke(instance,null),7);
                    }
                    else
                    {
                        methods[0].Invoke(instance,null);
                        Equal(type.GetField("DisposeCalls")!.GetValue(instance),1);
                        var relay=type.GetField("Relay")!.GetValue(instance)!;
                        Equal(relay.GetType().GetProperty("Count")!.GetValue(relay),0);
                        // A later call must also execute the genuine method exactly once.
                        methods[0].Invoke(instance,null);
                        Equal(type.GetField("DisposeCalls")!.GetValue(instance),2);
                    }
                }
                finally { Detach(); methods=[]; active=null; }
            }
            finally { context.Unload(); }
        }
    }

    public static void OtherOwnerSurvives()
    {
        WithTouch((method,instance) => {
            var other=new Harmony("FireGaze.HmsIsolation.Other."+Guid.NewGuid().ToString("N"));
            try
            {
                otherCalls=0;
                other.Patch(method,new HarmonyMethod(typeof(HmsIsolationTests).GetMethod(nameof(OtherPrefix))!));
                active!.Patch(method,new HarmonyMethod(typeof(HmsIsolationTests).GetMethod(nameof(Prefix))!));
                Detach();
                if(Harmony.GetPatchInfo(method)?.Owners.Contains(other.Id)!=true) throw new Exception("another owner's patch was removed");
                Equal(method.Invoke(instance,null),42);
                Equal(otherCalls,1);
                if(Detours()[method]==null) throw new Exception("another owner's native detour was removed");
            }
            finally { other.UnpatchAll(other.Id); }
        });
    }

    public static void MissingOwnerIsUntouched()
    {
        WithTouch((method,instance) => {
            active!.Patch(method,new HarmonyMethod(typeof(HmsIsolationTests).GetMethod(nameof(Prefix))!));
            active.UnpatchAll(active.Id);
            var before=Detours()[method];
            Detach();
            if(!ReferenceEquals(before,Detours()[method])) throw new Exception("an unowned native detour was changed");
            Equal(method.Invoke(instance,null),42);
        });
    }

    private static void WithTouch(Action<MethodBase,object> test)
    {
        var context=new AssemblyLoadContext("HMS-isolation-owner-"+Guid.NewGuid().ToString("N"),true);
        try
        {
            using var stream=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"FixtureRelease","HMSync.dll"));
            var type=context.LoadFromStream(stream).GetType("HMSync.HMSyncPlugin",true)!;
            var method=type.GetMethod("Touch")!;
            active=new Harmony("FireGaze.HmsIsolation.Owned."+Guid.NewGuid().ToString("N"));
            methods=[method];
            try { test(method,Activator.CreateInstance(type)!); }
            finally { Detach(); active=null; methods=[]; }
        }
        finally { context.Unload(); }
    }

    public static void Prefix() { }
    public static void OtherPrefix() => otherCalls++;
    public static void DetachPrefix() => Detach();
    public static IEnumerable<CodeInstruction> SafeTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
        => Isolation?.GetMethod("PreserveGenericMembers")?.Invoke(null,new object[]{instructions,__originalMethod}) as IEnumerable<CodeInstruction> ?? instructions;
    private static void Detach()
    {
        if(active==null) return;
        if(Isolation?.GetMethod("Detach") is { } detach) detach.Invoke(null,new object[]{active,methods});
        else active.UnpatchAll(active.Id);
    }
    private static IDictionary Detours() => (IDictionary)typeof(Harmony).Assembly.GetType("HarmonyLib.PatchTools",true)!.GetField("detours",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    private static void Equal(object? actual,object expected) { if(!Equals(actual,expected)) throw new Exception($"expected {expected}; got {actual}"); }
}
