using FireGaze.Translate;

namespace FireGaze.Discovery;

/// <summary>
///     插件发现的排序档位（用户 2026-10-06 定：每周点赞 / 推荐 / 最新更新 / 随机 / 作者名 / 插件名）。
/// </summary>
internal enum DiscoverySortMode
{
    /// <summary>本周点赞降序，同赞按插件名（默认档）。</summary>
    WeeklyLikes = 0,

    /// <summary>「从云端加进自己库」的次数降序，同数按插件名。</summary>
    Recommends = 1,

    /// <summary>插件最后更新时间降序（不知道的排最后）。</summary>
    Updated = 2,

    /// <summary>随机（由窗口给每个插件生成的乱序快照决定；「换一批」重生成）。</summary>
    Random = 3,

    /// <summary>作者名升序，没作者的排最后、同作者按插件名。</summary>
    Author = 4,

    /// <summary>插件名升序。</summary>
    Name = 5,
}

/// <summary>
///     排序需要的外部数据：点赞/加库来自中继；随机序由窗口生成快照（一次打开/一批内稳定，不随帧变）。
/// </summary>
internal sealed class DiscoverySortContext
{
    public DiscoverySortMode Mode { get; set; } = DiscoverySortMode.WeeklyLikes;

    public Dictionary<string, int> WeeklyLikes { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> Recommends { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> Shuffle { get; set; } = new(StringComparer.Ordinal);

    public int WeeklyOf(string internalName) => WeeklyLikes.TryGetValue(internalName, out var value) ? value : 0;

    public int RecommendsOf(string internalName) => Recommends.TryGetValue(internalName, out var value) ? value : 0;

    public int ShuffleOf(string internalName) => Shuffle.TryGetValue(internalName, out var value) ? value : int.MaxValue;
}

/// <summary>
///     插件发现的排序比较（纯函数，fgtest 离线可测）。
/// </summary>
internal static class DiscoverySort
{
    public static void Apply(List<TranslationIndexEntry> entries, DiscoverySortContext context)
        => entries.Sort((a, b) => Compare(a, b, context));

    public static int Compare(TranslationIndexEntry a, TranslationIndexEntry b, DiscoverySortContext context)
    {
        switch (context.Mode)
        {
            case DiscoverySortMode.WeeklyLikes:
                return DescThenName(context.WeeklyOf(a.InternalName), context.WeeklyOf(b.InternalName), a, b);
            case DiscoverySortMode.Recommends:
                return DescThenName(context.RecommendsOf(a.InternalName), context.RecommendsOf(b.InternalName), a, b);
            case DiscoverySortMode.Updated:
            {
                var left = a.Updated ?? long.MinValue;
                var right = b.Updated ?? long.MinValue;
                return left != right ? right.CompareTo(left) : NameCompare(a, b);
            }

            case DiscoverySortMode.Random:
            {
                var left = context.ShuffleOf(a.InternalName);
                var right = context.ShuffleOf(b.InternalName);
                return left != right ? left.CompareTo(right) : NameCompare(a, b);
            }

            case DiscoverySortMode.Author:
            {
                var left = string.IsNullOrWhiteSpace(a.Author) ? null : a.Author;
                var right = string.IsNullOrWhiteSpace(b.Author) ? null : b.Author;
                if (left is null && right is null)
                {
                    return NameCompare(a, b);
                }

                if (left is null)
                {
                    return 1;   // 没作者的排最后
                }

                if (right is null)
                {
                    return -1;
                }

                var byAuthor = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
                return byAuthor != 0 ? byAuthor : NameCompare(a, b);
            }

            default:
                return NameCompare(a, b);
        }
    }

    public static int NameCompare(TranslationIndexEntry a, TranslationIndexEntry b)
        => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);

    private static int DescThenName(int aCount, int bCount, TranslationIndexEntry a, TranslationIndexEntry b)
        => aCount != bCount ? bCount.CompareTo(aCount) : NameCompare(a, b);
}
