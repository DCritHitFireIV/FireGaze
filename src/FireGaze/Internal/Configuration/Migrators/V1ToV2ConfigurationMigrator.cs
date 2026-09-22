namespace FireGaze.Internal.Configuration.Migrators;

/// <summary>
///     1 → 2：安装器增强（位置记忆 / 拦住自动刷新）上线时，汉化默认值从「开」改为「关」。
/// </summary>
internal sealed class V1ToV2ConfigurationMigrator : ConfigMigratorBase
{
    public override int FromVersion => 1;

    public override int ToVersion => 2;

    public override void Migrate(global::FireGaze.Configuration config) =>
        config.TranslateEnabled = false;
}
