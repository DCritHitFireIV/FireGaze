using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FireGaze.Lifecycle;
using FireGaze.UIText;

namespace FireGaze.UI;

/// <summary>
///     「翻译设置」独立窗口：翻译通道、API key、灰名单口径都在这里改——
///     不放在翻译流程里，用户平时看不到它。
/// </summary>
internal sealed class UITextSettingsWindow : Window
{
    private readonly Plugin plugin;
    private string keyInput = string.Empty;
    private string messageKey = string.Empty;

    public UITextSettingsWindow(Plugin plugin)
        : base("翻译设置###FireGazeUITextSettings", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        this.Size = new System.Numerics.Vector2(660, 520);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(520, 320),
        };
    }


    /// <summary>
    ///     从「一键汉化」/ 主窗口点开时总是展开：折叠状态会被 ImGui 的 ini 记住，
    ///     点开只剩一条标题栏、看起来像「点不开」（2026-10-02 用户实测）。
    /// </summary>
    public override void OnOpen()
    {
        ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
    }

    public override void PreDraw()
    {
        var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var placement = FireGazeWindowPlacement.Prepare("FireGazeUITextSettings", new Vector2(660, 520) * scale, scale, "FireGaze");
        Size = placement.Size / scale;
        SizeCondition = ImGuiCond.Appearing;
        var maximum = ImGui.GetMainViewport().WorkSize / scale;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = Vector2.Min(new(520, 320), maximum), MaximumSize = maximum };
    }

    public override void Draw()
    {
        FireGazeWindowPlacement.Remember("FireGazeUITextSettings");
        ImGui.TextWrapped("选择「一键汉化」使用的翻译通道。各通道的可用额度、速度与费用不同。");
        ImGui.Separator();

        var config = this.plugin.Config;
        var changed = false;

        var channels = new (string Key, string Label)[]
        {
            ("public-caiyun", "FireGaze 公共彩云"),
            ("auto", "免费·自动"),
            ("caiyun", "彩云小译"),
            ("google", "Google 免 key"),
            ("mymemory", "MyMemory"),
            ("llm", "大模型（自填 key）"),
            ("deepl", "DeepL"),
        };
        for (var i = 0; i < channels.Length; i++)
        {
            if (i > 0)
            {
                // 通道多了以后一行放不下（窗口最小 520 宽）：放不下就换行，不要溢出裁剪区。
                UiHelpers.SameLineOrWrap(ImGui.GetFrameHeight() + ImGui.CalcTextSize(channels[i].Label).X, 8f);
            }

            if (ImGui.RadioButton(channels[i].Label, string.Equals(config.UITextChannel, channels[i].Key, StringComparison.Ordinal)))
            {
                if (!string.Equals(config.UITextChannel, channels[i].Key, StringComparison.Ordinal))
                {
                    config.UITextChannel = channels[i].Key;
                    // 在设置里主动选过通道，就不再在「一键汉化」时重复问一次了
                    config.UITextChannelChosen = true;
                    changed = true;
                }
            }

            if (ImGui.IsItemHovered())
            {
                var hint = ChannelHint(channels[i].Key);
                if (hint.Length > 0)
                {
                    ImGui.SetTooltip(hint);
                }
            }
        }

        if (string.Equals(config.UITextChannel, "public-caiyun", StringComparison.Ordinal))
        {
            ImGui.TextDisabled(
                "使用共享免费额度，无需填写 key。\n" +
                "不可用时会自动切换免费通道，你也可以选择其他通道。");
        }

        if (config.UITextChannel is "auto" or "google" or "mymemory")
        {
            ImGui.TextDisabled(
                "免费通道可能限流，批量翻译较慢。\n" +
                "也可选择「FireGaze 公共彩云」，或使用自己申请的「彩云小译」token。" +
                "可用额度与费用以服务商当前规则为准。");
        }

        if (config.UITextUseGlossary && !string.Equals(config.UITextChannel, "llm", StringComparison.Ordinal))
        {
            UiHelpers.ColoredWrapped(
                UiHelpers.Warn,
                "FF14 官方译名术语表只在大模型通道生效；当前通道不会用到它。");
        }

        var library = config.UITextLibraryEnabled;
        ImGui.Separator();
        ImGui.TextDisabled("通用选项");
        if (ImGui.Checkbox("从公共译文库下载现成译文", ref library))
        {
            config.UITextLibraryEnabled = library;
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "优先下载现成译文，节省翻译额度。自己改过的译文会保留。\n" +
                "没有可用译文时，继续使用所选翻译通道。");
        }

        var translateGrey = config.UITextTranslateGreyList;
        if (ImGui.Checkbox("批量翻译时连灰名单一起翻", ref translateGrey))
        {
            if (translateGrey)
            {
                // 开启前先确认一次：灰名单翻错可能破坏插件功能（2026-10-03 用户要求）
                this.greyConfirmPending = true;
            }
            else
            {
                config.UITextTranslateGreyList = false;
                changed = true;
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("灰名单是可能同时影响界面与插件功能的文字。\n" +
                             "翻译这些文字可能影响正常运行，建议在「编辑校对」中逐条确认。\n" +
                             "默认不翻译。");
        }

        // 开启灰名单的确认框（默认焦点在「取消」；Esc / 点外关闭会经 ref 自动复位）
        if (this.greyConfirmPending && !ImGui.IsPopupOpen("连灰名单一起翻###ConfirmGrey"))
        {
            ImGui.OpenPopup("连灰名单一起翻###ConfirmGrey");
        }

        FireGazeWindowPlacement.PrepareModal(new Vector2(540, 300));
        if (ImGui.BeginPopupModal("连灰名单一起翻###ConfirmGrey", ref this.greyConfirmPending, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("翻译灰名单可能影响插件正常运行。");
            ImGui.TextWrapped("建议保持关闭；确实需要时，到「编辑校对」里对具体条目逐条确认后再翻。");
            ImGui.Separator();
            if (ImGui.Button("取消", new Vector2(120, 0)))
            {
                this.greyConfirmPending = false;
                ImGui.CloseCurrentPopup();
            }

            if (ImGui.IsWindowAppearing())
            {
                ImGui.SetItemDefaultFocus();
            }

            ImGui.SameLine();
            if (ImGui.Button("仍然开启", new Vector2(120, 0)))
            {
                config.UITextTranslateGreyList = true;
                changed = true;
                this.greyConfirmPending = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        var autoRepatch = config.UITextAutoRepatch;
        if (ImGui.Checkbox("插件更新后自动恢复汉化", ref autoRepatch))
        {
            config.UITextAutoRepatch = autoRepatch;
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "插件更新后，原有汉化可能失效。\n" +
                "开启后，下次启动或打开插件时会自动恢复汉化，不主动重载插件。\n" +
                "关闭后，需要手动点击「一键汉化」。");
        }

        if (string.Equals(config.UITextChannel, "llm", StringComparison.Ordinal))
        {
            ImGui.Separator();
            ImGui.TextDisabled("大模型设置");
            var isDeepSeek = string.Equals(config.UITextLLMProvider, "deepseek", StringComparison.OrdinalIgnoreCase);
            if (ImGui.RadioButton("DeepSeek 官方", isDeepSeek) && !isDeepSeek)
            {
                config.UITextLLMProvider = "deepseek";
                config.UITextLLMBaseURL = "https://api.deepseek.com/v1";
                config.UITextLLMModel = "deepseek-flash";
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("其它 OpenAI 兼容服务", !isDeepSeek) && isDeepSeek)
            {
                config.UITextLLMProvider = "custom";
                changed = true;
            }

            var baseURL = config.UITextLLMBaseURL;
            ImGui.SetNextItemWidth(420);
            if (ImGui.InputText("接口地址", ref baseURL, 512))
            {
                config.UITextLLMBaseURL = baseURL;
                changed = true;
            }

            ImGui.SameLine();
            var model = config.UITextLLMModel;
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputText("模型", ref model, 128))
            {
                config.UITextLLMModel = model;
                changed = true;
            }

            this.DrawKeyRow("大模型 API key", config.UITextLLMKeyProtected, v => config.UITextLLMKeyProtected = v, ref changed);

            var glossary = config.UITextUseGlossary;
            if (ImGui.Checkbox("用 FF14 官方译名术语表", ref glossary))
            {
                config.UITextUseGlossary = glossary;
                changed = true;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "翻译时参考 FF14 官方译名，统一地名、技能、坐骑等名称。\n" +
                    "仅在大模型通道生效。\n" +
                    "术语表可离线使用。");
            }

            if (config.UITextUseGlossary)
            {
                this.EnsureGlossaryBuilding();
                ImGui.SameLine();
                if (FFXIVGlossary.Failed)
                {
                    ImGui.TextDisabled("加载失败");
                    if (ImGui.IsItemHovered() && FFXIVGlossary.FailureReason is { Length: > 0 } reason)
                    {
                        ImGui.SetTooltip(reason);
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton("重试###glossary-retry"))
                    {
                        FFXIVGlossary.ResetFailure();
                        this.glossaryBuilding = false;
                        this.EnsureGlossaryBuilding();
                    }
                }
                else
                {
                    ImGui.TextDisabled(FFXIVGlossary.Ready ? $"已就绪 {FFXIVGlossary.Count} 条" : "加载中…");
                }

                ImGui.TextDisabled("仅在大模型通道生效。");
            }
        }

        if (string.Equals(config.UITextChannel, "caiyun", StringComparison.Ordinal))
        {
            ImGui.Separator();
            ImGui.TextDisabled("彩云小译设置");
            ImGui.TextDisabled("彩云小译：到「彩云科技开放平台」注册 → 应用管理里创建应用 → 页面右边「管理」→「访问控制」里复制 token 填这里。");
            this.DrawKeyRow("彩云小译 token", config.UITextCaiyunKeyProtected, v => config.UITextCaiyunKeyProtected = v, ref changed);
        }

        if (string.Equals(config.UITextChannel, "deepl", StringComparison.Ordinal))
        {
            ImGui.Separator();
            ImGui.TextDisabled("DeepL 设置");
            this.DrawKeyRow("DeepL API key", config.UITextDeepLKeyProtected, v => config.UITextDeepLKeyProtected = v, ref changed);
        }

        ImGui.TextDisabled("key 仅在本机加密保存，不随反馈或译文投稿上传。更换电脑或 Windows 用户后需重新填写。");
        ImGui.TextDisabled("除 key 外，设置立即生效并自动保存；key 要点「保存 key」才生效。");
        if (this.messageKey.Length > 0)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Muted, this.messageKey);
        }

        if (changed)
        {
            this.plugin.SaveConfig();
        }
    }

    /// <summary>每个通道一句话：要不要 key、额度、快慢（盲评 CF-08：别让用户跨窗口背参数）。</summary>
    private static string ChannelHint(string key) => key switch
    {
        "public-caiyun" => "FireGaze 公共彩云：共享免费额度，无需 key；不可用时自动切换免费通道。",
        "auto" => "免费·自动：无需 key，自动尝试可用的免费通道；可能限流。",
        "caiyun" => "彩云小译：需要自己的 token，额度与费用以服务商规则为准。",
        "google" => "Google 免 key：无需 key，可能限流。",
        "mymemory" => "MyMemory：无需 key，可用额度受服务商限制。",
        "llm" => "大模型：需要自己的 API key，可使用 FF14 官方译名术语表；可能产生服务商费用。",
        "deepl" => "DeepL：需要自己的 API key，额度与费用以服务商规则为准。",
        _ => string.Empty,
    };

    private void DrawKeyRow(string label, string currentProtected, Action<string> apply, ref bool changed)
    {
        var existing = DPAPI.UnprotectFromBase64(currentProtected);
        ImGui.SetNextItemWidth(360);
        var input = this.keyInput;
        if (ImGui.InputTextWithHint($"###key-{label}", "粘贴新的 key 以更换", ref input, 256, ImGuiInputTextFlags.Password))
        {
            this.keyInput = input;
        }

        // 输入框里有内容时只说「输入还没保存」（v4 复评 F11/N8：与「当前：未保存」同屏是三重同义信号）；
        // 输入框空着时才报「当前」状态——这时它是唯一的状态信息。
        ImGui.SameLine();
        var typing = input.Trim().Length > 0;
        if (typing)
        {
            UiHelpers.ColoredText(UiHelpers.Warn, "输入还没保存");
        }
        else if (existing is null)
        {
            UiHelpers.ColoredText(UiHelpers.Muted, "当前：未保存");
        }
        else
        {
            UiHelpers.ColoredText(UiHelpers.Good, "当前：已保存 " + DPAPI.Mask(existing));
        }

        ImGui.SameLine();
        if (ImGui.Button("保存 key###save-" + label))
        {
            if (this.keyInput.Trim().Length == 0)
            {
                this.messageKey = "输入框是空的；要清掉已保存的 key 请点「清除」。";
            }
            else
            {
                var protectedValue = DPAPI.ProtectToBase64(this.keyInput.Trim());
                if (protectedValue.Length == 0)
                {
                    this.messageKey = "加密失败，key 没有保存。";
                }
                else
                {
                    apply(protectedValue);
                    changed = true;
                    this.keyInput = string.Empty;
                    this.messageKey = "已加密保存。";
                }
            }
        }

        ImGui.SameLine();
        using var disabledScope1 = UiHelpers.Disabled(existing is null);
        if (ImGui.Button("清除###clear-" + label))
        {
            apply(string.Empty);
            changed = true;
            this.keyInput = string.Empty;
            this.messageKey = "已清除。";
        }

        disabledScope1.Dispose();
        if (existing is null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("没有已保存的 key，不用清除。");
        }

        ImGui.SameLine();
        if (ImGui.Button("测试连接###test-" + label))
        {
            this.TestChannel();
        }
    }

    private bool glossaryBuilding;

    /// <summary>勾上「连灰名单一起翻」后先弹确认——确认前不写配置（默认焦点在「取消」，2026-10-03）。</summary>
    private bool greyConfirmPending;

    /// <summary>第一次需要时在后台把术语表建好（读游戏表要一两秒，不能占渲染线程）。</summary>
    private void EnsureGlossaryBuilding()
    {
        if (this.glossaryBuilding || FFXIVGlossary.Ready || FFXIVGlossary.Failed)
        {
            return;
        }

        this.glossaryBuilding = true;
        _ = this.plugin.RunTextTask(FFXIVGlossary.EnsureBuilt);
    }

    private void TestChannel()
    {
        var targets = new List<UITextTranslateItem> { new("Settings", "test") };
        var channel = UITextChannelFactory.Create(this.plugin.Config, out var error);
        if (channel is null)
        {
            this.messageKey = error ?? "通道不可用。";
            return;
        }

        this.messageKey = "测试中…";
        var current = channel;
        _ = this.plugin.RunTextTask(async () =>
        {
            try
            {
                var result = await current.TranslateAsync(targets, null, this.plugin.WorkLifetime.ShutdownToken).ConfigureAwait(false);
                if (this.plugin.WorkLifetime.State != PluginWorkLifetimeState.Running) return;
                var text = result.Translated.TryGetValue("Settings", out var value) ? value : null;
                this.messageKey = text is null
                    ? $"连接失败：{result.Error ?? "没有返回译文"}"
                    : $"连接正常（{current.Name}）：Settings → {text}";
            }
            catch (Exception e)
            {
                if (this.plugin.WorkLifetime.State != PluginWorkLifetimeState.Running) return;
                this.messageKey = "连接失败：" + e.Message;
            }
        });
    }
}
