namespace FireGaze.Internal.Configuration.Migrators;

/// <summary>
///     3 → 4：翻译通道默认值从「免费·自动」换成「FireGaze 公共彩云」——2026-10-03 起
///     维护者把自己申请的彩云小译额度放进中继给大家用（额度用完 / 密钥失效前免费），
///     比免 key 接口快得多。只劝「没主动选过通道」的配置改默认；选过的用户一律不动。
/// </summary>
internal sealed class V3ToV4ConfigurationMigrator : ConfigMigratorBase
{
    public override int FromVersion => 3;

    public override int ToVersion => 4;

    public override void Migrate(global::FireGaze.Configuration config)
    {
        if (config.UITextChannelChosen)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.UITextChannel)
            || string.Equals(config.UITextChannel, "auto", StringComparison.OrdinalIgnoreCase))
        {
            config.UITextChannel = "public-caiyun";
        }
    }
}
