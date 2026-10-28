namespace FireGaze.LocalPreview.Interaction;

/// <summary>
/// Detached player-facing values for a trial catalog. The drawing layer should not
/// rebuild copy or infer empty-state meaning from the live runtime arrays.
/// </summary>
public sealed record TrialCatalogPresentation(
    TrialPhase Phase,
    string StatusText,
    string CountText,
    string EmptyText,
    bool HasResults)
{
    public static TrialCatalogPresentation Create(
        TrialPresentation trial, int totalCount, int visibleCount, string? query, string label = "坐骑")
    {
        ArgumentNullException.ThrowIfNull(trial);
        var total = Math.Max(0, totalCount);
        var visible = Math.Clamp(visibleCount, 0, total);
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var status = trial.Phase switch
        {
            TrialPhase.Ready => string.Empty,
            TrialPhase.Active => trial.StatusText,
            TrialPhase.Switching => "正在切换",
            TrialPhase.Stopping => "正在结束试用",
            _ => trial.StatusText,
        };
        var count = hasQuery ? $"匹配 {visible} / {total}" : $"{label} {total}";
        var empty = hasQuery ? "没有找到匹配项目，试试其他名称" : $"暂无可试用{label}";
        return new(trial.Phase, status, count, empty, visible > 0);
    }
}
