using FireGaze.PortraitPreview;
using System.Diagnostics;

if (args.Length == 2 && args[0] == "--abandon")
{
    var peer = new PortraitStorageBudget(args[1], 100);
    peer.TryRunMigration(Path.Combine(args[1], "portrait-edit-history-v1.json"), 75, () =>
    {
        File.WriteAllBytes(Path.Combine(args[1], ".portrait-edit-history-v1.json.migration-" + new string('b', 32) + ".tmp"), new byte[75]);
        Environment.Exit(23); // Release the OS handle without any managed reconciliation.
    }, out _);
    return 24;
}

var passed = 0;
var failed = 0;
void Check(bool value, string name) { if (value) passed++; else { failed++; Console.Error.WriteLine("FAIL " + name); } }
void Run(Action<string> test)
{
    var root = Path.Combine(Path.GetTempPath(), "portrait-budget-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { test(root); }
    catch (Exception e) { failed++; Console.Error.WriteLine(e); }
    finally { Directory.Delete(root, true); }
}
bool Publish(PortraitStorageBudget budget, string path, int length, bool backup, out PortraitStorageResult result)
    => budget.TryPublish(path, length, stream => stream.Write(new byte[length]), backup, out result);
Run(root =>
{
    var file = Path.Combine(root, "history.json");
    var budget = new PortraitStorageBudget(root, 100);
    budget.RegisterPath(file);
    Check(Publish(budget, file, 40, true, out _), "first publish");
    Check(Publish(budget, file, 40, true, out _), "backup publish");
    Check(budget.Snapshot.UsedBytes == 80, "current and backup charged");
    Check(!Publish(budget, file, 30, true, out var rejection) && rejection.Code == PortraitStorageCode.Capacity, "full candidate reservation");
    Check(new FileInfo(file).Length == 40 && new FileInfo(file + ".bak").Length == 40, "rejection preserves generations");
    Check(Publish(budget, file, 20, true, out _), "exact peak fits");
    Check(budget.Snapshot.UsedBytes == 60 && budget.Snapshot.ReservedBytes == 0, "backup rotation arithmetic");
    Check(Publish(budget, file, 20, false, out _), "no backup replacement");
    Check(budget.Snapshot.UsedBytes == 60, "no backup preserves unrelated previous backup");
});
Run(root =>
{
    var path = Path.Combine(root, "history.json");
    var budget = new PortraitStorageBudget(root, 100); budget.RegisterPath(path);
    Check(!budget.TryPublish(path, 3, s => s.Write(new byte[4]), false, out _), "overrun rejected");
    Check(!budget.TryPublish(path, 3, s => s.Write(new byte[2]), false, out _), "underrun rejected");
    Check(!File.Exists(path) && budget.Snapshot.UsedBytes == 0 && budget.Snapshot.ReservedBytes == 0, "failed exact stream cleans candidate");
    Check(!budget.TryPublish(path, 3, s => s.Write(new byte[3]), false, out _, () => throw new IOException("validation")), "before replace error");
    Check(!File.Exists(path) && budget.Snapshot.UsedBytes == 0, "validation cannot publish");
    Check(!Publish(budget, Path.Combine(root, "..", "escape.json"), 1, false, out var outside) && outside.Code == PortraitStorageCode.InvalidPath, "outside root rejected");
});
Run(root =>
{
    File.WriteAllBytes(Path.Combine(root, "portrait-favorites-v1.json.bak"), new byte[101]);
    File.WriteAllBytes(Path.Combine(root, "unrelated.log"), new byte[500]);
    var budget = new PortraitStorageBudget(root, 100);
    Check(budget.Snapshot.UsedBytes == 101 && budget.Snapshot.Degraded, "startup existing protected data preserved");
    var path = Path.Combine(root, "history.json"); budget.RegisterPath(path);
    Check(!Publish(budget, path, 1, false, out var result) && result.Code == PortraitStorageCode.Capacity, "overquota forbids growth");
    Check(File.Exists(Path.Combine(root, "portrait-favorites-v1.json.bak")), "favorites remain saved");
    Check(budget.TryDeleteOwned(Path.Combine(root, "portrait-favorites-v1.json.bak"), out _), "explicit owned delete");
    Check(!budget.Snapshot.Degraded && Publish(budget, path, 1, false, out _), "known capacity recovers after explicit deletion");
    Check(!budget.TryDeleteOwned(Path.Combine(root, "unrelated.log"), out _), "unregistered file deletion rejected");
});
Run(root =>
{
    var path = Path.Combine(root, "history.json");
    var files = new FaultFiles { FailCleanup = true };
    var budget = new PortraitStorageBudget(root, 100, files); budget.RegisterPath(path);
    Check(!budget.TryPublish(path, 5, s => { s.Write(new byte[3]); throw new IOException("writer"); }, false, out _), "real partial writer failure");
    Check(budget.Snapshot.UsedBytes == 3 && budget.Snapshot.Degraded, "failed cleanup remains charged");
    Check(!Publish(budget, path, 1, false, out _), "failed cleanup seals growth");
    files.FailCleanup = false;
});
Run(root =>
{
    var path = Path.Combine(root, "history.json");
    var budget = new PortraitStorageBudget(root, 100); budget.RegisterPath(path);
    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
    var write = Task.Run(() => budget.TryPublish(path, 70, s => { entered.Set(); release.Wait(); s.Write(new byte[70]); }, false, out _));
    entered.Wait();
    Check(budget.Snapshot.ReservedBytes == 70 && budget.Snapshot.UsedBytes == 0, "candidate reserved without double charge");
    Check(!Publish(budget, path, 40, false, out var busy) && busy.Code == PortraitStorageCode.Busy, "concurrent root publication nonblocking");
    release.Set(); Check(write.GetAwaiter().GetResult(), "admitted writer completes");
    var other = new PortraitStorageBudget(root, 100); other.RegisterPath(path);
    using (new FileStream(Path.Combine(root, ".portrait-storage.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        Check(!Publish(other, path, 1, false, out var leased) && leased.Code == PortraitStorageCode.Busy, "cross authority lease blocks");
    Check(!Publish(other, path, 40, false, out var over) && over.Code == PortraitStorageCode.Capacity, "second authority refreshes actual bytes");
    Check(ReferenceEquals(PortraitStorageBudget.ForDirectory(root), PortraitStorageBudget.ForDirectory(root + Path.DirectorySeparatorChar)), "canonical production singleton");
});
Run(root =>
{
    var path = Path.Combine(root, "history.json"); var budget = new PortraitStorageBudget(root, 100); budget.RegisterPath(path);
    File.WriteAllBytes(path, new byte[30]);
    Check(!budget.TryRunMigration(path, 71, () => throw new Exception("must not execute"), out var cap) && cap.Code == PortraitStorageCode.Capacity, "migration reserves sum peak");
    Check(budget.TryRunMigration(path, 60, () => { File.Copy(path, path + ".bak"); File.WriteAllBytes(path, new byte[60]); }, out _), "bounded migration transaction");
    Check(budget.Snapshot.UsedBytes == 90, "migration reconcile counts backup");
    Check(!budget.TryRunMigration(path, 1, () => { File.WriteAllBytes(Path.Combine(root, ".history.json.migration-" + new string('a', 32) + ".tmp"), new byte[1]); throw new IOException("partial migration"); }, out _), "migration failure reported");
    Check(budget.Snapshot.UsedBytes == 91, "migration failure orphan charged");
});
Run(root =>
{
    var files = new FaultFiles { FailScan = true };
    var budget = new PortraitStorageBudget(root, 100, files);
    var path = Path.Combine(root, "portrait-edit-history-v1.json");
    Check(budget.Snapshot.Degraded, "failed initial enumeration degrades");
    Check(!Publish(budget, path, 1, false, out _), "unknown enumeration forbids growth");
    Check(budget.Snapshot.Degraded, "partial stat cannot clear failed full scan");
    files.FailScan = false;
    Check(Publish(budget, path, 1, false, out _), "successful full scan restores known metadata");
});
Run(root =>
{
    var files = new FaultFiles(); var budget = new PortraitStorageBudget(root, 100, files);
    var path = Path.Combine(root, "portrait-edit-history-v1.json");
    Check(!budget.TryPublish(path, 5, s => { s.Write(new byte[3]); files.FailTemporaryStat = true; files.FailCleanup = true; throw new IOException("partial"); }, false, out _), "metadata failure during staging");
    Check(budget.Snapshot.Degraded && budget.Snapshot.UsedBytes + budget.Snapshot.ReservedBytes >= 5, "unknown partial file retains conservative reservation");
    files.FailTemporaryStat = false; files.FailCleanup = false;
});
if (!args.Contains("--quick")) Run(root =>
{
    var path = Path.Combine(root, "portrait-edit-history-v1.json"); var budget = new PortraitStorageBudget(root, 10);
    for (var i = 0; i < 10_000; i++)
        if (!Publish(budget, path, 1, false, out _)) throw new Exception("bounded metadata write failed");
    Check(budget.TrackedFileCount <= 2 * 6, "ten thousand completed candidates keep bounded ledger metadata");
});
Run(root =>
{
    var path = Path.Combine(root, "portrait-edit-history-v1.json"); var budget = new PortraitStorageBudget(root, 100);
    Check(Publish(budget, path, 10, false, out _), "cross process baseline");
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    start.ArgumentList.Add("--abandon"); start.ArgumentList.Add(root);
    using var process = Process.Start(start)!;
    process.WaitForExit();
    Check(process.ExitCode == 23, "real peer abandons staged migration");
    Check(!Publish(budget, path, 20, false, out var capacity) && capacity.Code == PortraitStorageCode.Capacity, "entry epoch discovers crashed peer temporary");
    Check(budget.Snapshot.UsedBytes == 85, "crashed peer temporary remains fully charged");
    Check(new FileInfo(Path.Combine(root, ".portrait-storage.lock")).Length == 0, "persistent coordination file contains zero data bytes");
});
Run(root =>
{
    var path = Path.Combine(root, "portrait-edit-history-v1.json"); var files = new FaultFiles(); var budget = new PortraitStorageBudget(root, 100, files);
    Check(Publish(budget, path, 10, true, out _), "replace fault baseline");
    files.ThrowBeforeReplace = true;
    Check(!Publish(budget, path, 20, true, out _), "replace before mutation failure reported");
    Check(budget.Snapshot.UsedBytes == 10 && new FileInfo(path).Length == 10, "replace before mutation preserves original and charge");
    files.ThrowBeforeReplace = false; files.ThrowAfterReplace = true;
    Check(!Publish(budget, path, 20, true, out _), "ambiguous successful replace reports failure");
    Check(budget.Snapshot.UsedBytes == 30 && new FileInfo(path).Length == 20 && new FileInfo(path + ".bak").Length == 10, "ambiguous replace reconciles actual current and backup");
    files.ThrowAfterReplace = false;
    Check(budget.TryPreserveCopy(path, path + ".v1.bak", out _), "atomic immutable preserve copy");
    Check(budget.Snapshot.UsedBytes == 50, "immutable copy charged");
    Check(!budget.TryPreserveCopy(path, path + ".v1.bak", out var exists) && exists.Code == PortraitStorageCode.Conflict, "immutable copy never overwritten");
});
Run(root =>
{
    string[] names = ["portrait-edit-history-v1.json", "portrait-history-thumbnails-v1.bin", "portrait-favorites-v1.json", "portrait-favorite-photos-v1.json", "portrait-owned-cache-v1.json", "portrait-glamour-plates-v1.json"];
    var expected = 0;
    foreach (var name in names)
    {
        string[] paths = [name, name + ".bak", name + ".tmp-" + new string('a', 32), "." + name + "." + new string('a', 32) + ".tmp", "." + name + ".migration-" + new string('a', 32) + ".tmp", "." + name + ".backup-" + new string('a', 32) + ".tmp"];
        foreach (var path in paths) { File.WriteAllBytes(Path.Combine(root, path), [1]); expected++; }
    }
    string[] extras = [names[0] + ".v1-" + new string('c', 64) + ".bak", names[4] + ".v1.bak", names[4] + ".retainer-equipped." + new string('d', 64) + ".bak", ".portrait-glamour-plates-" + new string('e', 32) + ".tmp"];
    foreach (var path in extras) { File.WriteAllBytes(Path.Combine(root, path), [1]); expected++; }
    File.WriteAllBytes(Path.Combine(root, ".portrait-favorites-v1.json.not-a-guid.tmp"), new byte[100]);
    var budget = new PortraitStorageBudget(root, 1000);
    Check(budget.Snapshot.UsedBytes == expected, "startup accounts every production family migration backup and legacy temporary pattern");
    Check(Directory.GetFiles(root).Length == expected + 1, "startup preserves all existing and unrelated files");
});
Console.WriteLine($"Portrait storage budget: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

sealed class FaultFiles : PortraitStorageFiles
{
    public bool FailCleanup;
    public bool FailScan;
    public bool FailTemporaryStat;
    public bool ThrowBeforeReplace;
    public bool ThrowAfterReplace;
    public override void Replace(string candidate, string target, string? backup)
    {
        if (ThrowBeforeReplace) throw new IOException("before replace");
        base.Replace(candidate, target, backup);
        if (ThrowAfterReplace) throw new IOException("after replace");
    }
    public override IEnumerable<string> Enumerate(string directory)
    {
        if (FailScan) throw new UnauthorizedAccessException("scan fault");
        return base.Enumerate(directory);
    }
    public override long? GetLength(string path)
    {
        if (FailTemporaryStat && path.EndsWith(".tmp", StringComparison.Ordinal)) throw new IOException("stat fault");
        return base.GetLength(path);
    }
    public override void Delete(string path)
    {
        if (FailCleanup && path.EndsWith(".tmp", StringComparison.Ordinal)) throw new IOException("cleanup fault");
        base.Delete(path);
    }
}
