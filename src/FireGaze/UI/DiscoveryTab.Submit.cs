using Dalamud.Bindings.ImGui;
using FireGaze.Discovery;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

internal sealed partial class DiscoveryTab
{
    /// <summary>
    ///     投稿插件库：把一条仓库地址送进云端语料（收录后所有人能在发现页搜到）。
    ///     本机先做库链检测（卫月同款契约），不合格当场给理由；合格才发中继。
    /// </summary>
    private void DrawSubmitSection()
    {
        if (!ImGui.CollapsingHeader("投稿插件库###DiscoverySubmit", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        ImGui.TextWrapped("知道别的插件库？把地址发到云端语料 —— 收录后所有人能在发现页搜到，并跟着词表增量翻译。");
        ImGui.TextDisabled("需要公开可读的仓库文件（pluginmaster.json / repo.json）；官方主库不用投。");

        ImGui.SetNextItemWidth(430);
        ImGui.InputTextWithHint("###DiscoverySubmitUrl", "仓库地址…", ref submitInput, 512);
        ImGui.SameLine();
        var canSubmit = submitInput.Trim().Length > 0 && !submitBusy;
        if (!canSubmit)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(submitBusy ? "检测中…###DiscoverySubmitBtn" : "投稿###DiscoverySubmitBtn"))
        {
            StartSubmit();
        }

        if (!canSubmit)
        {
            ImGui.EndDisabled();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("先在本机把这个地址抓下来、用卫月的仓库契约检测一遍；不合格会告诉你原因。\n检测通过才发往云端，之后由云端工作流复核收录。");
        }

        ImGui.SameLine();
        ImGui.Checkbox("顺手加到我的库###DiscoverySubmitAdd", ref submitAddToLibrary);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("收录后把这条库也加进你自己的第三方插件列表（会先自动备份）。");
        }

        if (submitMessage is { Length: > 0 })
        {
            UiHelpers.ColoredWrapped(submitIsError ? UiHelpers.Bad : UiHelpers.Muted, submitMessage);
        }

        var pending = SubmittedButNotInCloud();
        if (pending.Count > 0)
        {
            ImGui.TextDisabled($"已投稿、等云端收录：{pending.Count} 条");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(string.Join("\n", pending.Take(8)));
            }
        }
    }

    /// <summary>投稿按钮：后台检测 + 提交；结果回写状态行（失败不谎报成功）。</summary>
    private void StartSubmit()
    {
        var url = submitInput.Trim();
        submitBusy = true;
        submitMessage = "正在检测这条地址…";
        submitIsError = false;
        var addToLibrary = submitAddToLibrary;
        _ = Task.Run(async () =>
        {
            try
            {
                var check = await RepoSubmitChecker.CheckAsync(url, CancellationToken.None).ConfigureAwait(false);
                if (!check.Ok)
                {
                    submitMessage = "打回：" + check.Message;
                    submitIsError = true;
                    return;
                }

                if (RepoExistsInIndex(check.NormalizedURL, onlyKnown: false))
                {
                    var canAdd = addToLibrary && !RepoExistsInIndex(check.NormalizedURL, onlyKnown: true);
                    if (canAdd)
                    {
                        submitAddPending = url;
                    }

                    submitMessage = "这条库链已经在云库里了" + (canAdd ? "；顺手加到你的库…" : string.Empty);
                    submitIsError = false;
                    return;
                }

                var (ok, message) = await DiscoveryRelay.SubmitRepoAsync(url, CancellationToken.None).ConfigureAwait(false);
                if (!ok)
                {
                    submitMessage = "投稿失败：" + message + "（地址没丢，网络好了再点一次）";
                    submitIsError = true;
                    return;
                }

                discoveryState.MarkRepoSubmitted(check.NormalizedURL ?? string.Empty);
                submitMessage = "已投稿（" + check.Message + "），等云端收录；收录后会触发一次增量翻译";
                submitIsError = false;
                submitInput = string.Empty;
                if (addToLibrary && !RepoExistsInIndex(check.NormalizedURL, onlyKnown: true))
                {
                    submitAddPending = url;
                }
            }
            catch (Exception e)
            {
                submitMessage = "投稿出错：" + e.GetType().Name;
                submitIsError = true;
            }
            finally
            {
                submitBusy = false;
            }
        });
    }

    /// <summary>UI 线程把「顺手加到我的库」执行掉（反射/config 操作不在后台做）。</summary>
    private void FlushSubmitAdd()
    {
        var url = submitAddPending;
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        submitAddPending = null;
        var added = plugin.AddThirdPartyRepository(url, out var message);
        SetStatus(added ? "已把这条件库加到你的列表" : message, !added);
        if (added)
        {
            ReportRepoAdds([url]);
        }
    }
}
