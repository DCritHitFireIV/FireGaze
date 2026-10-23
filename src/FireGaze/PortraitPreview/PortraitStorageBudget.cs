namespace FireGaze.PortraitPreview;

internal enum PortraitStorageCode { Success, Capacity, StorageError, Conflict, Busy, InvalidPath }
internal readonly record struct PortraitStorageResult(PortraitStorageCode Code, string Reason);
internal readonly record struct PortraitStorageSnapshot(long LimitBytes, long UsedBytes, long ReservedBytes, bool Degraded, string Reason);

/// <summary>
/// One directory owns one portrait budget. Every candidate reserves its complete length,
/// including bytes needed while the previous current and backup still exist. No store is
/// called under this authority's accounting lock. Existing data is never pruned here.
/// </summary>
internal sealed class PortraitStorageBudget
{
    public const long DefaultLimitBytes = 168L * 1024 * 1024;
    private static readonly object registryGate = new();
    private static readonly Dictionary<string, PortraitStorageBudget> registry = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ProductionNames =
    [
        "portrait-edit-history-v1.json", "portrait-history-thumbnails-v1.bin",
        "portrait-favorites-v1.json", "portrait-favorite-photos-v1.json",
        "portrait-favorites-trash-v1.json",
        "portrait-owned-cache-v1.json", "portrait-glamour-plates-v1.json",
    ];
    private readonly object gate = new();
    private readonly string directory;
    private readonly string lockPath;
    private readonly long limit;
    private readonly PortraitStorageFiles files;
    private readonly HashSet<string> families = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> directoryTargets = new(StringComparer.OrdinalIgnoreCase);
    private long used;
    private long reserved;
    private long uncertainReservation;
    private bool active;
    private bool scanNeeded;
    private bool metadataUnknown;
    private readonly HashSet<string> failedCleanup = new(StringComparer.OrdinalIgnoreCase);
    private string reason = "";
    private DateTime observedEpoch;
    private long reclaimRequested;
    private long reclaimCompleted;
    private long changeVersion;
    private TaskCompletionSource change = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool waitingForLease;
    private long observedUsed;
    private bool observedDegraded;
    private long directoryObservationVersion;

    public static PortraitStorageBudget ForDirectory(string directory)
    {
        var canonical = CanonicalDirectory(directory);
        lock (registryGate)
        {
            if (!registry.TryGetValue(canonical, out var value)) registry.Add(canonical, value = new(canonical, DefaultLimitBytes));
            return value;
        }
    }

    // Explicit limits and I/O faults are for independent, real-filesystem tests. Production
    // construction uses ForDirectory so individual stores cannot omit another family's bytes.
    public PortraitStorageBudget(string directory, long limitBytes, PortraitStorageFiles? files = null)
    {
        if (limitBytes < 0) throw new ArgumentOutOfRangeException(nameof(limitBytes));
        this.directory = CanonicalDirectory(directory);
        lockPath = Path.Combine(this.directory, ".portrait-storage.lock");
        limit = limitBytes;
        this.files = files ?? new PortraitStorageFiles();
        foreach (var name in ProductionNames) families.Add(name);
        Refresh(scan: true);
        observedUsed = used;
        observedDegraded = Snapshot.Degraded;
    }

    public PortraitStorageSnapshot Snapshot
    {
        get { lock (gate) return new(limit, used, reserved + uncertainReservation, metadataUnknown || failedCleanup.Count != 0 || used > limit, reason); }
    }
    public long ReclaimRequestVersion { get { lock (gate) return reclaimRequested; } }
    public long ChangeVersion { get { lock (gate) return changeVersion; } }
    public Task WaitForChangeAsync(long observedVersion, CancellationToken cancellationToken = default)
    {
        Task pending;
        lock (gate) pending = observedVersion != changeVersion ? Task.CompletedTask : change.Task;
        return cancellationToken.CanBeCanceled ? pending.WaitAsync(cancellationToken) : pending;
    }
    internal int TrackedFileCount { get { lock (gate) return known.Count; } }
    public bool NeedsReclaim { get { lock (gate) return reclaimRequested > reclaimCompleted; } }
    public void RequestReclaim() { lock (gate) reclaimRequested++; }
    public void CompleteReclaim(long version)
    {
        lock (gate)
        {
            var next = Math.Max(reclaimCompleted, Math.Min(version, reclaimRequested));
            if (next == reclaimCompleted) return;
            reclaimCompleted = next;
            PublishChange();
        }
    }
    private void PublishChange()
    {
        var previous = change;
        change = new(TaskCreationOptions.RunContinuationsAsynchronously);
        changeVersion++;
        previous.TrySetResult();
    }

    /// <summary>
    /// Bounded host polling may call this off the hot UI path. It never creates a file or
    /// advances the persistent epoch. True means one recovery edge, never a reclaim ack.
    /// </summary>
    public bool RefreshObservation()
    {
        long beforeUsed;
        bool beforeDegraded;
        long beforeDirectoryVersion;
        lock (gate)
        {
            if (active) return false;
            active = true;
            beforeUsed = used;
            beforeDegraded = metadataUnknown || failedCleanup.Count != 0 || used > limit;
            beforeDirectoryVersion = directoryObservationVersion;
        }
        FileStream? lease = null;
        try
        {
            var scan = false;
            try
            {
                lease = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
                var epoch = File.GetLastWriteTimeUtc(lease.SafeFileHandle);
                scan = epoch != observedEpoch;
                observedEpoch = epoch;
            }
            catch (FileNotFoundException) { scan = true; }
            catch (DirectoryNotFoundException) { scan = true; }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return false; }
            Refresh(scan);
            lock (gate)
            {
                var degraded = metadataUnknown || failedCleanup.Count != 0 || used > limit;
                var recovered = used < Math.Max(observedUsed, beforeUsed)
                    || (observedDegraded || beforeDegraded) && !degraded || waitingForLease
                    || directoryObservationVersion != beforeDirectoryVersion;
                observedUsed = used;
                observedDegraded = degraded;
                waitingForLease = false;
                if (recovered) PublishChange();
                return recovered;
            }
        }
        catch (Exception e) when (IsStorageError(e))
        {
            lock (gate) { metadataUnknown = true; reason = "肖像存储观察失败：" + e.Message; observedDegraded = true; }
            return false;
        }
        finally { lease?.Dispose(); lock (gate) active = false; }
    }

    /// <summary>Register fixture or future primary names in this same directory and authority.</summary>
    public void RegisterPath(string path)
    {
        if (!TryLocalPath(path, out var canonical)) throw new ArgumentException("Portrait storage path must be directly inside its registered directory.", nameof(path));
        lock (gate)
        {
            if (families.Add(Path.GetFileName(canonical))) scanNeeded = true;
        }
        // Do not race a publisher's provisional reservation. Its final reconciliation sees
        // newly registered families, and admission always refreshes before the next write.
        lock (gate) { if (active) return; active = true; }
        try { Refresh(scan: true); }
        finally { lock (gate) active = false; }
    }

    public bool TryPublish(string path, long expectedLength, Action<Stream> write, bool keepBackup,
        out PortraitStorageResult result, Action? beforeReplace = null, bool requestReclaim = true)
    {
        if (expectedLength < 0 || write is null || !TryOwnedPath(path, out var primary))
        { result = new(PortraitStorageCode.InvalidPath, "肖像写入路径或长度无效。"); return false; }
        // A registered path may temporarily be occupied by a directory (for example, a
        // failed writer fixture). Keep the family registered so it can recover after the
        // directory is removed, but reject this write locally without degrading quota.
        if (Directory.Exists(primary))
        {
            // This writer has observed the real obstruction even if the background observer
            // has not run yet. Retain that fact so removing it produces one recovery edge.
            lock (gate) { if (directoryTargets.Add(primary)) directoryObservationVersion++; }
            result = new(PortraitStorageCode.InvalidPath, "肖像写入目标不是普通文件。");
            return false;
        }
        return Mutate(() =>
        {
            if (!Reserve(expectedLength, out var rejection, requestReclaim)) return rejection;
            var temporary = Path.Combine(directory, $".{Path.GetFileName(primary)}.{Guid.NewGuid():N}.tmp");
            lock (gate) known.TryAdd(temporary, 0);
            try
            {
                using (var output = files.CreateNew(temporary))
                {
                    using var bounded = new ExactLengthStream(output, expectedLength);
                    write(bounded);
                    bounded.VerifyComplete();
                    output.Flush(flushToDisk: true);
                }
                beforeReplace?.Invoke();
                if (File.Exists(primary)) files.Replace(temporary, primary, keepBackup ? primary + ".bak" : null);
                else files.MoveNew(temporary, primary);
                return Success;
            }
            finally
            {
                try { files.Delete(temporary); }
                catch (Exception e) when (IsStorageError(e))
                {
                    lock (gate) { failedCleanup.Add(temporary); reason = "肖像临时文件清理失败，已保留计费：" + e.Message; }
                }
            }
        }, out result);
    }

    public bool TryPreserveCopy(string sourcePath, string backupPath, out PortraitStorageResult result)
    {
        if (!TryOwnedPath(sourcePath, out var source) || !TryOwnedPath(backupPath, out var backup))
        { result = new(PortraitStorageCode.InvalidPath, "肖像备份路径未登记。"); return false; }
        return Mutate(() =>
        {
            if (File.Exists(backup)) return new(PortraitStorageCode.Conflict, "肖像备份已存在，未覆盖。");
            var length = files.GetLength(source);
            if (length is null) return new(PortraitStorageCode.Conflict, "肖像备份源不存在。");
            if (!Reserve(length.Value, out var rejection)) return rejection;
            lock (gate) known.TryAdd(backup, 0);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(source)}.backup-{Guid.NewGuid():N}.tmp");
            lock (gate) known.TryAdd(temporary, 0);
            try
            {
                using (var input = File.OpenRead(source))
                using (var output = files.CreateNew(temporary))
                {
                    using var bounded = new ExactLengthStream(output, length.Value);
                    input.CopyTo(bounded);
                    bounded.VerifyComplete();
                    output.Flush(flushToDisk: true);
                }
                files.MoveNew(temporary, backup);
                return Success;
            }
            finally
            {
                try { files.Delete(temporary); }
                catch (Exception e) when (IsStorageError(e)) { lock (gate) failedCleanup.Add(temporary); }
            }
        }, out result);
    }

    /// <summary>
    /// Preserves migration fault-injection boundaries. The caller supplies the conservative
    /// maximum of all new backup and candidate bytes, using its existing bounded buffers.
    /// Migration callbacks must not enter another store or this authority recursively.
    /// </summary>
    public bool TryRunMigration(string primaryPath, long maximumAdditionalBytes, Action transaction, out PortraitStorageResult result)
    {
        if (maximumAdditionalBytes < 0 || transaction is null || !TryOwnedPath(primaryPath, out _))
        { result = new(PortraitStorageCode.InvalidPath, "肖像迁移路径或长度无效。"); return false; }
        return Mutate(() =>
        {
            if (!Reserve(maximumAdditionalBytes, out var rejection)) return rejection;
            try { transaction(); return Success; }
            finally { lock (gate) scanNeeded = true; }
        }, out result);
    }

    public bool TryDeleteOwned(string path, out PortraitStorageResult result)
    {
        if (!TryOwnedPath(path, out var canonical)) { result = new(PortraitStorageCode.InvalidPath, "肖像删除路径未登记。"); return false; }
        return Mutate(() => { files.Delete(canonical); lock (gate) failedCleanup.Remove(canonical); return Success; }, out result);
    }

    private static PortraitStorageResult Success => new(PortraitStorageCode.Success, "");

    private bool Mutate(Func<PortraitStorageResult> mutation, out PortraitStorageResult result)
    {
        long previousUsed;
        lock (gate)
        {
            if (active) { result = new(PortraitStorageCode.Busy, "肖像存储正在写入，请稍后重试。"); return false; }
            active = true;
            previousUsed = used;
        }
        var successful = false;
        FileStream? lease = null;
        try
        {
            Directory.CreateDirectory(directory);
            try { lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
            { lock (gate) waitingForLease = true; result = new(PortraitStorageCode.Busy, "另一肖像存储写入尚未结束，请稍后重试。"); return false; }
            var previous = File.GetLastWriteTimeUtc(lease.SafeFileHandle);
            var rescan = previous != observedEpoch;
            // Publish a persistent, verified epoch BEFORE any data changes. A crashed peer's
            // unknown GUID temporaries are then found on the next authority's admission.
            var next = new DateTime(Math.Max(DateTime.UtcNow.Ticks, checked(previous.Ticks + TimeSpan.TicksPerSecond)), DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(lease.SafeFileHandle, next);
            lease.Flush(flushToDisk: true);
            var actual = File.GetLastWriteTimeUtc(lease.SafeFileHandle);
            if (actual <= previous) throw new IOException("肖像存储协调代数未推进。");
            observedEpoch = actual;
            Refresh(rescan);
            result = mutation();
            successful = result.Code == PortraitStorageCode.Success;
            return successful;
        }
        catch (Exception e)
        { result = new(PortraitStorageCode.StorageError, "肖像存储写入失败：" + e.Message); return false; }
        finally
        {
            if (lease is not null)
            {
                // Ambiguous replacement and failed cleanup are charged from actual lengths.
                // Unknown metadata remains conservatively charged and seals further growth.
                Refresh(scan: false);
                lease.Dispose();
            }
            lock (gate)
            {
                reserved = 0; active = false;
                if (successful || used != previousUsed) PublishChange();
            }
        }
    }

    private bool Reserve(long bytes, out PortraitStorageResult rejection, bool requestReclaim = true)
    {
        lock (gate)
        {
            if (metadataUnknown || failedCleanup.Count != 0)
            { rejection = new(PortraitStorageCode.StorageError, reason.Length != 0 ? reason : "肖像存储状态未知，暂不新增数据。"); return false; }
            if (used > limit || bytes > limit - used - reserved)
            {
                if (requestReclaim) reclaimRequested++;
                rejection = new(PortraitStorageCode.Capacity, "肖像历史、图片、收藏和备份的共享存储已达到 168 MiB 上限。");
                return false;
            }
            reserved += bytes;
            rejection = Success;
            return true;
        }
    }

    private void Refresh(bool scan)
    {
        string[] names;
        lock (gate) { names = families.ToArray(); scan |= scanNeeded; }
        var unknown = false;
        var errorReason = "";
        if (scan)
        {
            try
            {
                foreach (var path in files.Enumerate(directory))
                    if (Matches(Path.GetFileName(path), names)) { lock (gate) known.TryAdd(path, 0); }
                lock (gate) scanNeeded = families.Count != names.Length;
            }
            catch (DirectoryNotFoundException) { lock (gate) scanNeeded = false; }
            catch (Exception e) when (IsStorageError(e))
            { unknown = true; lock (gate) scanNeeded = true; errorReason = "肖像文件目录不可读：" + e.Message; }
        }
        string[] paths;
        lock (gate)
        {
            foreach (var name in names) { known.TryAdd(Path.Combine(directory, name), 0); known.TryAdd(Path.Combine(directory, name + ".bak"), 0); }
            paths = known.Keys.ToArray();
        }
        foreach (var path in paths)
        {
            try
            {
                var length = files.GetLength(path);
                lock (gate)
                {
                    var isDirectory = length is null && Directory.Exists(path);
                    if (isDirectory ? directoryTargets.Add(path) : directoryTargets.Remove(path)) directoryObservationVersion++;
                    if (length is null)
                    {
                        failedCleanup.Remove(path);
                        // GUID temporaries and one-off migration entries are rediscovered by
                        // family scans. Never retain one zero-byte key per historical edit.
                        if (names.Any(name => string.Equals(path, Path.Combine(directory, name), StringComparison.OrdinalIgnoreCase)
                            || string.Equals(path, Path.Combine(directory, name + ".bak"), StringComparison.OrdinalIgnoreCase))) known[path] = 0;
                        else known.Remove(path);
                    }
                    else known[path] = length.Value;
                }
            }
            catch (Exception e) when (IsStorageError(e)) { unknown = true; errorReason = $"肖像文件长度未知（{path}）：{e.Message}"; }
        }
        lock (gate)
        {
            used = known.Values.Aggregate(0L, checked((total, value) => total + value));
            metadataUnknown = unknown;
            uncertainReservation = unknown ? Math.Max(uncertainReservation, reserved) : 0;
            if (unknown) reason = errorReason;
            else if (failedCleanup.Count != 0) reason = "肖像临时文件清理失败，现有文件持续计费。";
            else if (used > limit) reason = "现有肖像数据超过共享 168 MiB 上限，已保留原数据并暂停新增。";
            else reason = "";
        }
    }

    private bool TryLocalPath(string path, out string canonical)
    {
        canonical = "";
        try
        {
            canonical = Path.GetFullPath(path);
            return string.Equals(Path.GetDirectoryName(canonical), directory, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(canonical, lockPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private bool TryOwnedPath(string path, out string canonical)
    {
        if (!TryLocalPath(path, out canonical)) return false;
        lock (gate) return Matches(Path.GetFileName(canonical), families);
    }
    private static string CanonicalDirectory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool IsStorageError(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private static bool Hex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
    private static bool Matches(string file, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (file.Equals(name, StringComparison.OrdinalIgnoreCase) || file.Equals(name + ".bak", StringComparison.OrdinalIgnoreCase)
                || file.Equals(name + ".v1.bak", StringComparison.OrdinalIgnoreCase)) return true;
            if (file.StartsWith(name + ".tmp-", StringComparison.OrdinalIgnoreCase) && Hex(file[(name.Length + 5)..], 32)) return true;
            if (file.StartsWith(name + ".v1-", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                && Hex(file[(name.Length + 4)..^4], 64)) return true;
            if (file.StartsWith(name + ".retainer-equipped.", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                && Hex(file[(name.Length + 19)..^4], 64)) return true;
            if (file.StartsWith("." + name + ".", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                var token = file[(name.Length + 2)..^4];
                if (Hex(token, 32) || token.StartsWith("migration-", StringComparison.OrdinalIgnoreCase) && Hex(token[10..], 32)
                    || token.StartsWith("backup-", StringComparison.OrdinalIgnoreCase) && Hex(token[7..], 32)) return true;
            }
            if (name.Equals("portrait-glamour-plates-v1.json", StringComparison.OrdinalIgnoreCase)
                && file.StartsWith(".portrait-glamour-plates-", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                && Hex(file[25..^4], 32)) return true;
        }
        return false;
    }

    private sealed class ExactLengthStream(Stream inner, long length) : Stream
    {
        private long written;
        public void VerifyComplete() { if (written != length) throw new InvalidDataException("肖像候选文件实际长度与预留长度不同。"); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > length - written) throw new InvalidDataException("肖像候选文件超过预留长度。");
            inner.Write(buffer); written += buffer.Length;
        }
        public override void WriteByte(byte value) { Span<byte> b = stackalloc byte[1]; b[0] = value; Write(b); }
        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Small physical I/O seam used by fault tests; production always uses these methods.</summary>
internal class PortraitStorageFiles
{
    public virtual IEnumerable<string> Enumerate(string directory) => Directory.EnumerateFiles(directory);
    public virtual long? GetLength(string path)
    {
        try
        {
            // Exists suppresses access and sharing errors; those must remain unknown rather
            // than looking like zero bytes and accidentally freeing quota.
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("肖像存储不接受重解析文件。");
            if ((attributes & FileAttributes.Directory) != 0) return null;
            return new FileInfo(path).Length;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    public virtual FileStream CreateNew(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
    public virtual void Replace(string candidate, string target, string? backup) => File.Replace(candidate, target, backup);
    public virtual void MoveNew(string candidate, string target) => File.Move(candidate, target);
    public virtual void Delete(string path) => File.Delete(path);
}
