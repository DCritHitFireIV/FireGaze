using System.Collections;
using System.Resources;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

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
        bool includeAmbiguous = false)
    {
        var outcome = new UITextPatchOutcome();
        var map = new Dictionary<string, UITextPackEntry>(StringComparer.Ordinal);
        foreach (var entry in pack.Entries)
        {
            if (!entry.HasTranslation || pack.IsSkipped(entry.Original) || !entry.IsPatchable(includeAmbiguous))
            {
                continue;
            }

            map[entry.Original] = entry;
        }

        var resourceMap = new Dictionary<(string Container, string Key), UITextResourceEntry>();
        foreach (var entry in pack.Resources)
        {
            if (!entry.HasTranslation || pack.IsResourceSkipped(entry.Container, entry.Key))
            {
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

            attributeMap[entry.Original] = entry;
        }

        outcome.Candidates = map.Count + resourceMap.Count + attributeMap.Count;
        if (outcome.Candidates == 0)
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
                // ① 内嵌本地化资源（.resx / ResourceManager）：按「容器 + key」改值。
                //    只动主程序集的内嵌容器：官方 zh 卫星优先（ResourceManager 的查找顺序），
                //    我们只负责把它没覆盖的中性资源补上。
                if (resourceMap.Count > 0)
                {
                    PatchResources(module, resourceMap, seenResources, outcome);
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
                            var replaced = UITextText.BuildPatched(literal, entry.Translated, entry.PreserveID);
                            if (string.Equals(replaced, literal, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            instruction.Operand = replaced;
                            outcome.PatchedLiterals++;
                        }
                    }
                }

                if (outcome.PatchedTotal == 0)
                {
                    // NoMatch：盘上可能已经是我们的旧补丁（记录丢了）——调用方据此做一次自愈重试，见 UITextPatchManager.Apply
                    outcome.NoMatch = true;
                    outcome.Error = $"译文有 {outcome.Candidates} 条，但在 DLL 里一条都没对上。到插件安装器里把这个插件重装一次（回到原版）再点「一键汉化」；行尾有「还原原文」时也可以先点它。";
                    return outcome;
                }

                // 版本号顺手写进注释？不需要——但保留参数是为了将来做「版本戳」用
                _ = pluginVersion;
                module.Write(tempPath);
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
            if (exactMap.TryGetValue(translated, out var existing) && !string.Equals(existing, original, StringComparison.Ordinal))
            {
                ambiguous.Add(translated);
            }
            else
            {
                exactMap[translated] = original;
            }
        }

        foreach (var value in ambiguous)
        {
            exactMap.Remove(value);
        }

        if (suffixMap.Count == 0 && exactMap.Count == 0)
        {
            outcome.Error = "译文包里没有可反向还原的译文。";
            return outcome;
        }

        var tempPath = targetPath + ".fguitext.tmp";
        try
        {
            using (var module = UIStringExtractor.LoadModule(sourcePath, searchDirectories))
            {
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

                            var recovered = RecoverLiteral(literal, suffixMap, exactMap);
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

                if (outcome.PatchedTotal == 0)
                {
                    outcome.Error = "这个 DLL 里没有找到我们打过的补丁痕迹。";
                    return outcome;
                }

                module.Write(tempPath);
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
    ///     按「<c>###原文</c> 后缀 / 译文精确匹配」还原一条字面量；还原不了返回 null（纯函数，方便离线测）。
    /// </summary>
    public static string? RecoverLiteral(
        string literal,
        IReadOnlyDictionary<string, string> suffixMap,
        IReadOnlyDictionary<string, string> exactMap)
    {
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

        return exactMap.TryGetValue(literal, out var byExact) && !string.Equals(byExact, literal, StringComparison.Ordinal)
            ? byExact
            : null;
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
                            items.Add((resourceKey, entry.Translated));
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
