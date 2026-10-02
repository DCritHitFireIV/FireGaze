using System.Text;

namespace FireGaze.Internal;

/// <summary>
///     原子写文件：先写同目录的 <c>.tmp</c>、再 <c>File.Move</c> 覆盖。
/// </summary>
/// <remarks>
///     用途（2026-10-02 审计后统一）：词表、贡献记录、库缓存、导出文件这类「写一半崩了会丢数据 /
///     留半截文件」的写盘都走这里。补丁状态、译文包等之前就已经是「临时文件 + 替换」，这次把
///     其余的直接覆盖写补齐。
///     编码由调用方显式传入，**保持各处原来的编码**（带 BOM / 不带 BOM 不一，别在替换时顺手改掉）。
/// </remarks>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content, Encoding? encoding = null)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    ///     用 <paramref name="sourcePath" /> 覆盖 <paramref name="targetPath" />：先拷到同目录临时文件，再原子替换。
    ///     <para>
    ///     不要直接 <c>File.Copy(source, target, overwrite: true)</c> 覆盖插件 DLL：目标文件被进程占用时会被
    ///     拒绝写入（共享冲突），而临时文件 + <c>File.Move</c> 替换不受影响——2026-10-03「还原原文」在已加载
    ///     插件上报红的根因（写入补丁走的本来就是 Move，所以只有还原会失败）。
    ///     </para>
    /// </summary>
    public static void ReplaceFrom(string sourcePath, string targetPath)
    {
        var temp = targetPath + ".fg-replace.tmp";
        try
        {
            File.Copy(sourcePath, temp, overwrite: true);
            File.Move(temp, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
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
        catch
        {
            // 清理临时文件失败不影响调用方拿到的原始异常
        }
    }

    public static async Task WriteAllTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public static async Task WriteAllTextAsync(
        string path,
        string content,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, encoding, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
