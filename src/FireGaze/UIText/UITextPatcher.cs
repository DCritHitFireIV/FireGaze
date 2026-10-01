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

    /// <summary>实际写进去的总条数（字面量 + 资源）。</summary>
    public int PatchedTotal => this.PatchedLiterals + this.PatchedResources;

    /// <summary>
    ///     DLL 里没找回来的条目（可能是插件更新过、或原文已经变了）。
    /// </summary>
    public List<string> Missing { get; } = [];

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
    public static UITextPatchOutcome Patch(string sourcePath, string targetPath, UITextPack pack, string? pluginVersion = null)
    {
        var outcome = new UITextPatchOutcome();
        var map = new Dictionary<string, UITextPackEntry>(StringComparer.Ordinal);
        foreach (var entry in pack.Entries)
        {
            if (!entry.HasTranslation || pack.IsSkipped(entry.Original))
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

        outcome.Candidates = map.Count + resourceMap.Count;
        if (outcome.Candidates == 0)
        {
            outcome.Error = "包里还没有可应用的译文。";
            return outcome;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenResources = new HashSet<(string Container, string Key)>();
        var tempPath = targetPath + ".fguitext.tmp";
        try
        {
            using (var module = ModuleDefMD.Load(sourcePath))
            {
                // ① 内嵌本地化资源（.resx / ResourceManager）：按「容器 + key」改值。
                //    只动主程序集的内嵌容器：官方 zh 卫星优先（ResourceManager 的查找顺序），
                //    我们只负责把它没覆盖的中性资源补上。
                if (resourceMap.Count > 0)
                {
                    PatchResources(module, resourceMap, seenResources, outcome);
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
                    outcome.Error = "DLL 里没有找到任何一条能替换的文本（可能版本对不上）。";
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

        return outcome;
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
