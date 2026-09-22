using System.Collections.Frozen;
using FireGaze.Internal.Configuration.Migrators;

namespace FireGaze.Internal.Configuration;

/// <summary>
///     配置版本迁移调度：只做单步迁移（N → N+1），缺迁移器直接抛错、不静默兜底。
/// </summary>
internal static class ConfigurationMigrator
{
    internal const int LatestVersion = 3;

    private static readonly FrozenDictionary<int, ConfigMigratorBase> Migrators =
        new ConfigMigratorBase[]
        {
            new V1ToV2ConfigurationMigrator(),
            new V2ToV3ConfigurationMigrator(),
        }.ToFrozenDictionary(migrator => migrator.FromVersion);

    /// <summary>
    ///     把配置迁到最新版本；返回**迁移前**的版本（调用方据此决定要不要提示「默认值已调整」）。
    ///     只推进版本与修改配置，不负责保存。
    /// </summary>
    internal static int Migrate(global::FireGaze.Configuration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var fromVersion = config.Version;

        // 0（或更早）的配置从未出现过：按 1 处理，避免历史脏值把插件拖崩
        if (config.Version < 1)
        {
            config.Version = 1;
        }

        while (config.Version < LatestVersion)
        {
            if (!Migrators.TryGetValue(config.Version, out var migrator))
            {
                throw new InvalidOperationException($"不支持的配置版本: {config.Version}");
            }

            migrator.Migrate(config);
            config.Version = migrator.ToVersion;
        }

        return fromVersion;
    }
}
