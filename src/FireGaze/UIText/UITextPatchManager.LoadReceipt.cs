using System.Text.Json;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using FireGaze.RepoAudit;

namespace FireGaze.UIText;

internal sealed partial class UITextPatchManager
{
    private sealed record LoadReceipt(object Target, string Name, string? Version, string? MainPath,
        LiveLoadTarget LiveTarget, string State, LoadFiles Files, Assembly? PreviousAssembly,
        AssemblyLoadContext? PreviousContext, ActivatedLoad? Activated = null);

    private sealed record LoadFiles(string Signature, MainAssemblyImage Main);
    private sealed record MainAssemblyImage(string FullName, Guid Mvid, string MetadataHash);
    private sealed record ActivatedLoad(Assembly Assembly, AssemblyLoadContext Context);

    private sealed record LiveLoadTarget(string MainPath, string Name, string Version, Guid WorkingID);

    // Only a successfully completed Enable/Reload may create an entry. Keep a
    // failed persistence attempt for the next inspection, bound to the same
    // target and exact bytes; a name/loaded snapshot cannot manufacture one.
    private readonly Dictionary<string, (InstalledPluginEntry Entry, LoadReceipt Receipt)> completedLoads =
        new(StringComparer.Ordinal);

    private bool ConfirmCompletedLoad(InstalledPluginEntry entry, LoadReceipt receipt) =>
        this.WithFileOwner(() =>
        {
            if (!TryReadActivatedLoad(entry, receipt, out var activated)) return false;
            this.completedLoads[entry.InternalName] = (entry, receipt with { Activated = activated });
            return this.ConfirmRecordedLoad(entry.InternalName);
        }, false);

    // Called only under the admitted file owner/applyGate.
    private bool ConfirmRecordedLoad(string internalName)
    {
        if (!this.completedLoads.TryGetValue(internalName, out var completed)) return false;
        try
        {
            var state = this.store.LoadForConfirmation(internalName);
            if (!completed.Entry.IsLoaded || ReadLoadProperty(completed.Entry.RawPlugin, "IsLoaded") is not true
                || !this.MatchesLoadReceipt(completed.Entry, completed.Receipt, state))
            {
                this.completedLoads.Remove(internalName);
                return false;
            }
            if (state is null || !state.PendingVerify)
            {
                this.completedLoads.Remove(internalName);
                return true;
            }
            if (!state.IsWriteComplete || !string.IsNullOrEmpty(state.LastError))
            {
                this.completedLoads.Remove(internalName);
                return false;
            }
            if (!this.store.Save(state with { PendingVerify = false })) return false;
            this.completedLoads.Remove(internalName);
            return true;
        }
        catch (IOException)
        {
            // Unreadable identity is not a successful verification. Keep the
            // in-memory receipt so a later retry must recheck the entire set.
            return false;
        }
    }

    private void ConfirmRecordedLoads()
    {
        foreach (var name in this.completedLoads.Keys.ToArray())
            if (!this.reloading.Contains(name)) this.ConfirmRecordedLoad(name);
    }

    private LoadReceipt CaptureLoadReceipt(InstalledPluginEntry entry, UITextPatchState? state)
    {
        if (this.store.HasPendingOperation(entry.InternalName)) throw new IOException("文件操作尚未结束，未开始加载。");
        if (state is not null)
        {
            if (!state.IsWriteComplete || state.InternalName != entry.InternalName)
                throw new IOException("补丁文件身份与加载目标不一致。");
        }
        return new(entry.RawPlugin, entry.InternalName, entry.Version, entry.DLLPath, ReadLiveLoadTarget(entry),
            JsonSerializer.Serialize(state), this.LoadFileSignature(entry, state, requireCommittedBytes: true),
            ReadLoadProperty(entry.RawPlugin, "Assembly") as Assembly, ReadLoaderContext(entry.RawPlugin));
    }

    private bool MatchesLoadReceipt(InstalledPluginEntry entry, LoadReceipt receipt) =>
        this.MatchesLoadReceipt(entry, receipt, this.store.LoadForConfirmation(entry.InternalName));

    private bool MatchesLoadReceipt(InstalledPluginEntry entry, LoadReceipt receipt, UITextPatchState? current)
    {
        if (!ReferenceEquals(entry.RawPlugin, receipt.Target) || entry.InternalName != receipt.Name
            || entry.Version != receipt.Version || entry.DLLPath != receipt.MainPath
            || this.store.HasPendingOperation(entry.InternalName)
            || ReadLiveLoadTarget(entry) != receipt.LiveTarget
            || receipt.Activated is not null && (!TryReadActivatedLoad(entry, receipt, out var currentActivation)
                || !ReferenceEquals(currentActivation!.Assembly, receipt.Activated.Assembly)
                || !ReferenceEquals(currentActivation.Context, receipt.Activated.Context))) return false;
        return JsonSerializer.Serialize(current) == receipt.State
            && this.LoadFileSignature(entry, current) == receipt.Files;
    }

    private static AssemblyLoadContext? ReadLoaderContext(object raw)
    {
        for (var type = raw.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField("loader", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null) return ReadLoadProperty(field.GetValue(raw), "LoadContext") as AssemblyLoadContext;
        }
        return null;
    }

    private static unsafe bool TryReadActivatedLoad(InstalledPluginEntry entry, LoadReceipt receipt, out ActivatedLoad? activated)
    {
        activated = null;
        var assembly = ReadLoadProperty(entry.RawPlugin, "Assembly") as Assembly;
        var context = ReadLoaderContext(entry.RawPlugin);
        if (assembly is null || context is null || !context.IsCollectible
            || !ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly), context)
            || ReferenceEquals(assembly, receipt.PreviousAssembly) || ReferenceEquals(context, receipt.PreviousContext)
            || assembly.FullName != receipt.Files.Main.FullName
            || assembly.ManifestModule.ModuleVersionId != receipt.Files.Main.Mvid
            || !assembly.TryGetRawMetadata(out var metadata, out var length)
            || length <= 0 || Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(metadata, length)))
                != receipt.Files.Main.MetadataHash) return false;
        // LoadFromStream leaves Location empty in the CN host. Use its actual
        // Assembly/loader ALC and a metadata witness, never a Location fallback.
        // Metadata includes rewritten user strings, but is not a hash of all
        // loaded IL, managed resource payloads, or native dependencies.
        activated = new(assembly, context);
        return true;
    }

    private static LiveLoadTarget ReadLiveLoadTarget(InstalledPluginEntry entry)
    {
        var manifest = ReadLoadProperty(entry.RawPlugin, "Manifest");
        var dll = ReadLoadProperty(entry.RawPlugin, "DllFile") as FileInfo;
        var name = ReadLoadProperty(manifest, "InternalName") as string;
        var version = ReadLoadProperty(manifest, "AssemblyVersion") as Version;
        var workingID = ReadLoadProperty(entry.RawPlugin, "EffectiveWorkingPluginId");
        if (dll is null || string.IsNullOrEmpty(name) || version is null
            || workingID is not Guid id || id == Guid.Empty
            || string.IsNullOrEmpty(entry.DLLPath)
            || !string.Equals(dll.FullName, Path.GetFullPath(entry.DLLPath), StringComparison.OrdinalIgnoreCase)
            || name != entry.InternalName || version.ToString() != entry.Version)
            throw new IOException("实际加载目标身份缺失或与插件索引不一致，未开始加载或确认。");
        return new(dll.FullName, name, version.ToString(), id);
    }

    private static object? ReadLoadProperty(object? target, string name)
    {
        for (var type = target?.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null) return property.GetValue(target);
        }
        return null;
    }

    private LoadFiles LoadFileSignature(InstalledPluginEntry entry, UITextPatchState? state, bool requireCommittedBytes = false)
    {
        var expectedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paths = UITextRules.ResolveCompanions(entry.DLLPath, entry.InternalName);
        if (!string.IsNullOrEmpty(entry.DLLPath)) paths.Add(Path.GetFullPath(entry.DLLPath));
        if (state is not null)
        {
            if (!UITextFileIdentity.TryMap(state, entry.DLLPath ?? string.Empty, out var mapped, out var error))
                throw new IOException(error);
            paths.AddRange(mapped.Keys);
            if (requireCommittedBytes)
            {
                foreach (var (path, file) in mapped)
                    expectedHashes[path] = string.IsNullOrEmpty(file.PatchedHash) ? file.SourceHash : file.PatchedHash;
                var main = Path.GetFullPath(entry.DLLPath!);
                if (!mapped.ContainsKey(main))
                {
                    if (!Guid.TryParse(state.OperationID, out _)
                        || !string.Equals(Path.GetFileName(state.DLLPath), Path.GetFileName(main), StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrEmpty(state.SourceHash)
                        || !string.Equals(state.SourceHash, state.PatchedHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("补丁文件身份与加载目标不一致。");
                    expectedHashes[main] = state.SourceHash;
                }
            }
        }
        var mainPath = Path.GetFullPath(entry.DLLPath!);
        MainAssemblyImage? mainImage = null;
        var hashes = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                if (!string.Equals(path, mainPath, StringComparison.OrdinalIgnoreCase))
                    return (Path: path, Hash: UITextPatchStore.HashOf(path));
                var bytes = File.ReadAllBytes(path);
                using var pe = new PEReader(new MemoryStream(bytes, writable: false));
                var metadata = pe.GetMetadataReader();
                var definition = metadata.GetAssemblyDefinition();
                var name = new AssemblyName { Name = metadata.GetString(definition.Name), Version = definition.Version,
                    CultureName = definition.Culture.IsNil ? string.Empty : metadata.GetString(definition.Culture),
                    Flags = (AssemblyNameFlags)definition.Flags };
                if (!definition.PublicKey.IsNil && metadata.GetBlobBytes(definition.PublicKey) is { Length: > 0 } publicKey)
                    name.SetPublicKey(publicKey);
                else name.SetPublicKeyToken([]);
                mainImage = new(name.FullName!, metadata.GetGuid(metadata.GetModuleDefinition().Mvid),
                    Convert.ToHexString(SHA256.HashData(pe.GetMetadata().GetContent().AsSpan())));
                return (Path: path, Hash: Convert.ToHexString(SHA256.HashData(bytes)));
            }).ToList();
        if (hashes.Any(file => file.Hash.Length == 0)) throw new IOException("无法核对加载文件身份。");
        // Validate exactly the bytes recorded in the receipt. A separate
        // validation read followed by a second capture could accept a file
        // replaced between those reads as the earlier committed patch.
        foreach (var (path, hash) in hashes)
            if (expectedHashes.TryGetValue(path, out var expected)
                && (expected.Length == 0 || !string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("补丁文件已被替换，未开始加载或确认。");
        return new(string.Join("\n", hashes.Select(file => file.Path + ":" + file.Hash)),
            mainImage ?? throw new IOException("缺少主程序集 PE 身份，未开始加载或确认。"));
    }
}
