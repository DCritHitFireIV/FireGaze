namespace FireGaze.PortraitPreview;

/// <summary>Framework-side scheduling only: disk completion, not submission, makes a revision clean.</summary>
internal sealed class OwnedCacheSavePolicy
{
    private readonly OwnedCacheWriter writer;
    private readonly long baselineRevision;

    public OwnedCacheSavePolicy(OwnedCacheWriter writer, long baselineRevision = 0)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        if (baselineRevision < 0) throw new ArgumentOutOfRangeException(nameof(baselineRevision));
        this.baselineRevision = baselineRevision;
    }

    /// <summary>
    /// Call after each successful source-capture batch, and during close/unload before draining the writer.
    /// The catalog and serialization remain on their owning Framework thread; only detached JSON is queued.
    /// </summary>
    public bool QueueIfNeeded(OwnedEquipmentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var revision = catalog.ContentRevision;
        if (revision <= baselineRevision || !writer.NeedsRevision(revision)) return false;
        return writer.Queue(revision, catalog.ExportJSON());
    }
}
