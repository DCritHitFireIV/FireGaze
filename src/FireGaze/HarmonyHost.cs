using System.Reflection;
using System.Runtime.Loader;

namespace FireGaze;

/// <summary>
/// 通过反射使用 Harmony（0Harmony.dll 不参与编译，插件程序集不引用它）。
///
/// 为什么这么绕：卫月用 <c>IsUnloadable + LoadInMemory</c> 的 collectible ALC 加载插件，
/// 若 0Harmony 作为「插件私有依赖」被加载进那个 ALC，Harmony 自建内部动态程序集时会
/// 直接抛 <c>FileLoadException: Could not load file or assembly '0Harmony' … 0x80131515</c>。
/// 正确做法（ProxyPlugin 同款）：把 0Harmony.dll 放在插件目录的 <c>libs/</c> 子目录里
/// （卫月不会把它当私有依赖），启动时手动 <see cref="AssemblyLoadContext.LoadFromAssemblyPath"/>
/// 进默认 ALC，再反射调用它。
/// </summary>
internal sealed class HarmonyHost
{
    private const string HarmonyAssemblyName = "0Harmony";

    private readonly object instance;
    private readonly Type harmonyType;
    private readonly Type harmonyMethodType;
    private readonly MethodInfo patch;
    private readonly MethodInfo? unpatchAll;

    private HarmonyHost(object instance, Type harmonyType, Type harmonyMethodType, MethodInfo patch, MethodInfo? unpatchAll)
    {
        this.instance = instance;
        this.harmonyType = harmonyType;
        this.harmonyMethodType = harmonyMethodType;
        this.patch = patch;
        this.unpatchAll = unpatchAll;
    }

    /// <summary>创建实例；失败时返回 null 并给出原因。</summary>
    public static HarmonyHost? Create(string id, string pluginDirectory, out string? error)
    {
        var assembly = ResolveHarmonyAssembly(pluginDirectory, out error);
        if (assembly is null)
        {
            return null;
        }

        try
        {
            var harmonyType = assembly.GetType("HarmonyLib.Harmony", throwOnError: true)!;
            var harmonyMethodType = assembly.GetType("HarmonyLib.HarmonyMethod", throwOnError: true)!;

            var instance = Activator.CreateInstance(harmonyType, id)
                           ?? throw new InvalidOperationException("Harmony 实例创建失败");

            var patch = harmonyType.GetMethod(
                            "Patch",
                            BindingFlags.Instance | BindingFlags.Public,
                            null,
                            [typeof(MethodBase), harmonyMethodType, harmonyMethodType, harmonyMethodType, harmonyMethodType],
                            null)
                        ?? throw new MissingMethodException("HarmonyLib.Harmony.Patch(MethodBase, HarmonyMethod, HarmonyMethod, HarmonyMethod, HarmonyMethod)");

            var unpatchAll = harmonyType.GetMethod(
                "UnpatchAll",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                [typeof(string)],
                null);

            error = null;
            return new HarmonyHost(instance, harmonyType, harmonyMethodType, patch, unpatchAll);
        }
        catch (Exception e)
        {
            error = $"Harmony 初始化失败：{e.Message}";
            return null;
        }
    }

    /// <summary>给某个方法挂前缀钩子。</summary>
    public void PatchPrefix(MethodBase target, MethodInfo prefix)
    {
        var harmonyMethod = this.CreateHarmonyMethod(prefix);
        this.patch.Invoke(this.instance, [target, harmonyMethod, null, null, null]);
    }

    /// <summary>取消本实例挂的全部钩子。</summary>
    public void Unpatch(string id) => this.unpatchAll?.Invoke(this.instance, [id]);

    private object CreateHarmonyMethod(MethodInfo method)
    {
        var ctor = this.harmonyMethodType
            .GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 1 &&
                                 c.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(MethodInfo)));

        if (ctor is null)
        {
            throw new MissingMethodException("HarmonyLib.HarmonyMethod(MethodInfo)");
        }

        return ctor.Invoke([method]);
    }

    private static Assembly? ResolveHarmonyAssembly(string pluginDirectory, out string? error)
    {
        error = null;

        // 1) 默认 ALC 里已经有 0Harmony（别的插件加载过）→ 直接复用。
        //    这样不新增任何全局副作用（重要：其他插件的自保护/加固器对默认 ALC 的变化很敏感）。
        foreach (var loaded in AssemblyLoadContext.Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, HarmonyAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return loaded;
            }
        }

        var path = Path.Combine(pluginDirectory, "libs", HarmonyAssemblyName + ".dll");
        if (!File.Exists(path))
        {
            error = $"找不到 {path}";
            return null;
        }

        // 2) 加载进我们自己的隔离 ALC（非可回收），不污染默认 ALC。
        //    与插件自身的 collectible / in-memory ALC 不同：这里是从磁盘加载的普通 ALC，
        //    Harmony 自建动态程序集不会有 0x80131515 问题，也不会改变其他插件看到的环境。
        try
        {
            var context = new AssemblyLoadContext("FireGaze.Harmony", isCollectible: false);
            context.Resolving += (ctx, name) =>
                string.Equals(name.Name, HarmonyAssemblyName, StringComparison.OrdinalIgnoreCase)
                    ? ctx.LoadFromAssemblyPath(path)
                    : null;

            var assembly = context.LoadFromAssemblyPath(path);
            HarmonyContext = context;   // 保持引用
            return assembly;
        }
        catch (Exception e)
        {
            error = $"加载 {path} 失败：{e.Message}";
            return null;
        }
    }

    /// <summary>我们自己的 0Harmony ALC（保持引用，防被回收；非可回收 ALC 本身也不会被回收）。</summary>
    private static AssemblyLoadContext? HarmonyContext { get; set; }
}
