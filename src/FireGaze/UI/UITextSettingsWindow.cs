using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
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


    public override void Draw()
    {
        ImGui.TextWrapped("翻译通道决定「一键汉化」用什么把英文翻成中文。免费接口按 IP 限流，条目多时建议用自己的大模型 key。");
        ImGui.Separator();

        var config = this.plugin.Config;
        var changed = false;

        var channels = new (string Key, string Label)[]
        {
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
                ImGui.SameLine();
            }

            if (ImGui.RadioButton(channels[i].Label, string.Equals(config.UITextChannel, channels[i].Key, StringComparison.Ordinal)))
            {
                if (!string.Equals(config.UITextChannel, channels[i].Key, StringComparison.Ordinal))
                {
                    config.UITextChannel = channels[i].Key;
                    changed = true;
                }
            }
        }

        if (config.UITextChannel is "auto" or "google" or "mymemory")
        {
            ImGui.TextDisabled(
                "免费通道按 IP 限流：Google 会返回 429、MyMemory 额度只有几千字符/天，而且是一条一条翻（约 1 条/秒）。\n" +
                "想免费又要快，用「彩云小译」：注册后到「应用管理」创建应用，页面右边「管理」→「访问控制」里复制 token 填进来，" +
                "新用户送 100 万字（一个月），一次能提交 50 条、几百条几秒翻完。");
        }

        var translateGrey = config.UITextTranslateGreyList;
        if (ImGui.Checkbox("批量翻译时连灰名单一起翻", ref translateGrey))
        {
            config.UITextTranslateGreyList = translateGrey;
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("灰名单 = 既画在界面上、又被拿去做比较/当键名的字符串，默认不翻（翻错可能影响功能）。");
        }

        var autoRepatch = config.UITextAutoRepatch;
        if (ImGui.Checkbox("插件更新后自动重打汉化补丁", ref autoRepatch))
        {
            config.UITextAutoRepatch = autoRepatch;
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "插件更新会换掉 DLL，汉化补丁需要重打。\n" +
                "勾上：下次启动 / 打开插件时自动重打（静默写入，不主动重载插件）。\n" +
                "取消勾选：只在你点「一键汉化」时才动文件。");
        }

        if (string.Equals(config.UITextChannel, "llm", StringComparison.Ordinal))
        {
            ImGui.Separator();
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
        }

        if (string.Equals(config.UITextChannel, "caiyun", StringComparison.Ordinal))
        {
            ImGui.Separator();
            ImGui.TextDisabled("彩云小译：到「彩云科技开放平台」注册 → 应用管理里创建应用 → 页面右边「管理」→「访问控制」里复制 token 填这里。");
            this.DrawKeyRow("彩云小译 token", config.UITextCaiyunKeyProtected, v => config.UITextCaiyunKeyProtected = v, ref changed);
        }

        if (string.Equals(config.UITextChannel, "deepl", StringComparison.Ordinal))
        {
            ImGui.Separator();
            this.DrawKeyRow("DeepL API key", config.UITextDeepLKeyProtected, v => config.UITextDeepLKeyProtected = v, ref changed);
        }

        ImGui.TextDisabled("key 只存在本机（DPAPI 加密），不会随任何提交上传；换机器/换 Windows 用户后需要重填。");
        ImGui.TextDisabled("设置立即生效并自动保存，不需要点「确定」。");
        if (this.messageKey.Length > 0)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Muted, this.messageKey);
        }

        if (changed)
        {
            this.plugin.SaveConfig();
        }
    }

    private void DrawKeyRow(string label, string currentProtected, Action<string> apply, ref bool changed)
    {
        var existing = DPAPI.UnprotectFromBase64(currentProtected);
        ImGui.SetNextItemWidth(360);
        var input = this.keyInput;
        if (ImGui.InputTextWithHint($"###key-{label}", "粘贴新的 key 以更换", ref input, 256, ImGuiInputTextFlags.Password))
        {
            this.keyInput = input;
        }

        ImGui.SameLine();
        if (existing is null)
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
                    this.messageKey = "已保存（DPAPI 加密）。";
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("清除###clear-" + label))
        {
            apply(string.Empty);
            changed = true;
            this.keyInput = string.Empty;
            this.messageKey = "已清除。";
        }

        ImGui.SameLine();
        if (ImGui.Button("测试连接###test-" + label))
        {
            this.TestChannel();
        }
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
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await current.TranslateAsync(targets, null, CancellationToken.None).ConfigureAwait(false);
                var text = result.Translated.TryGetValue("Settings", out var value) ? value : null;
                this.messageKey = text is null
                    ? $"连接失败：{result.Error ?? "没有返回译文"}"
                    : $"连接正常（{current.Name}）：Settings → {text}";
            }
            catch (Exception e)
            {
                this.messageKey = "连接失败：" + e.Message;
            }
        });
    }
}
