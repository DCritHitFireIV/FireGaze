namespace FireGaze.Translate;

/// <summary>投稿发送的结果严重度（界面据此上色）。</summary>
internal enum ContributeSendSeverity
{
    Info,
    Good,
    Bad,
}

/// <summary>投稿发送结果：一句话（可能带导出文件路径）与严重度。</summary>
internal readonly record struct ContributeSendResult(ContributeSendSeverity Severity, string Message);

/// <summary>
///     插件界面文字译文投稿的发送：**中继优先**（一步、匿名、不需要 GitHub 账号），
///     中继不可用 / 正文太大时才回退到「填好内容打开 GitHub 提交页」。
/// </summary>
/// <remarks>
///     列表行「一键上传」与编辑器「提交人工译文到公共库…」共用这一条通道
///     （2026-10-02 用户发现两条不一致：编辑器以前直接开 GitHub 页，标题还缺 Worker 校验用的 `contributions` 关键词）。
/// </remarks>
internal static class ContributeSender
{
    /// <summary>正文能直接塞进 GitHub 提交页 URL 的上限（超过就导出文件当附件）。</summary>
    private const int GitHubURLBodyLimit = 6000;

    /// <param name="storeDirectory">译文包目录（<c>uitrans</c>）：导出的大包放它的同级 <c>contributions/</c>。</param>
    public static async Task<ContributeSendResult> SubmitAsync(
        string internalName,
        int total,
        string title,
        string body,
        string storeDirectory)
    {
        var relayError = string.Empty;
        if (body.Length <= ContributeRelay.MaxBody)
        {
            var (ok, message) = await ContributeRelay.TrySubmitAsync(title, body).ConfigureAwait(false);
            if (ok)
            {
                return new ContributeSendResult(ContributeSendSeverity.Good, $"已上传 {total} 条译文到社区：{message}。感谢！");
            }

            relayError = message;
        }

        // 回退 1：正文放得进 URL —— 直接填好内容打开 GitHub 提交页
        if (body.Length <= GitHubURLBodyLimit)
        {
            OpenIssue(ContributeRelay.BuildIssueURL(title, body));
            return relayError.Length > 0
                ? new ContributeSendResult(ContributeSendSeverity.Bad, $"上传没成功：{relayError}。已打开 GitHub 提交页，按 Submit 即可提交。")
                : new ContributeSendResult(ContributeSendSeverity.Info, "译文较多，已打开 GitHub 提交页，按 Submit 即可提交。");
        }

        // 回退 2：正文太长，URL 会被截断 —— 导出文件，让玩家拖进附件
        var file = SavePayload(internalName, title, body, storeDirectory);
        var shortBody = $"### FireGaze contributions · 插件界面文字译文贡献\n\n- 插件：`{internalName}`\n- 条数：{total}\n\n条目较多，正文放不下；投稿文件见本 issue 的附件。\n";
        var reason = relayError.Length > 0 ? $"上传没成功：{relayError}；" : string.Empty;
        var text = file is null
            ? $"{reason}条目较多（{total} 条），正文放不下 GitHub 的提交页，导出投稿文件也失败了。"
            : $"{reason}条目较多（{total} 条），正文放不下 GitHub 的提交页；已导出投稿文件：\n{file}\n请在打开的页面里把它拖进输入框作为附件，再按 Submit。";
        OpenIssue(ContributeRelay.BuildIssueURL(title, shortBody));
        return new ContributeSendResult(ContributeSendSeverity.Bad, text);
    }

    private static string? SavePayload(string internalName, string title, string body, string storeDirectory)
    {
        try
        {
            var parent = Path.GetDirectoryName(storeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var directory = Path.Combine(parent ?? storeDirectory, "contributions");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"uit-{internalName}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(file, title + "\n\n" + body + "\n", new System.Text.UTF8Encoding(false));
            return file;
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 导出投稿文件失败");
            return null;
        }
    }

    private static void OpenIssue(string url)
    {
        try
        {
            Dalamud.Utility.Util.OpenLink(url);
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 打开 GitHub 提交页失败");
        }
    }
}
