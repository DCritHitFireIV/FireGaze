using FireGaze.RepoAudit;

namespace FireGaze.UIText;

internal sealed partial class UITextPatchManager
{
    // The approved package and file observations form one synchronous commit.
    // Lock order is lifetime execution -> Manager file owner -> Store directories.
    // No caller may enter this method while already owning a Store transaction.
    internal bool SaveMaintenanceCandidate(InstalledPluginEntry entry, string expectedJSON,
        string expectedFileSignature, string expectedSourceSignature, UITextPack candidate,
        out UITextPack? saved, out string? error)
        => this.SaveMaintenanceCandidateCore(entry, expectedJSON, expectedFileSignature, expectedSourceSignature,
            candidate, null, out saved, out error);

    internal bool SaveMaintenanceCandidateWithIntent(InstalledPluginEntry entry, string expectedJSON,
        string expectedFileSignature, string expectedSourceSignature, UITextPack candidate,
        Func<UITextTarget, bool> editFence, out UITextPack? saved, out string? error)
        => this.SaveMaintenanceCandidateCore(entry, expectedJSON, expectedFileSignature, expectedSourceSignature,
            candidate, editFence, out saved, out error);

    private bool SaveMaintenanceCandidateCore(InstalledPluginEntry entry, string expectedJSON,
        string expectedFileSignature, string expectedSourceSignature, UITextPack candidate,
        Func<UITextTarget, bool>? editFence, out UITextPack? saved, out string? error)
    {
        var proposal = UITextPackEditMerge.Clone(candidate);
        UITextPack? committed = null;
        string? failure = ClosingMessage;
        var success = this.WithFileOwner(() => this.packs.SaveMerged(entry.InternalName, latest =>
        {
            // applyGate is already held before the Store gate. These source
            // observations belong to that owner; they do not acquire an outer
            // Manager owner from a standalone Store callback.
            if (string.IsNullOrEmpty(entry.DLLPath)
                || UITextMaintenance.TargetFileSignature(entry.DLLPath, entry.InternalName, proposal) != expectedFileSignature
                || UITextMaintenance.FileSignature(this.ExtractionSourceOf(entry, out _, out _)) != expectedSourceSignature
                || latest.ToJSON() != expectedJSON)
                throw new IOException("插件或本机译文已经变化，已停止覆盖，请重新检查。");
            // The shared Store gate also owns the manual-action journal. Check
            // both sides here so unsaved or serialized ABA actions cannot be
            // admitted between the observation and this strict whole-pack write.
            if (editFence is not null && (!Unedited(latest, editFence) || !Unedited(proposal, editFence)))
                throw new IOException("操作期间已手动编辑译文，已停止覆盖，请重新检查。");
            return proposal;
        }, out committed, out failure), false);
        saved = success ? committed : null;
        error = success ? null : failure;
        return success;
    }

    private static bool Unedited(UITextPack pack, Func<UITextTarget, bool> editFence)
        => pack.Entries.All(row => editFence(new(row.Original, row.Context, row, null)))
            && pack.Resources.All(row => editFence(new(row.Original, null, null, row)))
            && pack.Attributes.All(row => editFence(new(row.Original, row.Context, null, null, row)));
}
