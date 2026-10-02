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
