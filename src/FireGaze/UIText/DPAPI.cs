using System.Runtime.InteropServices;
using System.Text;
using FireGaze.RepoAudit;

namespace FireGaze.UIText;

/// <summary>
///     Windows DPAPI（当前用户）：用户自己填的 API key 只在本机加密存放，配置文件里看不到明文。
/// </summary>
internal static partial class DPAPI
{
    private const int UIForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true)]
    private static partial int CryptProtectData(ref DataBlob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob output);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    private static partial int CryptUnprotectData(ref DataBlob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob output);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
    private static partial nint LocalFree(nint memory);

    // 解密结果缓存：设置面板每帧都会问一次，DPAPI 不该每帧调
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.Ordinal);

    /// <summary>
    ///     加密成 base64；失败返回空串（调用方据此提示「存不了」）。
    /// </summary>
    public static string ProtectToBase64(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var input = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            if (CryptProtectData(ref input, 0, 0, 0, 0, UIForbidden, out var output) == 0)
            {
                return string.Empty;
            }

            try
            {
                var protectedBytes = new byte[output.Size];
                Marshal.Copy(output.Data, protectedBytes, 0, output.Size);
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        catch (Exception e)
        {
            PluginLogFallback.Write("[内部文本] DPAPI 加密失败：" + e.Message);
            return string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }

    /// <summary>
    ///     解密 base64；解不开（换机器 / 换用户 / 手工改过）返回 null。
    /// </summary>
    public static string? UnprotectFromBase64(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
        {
            return null;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(protectedBase64, out var cached))
            {
                return cached;
            }

            if (Cache.Count > 64)
            {
                Cache.Clear();
            }
        }

        var result = UnprotectCore(protectedBase64);
        lock (Cache)
        {
            Cache[protectedBase64] = result;
        }

        return result;
    }

    private static string? UnprotectCore(string protectedBase64)
    {

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(protectedBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        var input = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            if (CryptUnprotectData(ref input, 0, 0, 0, 0, UIForbidden, out var output) == 0)
            {
                return null;
            }

            try
            {
                var plain = new byte[output.Size];
                Marshal.Copy(output.Data, plain, 0, output.Size);
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        catch (Exception e)
        {
            PluginLogFallback.Write("[内部文本] DPAPI 解密失败：" + e.Message);
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }

    /// <summary>
    ///     界面上只显示尾 4 位。
    /// </summary>
    public static string Mask(string? secret) =>
        string.IsNullOrEmpty(secret) ? string.Empty : "尾 " + (secret.Length <= 4 ? secret : secret[^4..]);
}
