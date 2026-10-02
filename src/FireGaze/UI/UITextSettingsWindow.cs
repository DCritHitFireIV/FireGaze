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


    /// <summary>
    ///     从「一键汉化」/ 主窗口点开时总是展开：折叠状态会被 ImGui 的 ini 记住，
    ///     点开只剩一条标题栏、看起来像「点不开」（2026-10-02 用户实测）。
    /// </summary>
    public override void OnOpen()
    {
        ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
    }

    public override void Draw()
    {
        ImGui.TextWrapped("这里决定「一键汉化」用什么方式把英文翻成中文。免费接口按 IP 限流，条目多时建议用自己的大模型 key。");
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

        if (config.UITextChannel is "auto" or "google" or "mymemory")
        {
            ImGui.TextDisabled(
                "免费通道按 IP 限流：Google 会返回 429、MyMemory 每天只有约 5000 词，而且是一条一条翻（每个词条约 0.8 秒）。\n" +
                "想免费又要快，用「彩云小译」：注册后到「应用管理」创建应用，页面右边「管理」→「访问控制」里复制 token 填进来，" +
                "新用户送 100 万字（一个月），一次能提交 50 条、几百条几秒翻完。");
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
                "有现成译文时直接下载，不用花你自己的 key；玩家自己改过的译文不会被覆盖。\n" +
                "拉不到（网络 / 仓库里还没有这个插件）就照旧用自己的翻译通道。");
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

            var glossary = config.UITextUseGlossary;
            if (ImGui.Checkbox("用 FF14 官方译名术语表", ref glossary))
            {
                config.UITextUseGlossary = glossary;
                changed = true;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "翻译时把命中的官方译名（地名 / 副本 / 技能 / 状态 / 坐骑…）喂给模型，专有名词前后一致。\n" +
                    "只在大模型通道生效；免费接口带不了术语表。\n" +
                    "术语表随插件打包，不用联网，也不会读你的游戏客户端。");
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

                ImGui.TextDisabled("只在大模型通道生效；免费接口带不了术语表。");
            }
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
        "auto" => "免费·自动：按可用性依次尝试免 key 接口，失败会自动换下一个；按 IP 限流，条目多时慢。",
        "caiyun" => "彩云小译：一次最多 50 条，新号送 100 万字 / 一个月；需要自填 token（免费注册）。",
        "google" => "Google 免 key：不用 key，逐条翻译、随时可能被限流（429）。",
        "mymemory" => "MyMemory：不用 key，每天约 5000 词，逐条翻译。",
        "llm" => "大模型：速度最快、质量最好，需要自填 API key（只存本机）。",
        "deepl" => "DeepL：需要自填 API key。",
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

        ImGui.SameLine();
        if (existing is null)
        {
            UiHelpers.ColoredText(UiHelpers.Muted, "当前：未保存");
        }
        else
        {
            UiHelpers.ColoredText(UiHelpers.Good, "当前：已保存 " + DPAPI.Mask(existing));
        }

        // 说清保存模型：底部写「自动保存」，但 key 是例外（盲评 CF-09/S1）
        if (input.Trim().Length > 0)
        {
            ImGui.SameLine();
            UiHelpers.ColoredText(UiHelpers.Warn, "输入还没保存");
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
        ImGui.BeginDisabled(existing is null);
        if (ImGui.Button("清除###clear-" + label))
        {
            apply(string.Empty);
            changed = true;
            this.keyInput = string.Empty;
            this.messageKey = "已清除。";
        }

        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("测试连接###test-" + label))
        {
            this.TestChannel();
        }
    }

    private bool glossaryBuilding;

    /// <summary>第一次需要时在后台把术语表建好（读游戏表要一两秒，不能占渲染线程）。</summary>
    private void EnsureGlossaryBuilding()
    {
        if (this.glossaryBuilding || FFXIVGlossary.Ready || FFXIVGlossary.Failed)
        {
            return;
        }

        this.glossaryBuilding = true;
        _ = Task.Run(FFXIVGlossary.EnsureBuilt);
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
