using FireGaze.Internal;

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
    /// <summary>拼好的提交页 URL 上限：再长会被浏览器/平台截断（简介投稿通道同款上限，2026-10-03 C-07 修正：以前按未转义长度估，中文正文转义后会膨胀到 9 倍）。</summary>
    private const int MaxIssueURLLength = 20000;

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

        var relayNote = relayError.Length > 0
            ? $"上传没成功：{relayError}。"
            : $"正文很大（{body.Length:N0} 字符），超过了中继上限。";

        // 回退 1：正文（转义后）塞得进 URL —— 打开填好内容的 GitHub 提交页，玩家按一下 Submit 就行
        var url = ContributeRelay.BuildIssueURL(title, body);
        if (url.Length <= MaxIssueURLLength)
        {
            return OpenIssue(url, relayNote + "已替你打开提交页，按 Submit 即可提交。");
        }

        // 回退 2：正文太长，URL 塞不下 —— 导出文件，让玩家把内容粘贴进提交页正文
        //（2026-10-03 C-03：“拖附件”没有接收端——工作流只读正文，从不读附件；改成可完成的粘贴流程）
        var file = SavePayload(internalName, title, body, storeDirectory);
        var shortBody = $"### FireGaze contributions · 插件界面文字译文贡献\n\n- 插件：`{internalName}`\n- 条数：{total}\n\n正文较长，放不进提交页链接；投稿内容已经导出成文件，请把**文件内容**（含 JSON 代码块）整段粘贴到这里再提交。\n";
        if (file is not null)
        {
            shortBody += $"\n> 文件：`{file}`\n";
        }

        return OpenIssue(
            ContributeRelay.BuildIssueURL(title, shortBody),
            relayNote + $"条目较多（{total} 条），正文放不进提交页链接。" + (file is null
                ? "导出投稿文件也失败了——请先把译文留在本地，等中继恢复后再传。"
                : $"\n投稿内容已导出：{file}\n请打开这个文件，把里面的内容整段粘贴到打开页面的正文框里，再按 Submit。"));
    }

    private static string? SavePayload(string internalName, string title, string body, string storeDirectory)
    {
        try
        {
            var parent = Path.GetDirectoryName(storeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var directory = Path.Combine(parent ?? storeDirectory, "contributions");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"uit-{internalName}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            AtomicFile.WriteAllText(file, title + "\n\n" + body + "\n", new System.Text.UTF8Encoding(false));
            return file;
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 导出投稿文件失败");
            return null;
        }
    }

    /// <summary>打开提交页；打不开就把「怎么手动提交」说清楚（绝不谎报「已替你打开」）。</summary>
    private static ContributeSendResult OpenIssue(string url, string message)
    {
        try
        {
            Dalamud.Utility.Util.OpenLink(url);
            return new ContributeSendResult(ContributeSendSeverity.Bad, message);
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 打开 GitHub 提交页失败");
            return new ContributeSendResult(
                ContributeSendSeverity.Bad,
                message + "\n（没能自动打开浏览器：请手动到 GitHub 仓库新建 issue，把内容粘贴进去。）");
        }
    }
}
