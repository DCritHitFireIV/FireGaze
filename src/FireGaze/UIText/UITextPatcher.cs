using System.Collections;
using System.Text;
using System.Resources;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnlib.DotNet.Writer;

namespace FireGaze.UIText;

/// <summary>
///     打补丁的结果。
/// </summary>
internal sealed class UITextPatchOutcome
{
    public string? Error { get; set; }

    /// <summary>
    ///     包里「有译文且没标不翻」的条目数。
    /// </summary>
    public int Candidates { get; set; }

    /// <summary>
    ///     在 DLL 里找到并且真的改掉的字面量个数（同一原文出现在多处会重复计）。
    /// </summary>
    public int PatchedLiterals { get; set; }

    /// <summary>
    ///     为避免 ImGui 控件 ID 撞车、被强制写成「译文###原文」的条目数（同一译文被多个原文占用）。
    /// </summary>
    public int PreserveIDForced { get; set; }

    /// <summary>
    ///     真的改掉的内嵌资源条目数（按 容器 + key 计）。
    /// </summary>
    public int PatchedResources { get; set; }

    /// <summary>
    ///     真的改掉的属性字符串数（自定义特性参数，含重复出现）。
    /// </summary>
    public int PatchedAttributes { get; set; }

    /// <summary>实际写进去的总条数（字面量 + 资源 + 属性）。</summary>
    public int PatchedTotal => this.PatchedLiterals + this.PatchedResources + this.PatchedAttributes;

    /// <summary>
    ///     DLL 里没找回来的条目（可能是插件更新过、或原文已经变了）。
    /// </summary>
    public List<string> Missing { get; } = [];

    /// <summary>
    ///     一条都没对上：DLL 里找不到任何一条包里译文的原文。
    ///     最常见的原因：盘上已经是我们的旧补丁、而补丁记录丢了（记录在时会在打之前先还原基线）。
    /// </summary>
    public bool NoMatch { get; set; }

    /// <summary>
    ///     调试符号（PDB）没能保进新文件，退回了「不带 PDB 写」——插件里用 StackFrame 的地方可能报 null
    /// （Collections 实测过 NRE）。写盘前会记一条警告日志（2026-10-03 评审 B-11）。
    /// </summary>
    public bool PdbDropped { get; set; }

    /// <summary>占位符对不上、被拦下没写进 DLL 的条数（手工改 / 导入的译文也走这道闸，2026-10-03 评审 B-10）。</summary>
    public int PlaceholderSkipped { get; set; }

    /// <summary>Pack entries blocked because current IL uses them as plugin/IPC identities.</summary>
    public int FunctionalIdentifierSkipped { get; set; }

    public bool Ok => this.Error is null;
}

/// <summary>
///     把译文写进插件 DLL：只改字符串字面量（<c>ldstr</c>），代码结构一个字节都不动。
/// </summary>
/// <remarks>
///     口径：
///       · 只改「有译文、且没被用户标记不翻」的条目；
///       · 按**文本内容**匹配，不绑版本号——插件更新后仍按原文匹配，对不上的记进 <see cref="UITextPatchOutcome.Missing" />；
///       · 被 ImGui 当控件 ID 用的字面量写成「译文###原文」，显示译文、ID 还在原文上；
///       · 原文自带 <c>###</c> 时只替换显示段，ID 段原样保留。
/// </remarks>
internal static class UITextPatcher
{
    /// <summary>
    ///     把 <paramref name="sourcePath" /> 打上补丁写到 <paramref name="targetPath" />（可以是同一个文件，
    ///     调用方负责先备份；这里只保证「先写临时文件再替换」不会留半截 DLL）。
    /// </summary>
    /// <remarks>
    ///     <paramref name="searchDirectories" /> 是给程序集解析器的补充搜索目录：
    ///     源文件是 <c>uitrans/backups</c> 里的备份时要传**原插件目录**，
    ///     否则特性的跨程序集参数类型（ARSR 的 <c>CombatType</c> 在 <c>RotationSolver.Basic.dll</c>）解析不了，
    ///     命名参数改不动（2026-10-02）。
    /// </remarks>
    public static UITextPatchOutcome Patch(
        string sourcePath,
        string targetPath,
        UITextPack pack,
        string? pluginVersion = null,
        IReadOnlyList<string>? searchDirectories = null,
        bool includeAmbiguous = false,
        bool allowEmptyPatch = false)
    {
        var outcome = new UITextPatchOutcome();
        var map = new Dictionary<string, UITextPackEntry>(StringComparer.Ordinal);
        // 同一文件里两个不同原文翻成同一条译文时，ImGui 会用译文当控件 ID → 撞车 → 第二个点不动
        // （2026-10-02 AutoHook 实测「刺鱼设置点不了」）。除第一个占用者外，其余强制走「译文###原文」保 ID。
        var usedTranslation = new Dictionary<string, string>(StringComparer.Ordinal);
        var forcePreserve = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in pack.Entries)
        {
            if (!entry.HasTranslation || pack.IsSkipped(entry.Original) || !entry.IsPatchable(includeAmbiguous))
            {
                continue;
            }

            // 占位符对不上的绝不写进 DLL（打进去会让插件运行时抛 FormatException；库合并之外，
            // 手工编辑 / 导入的译文也走这道闸，2026-10-03 评审 B-10）
            if (UITextText.CheckPlaceholders(entry.Original, entry.Translated.Trim()) is not null)
            {
                outcome.PlaceholderSkipped++;
                continue;
            }

            if (!entry.PreserveID)
            {
                var effective = entry.Translated.Trim();
                if (usedTranslation.ContainsKey(effective))
                {
                    // 撞译文时只有「可能被当控件标签」的条目才强制保 ID：纯文字绘制（Text* / tooltip /
                    // 包装库的画文字辅助方法）根本不用 ID，加 ### 只会把后缀画到界面上（2026-10-03 评审 B-08）。
                    if (MightBeLabel(entry))
                    {
                        forcePreserve.Add(entry.Original);
                    }
                }
                else
                {
                    usedTranslation[effective] = entry.Original;
                }
            }

            map[entry.Original] = entry;
        }

        outcome.PreserveIDForced = forcePreserve.Count;

        var resourceMap = new Dictionary<(string Container, string Key), UITextResourceEntry>();
        foreach (var entry in pack.Resources)
        {
            if (!entry.HasTranslation || pack.IsResourceSkipped(entry.Container, entry.Key))
            {
                continue;
            }

            if (UITextText.CheckPlaceholders(entry.Original, entry.Translated.Trim()) is not null)
            {
                outcome.PlaceholderSkipped++;
                continue;
            }

            resourceMap[(entry.Container, entry.Key)] = entry;
        }

        var attributeMap = new Dictionary<string, UITextAttributeEntry>(StringComparer.Ordinal);
        foreach (var entry in pack.Attributes)
        {
            if (!entry.HasTranslation || pack.IsAttributeSkipped(entry.Original))
            {
                continue;
            }

            if (UITextText.CheckPlaceholders(entry.Original, entry.Translated.Trim()) is not null)
            {
                outcome.PlaceholderSkipped++;
                continue;
            }

            attributeMap[entry.Original] = entry;
        }

        outcome.Candidates = map.Count + resourceMap.Count + attributeMap.Count;
        if (outcome.Candidates == 0 && !allowEmptyPatch)
        {
            var total = pack.Entries.Count + pack.Resources.Count + pack.Attributes.Count;
            if (total == 0)
            {
                outcome.Error = "译文包里还没有任何条目（先「一键汉化」或「抽取界面文本」）。";
            }
            else if (pack.TranslatedTotal == 0)
            {
                outcome.Error = "包里还没有译文（候选都还没翻，或都被标了「不翻」）。";
            }
            else
            {
                outcome.Error = "译文都有，但没有一条能写进 DLL（灰名单条目要打开「连灰名单一起翻」，或都被标了「不翻」）。";
            }

            return outcome;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenResources = new HashSet<(string Container, string Key)>();
        var seenAttributes = new HashSet<string>(StringComparer.Ordinal);
        var tempPath = targetPath + ".fguitext.tmp";
        try
        {
            using (var module = UIStringExtractor.LoadModule(sourcePath, searchDirectories))
            {
                // Pack roles can be stale or come from another localized fork. Current IL is authoritative.
                var identities = UIStringExtractor.FunctionalIdentifiers(module);
                foreach (var identity in identities)
                {
                    if (map.Remove(identity))
                    {
                        seen.Add(identity);
                        outcome.FunctionalIdentifierSkipped++;
                    }
                }

                if (map.Count + resourceMap.Count + attributeMap.Count == 0 && !allowEmptyPatch)
                {
                    outcome.Error = "译文只命中了插件 / IPC 标识符，为保留插件识别功能，没有写入。";
                    return outcome;
                }

                // ① 内嵌本地化资源（.resx / ResourceManager）：按「容器 + key」改值。
                //    只动主程序集的内嵌容器：官方 zh 卫星优先（ResourceManager 的查找顺序），
                //    我们只负责把它没覆盖的中性资源补上。
                if (resourceMap.Count > 0)
                {
                    PatchResources(module, resourceMap, seenResources, outcome);
                }

                // ①a2 内嵌 JSON 本地化表（json: 容器，HaselTweaks 这类）：只补 zh 空的槽。
                if (resourceMap.Count > 0)
                {
                    PatchJSONResources(module, resourceMap, seenResources, outcome);
                }

                // ①b 自定义特性参数里的界面文字（UIAttribute / TweakName…）：改 UTF-8 blob 里的字符串
                if (attributeMap.Count > 0)
                {
                    PatchAttributes(module, attributeMap, seenAttributes, outcome);
                }

                // ② 字面量
                foreach (var type in module.GetTypes())
                {
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody)
                        {
                            continue;
                        }

                        foreach (var instruction in method.Body.Instructions)
                        {
                            if (instruction.OpCode.Code != Code.Ldstr || instruction.Operand is not string literal)
                            {
                                continue;
                            }

                            if (!map.TryGetValue(literal, out var entry))
                            {
                                continue;
                            }

                            seen.Add(literal);
                            var replaced = UITextText.BuildPatched(literal, entry.Translated, entry.PreserveID || forcePreserve.Contains(entry.Original));
                            if (string.Equals(replaced, literal, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            instruction.Operand = replaced;
                            outcome.PatchedLiterals++;
                        }
                    }
                }

                if (outcome.PatchedTotal == 0 && !allowEmptyPatch)
                {
                    // NoMatch：盘上可能已经是我们的旧补丁（记录丢了）——调用方据此做一次自愈重试，见 UITextPatchManager.Apply
                    outcome.NoMatch = true;
                    outcome.Error = $"译文有 {outcome.Candidates} 条，但在 DLL 里一条都没对上。到插件安装器里把这个插件重装一次（回到原版）再点「一键汉化」；行尾有「还原原文」时也可以先点它。";
                    return outcome;
                }

                // 版本号顺手写进注释？不需要——但保留参数是为了将来做「版本戳」用
                _ = pluginVersion;
                WriteModule(module, tempPath, outcome);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (Exception e)
        {
            outcome.Error = $"{e.GetType().Name}: {e.Message}";
            TryDelete(tempPath);
            return outcome;
        }

        foreach (var original in map.Keys)
        {
            if (!seen.Contains(original))
            {
                outcome.Missing.Add(original);
            }
        }

        foreach (var (container, key) in resourceMap.Keys)
        {
            if (!seenResources.Contains((container, key)))
            {
                outcome.Missing.Add($"资源：{container} · {key}");
            }
        }

        foreach (var original in attributeMap.Keys)
        {
            if (!seenAttributes.Contains(original))
            {
                outcome.Missing.Add($"属性：{original}");
            }
        }

        return outcome;
    }

    /// <summary>
    ///     反向补丁（自救）：把 DLL 上我们自己打过的「译文」还原成原文，
    ///     用来在丢了补丁记录 / 原始备份时重建一份可再打补丁的基线（2026-10-02 用户实测那批）。
    /// </summary>
    /// <remarks>
    ///     匹配规则（按优先级）：<br />
    ///     ① 字面量以 <c>###原文</c> 结尾（含原文自带 ### 的、以及历史上双层拼接过的形态）→ 整条换成原文；<br />
    ///     ② 字面量正好等于某条译文，且这个译文只对应一条原文 → 换成原文（多对一就跳过，宁可不还原也不乱还原）；<br />
    ///     ③ 内嵌资源：按「容器 + key + 当前值 = 译文」换回原文（逐 key 比，不跨 key 猜）。<br />
    ///     只碰包里有译文的条目，其它字节不动；不是「打过补丁」的文件会回到 0 条。
    /// </remarks>
    public static UITextPatchOutcome Revert(
        string sourcePath,
        string targetPath,
        UITextPack pack,
        IReadOnlyList<string>? searchDirectories = null)
    {
        var outcome = new UITextPatchOutcome();
        var suffixMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var exactMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        var ambiguousLiterals = new HashSet<string>(StringComparer.Ordinal);
        var patchedProducts = new List<(string Literal, string Original)>();

        foreach (var entry in pack.Entries)
        {
            if (!entry.HasTranslation || pack.IsSkipped(entry.Original))
            {
                continue;
            }

            var original = entry.Original;
            var translated = entry.Translated.Trim();
            if (string.Equals(original, translated, StringComparison.Ordinal))
            {
                continue;
            }

            suffixMap.TryAdd("###" + original, original);
            // 原文自带 ### 的：补丁写出来是「译文 + 原文的 ID 段」，suffixMap 的「###全原文」对不上，
            // 额外登记一条「补丁产物 → 原文」的精确映射，否则这类字面量永远还原不回来（2026-10-03 评审 B-09）；
            // 产物是否与别的条目的 ### 后缀撞车放到建完 suffixMap 后再判（P3-c）。
            if (UITextText.IDPart(original).Length > 0)
            {
                patchedProducts.Add((UITextText.BuildPatched(original, translated, preserveID: false), original));
            }

            if (exactMap.TryGetValue(translated, out var existing) && !string.Equals(existing, original, StringComparison.Ordinal))
            {
                ambiguous.Add(translated);
            }
            else
            {
                exactMap[translated] = original;
            }
        }

        // 补丁产物与「### 后缀」撞车时（同一条译文既是普通条目的产物、又遇上自带 ### 的条目），
        // 这次还原本质上有歧义：宁可返回 null 让用户看到「还原不了」，也不要把错的原文写回去（评审 P3-c）。
        foreach (var (literal, original) in patchedProducts)
        {
            if (SuffixClaimable(literal, suffixMap))
            {
                ambiguousLiterals.Add(literal);
            }
            else
            {
                exactMap.TryAdd(literal, original);
            }
        }

        foreach (var value in ambiguous)
        {
            exactMap.Remove(value);
        }

        if (suffixMap.Count == 0 && exactMap.Count == 0
            && !pack.Resources.Any(r => r.HasTranslation && !pack.IsResourceSkipped(r.Container, r.Key)))
        {
            // 资源条目（.resources / 内嵌 JSON）也能反向还原——不能只看字面量条目，
            // 否则只有资源译文的包会被挡在门外（2026-10-03 fgtest 实测）。
            outcome.Error = "译文包里没有可反向还原的译文。";
            return outcome;
        }

        var tempPath = targetPath + ".fguitext.tmp";
        try
        {
            using (var module = UIStringExtractor.LoadModule(sourcePath, searchDirectories))
            {
                var identities = UIStringExtractor.FunctionalIdentifiers(module);
                foreach (var type in module.GetTypes())
                {
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody)
                        {
                            continue;
                        }

                        foreach (var instruction in method.Body.Instructions)
                        {
                            if (instruction.OpCode.Code != Code.Ldstr || instruction.Operand is not string literal)
                            {
                                continue;
                            }

                            if (identities.Contains(literal))
                            {
                                continue;
                            }

                            var recovered = RecoverLiteral(literal, suffixMap, exactMap, ambiguousLiterals);
                            if (recovered is null || string.Equals(recovered, literal, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            instruction.Operand = recovered;
                            outcome.PatchedLiterals++;
                        }
                    }
                }

                RevertResources(module, pack, outcome);
                RevertJSONResources(module, pack, outcome);

                if (outcome.PatchedTotal == 0)
                {
                    outcome.Error = "这个 DLL 里没有找到我们打过的补丁痕迹。";
                    return outcome;
                }

                WriteModule(module, tempPath, outcome);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (Exception e)
        {
            outcome.Error = $"{e.GetType().Name}: {e.Message}";
            TryDelete(tempPath);
            return outcome;
        }

        return outcome;
    }

    /// <summary>
    ///     按「译文精确匹配 / <c>###原文</c> 后缀」还原一条字面量；还原不了返回 null（纯函数，方便离线测）。
    ///     先试精确映射（能处理「原文自带 ###」这类只有补丁产物才对得上的情况），再走 ### 后缀；
    ///     <paramref name="ambiguous" /> 里的字面量直接放弃（两种解释都说得通，不猜）。
    /// </summary>
    public static string? RecoverLiteral(
        string literal,
        IReadOnlyDictionary<string, string> suffixMap,
        IReadOnlyDictionary<string, string> exactMap,
        IReadOnlySet<string>? ambiguous = null)
    {
        if (ambiguous is not null && ambiguous.Contains(literal))
        {
            return null;
        }

        if (exactMap.TryGetValue(literal, out var byExact) && !string.Equals(byExact, literal, StringComparison.Ordinal))
        {
            return byExact;
        }

        var index = literal.IndexOf(UITextText.IDSeparator, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (suffixMap.TryGetValue(literal[index..], out var bySuffix)
                && !string.Equals(bySuffix, literal, StringComparison.Ordinal))
            {
                return bySuffix;
            }

            index = literal.IndexOf(UITextText.IDSeparator, index + UITextText.IDSeparator.Length, StringComparison.Ordinal);
        }

        return null;
    }

    /// <summary>字面量会不会被某个条目的「###原文」后缀半路认领（还原歧义的判据，2026-10-03 评审 P3-c）。</summary>
    private static bool SuffixClaimable(string literal, Dictionary<string, string> suffixMap)
    {
        var index = literal.IndexOf(UITextText.IDSeparator, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (suffixMap.ContainsKey(literal[index..]))
            {
                return true;
            }

            index = literal.IndexOf(UITextText.IDSeparator, index + UITextText.IDSeparator.Length, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    ///     内嵌资源反向还原：逐 key 看「当前值 == 这条的译文」才换回原文（不跨 key 猜）。
    /// </summary>
    private static void RevertResources(ModuleDefMD module, UITextPack pack, UITextPatchOutcome outcome)
    {
        var byContainer = new Dictionary<string, Dictionary<string, UITextResourceEntry>>(StringComparer.Ordinal);
        foreach (var entry in pack.Resources)
        {
            if (!entry.HasTranslation || pack.IsResourceSkipped(entry.Container, entry.Key))
            {
                continue;
            }

            if (!byContainer.TryGetValue(entry.Container, out var keys))
            {
                keys = new Dictionary<string, UITextResourceEntry>(StringComparer.Ordinal);
                byContainer[entry.Container] = keys;
            }

            keys[entry.Key] = entry;
        }

        if (byContainer.Count == 0)
        {
            return;
        }

        foreach (var resource in module.Resources.ToList())
        {
            if (resource is not EmbeddedResource embedded)
            {
                continue;
            }

            var container = embedded.Name?.String ?? string.Empty;
            if (!byContainer.TryGetValue(container, out var keys))
            {
                continue;
            }

            try
            {
                var data = embedded.CreateReader().ToArray();
                var items = new List<(string Key, object? Value)>();
                var changed = false;
                using (var reader = new ResourceReader(new MemoryStream(data)))
                {
                    foreach (DictionaryEntry item in reader)
                    {
                        if (item.Key is not string resourceKey)
                        {
                            throw new NotSupportedException("容器里出现了非字符串的 key");
                        }

                        if (item.Value is string value
                            && keys.TryGetValue(resourceKey, out var entry)
                            && string.Equals(value, entry.Translated.Trim(), StringComparison.Ordinal))
                        {
                            items.Add((resourceKey, entry.Original));
                            changed = true;
                        }
                        else
                        {
                            items.Add((resourceKey, item.Value));
                        }
                    }
                }

                if (!changed)
                {
                    continue;
                }

                var stream = new MemoryStream();
                using (var writer = new ResourceWriter(stream))
                {
                    foreach (var (key, value) in items)
                    {
                        writer.AddResource(key, value);
                    }

                    writer.Generate();
                }

                module.Resources.Remove(embedded);
                module.Resources.Add(new EmbeddedResource(container, stream.ToArray(), embedded.Attributes));
                outcome.PatchedResources++;
            }
            catch (Exception e)
            {
                outcome.Missing.Add($"资源：{container}（反向重写失败：{e.Message}）");
            }
        }
    }

    /// <summary>
    ///     把属性译文写进自定义特性参数：只碰 <see cref="UITextRules.IsUIAttribute" /> 认的特性、
    ///     只改构造函数参数里的字符串（含字符串数组的每一项）；命名参数常是键/ID，一概不动。
    /// </summary>
    /// <remarks>
    ///     dnlib 的 <c>CAArgument</c> 是**结构体**：不能传参后直接改（改的是副本），
    ///     必须把返回值写回列表（2026-10-02 踩过：补丁“打成了”但文件里没变）。
    /// </remarks>
    private static void PatchAttributes(
        ModuleDefMD module,
        Dictionary<string, UITextAttributeEntry> attributeMap,
        HashSet<string> seen,
        UITextPatchOutcome outcome)
    {
        void Handle(IHasCustomAttribute host)
        {
            foreach (var attribute in host.CustomAttributes)
            {
                if (!UITextRules.IsUIAttribute(attribute.TypeFullName))
                {
                    continue;
                }

                // 命令类特性（Cmd / SubCmd / Command）：第 0 个参数是命令名，跳过（与抽取端同一规则）
                var arguments = attribute.ConstructorArguments;
                var startIndex = UITextRules.CommandHelpStartIndex(attribute.TypeFullName) ?? 0;
                for (var i = startIndex; i < arguments.Count; i++)
                {
                    arguments[i] = PatchAttributeArgument(arguments[i], attributeMap, seen, outcome);
                }

                // 命名参数：与抽取端同一份白名单（ARSR 的 [RotationConfig(…, Name = "…")] 这类）。
                // CANamedArgument 是引用类型，直接改它的 Argument 属性即可（CAArgument 是结构体，返回值得写回）。
                foreach (var named in attribute.NamedArguments)
                {
                    if (!UITextRules.IsUINamedArgument(named.Name?.String))
                    {
                        continue;
                    }

                    named.Argument = PatchAttributeArgument(named.Argument, attributeMap, seen, outcome);
                }
            }
        }

        foreach (var type in module.GetTypes())
        {
            Handle(type);
            foreach (var method in type.Methods)
            {
                Handle(method);
            }

            foreach (var field in type.Fields)
            {
                Handle(field);
            }

            foreach (var property in type.Properties)
            {
                Handle(property);
            }
        }
    }

    /// <summary>返回可能改过的参数（<c>CAArgument</c> 是结构体，调用方要把返回值写回列表）。</summary>
    private static CAArgument PatchAttributeArgument(
        CAArgument argument,
        Dictionary<string, UITextAttributeEntry> attributeMap,
        HashSet<string> seen,
        UITextPatchOutcome outcome)
    {
        switch (argument.Value)
        {
            case UTF8String utf8:
                if (attributeMap.TryGetValue(utf8.String, out var entry))
                {
                    seen.Add(utf8.String);
                    outcome.PatchedAttributes++;
                    argument.Value = new UTF8String(entry.Translated);
                }

                return argument;
            case IList<CAArgument> list:
                for (var i = 0; i < list.Count; i++)
                {
                    list[i] = PatchAttributeArgument(list[i], attributeMap, seen, outcome);
                }

                return argument;
            default:
                return argument;
        }
    }

    /// <summary>
    ///     把资源条目写进内嵌 <c>.resources</c> 容器：读出全部条目 → 换掉匹配 key 的值 → 重写容器。
    ///     非字符串的值原样保留；重写失败（自定义序列化类型）只跳过该容器，不算整体失败。
    /// </summary>
    private static void PatchResources(
        ModuleDefMD module,
        Dictionary<(string Container, string Key), UITextResourceEntry> resourceMap,
        HashSet<(string Container, string Key)> seen,
        UITextPatchOutcome outcome)
    {
        var byContainer = new Dictionary<string, Dictionary<string, UITextResourceEntry>>(StringComparer.Ordinal);
        foreach (var ((container, key), entry) in resourceMap)
        {
            if (!byContainer.TryGetValue(container, out var keys))
            {
                keys = new Dictionary<string, UITextResourceEntry>(StringComparer.Ordinal);
                byContainer[container] = keys;
            }

            keys[key] = entry;
        }

        // module.Resources 是集合，替换时不能边遍历边改
        foreach (var resource in module.Resources.ToList())
        {
            if (resource is not EmbeddedResource embedded)
            {
                continue;
            }

            var container = embedded.Name?.String ?? string.Empty;
            if (!byContainer.TryGetValue(container, out var keys))
            {
                continue;
            }

            try
            {
                var data = embedded.CreateReader().ToArray();
                var items = new List<(string Key, object? Value)>(keys.Count + 8);
                using (var reader = new ResourceReader(new MemoryStream(data)))
                {
                    foreach (DictionaryEntry item in reader)
                    {
                        if (item.Key is not string resourceKey)
                        {
                            throw new NotSupportedException("容器里出现了非字符串的 key");
                        }

                        if (keys.TryGetValue(resourceKey, out var entry))
                        {
                            // 写回用 Trim 后的值：反向还原是按 Trim 比较的（不统一会还原不回来，2026-10-03 评审 B-12）
                            items.Add((resourceKey, entry.Translated.Trim()));
                            seen.Add((container, resourceKey));
                            outcome.PatchedResources++;
                        }
                        else
                        {
                            items.Add((resourceKey, item.Value));
                        }
                    }
                }

                if (items.Count == 0)
                {
                    continue;
                }

                var stream = new MemoryStream();
                using (var writer = new ResourceWriter(stream))
                {
                    foreach (var (key, value) in items)
                    {
                        writer.AddResource(key, value);
                    }

                    writer.Generate();
                }

                module.Resources.Remove(embedded);
                module.Resources.Add(new EmbeddedResource(container, stream.ToArray(), embedded.Attributes));
            }
            catch (Exception e)
            {
                // 重写不了就整张容器跳过（key 会在收尾时进 Missing，让人知道没打成）
                outcome.Missing.Add($"资源：{container}（重写失败：{e.Message}）");
            }
        }
    }

    /// <summary>
    ///     把译文写进内嵌 JSON 本地化表（<c>json:</c> 容器）：只补 zh 还是空的槽，
    ///     上游已有 zh 的键原样保留；整表重写（JsonNode 会保留其余结构与语言）。
    /// </summary>
    private static void PatchJSONResources(
        ModuleDefMD module,
        Dictionary<(string Container, string Key), UITextResourceEntry> resourceMap,
        HashSet<(string Container, string Key)> seen,
        UITextPatchOutcome outcome)
    {
        var byResource = new Dictionary<string, Dictionary<string, UITextResourceEntry>>(StringComparer.Ordinal);
        foreach (var ((container, key), entry) in resourceMap)
        {
            if (!UITextJSONResources.IsJSONContainer(container))
            {
                continue;
            }

            var resourceName = UITextJSONResources.ResourceNameOf(container);
            if (!byResource.TryGetValue(resourceName, out var keys))
            {
                keys = new Dictionary<string, UITextResourceEntry>(StringComparer.Ordinal);
                byResource[resourceName] = keys;
            }

            keys[key] = entry;
        }

        if (byResource.Count == 0)
        {
            return;
        }

        // module.Resources 是集合，替换时不能边遍历边改
        foreach (var resource in module.Resources.ToList())
        {
            if (resource is not EmbeddedResource embedded)
            {
                continue;
            }

            var name = embedded.Name?.String ?? string.Empty;
            if (!byResource.TryGetValue(name, out var keys))
            {
                continue;
            }

            try
            {
                var text = Encoding.UTF8.GetString(embedded.CreateReader().ToArray());
                if (!UITextJSONResources.TryParse(text, out _))
                {
                    outcome.Missing.Add($"内嵌 JSON：{name}（形状不像语言表，没写成）");
                    continue;
                }

                var translations = keys.ToDictionary(pair => pair.Key, pair => pair.Value.Translated, StringComparer.Ordinal);
                var updated = UITextJSONResources.Apply(text, translations, out var changed);
                foreach (var key in keys.Keys)
                {
                    // 没改动也可能是「上游已经有 zh」——都算处理过，不再报「没对上」
                    seen.Add((UITextJSONResources.Prefix + name, key));
                }

                if (updated is null)
                {
                    continue;
                }

                module.Resources.Remove(embedded);
                module.Resources.Add(new EmbeddedResource(name, new UTF8Encoding(false).GetBytes(updated), embedded.Attributes));
                outcome.PatchedResources += changed;
            }
            catch (Exception e)
            {
                outcome.Missing.Add($"内嵌 JSON：{name}（重写失败：{e.Message}）");
            }
        }
    }

    /// <summary>
    ///     内嵌 JSON 本地化表反向还原：当前 zh == 我们打过的译文才摘掉 zh 槽（不跨 key 猜）。
    /// </summary>
    private static void RevertJSONResources(ModuleDefMD module, UITextPack pack, UITextPatchOutcome outcome)
    {
        var byResource = new Dictionary<string, Dictionary<string, UITextResourceEntry>>(StringComparer.Ordinal);
        foreach (var entry in pack.Resources)
        {
            if (!entry.HasTranslation || pack.IsResourceSkipped(entry.Container, entry.Key)
                || !UITextJSONResources.IsJSONContainer(entry.Container))
            {
                continue;
            }

            var resourceName = UITextJSONResources.ResourceNameOf(entry.Container);
            if (!byResource.TryGetValue(resourceName, out var keys))
            {
                keys = new Dictionary<string, UITextResourceEntry>(StringComparer.Ordinal);
                byResource[resourceName] = keys;
            }

            keys[entry.Key] = entry;
        }

        if (byResource.Count == 0)
        {
            return;
        }

        foreach (var resource in module.Resources.ToList())
        {
            if (resource is not EmbeddedResource embedded)
            {
                continue;
            }

            var name = embedded.Name?.String ?? string.Empty;
            if (!byResource.TryGetValue(name, out var keys))
            {
                continue;
            }

            try
            {
                var text = Encoding.UTF8.GetString(embedded.CreateReader().ToArray());
                var translations = keys.ToDictionary(pair => pair.Key, pair => pair.Value.Translated, StringComparer.Ordinal);
                var updated = UITextJSONResources.Revert(text, translations, out var changed);
                if (updated is null)
                {
                    continue;
                }

                module.Resources.Remove(embedded);
                module.Resources.Add(new EmbeddedResource(name, new UTF8Encoding(false).GetBytes(updated), embedded.Attributes));
                outcome.PatchedResources += changed;
            }
            catch (Exception e)
            {
                outcome.Missing.Add($"内嵌 JSON：{name}（反向重写失败：{e.Message}）");
            }
        }
    }

    /// <summary>
    ///     写回模块：有 PDB 状态时按「内嵌可移植 PDB」写（保持调试目录）——
    ///     插件里用 <c>StackFrame.GetFileName()</c> 的代码靠它，丢了会 NRE（Collections 实测）。
    ///     写 PDB 失败先清掉无法序列化的本地常量再试一次；仍失败才退回不带 PDB 写（会记日志 + 留标记）。
    /// </summary>
    private static void WriteModule(ModuleDefMD module, string tempPath, UITextPatchOutcome outcome)
    {
        var options = new ModuleWriterOptions(module);
        if (module.PdbState is { } pdb)
        {
            pdb.PdbFileKind = PdbFileKind.EmbeddedPortablePDB;
            options.WritePdb = true;
        }

        try
        {
            module.Write(tempPath, options);
            return;
        }
        catch (Exception e) when (options.WritePdb)
        {
            // dnlib 写 Portable PDB 的已知硬伤：本地常量（const 局部）里出现它序列化不了的值时直接抛
            // （如 “Expected a null constant”——类类型非空常量，HaselTweaks 实测）。
            // 整份 PDB 抛弃太亏（文件名/行号才是关键），先把本地常量的值清成 null 再试一次。
            TryDelete(tempPath);
            // 关键：必须把常量「条目」整个移除，不能只把值清成 null——I4 常量的值是 null 时
            // 写入器同样拒绝（"Expected an Int32 constant"，2026-10-03 实测定型）。
            var dropped = DropPdbLocalConstants(module);
            if (dropped > 0)
            {
                try
                {
                    module.Write(tempPath, options);
                    Plugin.Log?.Information($"[内部文本] 调试符号已保留（丢掉了 {dropped} 个无法序列化的本地常量，文件名 / 行号不受影响）");
                    return;
                }
                catch (Exception retryError) when (options.WritePdb)
                {
                    Plugin.Log?.Warning(retryError, "[内部文本] 丢掉本地常量后仍写不出 PDB，退回不带 PDB 写");
                    TryDelete(tempPath);
                }
            }
            else
            {
                Plugin.Log?.Warning(e, "[内部文本] 保留调试符号失败，退回不带 PDB 写");
            }

            outcome.PdbDropped = true;
            module.Write(tempPath, new ModuleWriterOptions(module));
        }
    }

    /// <summary>把 PDB 里所有「本地常量」的条目整个移除（只影响调试器的常量显示，不影响文件名 / 行号）。返回移除条数。</summary>
    private static int DropPdbLocalConstants(ModuleDefMD module)
    {
        var count = 0;
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.Body?.PdbMethod?.Scope is not { } scope)
                {
                    continue;
                }

                count += DropScopeConstants(scope);
            }
        }

        return count;
    }

    private static int DropScopeConstants(PdbScope scope)
    {
        var count = scope.Constants.Count;
        scope.Constants.Clear();
        foreach (var child in scope.Scopes)
        {
            count += DropScopeConstants(child);
        }

        return count;
    }

    /// <summary>
    ///     这条译文在 ImGui 上「可能被当控件标签（### 会被剥掉）」还是「确定是纯文字绘制（### 会原样显示）」。
    ///     判据是抽取时记下的调用目标（RoleReason）：目标是 Text* / tooltip / 包装库画文字方法时不算标签；
    ///     没信息 / 其它目标保守当作标签（宁多一个后缀，也别让按钮因撞 ID 点不动；2026-10-03 评审 B-08）。
    /// </summary>
    public static bool MightBeLabel(UITextPackEntry entry)
    {
        var reason = entry.RoleReason ?? string.Empty;
        if (reason.Length == 0)
        {
            return true;
        }

        string? target = null;
        var arrow = reason.LastIndexOf('→');
        if (arrow >= 0)
        {
            target = reason[(arrow + 1)..].Trim();
        }
        else
        {
            var open = reason.IndexOf("UI（", StringComparison.Ordinal);
            var close = open >= 0 ? reason.IndexOf('）', open) : -1;
            if (open >= 0 && close > open)
            {
                target = reason[(open + 3)..close].Trim();
            }
        }

        if (string.IsNullOrEmpty(target))
        {
            return true;
        }

        var dot = target.LastIndexOf('.');
        var method = dot >= 0 ? target[(dot + 1)..] : target;
        var cut = method.IndexOfAny(['(', ' ', '>', ',']);
        if (cut > 0)
        {
            method = method[..cut];
        }

        return !UICallSemantics.IsPlainTextCallName(method);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 临时文件删不掉无所谓，下次会覆盖
        }
    }
}
