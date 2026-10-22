using System.Runtime.InteropServices;

namespace FireGaze.PortraitPreview;

[StructLayout(LayoutKind.Explicit, Size = 44)]
internal readonly record struct PortraitOwnedD3D11Texture2DDesc(
    [field: FieldOffset(0)] uint Width, [field: FieldOffset(4)] uint Height,
    [field: FieldOffset(8)] uint MipLevels, [field: FieldOffset(12)] uint ArraySize,
    [field: FieldOffset(16)] uint Format, [field: FieldOffset(20)] uint SampleCount,
    [field: FieldOffset(24)] uint SampleQuality, [field: FieldOffset(28)] uint Usage,
    [field: FieldOffset(32)] uint BindFlags, [field: FieldOffset(36)] uint CPUAccessFlags,
    [field: FieldOffset(40)] uint MiscFlags);

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal readonly record struct PortraitOwnedD3D11SrvDesc(
    [field: FieldOffset(0)] uint Format, [field: FieldOffset(4)] uint Dimension,
    [field: FieldOffset(8)] uint MostDetailedMip, [field: FieldOffset(12)] uint MipLevels,
    [field: FieldOffset(16)] uint Union2 = 0, [field: FieldOffset(20)] uint Union3 = 0);

// Canonical pointers are detached equality identities after their temporary AddRefs release.
// They are never dereferenced by asynchronous image work.
internal readonly record struct PortraitOwnedSceneRenderTargetComDescription(nint CanonicalResource,
    nint CanonicalDevice, PortraitOwnedD3D11Texture2DDesc Texture, PortraitOwnedD3D11SrvDesc View)
{
    internal bool IsValid => CanonicalResource != 0 && CanonicalDevice != 0 && Texture.Width > 0 && Texture.Height > 0
        && Texture.MipLevels > 0 && Texture.ArraySize == 1 && Texture.SampleCount == 1 && Texture.SampleQuality == 0
        && (Texture.BindFlags & 0x28) == 0x28 && View.Dimension == 4 && View.MostDetailedMip == 0
        && (View.MipLevels == uint.MaxValue || View.MipLevels is > 0 && View.MipLevels <= Texture.MipLevels)
        && CompatibleColorFormat(Texture.Format, View.Format);

    private static bool CompatibleColorFormat(uint resource, uint view)
        => view is 28 or 29 or 87 or 91 && (resource == view
            || resource == 27 && view is 28 or 29 || resource == 90 && view is 87 or 91);
}

internal interface IPortraitOwnedSceneRenderTargetComApi
{
    int QueryInterface(nint source, Guid iid, out nint result);
    bool GetResource(nint srv, out nint resource);
    bool GetDevice(nint child, out nint device);
    bool GetTextureDesc(nint texture, out PortraitOwnedD3D11Texture2DDesc descriptor);
    bool GetViewDesc(nint srv, out PortraitOwnedD3D11SrvDesc descriptor);
    bool Release(nint owned);
}

internal sealed class PortraitOwnedSceneRenderTargetComReader(IPortraitOwnedSceneRenderTargetComApi api,
    Func<nint> readUIDevice, Func<bool>? admission = null)
{
    private readonly PortraitCaptureRetirement physical = new();
    internal static readonly Guid Unknown = new("00000000-0000-0000-c000-000000000046");
    internal static readonly Guid Srv = new("b0e06fe0-8192-4e1a-b1ca-36d7414710b2");
    internal static readonly Guid Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    internal static readonly Guid Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    internal bool IsPhysicallyClean => physical.CleanupFailures == 0;
    internal Task DrainPhysicalAsync() => physical.Seal();
    internal bool TryRead(PortraitOwnedSceneRenderTargetRead kernel, Func<bool> current,
        out PortraitOwnedSceneRenderTargetComDescription descriptor)
    {
        descriptor = default;
        if (!kernel.IsKernelValid || current is null || !Current(current) || !physical.TryEnter(out var held)) return false;
        var refs = new List<OwnedReference>(14);
        var candidate = default(PortraitOwnedSceneRenderTargetComDescription);
        var accepted = false;
        bool Probe()
        {
            // Query the borrowed slot SRV into an independently owned exact interface before
            // using its inherited COM methods. Every returned COM object owns one AddRef.
            if (!Query(kernel.Srv, Srv, current, refs, out var srv)
                || !Get(api.GetResource, srv, current, refs, out var resource)
                || !Query(resource, Texture2D, current, refs, out var texture)
                || !Query(kernel.Resource, Texture2D, current, refs, out var kernelTexture)
                || !Query(texture, Unknown, current, refs, out var resourceIdentity)
                || !Query(kernelTexture, Unknown, current, refs, out var kernelIdentity)
                || resourceIdentity != kernelIdentity
                || !Get(api.GetDevice, srv, current, refs, out var srvDevice)
                || !Get(api.GetDevice, texture, current, refs, out var textureDevice)
                || !Current(current)) return false;
            // IUiBuilder.DeviceHandle is a BORROWED IUnknown from the active UI backend.
            // Never Release its raw value. QI owns the exact device reference used below.
            var uiDevice = readUIDevice();
            if (uiDevice == 0 || !Current(current)
                || !Query(uiDevice, Device, current, refs, out var publicDevice)
                || !Query(srvDevice, Unknown, current, refs, out var srvIdentity)
                || !Query(textureDevice, Unknown, current, refs, out var textureIdentity)
                || !Query(publicDevice, Unknown, current, refs, out var publicIdentity)
                || srvIdentity != textureIdentity || srvIdentity != publicIdentity
                || !Current(current) || !api.GetTextureDesc(texture, out var textureBefore) || !Current(current)
                || !api.GetViewDesc(srv, out var viewBefore) || !Current(current)
                || !api.GetTextureDesc(texture, out var textureAfter) || !Current(current)
                || !api.GetViewDesc(srv, out var viewAfter) || !Current(current)
                || textureBefore != textureAfter || viewBefore != viewAfter
                || textureBefore.Width != kernel.AllocatedWidth || textureBefore.Height != kernel.AllocatedHeight
                || readUIDevice() != uiDevice || !Current(current)) return false;
            candidate = new(resourceIdentity, srvIdentity, textureBefore, viewBefore);
            return candidate.IsValid;
        }
        try { accepted = Probe(); }
        catch (Exception error) { physical.ObserveFailure(error); }
        finally
        {
            // Cleanup uses only independently owned COM references, not the scene lease.
            // It is legal after current() becomes false; an uncertain Release is quarantined
            // and never retried. All operations are bounded, with no COM polling/retry loop.
            for (var i = refs.Count - 1; i >= 0; i--) physical.Release(refs[i]);
            held.Dispose();
        }
        if (!IsPhysicallyClean)
            throw new PortraitCaptureCleanupException(refs, [new InvalidOperationException("Temporary owned COM reference cleanup is uncertain.")]);
        if (!accepted || !Current(current)) return false;
        descriptor = candidate; return true;
    }

    private bool Current(Func<bool> current)
    { try { return admission?.Invoke() == true && IsPhysicallyClean && current(); } catch { return false; } }

    private bool Query(nint source, Guid iid, Func<bool> current, List<OwnedReference> refs, out nint value)
    {
        value = 0;
        if (source == 0 || refs.Count >= 14 || !Current(current)) return false;
        var status = api.QueryInterface(source, iid, out var result);
        if (result != 0)
        {
            if (status < 0) throw Ambiguous(result, refs);
            refs.Add(new(api, result));
        }
        value = result;
        return status >= 0 && value != 0 && Current(current);
    }
    private delegate bool Getter(nint source, out nint value);
    private bool Get(Getter getter, nint source, Func<bool> current, List<OwnedReference> refs, out nint value)
    {
        value = 0;
        if (source == 0 || refs.Count >= 14 || !Current(current)) return false;
        var returned = getter(source, out var result);
        if (result != 0)
        {
            if (!returned) throw Ambiguous(result, refs);
            refs.Add(new(api, result));
        }
        value = result;
        return returned && value != 0 && Current(current);
    }
    private static PortraitCaptureCleanupException Ambiguous(nint value, List<OwnedReference> refs)
    {
        var uncertain = new OwnedReference(null, value); refs.Add(uncertain);
        return new([uncertain], [new InvalidOperationException("Failed COM call returned a non-null pointer with unknown ownership.")]);
    }
    private sealed class OwnedReference(IPortraitOwnedSceneRenderTargetComApi? owner, nint value) : IDisposable
    {
        private int attempted;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0 || owner is null || !owner.Release(value))
                throw new InvalidOperationException("Owned COM Release could not be established; no retry permitted.");
        }
    }
}
