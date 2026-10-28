using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using FireGaze.RepoAudit;

namespace FireGaze.UIText;

/// <summary>Acquire the installed build's original package without reinstalling or guessing translated identifiers.</summary>
internal sealed class UITextOriginalRecovery
{
    private const int MaxPackageBytes = 128 * 1024 * 1024;
    private const int MaxMetadataBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly string directory;
    private readonly HttpClient client;
    private readonly object gate;
    private readonly Func<bool>? admission;

    public UITextOriginalRecovery(string directory, HttpClient? client = null, object? gate = null, Func<bool>? admission = null)
    {
        this.directory = directory;
        this.client = client ?? Client;
        this.gate = gate ?? new object();
        this.admission = admission;
    }

    public async Task<UITextPatchState> RecoverAsync(InstalledPluginEntry entry, UITextPatchStore store, CancellationToken cancel)
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);
        var repos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddManifest(object? manifest)
        {
            if (manifest is null || Read(manifest, "InternalName") as string != entry.InternalName) return;
            AddGitHubRepo(Read(manifest, "RepoUrl") as string, repos);
            var testing = Read(entry.Manifest, "Testing") is true;
            var version = Read(manifest, testing ? "TestingAssemblyVersion" : "AssemblyVersion")?.ToString();
            if (!SameVersion(version, entry.Version)) return;
            AddURL(Read(manifest, testing ? "DownloadLinkTesting" : "DownloadLinkInstall") as string, urls);
            if (!testing) AddURL(Read(manifest, "DownloadLinkUpdate") as string, urls);
        }
        AddManifest(entry.Manifest);
        // Official DIP17 URLs and enabled third-party URLs are already resolved by the native repository loader.
        foreach (var manifest in AvailableManifests(entry)) AddManifest(manifest);
        if (entry.IsThirdParty && Uri.TryCreate(entry.RepositoryURL, UriKind.Absolute, out var repository)
            && repository.Scheme is "https" or "http")
        {
            try
            {
                using var json = ParseMetadata(await this.FetchAsync(repository, MaxMetadataBytes, cancel).ConfigureAwait(false));
                if (json.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var manifest in json.RootElement.EnumerateArray())
                    {
                        if (Text(manifest, "InternalName") != entry.InternalName) continue;
                        AddGitHubRepo(Text(manifest, "RepoUrl"), repos);
                        var testing = Read(entry.Manifest, "Testing") is true;
                        if (!SameVersion(Text(manifest, testing ? "TestingAssemblyVersion" : "AssemblyVersion"), entry.Version)) continue;
                        AddURL(Text(manifest, testing ? "DownloadLinkTesting" : "DownloadLinkInstall"), urls);
                        if (!testing) AddURL(Text(manifest, "DownloadLinkUpdate"), urls);
                    }
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
            { Plugin.Log?.Debug("[内部文本] 原始包仓库读取暂不可用：{Name}，{Reason}", entry.InternalName, e.GetBaseException().Message); }
        }
        foreach (var url in urls.ToArray()) AddGitHubRepo(url, repos);
        Directory.CreateDirectory(this.directory);
        var temporary = Path.Combine(this.directory, Guid.NewGuid() + ".zip");
        try
        {
            // Try direct installed-version links first; release history is used only if the repository moved on.
            foreach (var url in urls.Take(8))
                if (await this.TryPackageAsync(url, temporary, entry, store, cancel).ConfigureAwait(false) is { } recovered) return recovered;
            foreach (var repo in repos.Take(2))
            {
                try
                {
                    using var releases = ParseMetadata(await this.FetchAsync(new Uri($"https://api.github.com/repos/{repo}/releases?per_page=100"), MaxMetadataBytes, cancel).ConfigureAwait(false));
                    foreach (var release in releases.RootElement.EnumerateArray())
                    {
                        // Tags are only search hints. The package, MVID and executable code must still match.
                        var tag = Text(release, "tag_name");
                        if (!SameVersion(tag?.TrimStart('v', 'V'), entry.Version)) continue;
                        if (!release.TryGetProperty("assets", out var assets)) continue;
                        foreach (var asset in assets.EnumerateArray().Where(a => Text(a, "name")?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true).Take(8))
                            if (Text(asset, "browser_download_url") is { } url
                                && await this.TryPackageAsync(url, temporary, entry, store, cancel).ConfigureAwait(false) is { } recovered) return recovered;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
                { Plugin.Log?.Debug("[内部文本] 原始包历史读取暂不可用：{Name}，{Reason}", entry.InternalName, e.GetBaseException().Message); }
            }
            throw new UITextRecoveryPendingException();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<UITextPatchState?> TryPackageAsync(string url, string path, InstalledPluginEntry entry, UITextPatchStore store, CancellationToken cancel)
    {
        try
        {
            await File.WriteAllBytesAsync(path, await this.FetchAsync(new Uri(url), MaxPackageBytes, cancel).ConfigureAwait(false), cancel).ConfigureAwait(false);
            lock (this.gate)
            {
                if (this.admission is not null && !this.admission())
                    throw new IOException("原始包下载期间操作身份已变化，未更新恢复记录。");
                return AdoptPackage(entry, path, store, cancel);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        { Plugin.Log?.Debug("[内部文本] 原始包未能匹配：{Name}，{Reason}", entry.InternalName, e.GetBaseException().Message); return null; }
    }

    private async Task<byte[]> FetchAsync(Uri url, int limit, CancellationToken cancel)
    {
        if (url.Scheme is not ("https" or "http")) throw new IOException("原始包地址无效");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                deadline.CancelAfter(TimeSpan.FromSeconds(25));
                var token = deadline.Token;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("FireGaze-original-recovery/1.0");
                using var response = await this.client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > limit) throw new IOException("原始包超过大小上限");
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var bytes = new MemoryStream();
                var buffer = new byte[65536];
                int read;
                while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    if (bytes.Length + read > limit) throw new IOException("原始包超过大小上限");
                    bytes.Write(buffer, 0, read);
                }
                return bytes.ToArray();
            }
            catch (Exception e) when (attempt < 2 && !cancel.IsCancellationRequested && e is HttpRequestException or TaskCanceledException)
            { await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancel).ConfigureAwait(false); }
        }
    }

    public static UITextPatchState AdoptPackage(InstalledPluginEntry entry, string zipPath, UITextPatchStore store, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        UITextPatchState? ReadCompletedState()
        {
            var state = store.Load(entry.InternalName);
            if (store.HasPendingOperation(entry.InternalName) || state is { IsWriteComplete: false })
                throw new IOException("文件操作尚未结束，不能用下载的原始包覆盖恢复记录。");
            if (state is not null && !state.InternalName.Equals(entry.InternalName, StringComparison.Ordinal))
                throw new IOException("补丁记录的插件身份不匹配。");
            return state;
        }
        _ = ReadCompletedState();
        if (entry.DLLPath is null || !Path.IsPathFullyQualified(entry.DLLPath) || !File.Exists(entry.DLLPath))
            throw new IOException("插件文件已变化");
        var mainPath = Path.GetFullPath(entry.DLLPath);
        var installed = UITextRules.ResolveCompanions(mainPath, entry.InternalName).Select(Path.GetFullPath).ToList();
        var signature = UITextMaintenance.FileSignature(installed);
        var packageDirectory = Path.GetFullPath(Path.GetDirectoryName(zipPath)!);
        var staging = Path.GetFullPath(Path.Combine(packageDirectory, "original-" + Guid.NewGuid()));
        if (!staging.StartsWith(packageDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("原始包临时目录无效");
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var mainName = Path.GetFileName(mainPath);
            var main = FindEntry(archive, mainName);
            var prefix = main.FullName[..^mainName.Length];
            var manifestEntry = archive.GetEntry(prefix + Path.ChangeExtension(mainName, ".json"));
            if (manifestEntry is null || manifestEntry.Length > MaxMetadataBytes) throw new IOException("原始包缺少插件清单");
            using (var stream = manifestEntry.Open())
            using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true))
            using (var manifest = JsonDocument.Parse(reader.ReadToEnd()))
                if (Text(manifest.RootElement, "InternalName") != entry.InternalName
                    || !SameVersion(Text(manifest.RootElement, "AssemblyVersion"), entry.Version))
                    throw new IOException("原始包的名称或版本不匹配");
            var pairs = new List<(string Current, string Original)>();
            foreach (var current in installed)
            {
                cancel.ThrowIfCancellationRequested();
                var name = Path.GetFileName(current);
                var item = archive.GetEntry(prefix + name) ?? throw new IOException("原始包缺少伴生程序集");
                if (item.Length > MaxPackageBytes) throw new IOException("原始程序集超过大小上限");
                // Only preselected flat filenames are written, never archive-provided relative paths.
                var original = Path.Combine(staging, name);
                item.ExtractToFile(original);
                if (!SameBuild(original, current)) throw new IOException("原始包与已安装插件的构建或代码不匹配");
                pairs.Add((current, original));
            }
            cancel.ThrowIfCancellationRequested();
            if (UITextMaintenance.FileSignature(installed) != signature) throw new IOException("恢复期间插件文件已变化");
            var files = new List<UITextPatchFile>();
            foreach (var (current, original) in pairs)
            {
                var hash = UITextPatchStore.HashOf(original);
                var backup = store.Backup(entry.InternalName, original, hash);
                if (backup is null || UITextPatchStore.HashOf(backup) != hash) throw new IOException("原始包备份保存失败");
                files.Add(new() { Path = current, SourceHash = hash, PatchedHash = UITextPatchStore.HashOf(current), BackupPath = backup });
            }
            // Recovery repairs a verified baseline. It neither starts a file
            // operation nor confirms a pending reload. Read the latest lifecycle
            // metadata after package verification and durable backup creation.
            var old = ReadCompletedState();
            if (old is not null)
            {
                if (!UITextFileIdentity.TryMap(old, mainPath, out var mapped, out var error)) throw new IOException(error);
                for (var i = 0; i < files.Count; i++)
                    if (mapped.TryGetValue(files[i].Path, out var previous))
                        files[i] = files[i] with { BeforeHash = previous.BeforeHash };
                // Every retained file must keep its own current relative identity
                // and verified original. Never silently discard an unverifiable
                // JSON record or borrow a same-named file's backup.
                foreach (var (current, previous) in mapped)
                {
                    if (files.Any(file => file.Path.Equals(current, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!Path.IsPathFullyQualified(previous.BackupPath ?? string.Empty) || !previous.HasBackup
                        || string.IsNullOrEmpty(previous.SourceHash) || string.IsNullOrEmpty(previous.PatchedHash)
                        || !string.Equals(UITextPatchStore.HashOf(previous.BackupPath!), previous.SourceHash, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(UITextPatchStore.HashOf(current), previous.PatchedHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("保留的本地化文件或原始备份已变化，不能覆盖恢复记录。");
                    files.Add(previous with { Path = current });
                }
            }
            cancel.ThrowIfCancellationRequested();
            if (UITextMaintenance.FileSignature(installed) != signature) throw new IOException("恢复期间插件文件已变化");
            var mainFile = files.Single(file => file.Path.Equals(mainPath, StringComparison.OrdinalIgnoreCase));
            var state = (old ?? new UITextPatchState { InternalName = entry.InternalName }) with
            {
                DLLPath = mainPath, PluginVersion = entry.Version, Files = files,
                SourceHash = mainFile.SourceHash, PatchedHash = mainFile.PatchedHash, BackupPath = mainFile.BackupPath,
            };
            // Either durable metadata owner is useful if the other write fails.
            // Keep all backups and the previous metadata; no operation ID or
            // recovery phase may be invented for this baseline-only update.
            var stateSaved = store.Save(state);
            var manifestSaved = store.PublishManifest(entry.InternalName, entry.Version, state.PatchedAt,
                state.AppliedEntries, files, Path.GetDirectoryName(mainPath)!, mainPath, state.OperationID,
                state.PatchedHash ?? state.SourceHash);
            if (!stateSaved || !manifestSaved)
                throw new IOException(!stateSaved ? "原始包恢复记录保存失败，已保留原始备份。" : "原始包恢复清单保存失败，已保留恢复记录与原始备份。");
            return state;
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    /// <summary>FireGaze changes literals/resources/attribute text, never executable structure or build identity.</summary>
    public static bool SameBuild(string originalPath, string currentPath)
    {
        using var original = UIStringExtractor.LoadModule(originalPath);
        using var current = UIStringExtractor.LoadModule(currentPath);
        if (original.Mvid is null || original.Mvid != current.Mvid || original.Assembly?.FullName != current.Assembly?.FullName) return false;
        if (!original.GetAssemblyRefs().Select(a => a.FullName).Order().SequenceEqual(current.GetAssemblyRefs().Select(a => a.FullName).Order())) return false;
        IEnumerable<string> Shape(ModuleDef module)
        {
            foreach (var type in module.GetTypes())
            {
                yield return $"T:{type.FullName}:{type.Attributes}:{type.BaseType}";
                foreach (var i in type.Interfaces) yield return "I:" + i.Interface;
                foreach (var f in type.Fields) yield return $"F:{f.FullName}:{f.Attributes}:{Convert.ToHexString(f.InitialValue ?? [])}";
                foreach (var method in type.Methods)
                {
                    yield return $"M:{method.FullName}:{method.Attributes}:{method.ImplAttributes}:{method.HasBody}";
                    if (!method.HasBody) continue;
                    var body = method.Body;
                    int Index(Instruction? instruction) => instruction is null ? -1 : body.Instructions.IndexOf(instruction);
                    yield return $"B:{body.InitLocals}:{string.Join(',', body.Variables.Select(v => v.Type.FullName))}";
                    foreach (var instruction in body.Instructions)
                    {
                        var operand = instruction.OpCode.Code == Code.Ldstr ? "<text>" : instruction.Operand switch
                        {
                            Instruction target => Index(target).ToString(CultureInfo.InvariantCulture),
                            IList<Instruction> targets => string.Join(',', targets.Select(Index)),
                            IFullName member => member.FullName,
                            Local local => "local:" + local.Index,
                            Parameter parameter => "arg:" + parameter.Index,
                            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
                            null => string.Empty,
                            var value => value.ToString(),
                        };
                        yield return instruction.OpCode.Code + ":" + operand;
                    }
                    foreach (var h in body.ExceptionHandlers) yield return $"E:{h.HandlerType}:{h.CatchType}:{Index(h.TryStart)}:{Index(h.TryEnd)}:{Index(h.HandlerStart)}:{Index(h.HandlerEnd)}:{Index(h.FilterStart)}";
                }
            }
        }
        return Shape(original).SequenceEqual(Shape(current));
    }

    private static JsonDocument ParseMetadata(byte[] bytes) => JsonDocument.Parse(
        bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0));

    private static ZipArchiveEntry FindEntry(ZipArchive archive, string name)
    {
        var matches = archive.Entries.Where(e => Path.GetFileName(e.FullName).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : throw new IOException("原始包程序集缺失或重复");
    }
    private static bool SameVersion(string? first, string? second) => Version.TryParse(first, out var a) && Version.TryParse(second, out var b)
        && (a.Major, a.Minor, Math.Max(0, a.Build), Math.Max(0, a.Revision)) == (b.Major, b.Minor, Math.Max(0, b.Build), Math.Max(0, b.Revision));
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static void AddURL(string? value, HashSet<string> urls)
    { if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") urls.Add(uri.AbsoluteUri); }
    private static void AddGitHubRepo(string? value, HashSet<string> repos)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host != "github.com") return;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length >= 2 && parts.Take(2).All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            repos.Add(parts[0] + "/" + parts[1].Replace(".git", string.Empty, StringComparison.Ordinal));
    }
    private static object? Read(object? value, string member) => value?.GetType().GetProperty(member,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
    private static IEnumerable<object> AvailableManifests(InstalledPluginEntry entry)
    {
        try
        {
            var assembly = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;
            if (entry.RawPlugin.GetType().Assembly != assembly) return [];
            var managerType = assembly.GetType("Dalamud.Plugin.Internal.PluginManager", true)!;
            var nullable = assembly.GetType("Dalamud.Service`1", true)!.MakeGenericType(managerType)
                .GetMethod("GetNullable", BindingFlags.Public | BindingFlags.Static)!;
            var manager = nullable.Invoke(null, [Enum.Parse(nullable.GetParameters()[0].ParameterType, "None")]);
            if (manager is null) return [];
            return (Read(manager, "AvailablePlugins") as IEnumerable)?.Cast<object>().Where(m =>
            {
                var repo = Read(m, "SourceRepo");
                if (entry.IsThirdParty)
                    return Read(repo, "IsThirdParty") is true && Uri.TryCreate(Read(repo, "PluginMasterUrl") as string, UriKind.Absolute, out var a)
                        && Uri.TryCreate(entry.RepositoryURL, UriKind.Absolute, out var b) && a == b;
                return entry.RepositoryURL == "OFFICIAL" && Read(repo, "IsThirdParty") is false;
            }).ToArray() ?? [];
        }
        catch (Exception) { return []; }
    }
}

internal sealed class UITextRecoveryPendingException : IOException
{
    public UITextRecoveryPendingException() : base("原始插件包暂不可用，现有译文和插件已保留。") { }
}
