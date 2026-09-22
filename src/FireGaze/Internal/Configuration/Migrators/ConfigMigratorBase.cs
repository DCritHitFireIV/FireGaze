namespace FireGaze.Internal.Configuration.Migrators;

/// <summary>
///     单步配置迁移器基类：一次只处理一个版本台阶（N → N+1）。
/// </summary>
internal abstract class ConfigMigratorBase
{
    /// <summary>
    ///     迁移前版本。
    /// </summary>
    public abstract int FromVersion { get; }

    /// <summary>
    ///     迁移后版本。
    /// </summary>
    public abstract int ToVersion { get; }

    /// <summary>
    ///     就地修改配置（调用方负责推进版本号与保存）。
    /// </summary>
    public abstract void Migrate(global::FireGaze.Configuration config);
}
