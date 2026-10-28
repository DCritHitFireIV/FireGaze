namespace FireGaze.LocalPreview.Runtime;

internal interface INativeIdleGateCodeImage
{
    bool IsFrameworkThread { get; }
    nint ModuleBase { get; }
    int ImageSize { get; }
    string GameSha256 { get; }
    string SdkSha256 { get; }
    Guid SdkMvid { get; }
    bool TryRead(nint address, Memory<byte> destination);
    bool IsExecutable(nint address, int length);
}

internal sealed unsafe class NativeCompanionIdleFollowGateway(INativeIdleGateCodeImage image) : INativeIdleFollowGateway
{
    private int qualified;
    private readonly byte[] tableBytes = new byte[8];
    private readonly byte[][] runtimeBuffers = NativeCompanionIdleGateManifest.Spans.Select(x => new byte[x.Bytes.Length]).ToArray();
    public bool IsFrameworkThread => image.IsFrameworkThread;
    public nint CompanionVirtualTable => image.ModuleBase + 0x21960D0;
    public string Failure { get; private set; } = "not-qualified";
    /// <summary>Diagnostics for the last loaded-image check. Verification reads the
    /// already loaded process image through the framework reader; it never opens the
    /// executable or SDK on a per-frame path.</summary>
    public int LastReadCount { get; private set; }
    public int LastReadBytes { get; private set; }
    public bool LastVerificationUsedDisk => false;
    public bool Verify()
    {
        Volatile.Write(ref qualified, 0);
        LastReadCount = 0;
        LastReadBytes = 0;
        try
        {
            Failure = "runtime-identity";
            if (!image.IsFrameworkThread || image.ModuleBase <= 0 || image.ImageSize < 0x2196210
                || image.ModuleBase > nint.MaxValue - image.ImageSize
                || !string.Equals(image.GameSha256, NativeCompanionIdleGateManifest.GameSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(image.SdkSha256, NativeCompanionIdleGateManifest.SdkSha256, StringComparison.OrdinalIgnoreCase)
                || image.SdkMvid != NativeCompanionIdleGateManifest.SdkMvid) return false;
            Failure = "companion-slot39";
            if (!TryRead(CompanionVirtualTable + 39 * 8, tableBytes)
                || BitConverter.ToInt64(tableBytes) != (long)(image.ModuleBase + 0x9076D0)) return false;
            for (var index = 0; index < NativeCompanionIdleGateManifest.Spans.Length; index++)
            {
                var span = NativeCompanionIdleGateManifest.Spans[index];
                Failure = $"native-code-{span.Rva:X}";
                if (span.Rva <= 0 || span.Bytes.Length <= 0 || span.Rva > image.ImageSize - span.Bytes.Length
                    || !image.IsExecutable(image.ModuleBase + span.Rva, span.Bytes.Length)) return false;
                var bytes = runtimeBuffers[index];
                if (!TryRead(image.ModuleBase + span.Rva, bytes) || !bytes.AsSpan().SequenceEqual(span.Bytes)) return false;
            }
            Failure = "";
            Volatile.Write(ref qualified, 1);
            return true;
        }
        catch (Exception) { Failure = "qualification-exception"; return false; }
    }
    private bool TryRead(nint address, Memory<byte> destination)
    {
        LastReadCount++;
        LastReadBytes += destination.Length;
        return image.TryRead(address, destination);
    }
    public bool TryInvoke(nint child, byte nativeGate)
    {
        // The coordinator verifies code, then revalidates the exact raw actor pair.
        // Do not insert another fallible read/callback between that final capture
        // and the native boundary. A proof can be consumed only once.
        if (child <= 0 || nativeGate != 0 || !image.IsFrameworkThread || Interlocked.Exchange(ref qualified, 0) != 1)
            return false;
        ((delegate* unmanaged<nint, byte, void>)(image.ModuleBase + 0x1750310))(child, 0);
        return true;
    }
}
