using FireGaze.UIText;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System.IO.Compression;
using FireGaze.RepoAudit;

// Real emitted plugin binaries: a dependency lookup shares its text with a UI label.
// Replacing the lookup/IPC argument must fail this test, even for stale or user packs.
var root = Path.Combine(Path.GetTempPath(), "firegaze-identity-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var failures = new List<string>();
var checks = 0;
void Check(bool ok, string name)
{
    checks++;
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures.Add(name);
}
try
{
    foreach (var (typeName, methodName, identity, identityIndex, parameterCount) in new[]
    {
        ("ECommons.Reflection.DalamudReflector", "TryGetDalamudPlugin", "vnavmesh", 0, 1),
        ("Dalamud.Plugin.IDalamudPluginInterface", "GetIpcSubscriber", "vnavmesh.Nav.IsReady", 0, 1),
        ("Dalamud.Plugin.IDalamudPluginInterface", "GetIpcProvider", "vnavmesh.Path.IsRunning", 0, 1),
        ("Dalamud.Plugin.IDalamudPluginInterface", "GetIpcCaller", "vnavmesh.Path.Stop", 0, 1),
        ("ECommons.Reflection.DalamudReflector", "TryGetDalamudPlugin", "导航插件", 0, 1),
        ("Fixture.ExternalPluginInfo", ".ctor", "vnavmesh", 0, 1),
        ("ECommons.EzIpcManager.EzIPC", "Init", "vnavmesh", 1, 3),
        ("ECommons.EzIpcManager.EzIPC", "Init", "WrathCombo", 2, 4),
        ("ECommonsLite.EzIpcManager.EzIPC", "Init", "CustomizePlus", 1, 3),
        ("GatherBuddy.Plugin.EzIPC", "Init", "AutoHook", 1, 3),
        ("Dalamud.Plugin.IDalamudPluginInterface", "GetOrCreateData", "YesAlready.StopRequests", 0, 2),
        ("Dalamud.Plugin.IDalamudPluginInterface", "TryGetData", "vnav.PathIsRunning", 0, 2),
        ("Dalamud.Plugin.IDalamudPluginInterface", "RelinquishData", "TextAdvance.StopRequests", 0, 1),
        ("ECommons.EzSharedDataManager.EzSharedData", "GetOrCreate", "RenderDisableRequests", 0, 2),
        ("ECommons.EzSharedDataManager.EzSharedData", "TryGet", "RenderDisableRequests", 0, 4),
    })
    {
        var source = Path.Combine(root, checks + ".dll");
        BuildFixture(source, typeName, methodName, identity, identityIndex, parameterCount);
        var extraction = UIStringExtractor.Extract(source);
        Check(extraction.Error is null, "fixture extraction succeeds: " + identity);
        Check(extraction.Entries.Single(e => e.Original == identity).Role == UITextRole.Excluded,
            "functional identifier is excluded even when also displayed: " + identity);
        foreach (var user in new[] { false, true })
        {
            var pack = new UITextPack();
            var key = pack.GetOrAdd(identity, null, false);
            key.Translated = "被误译的标识符";
            key.Source = user ? "user" : "library";
            var label = pack.GetOrAdd("Enable navigation", null, false);
            label.Translated = "启用导航";
            var target = source + (user ? ".user.dll" : ".library.dll");
            var outcome = UITextPatcher.Patch(source, target, pack, includeAmbiguous: true);
            Check(outcome.Ok, "patch succeeds with ordinary UI translation: " + identity);
            if (!outcome.Ok) continue;
            Check(ReadString(target, "Lookup") == identity, "stale/user pack preserves dependency lookup: " + identity);
            Check(ReadString(target, "DrawEnglish") == "启用导航", "remaining English UI is translated");
            Check(ReadString(target, "DrawChinese") == "已有中文界面", "native Chinese UI is preserved");
            var second = target + ".again.dll";
            // Patch manager normally restores the recorded baseline before applying again.
            var repeated = UITextPatcher.Patch(source, second, pack, includeAmbiguous: true);
            Check(repeated.Ok && ReadString(second, "Lookup") == identity,
                "repeated application from baseline preserves dependency lookup");
            var revertPack = new UITextPack();
            revertPack.GetOrAdd("original plugin name", null, false).Translated = identity;
            revertPack.GetOrAdd("Enable navigation", null, false).Translated = "启用导航";
            var restored = target + ".restored.dll";
            var revert = UITextPatcher.Revert(target, restored, revertPack);
            Check(revert.Ok && ReadString(restored, "Lookup") == identity,
                "inverse recovery cannot rewrite a native functional identifier");
        }
        var identityOnly = new UITextPack();
        identityOnly.GetOrAdd(identity, null, false).Translated = "被误译的标识符";
        var blocked = UITextPatcher.Patch(source, source + ".blocked.dll", identityOnly);
        Check(!blocked.Ok && !blocked.NoMatch && !File.Exists(source + ".blocked.dll"),
            "identity-only pack is refused without triggering inverse recovery");
    }
    foreach (var (type, method, index, count) in new[]
    {
        ("Dalamud.Plugin.Ipc.ICallGateSubscriber`1", "InvokeAction", 0, 1),
        ("Dalamud.Plugin.IDalamudPluginInterface", "GetOrCreateData", 1, 2),
    })
    {
        var source = Path.Combine(root, "payload-" + checks + ".dll");
        BuildFixture(source, type, method, "Display payload", index, count);
        var pack = new UITextPack();
        var text = pack.GetOrAdd("Display payload", null, false);
        text.Translated = "显示内容";
        text.Source = "user";
        var target = source + ".translated.dll";
        var outcome = UITextPatcher.Patch(source, target, pack, includeAmbiguous: true);
        Check(outcome.Ok && ReadString(target, "Lookup") == "显示内容",
            "IPC payload/default data remains eligible for explicit UI translation: " + method);
    }
    var clean = Path.Combine(root, "repair-baseline.dll");
    BuildFixture(clean, "Dalamud.Plugin.IDalamudPluginInterface", "GetIpcSubscriber", "vnavmesh.Nav.IsReady");
    var broken = clean + ".broken.dll";
    using (var module = ModuleDefMD.Load(clean))
    {
        foreach (var ins in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions))
            if (ins.OpCode.Code == Code.Ldstr && Equals(ins.Operand, "vnavmesh.Nav.IsReady")) ins.Operand = "错误的频道名";
        module.Write(broken);
    }
    string Hash(string file) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
    var repairDetector = typeof(UITextMaintenance).GetMethod("FunctionalRepairs");
    Check(repairDetector is not null && (int)repairDetector.Invoke(null, [clean, broken, Hash(clean), Hash(broken)])! == 1,
        "update check detects an already translated IPC channel from a verified original backup");
    var updatePolicy = typeof(UITextMaintenance).GetMethod("CanApplyUpdate");
    Check(updatePolicy?.GetParameters().Length == 3 && (bool)updatePolicy.Invoke(null, [new UITextFlow.MergePreview(0, 0, 0, 0), null, 1])!,
        "safety-only repair is offered even without any new library translations");
    var onlyUnsafe = new UITextPack();
    onlyUnsafe.GetOrAdd("vnavmesh.Nav.IsReady", null, false).Translated = "错误的频道名";
    var cleanExtraction = UIStringExtractor.Extract(clean);
    var sanitized = UITextPack.FromJSON(onlyUnsafe.ToJSON(), out _)!;
    UITextFlow.MergeExtraction(sanitized, cleanExtraction);
    Check(sanitized.IsSkipped("vnavmesh.Nav.IsReady") && onlyUnsafe.Find("vnavmesh.Nav.IsReady")!.Translated == "错误的频道名",
        "checking sanitizes a candidate while preserving the saved pack and its translations");
    var patchMethod = typeof(UITextPatcher).GetMethod("Patch");
    var repairedCopy = clean + ".repair-only.dll";
    var emptyRepair = patchMethod?.GetParameters().Length == 7
        ? (UITextPatchOutcome)patchMethod.Invoke(null, [clean, repairedCopy, sanitized, null, null, false, true])! : null;
    Check(emptyRepair?.Ok == true && ReadString(repairedCopy, "Lookup") == "vnavmesh.Nav.IsReady",
        "verified safety repair can restore a baseline even when all translations are unsafe");
    Check(UITextMaintenance.FunctionalTranslationChanges(onlyUnsafe, sanitized, cleanExtraction) == 1,
        "a newly blocked old translation counts as an update independently of library additions");
    Check(UITextMaintenance.FunctionalTranslationChanges(sanitized, sanitized, cleanExtraction) == 0,
        "already sanitized packs do not offer a permanent fake repair update");
    Check(UITextMaintenance.FunctionalRepairs(clean, repairedCopy, Hash(clean), Hash(repairedCopy)) == 0,
        "a repaired binary no longer reports an IPC repair");
    Check(UITextMaintenance.SuspectFunctionalTranslations(onlyUnsafe, UIStringExtractor.Extract(broken)),
        "missing-original case detects a translated IPC identity for automatic recovery");
    Check(UITextMaintenance.CanApplyUpdate(new(0, 0, 0, 0), "library offline", 1)
        && !UITextMaintenance.CanApplyUpdate(new(1, 0, 0, 0), "library offline", 0),
        "offline local repair remains available while partial library updates remain blocked");
    var rejectedHash = false;
    try { _ = UITextMaintenance.FunctionalRepairs(clean, broken, "wrong hash", Hash(broken)); }
    catch (IOException) { rejectedHash = true; }
    Check(rejectedHash, "an unverified original backup cannot authorize an IPC repair");
    var recovery = typeof(UITextPatcher).Assembly.GetType("FireGaze.UIText.UITextOriginalRecovery");
    var adoptPackage = recovery?.GetMethod("AdoptPackage");
    Check(adoptPackage is not null, "missing backups can be recovered automatically from an original package");
    if (adoptPackage is not null)
    {
        var installed = Path.Combine(root, "installed", "Fixture.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.Copy(broken, installed);
        var entry = new InstalledPluginEntry { InternalName = "Fixture", DisplayName = "Fixture", Version = "1.0.0.0",
            DLLPath = installed, RawPlugin = new object(), RepositoryURL = "https://fixture.test/repository", IsThirdParty = true };
        var backupStore = new UITextPatchStore(Path.Combine(root, "config"), Path.Combine(root, "originals"));
        string Package(string label, string dll, string version = "1.0.0.0")
        {
            var zip = Path.Combine(root, label + ".zip");
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(dll, "nested/Fixture.dll");
            using var writer = new StreamWriter(archive.CreateEntry("nested/Fixture.json").Open());
            writer.Write("{\"InternalName\":\"Fixture\",\"AssemblyVersion\":\"" + version + "\"}");
            return zip;
        }
        bool AdoptRejected(string zip)
        {
            try { adoptPackage.Invoke(null, [entry, zip, backupStore, CancellationToken.None]); return false; }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is IOException) { return true; }
        }
        var wrongBuild = Path.Combine(root, "wrong-build.dll");
        BuildFixture(wrongBuild, "Dalamud.Plugin.IDalamudPluginInterface", "GetIpcSubscriber", "vnavmesh.Nav.IsReady");
        Check(AdoptRejected(Package("wrong-fork", wrongBuild)), "same name and version from another build cannot replace a Chinese fork");
        Check(AdoptRejected(Package("wrong-version", clean, "1.0.1.0")), "automatic recovery refuses another package version");
        var changedCode = Path.Combine(root, "changed-code.dll");
        using (var module = ModuleDefMD.Load(clean))
        {
            var call = module.GetTypes().SelectMany(t => t.Methods).Single(m => m.Name == "DrawEnglish").Body.Instructions[1];
            ((MemberRef)call.Operand).Name = "ChangedOperation";
            module.Write(changedCode);
        }
        Check(AdoptRejected(Package("changed-code", changedCode)), "identical MVID cannot conceal changed executable code");
        Check(backupStore.Load("Fixture") is null && Hash(installed) == Hash(broken), "rejected recovery preserves plugin and creates no trusted state");
        var state = (UITextPatchState)adoptPackage.Invoke(null, [entry, Package("correct-original", clean), backupStore, CancellationToken.None])!;
        Check(state.HasBackup && Hash(state.BackupPath!) == Hash(clean) && string.Equals(state.PatchedHash, Hash(installed), StringComparison.OrdinalIgnoreCase), "automatic package recovery records a verified original baseline");
        Check(Hash(installed) == Hash(broken), "checking for recovery prepares backups without rewriting an installed plugin");
        Check(UITextMaintenance.FunctionalRepairs(state.BackupPath!, installed, state.SourceHash, state.PatchedHash!) == 1,
            "recovered original identifies the broken IPC name without reverse-translation guesses");
        var recoveredCopy = Path.Combine(root, "automatically-repaired.dll");
        var repairedOutcome = UITextPatcher.Patch(state.BackupPath!, recoveredCopy, sanitized, allowEmptyPatch: true);
        Check(repairedOutcome.Ok && ReadString(recoveredCopy, "Lookup") == "vnavmesh.Nav.IsReady", "recovered baseline repairs the already localized binary");
        Check(backupStore.TryAdopt("Fixture", installed, entry.Version)?.HasBackup == true, "recovered baseline survives lost config through durable manifests");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellationObserved = false;
        try { adoptPackage.Invoke(null, [entry, Path.Combine(root, "correct-original.zip"), backupStore, cancelled.Token]); }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is OperationCanceledException) { cancellationObserved = true; }
        Check(cancellationObserved && Hash(installed) == Hash(broken), "cancelled recovery preserves the installed plugin");
        var packageBytes = File.ReadAllBytes(Path.Combine(root, "correct-original.zip"));
        var packageAttempts = 0;
        var repoAttempts = 0;
        using var http = new HttpClient(new FixtureHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/repository")
            {
                repoAttempts++;
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("[{\"InternalName\":\"Fixture\",\"AssemblyVersion\":\"1.0.0.0\",\"DownloadLinkInstall\":\"https://fixture.test/Fixture.zip\"}]") };
            }
            packageAttempts++;
            return packageAttempts == 1 ? new(System.Net.HttpStatusCode.ServiceUnavailable)
                : new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(packageBytes) };
        }));
        var downloadStore = new UITextPatchStore(Path.Combine(root, "download-config"), Path.Combine(root, "download-originals"));
        var downloader = new UITextOriginalRecovery(Path.Combine(root, "downloads"), http);
        var downloaded = await downloader.RecoverAsync(entry, downloadStore, CancellationToken.None);
        Check(repoAttempts == 1 && packageAttempts == 2 && downloaded.HasBackup,
            "empty installed download links recover from the exact repository and retry transient package failures");
        Check(!Directory.EnumerateFiles(Path.Combine(root, "downloads")).Any() && Hash(installed) == Hash(broken),
            "automatic download removes temporary packages and preserves installed bytes");
        var releasesRequested = false;
        using var historyHttp = new HttpClient(new FixtureHttpHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/repository")
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("[{\"InternalName\":\"Fixture\",\"AssemblyVersion\":\"2.0.0.0\",\"RepoUrl\":\"https://github.com/owner/Fixture\"}]") };
            if (uri.Host == "api.github.com")
            {
                releasesRequested = true;
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("[{\"tag_name\":\"v1.0.0.0\",\"assets\":[{\"name\":\"Fixture.zip\",\"browser_download_url\":\"https://fixture.test/history.zip\"}]}]") };
            }
            return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(packageBytes) };
        }));
        var historical = await new UITextOriginalRecovery(Path.Combine(root, "history"), historyHttp).RecoverAsync(entry, downloadStore, CancellationToken.None);
        Check(releasesRequested && historical.HasBackup, "installed version is recovered from release history after its repository advances");
        using var offlineHttp = new HttpClient(new FixtureHttpHandler(_ => new(System.Net.HttpStatusCode.NotFound)));
        var pending = false;
        var offlineStore = new UITextPatchStore(Path.Combine(root, "offline-config"));
        try { await new UITextOriginalRecovery(Path.Combine(root, "offline"), offlineHttp).RecoverAsync(entry, offlineStore, CancellationToken.None); }
        catch (UITextRecoveryPendingException e) { pending = !e.Message.Contains("重新安装") && !e.Message.Contains("检查失败"); }
        Check(pending && offlineStore.Load("Fixture") is null && Hash(installed) == Hash(broken),
            "unavailable original stays pending for automatic retry without reinstall instructions or a false success");
    }
    Console.WriteLine($"{checks - failures.Count}/{checks} identity safety checks passed");
    return failures.Count == 0 ? 0 : 1;
}
finally { Directory.Delete(root, recursive: true); }

static string ReadString(string path, string method)
{
    using var module = ModuleDefMD.Load(path);
    return (string)module.GetTypes().SelectMany(t => t.Methods).Single(m => m.Name == method)
        .Body.Instructions.First(i => i.OpCode.Code == Code.Ldstr).Operand;
}

static void BuildFixture(string path, string lookupType, string lookupMethod, string identity, int identityIndex = 0, int parameterCount = 1)
{
    using var module = new ModuleDefUser("Fixture", Guid.NewGuid(), new AssemblyRefUser("System.Runtime", new Version(10, 0, 0, 0)));
    new AssemblyDefUser("Fixture", new Version(1, 0)).Modules.Add(module);
    var type = new TypeDefUser("Fixture", "Plugin", module.CorLibTypes.Object.TypeDefOrRef);
    module.Types.Add(type);
    var split = lookupType.LastIndexOf('.');
    var parameters = Enumerable.Range(0, parameterCount).Select(i => (TypeSig)(i == identityIndex ? module.CorLibTypes.String : module.CorLibTypes.Object)).ToArray();
    var lookup = new MemberRefUser(module, lookupMethod, MethodSig.CreateStatic(module.CorLibTypes.Boolean, parameters),
        new TypeRefUser(module, lookupType[..split], lookupType[(split + 1)..], new AssemblyRefUser("ExternalDependency", new Version(1, 0))));
    var ui = new MemberRefUser(module, "TextUnformatted", MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String),
        new TypeRefUser(module, "ImGuiNET", "ImGui", new AssemblyRefUser("ExternalUI", new Version(1, 0))));
    MethodDef? recordCtor = null;
    if (lookupMethod == ".ctor")
    {
        var record = new TypeDefUser("Fixture", "ExternalPluginInfo", module.CorLibTypes.Object.TypeDefOrRef);
        module.Types.Add(record);
        var identityField = new FieldDefUser("<InternalName>k__BackingField", new FieldSig(module.CorLibTypes.String), FieldAttributes.Private);
        record.Fields.Add(identityField);
        recordCtor = new MethodDefUser(".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.String),
            MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName) { Body = new CilBody() };
        record.Methods.Add(recordCtor);
        recordCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        recordCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        recordCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, identityField));
        recordCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        recordCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MemberRefUser(module, ".ctor",
            MethodSig.CreateInstance(module.CorLibTypes.Void), module.CorLibTypes.Object.TypeDefOrRef)));
        recordCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }
    // Lookup is deliberately wrapped: safety must flow back through local parameters.
    var wrapper = new MethodDefUser("WrappedLookup", MethodSig.CreateStatic(module.CorLibTypes.Boolean, module.CorLibTypes.String), MethodImplAttributes.IL,
        MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
    type.Methods.Add(wrapper);
    for (var i = 0; i < parameterCount; i++)
        wrapper.Body.Instructions.Add(i == identityIndex ? Instruction.Create(OpCodes.Ldarg_0) : Instruction.Create(OpCodes.Ldnull));
    if (recordCtor is null) wrapper.Body.Instructions.Add(Instruction.Create(OpCodes.Call, lookup));
    else
    {
        wrapper.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, recordCtor));
        wrapper.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        wrapper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
    }
    wrapper.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    foreach (var (name, text, isLookup) in new[] { ("Lookup", identity, true), ("DrawIdentity", identity, false),
        ("DrawEnglish", "Enable navigation", false), ("DrawChinese", "已有中文界面", false) })
    {
        var method = new MethodDefUser(name, MethodSig.CreateStatic(isLookup ? module.CorLibTypes.Boolean : module.CorLibTypes.Void),
            MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
        type.Methods.Add(method);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, text));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, isLookup ? wrapper : ui));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }
    module.Write(path);
}

sealed class FixtureHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
