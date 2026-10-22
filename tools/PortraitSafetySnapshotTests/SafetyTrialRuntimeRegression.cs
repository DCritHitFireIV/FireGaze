using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Dalamud.Plugin.Services;

internal static class SafetyTrialRuntimeRegression
{
    internal static void Run(Action<bool, string> check)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "src", "FireGaze", "FireGaze.csproj"))) repo = repo.Parent;
        var assemblyPath = repo is null ? "" : Path.Combine(repo.FullName, "src", "FireGaze", "bin", "Release", "FireGaze.dll");
        if (!File.Exists(assemblyPath)) { check(false, "built production assembly is required for actual trial lifecycle regression"); return; }
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN", "addon", "Hooks", "dev");
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (var folder in new[] { Path.GetDirectoryName(assemblyPath)!, sdk })
            {
                var candidate = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
            }
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            var featureType = assembly.GetType("FireGaze.LocalPreview.LocalPreviewFeature", true)!;
            var runtimeType = assembly.GetType("FireGaze.LocalPreview.Runtime.ActorPreviewRuntime", true)!;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var read = featureType.GetMethod("ReadTrialSafetyState", flags)!;
            var feature = RuntimeHelpers.GetUninitializedObject(featureType);
            var framework = DispatchProxy.Create<IFramework, SafetyReadonlyFrameworkProxy>();
            var proxy = (SafetyReadonlyFrameworkProxy)(object)framework;
            featureType.GetField("Framework", flags)!.SetValue(feature, framework);
            void SetFeature(string name, object? value) => featureType.GetField(name, flags)!.SetValue(feature, value);
            void SetRuntime(object instance, string name, object value) => runtimeType.GetField(name, flags)!.SetValue(instance, value);
            (bool Known, bool Active, long Revision) Sample()
            {
                var value = read.Invoke(feature, null)!;
                var type = value.GetType();
                return ((bool)type.GetProperty("Known")!.GetValue(value)!, (bool)type.GetProperty("Active")!.GetValue(value)!,
                    (long)type.GetProperty("Revision")!.GetValue(value)!);
            }
            object NewRuntime(long revision)
            {
                var instance = RuntimeHelpers.GetUninitializedObject(runtimeType);
                var effects = runtimeType.GetField("effects", flags)!;
                effects.SetValue(instance, Activator.CreateInstance(effects.FieldType));
                SetRuntime(instance, "revision", revision);
                return instance;
            }
            check(!Sample().Known, "actual uninitialized sibling accessor reports unknown without a UI cache");
            SetFeature("initialized", true);
            check(!Sample().Known, "actual initialized but missing sibling runtime remains unknown");
            var first = NewRuntime(7);
            SetFeature("runtime", first);
            check(Sample() == (true, false, 7), "actual runtime effect set and revision are read freshly on Framework");
            var dictionary = (IDictionary)runtimeType.GetField("effects", flags)!.GetValue(first)!;
            var types = dictionary.GetType().GenericTypeArguments;
            var mount = Enum.ToObject(types[0], 0);
            dictionary.Add(mount, Activator.CreateInstance(types[1], mount, (ushort)1, (byte)0));
            check(Sample() == (true, true, 7), "actual active runtime is visible even when uiActiveEffects is absent");
            SetRuntime(first, "disposed", true);
            check(!Sample().Known, "actual disposed runtime cannot be mistaken for current no-trial evidence");
            var replacement = NewRuntime(19);
            SetFeature("runtime", replacement);
            check(Sample() == (true, false, 19), "actual sibling replacement reads the new runtime without retaining old effects or revision");
            proxy.Current = false;
            check(!Sample().Known, "actual sibling accessor refuses non-Framework reads");
            proxy.Current = true;
            SetFeature("disposed", true);
            check(!Sample().Known, "actual disposed sibling feature remains unknown");
            check(proxy.OtherCalls == 0, "actual sibling sampling invokes no framework action or game service");
        }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
    }
}

public class SafetyReadonlyFrameworkProxy : DispatchProxy
{
    public bool Current = true;
    public int OtherCalls;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "get_IsInFrameworkUpdateThread") return Current;
        OtherCalls++;
        throw new InvalidOperationException("No action or game service is available to this readonly fixture");
    }
}
