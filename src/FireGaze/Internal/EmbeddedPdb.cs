using System.IO.Compression;
using System.Reflection.PortableExecutable;

namespace FireGaze.Internal;

/// <summary>
///     读取 PE 里内嵌的可移植 PDB（<c>EmbeddedPortablePdb</c>），解压成 dnlib 可用的字节流。
/// </summary>
/// <remarks>
///     dnlib 的 PDB 读取只认磁盘上的 <c>.pdb</c>（CodeView 路径），不认 PE 里内嵌的 PDB；
///     补丁器写出时若没有 <c>PdbState</c> 就会把整个调试目录丢掉——插件里任何用
///     <c>StackFrame.GetFileName()</c> 的代码都会拿到 <c>null</c>。
///     2026-10-03 实测：Collections 的 <c>Dev.Stop → Log → StripDirectoryPath(null)</c>
///     因此在「启用插件」时抛 NullReferenceException。
///     这里把内嵌 PDB 解出来喂给 <c>ModuleCreationOptions.PdbFileOrData</c>，
///     写出时再用 <c>WritePdb</c> 写回内嵌形态。
/// </remarks>
internal static class EmbeddedPdb
{
    /// <summary>没有内嵌 PDB 或读取失败时返回 null（不抛）。</summary>
    public static byte[]? TryRead(string assemblyPath)
    {
        try
        {
            using var fs = File.OpenRead(assemblyPath);
            using var pe = new PEReader(fs);
            foreach (var entry in pe.ReadDebugDirectory())
            {
                if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb || entry.DataSize <= 8)
                {
                    continue;
                }

                // DataPointer 按规范是 RVA，但实际工具两种都出现过：先按 RVA 读，读不到再按文件偏移读
                var raw = ReadAtRva(pe, entry);
                if (raw is null || !HasMagic(raw))
                {
                    raw = ReadAtFileOffset(fs, entry);
                }

                if (raw is null || !HasMagic(raw))
                {
                    continue;
                }

                // 内嵌格式：'MPDB' + Int32 原始长度 + deflate 数据
                using var input = new MemoryStream(raw, 8, raw.Length - 8);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }
        catch
        {
            // 读不到就当没有：补丁照打，只是没有调试信息
        }

        return null;
    }

    private static bool HasMagic(byte[] raw) =>
        raw.Length >= 8 && raw[0] == (byte)'M' && raw[1] == (byte)'P' && raw[2] == (byte)'D' && raw[3] == (byte)'B';

    private static byte[]? ReadAtRva(PEReader pe, DebugDirectoryEntry entry)
    {
        try
        {
            var content = pe.GetSectionData(entry.DataPointer).GetContent(0, entry.DataSize);
            return content.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReadAtFileOffset(FileStream fs, DebugDirectoryEntry entry)
    {
        try
        {
            var raw = new byte[entry.DataSize];
            fs.Position = entry.DataPointer;
            var read = 0;
            while (read < raw.Length)
            {
                var count = fs.Read(raw, read, raw.Length - read);
                if (count <= 0)
                {
                    return null;
                }

                read += count;
            }

            return raw;
        }
        catch
        {
            return null;
        }
    }
}
