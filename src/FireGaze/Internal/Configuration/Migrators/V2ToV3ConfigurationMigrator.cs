namespace FireGaze.Internal.Configuration.Migrators;

/// <summary>
///     2 → 3：安装器的两项功能（记住浏览位置 / 拦住自动刷新）改为默认关，由用户自己打开。
/// </summary>
internal sealed class V2ToV3ConfigurationMigrator : ConfigMigratorBase
{
    public override int FromVersion => 2;

    public override int ToVersion => 3;

    public override void Migrate(global::FireGaze.Configuration config)
    {
        config.RememberListScroll = false;
        config.BlockInstallerAutoRefresh = false;
        config.ListScrollY = null;
    }
}
