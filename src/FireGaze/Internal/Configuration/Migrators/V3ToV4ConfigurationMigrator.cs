namespace FireGaze.Internal.Configuration.Migrators;

/// <summary>
///     3 → 4：翻译通道默认值从「免费·自动」换成「FireGaze 公共彩云」——2026-10-03 起
///     维护者把自己申请的彩云小译额度放进中继给大家用（额度用完 / 密钥失效前免费），
///     比免 key 接口快得多。
/// </summary>
/// <remarks>
///     2026-10-03 用户定：**走免费通道的一律换**（含用户在首次弹窗里亲眼选过「免费 Google / MyMemory」
///     或「免费·自动」的——他们对免费没有绑定关系，只是没得选）；用了大模型 / 自己的彩云 token / DeepL
///     的一律保留——那是用户自己配的 key，换掉反而把人家配好的东西丢了。
/// </remarks>
internal sealed class V3ToV4ConfigurationMigrator : ConfigMigratorBase
{
    public override int FromVersion => 3;

    public override int ToVersion => 4;

    public override void Migrate(global::FireGaze.Configuration config)
    {
        switch ((config.UITextChannel ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "":
            case "auto":
            case "google":
            case "mymemory":
                config.UITextChannel = "public-caiyun";
                break;

            // "llm" / "caiyun" / "deepl"：保留用户自己的选择（都是自己配的 key）。
        }
    }
}
