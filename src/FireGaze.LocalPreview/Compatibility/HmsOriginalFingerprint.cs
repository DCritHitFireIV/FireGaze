using System.Security.Cryptography;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace FireGaze.LocalPreview.Compatibility;

public sealed record HmsBinaryProfile(string Name, string Sha256, Guid Mvid, Version Version);

/// <summary>Original upstream release and the separately audited local dev build only.</summary>
public static class HmsOriginalFingerprint
{
    private static readonly HmsBinaryProfile[] Profiles = {
        new("upstream 1.0.4.2", "313A413D714C2C512CA8FD0844933C1E79335B284DE33045423C55E21DA08612",
            new Guid("88ae88af-b380-42a9-b8c6-bc8184a067a7"), new Version(1,0,4,2)),
        new("local dev 1.0.4.0", "674C2F2355364771F985CB9AA433FEDF6848DED72BE8DBFCD94300A8C6B35272",
            new Guid("3a80928e-b1e5-4295-abc9-96c4a2bdb65b"), new Version(1,0,4,0)),
    };

    public static HmsBinaryProfile? Identify(byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return Profiles.FirstOrDefault(profile => profile.Sha256 == hash);
    }

    public static void VerifyLoadedMethod(byte[] binary,MethodInfo method)
    {
        using var stream=new MemoryStream(binary,false);
        using var pe=new PEReader(stream);
        var metadata=pe.GetMetadataReader();
        var handle=MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00FFFFFF);
        var definition=metadata.GetMethodDefinition(handle);
        if(definition.RelativeVirtualAddress==0 || method.GetMethodBody()?.GetILAsByteArray() is not { } live
            || !live.SequenceEqual(pe.GetMethodBody(definition.RelativeVirtualAddress).GetILBytes()!))
            throw new InvalidOperationException("Loaded HMS method differs from audited binary: "+method.Name);
    }
}
