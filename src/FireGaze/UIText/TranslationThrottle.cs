namespace FireGaze.UIText;

/// <summary>
///     免费接口的限流策略（纯逻辑，方便离线测）：
///     遇到 429 / 5xx 时该退避多久、连续几次之后该把这条通道冷却、两条都冷却时该不该整批停下。
/// </summary>
internal static class TranslationThrottle
{
    /// <summary>
    ///     每发一条请求之间的礼让间隔（毫秒）：免费接口是按 IP 限流的，压太紧必炸。
    /// </summary>
    public const int PerRequestDelayMilliseconds = 800;

    /// <summary>
    ///     单条请求最多重试几次（含第一次）。
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>
    ///     连续被限流到这个次数，就把这条通道冷却一段时间。
    /// </summary>
    public const int RateLimitStreakForCooldown = 5;

    /// <summary>
    ///     第 <paramref name="attempt" /> 次失败后的重试等待（毫秒）；attempt 从 0 开始。
    /// </summary>
    public static int RetryDelayMilliseconds(int attempt) => attempt switch
    {
        0 => 2_000,
        1 => 5_000,
        _ => 10_000,
    };

    /// <summary>
    ///     连续被限流 <paramref name="streak" /> 次后，冷却多少分钟。
    /// </summary>
    public static int CooldownMinutes(int streak) => streak switch
    {
        <= RateLimitStreakForCooldown => 0,
        <= RateLimitStreakForCooldown + 3 => 5,
        <= RateLimitStreakForCooldown + 8 => 15,
        _ => 60,
    };

    /// <summary>
    ///     这个 HTTP 状态码算不算「被限流 / 服务端暂时不可用」。
    /// </summary>
    public static bool IsRetryableStatus(int statusCode) => statusCode is 429 or 500 or 502 or 503 or 504;

    /// <summary>
    ///     两条通道都不可用时的整批中止文案。
    /// </summary>
    public static string BothProvidersBlocked =>
        "免费接口都被限流了：等一下再试，或者改用「大模型（自填 key）」通道（免费接口本身也有每日额度，翻大插件不够用）。";
}
