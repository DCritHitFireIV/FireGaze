using System.Reflection;

namespace FireGaze.RepoAudit;

/// <summary>
///     插件界面入口（主界面 / 设置界面）的读取与打开。
/// </summary>
/// <remarks>
///     卫月的插件对象分两层：插件管理器里的 <c>LocalPlugin</c>（FireGaze 的 <see cref="InstalledPluginEntry.RawPlugin" />
///     拿到的就是这个），以及给第三方看的 <c>IExposedPlugin</c> 包装器。界面入口记在插件自己的
///     <c>DalamudInterface.LocalUiBuilder</c>（<see cref="Dalamud.Interface.UiBuilder" />）上——
///     官方插件安装器的「打开 / 设置」按钮走的就是这条路：
///     <code>
///       plugin.DalamudInterface?.LocalUiBuilder.HasMainUi
///       plugin.DalamudInterface?.LocalUiBuilder.HasConfigUi
///       plugin.DalamudInterface.LocalUiBuilder.OpenMain() / OpenConfig()
///     </code>
///     其中 <c>LocalUiBuilder</c> / <c>HasMainUi</c> / <c>OpenMain</c> 都是 internal，所以这里用反射；
///     反射一律走 <c>DeclaredOnly</c> + 自己沿 <c>BaseType</c> 找（2026-09-28 GlamBridge 血教训：
///     子类上找私有成员必须这么找，否则开发版插件——<c>LocalDevPlugin : LocalPlugin</c>——找不到）。
///     打开用卫月自己的 <c>OpenMain()/OpenConfig()</c>（内部 <c>InvokeSafely</c> 包了异常），不直接调委托。
/// </remarks>
internal static class PluginUiBridge
{
    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>
    ///     这个插件注册了哪种入口。拿不到（没加载 / 卫月改结构）就都算没有。
    /// </summary>
    public static (bool HasMain, bool HasConfig) Probe(object? rawPlugin)
    {
        var builder = GetUIBuilder(rawPlugin);
        return builder is null
            ? (false, false)
            : (ReadBool(builder, "HasMainUi"), ReadBool(builder, "HasConfigUi"));
    }

    /// <summary>
    ///     打开插件入口：有主界面开主界面，没有就开设置界面；两者都没有返回 false（调用方什么都不做，也不报错）。
    /// </summary>
    public static bool Open(object? rawPlugin)
    {
        var builder = GetUIBuilder(rawPlugin);
        if (builder is null)
        {
            return false;
        }

        if (ReadBool(builder, "HasMainUi"))
        {
            return Invoke(builder, "OpenMain");
        }

        return ReadBool(builder, "HasConfigUi") && Invoke(builder, "OpenConfig");
    }

    private static object? GetUIBuilder(object? rawPlugin)
    {
        var iface = ReadProperty(rawPlugin, "DalamudInterface");
        return iface is null ? null : ReadProperty(iface, "LocalUiBuilder");
    }

    private static object? ReadProperty(object? target, string name)
    {
        if (target is null)
        {
            return null;
        }

        var property = FindProperty(target.GetType(), name);
        return property?.GetValue(target);
    }

    private static bool ReadBool(object target, string name)
    {
        var property = FindProperty(target.GetType(), name);
        return property?.GetValue(target) as bool? ?? false;
    }

    private static bool Invoke(object target, string name)
    {
        var method = FindMethod(target.GetType(), name);
        if (method is null)
        {
            return false;
        }

        method.Invoke(target, null);
        return true;
    }

    /// <summary>沿继承链按 DeclaredOnly 找属性（私有 / internal 成员在子类上不参与继承查找）。</summary>
    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(name, Flags);
            if (property is not null)
            {
                return property;
            }
        }

        return null;
    }

    /// <summary>沿继承链按 DeclaredOnly 找无参方法。</summary>
    private static MethodInfo? FindMethod(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var method = current.GetMethod(name, Flags, null, Type.EmptyTypes, null);
            if (method is not null)
            {
                return method;
            }
        }

        return null;
    }
}
