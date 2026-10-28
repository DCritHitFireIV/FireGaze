namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitFavoriteGalleryContext(
    ulong OwnerAccountID, ulong OwnerContentID, long PreviewSessionRevision)
{
    public bool IsValid => OwnerAccountID != 0 && OwnerContentID != 0 && PreviewSessionRevision > 0;
}

// ImageSubmitted records a completed ImGui.Image invocation in one UI draw.
// It grants no GPU completion, final pixel visibility or durable-photo proof.
internal readonly record struct PortraitFavoriteGalleryCard(
    Guid ID, ulong SourceContentID, bool RectVisible, bool ImageSubmitted);

internal sealed class PortraitFavoriteGallerySnapshot
{
    public string Schema => "portrait-favorite-gallery/v1";
    public long WindowGeneration { get; }
    public long Sequence { get; }
    public PortraitFavoriteGalleryContext Context { get; }
    public bool Open { get; }
    public bool Trash { get; }
    public bool Complete { get; }
    public bool WindowDrawn { get; }
    public bool Truncated { get; }
    public IReadOnlyList<PortraitFavoriteGalleryCard> Cards { get; }
    public bool PixelVisibilityVerified => false;

    internal PortraitFavoriteGallerySnapshot(long generation, long sequence,
        PortraitFavoriteGalleryContext context, bool open, bool trash, bool complete,
        bool windowDrawn, bool truncated, PortraitFavoriteGalleryCard[] cards)
    {
        WindowGeneration = generation; Sequence = sequence; Context = context;
        Open = open; Trash = trash; Complete = complete; WindowDrawn = windowDrawn;
        Truncated = truncated; Cards = Array.AsReadOnly(cards.ToArray());
    }
}

/// <summary>
/// Last completed UI submission, not current screen state. Readers must observe
/// advancing complete sequences after their own baseline, with stable context
/// and window generation. Read never draws, loads a texture or resolves native state.
/// </summary>
internal sealed class PortraitFavoriteGalleryPublisher
{
    public const int MaximumCards = 32;
    private readonly object gate = new();
    private long generation = 1;
    private long sequence;
    private PortraitFavoriteGallerySnapshot current = new(1, 0, default,
        false, false, false, false, false, []);

    public PortraitFavoriteGallerySnapshot Read() => Volatile.Read(ref current);

    public PortraitFavoriteGallerySnapshot Begin(PortraitFavoriteGalleryContext context, bool open, bool trash)
    {
        lock (gate)
        {
            if (current.Context != context) generation++;
            var started = new PortraitFavoriteGallerySnapshot(generation, ++sequence, context,
                open, trash, false, false, false, []);
            Volatile.Write(ref current, started);
            return started;
        }
    }

    public bool Complete(PortraitFavoriteGallerySnapshot started,
        PortraitFavoriteGalleryContext context, bool open, bool trash, bool windowDrawn,
        IEnumerable<PortraitFavoriteGalleryCard> cards)
    {
        // Materialize only detached bounded values, without holding the publication lock.
        var bounded = cards.Take(MaximumCards + 1).ToArray();
        lock (gate)
        {
            if (!ReferenceEquals(current, started) || context != started.Context
                || started.WindowGeneration != generation) return false;
            var valid = context.IsValid;
            Volatile.Write(ref current, new(generation, started.Sequence, context,
                open, trash, valid, windowDrawn, bounded.Length > MaximumCards,
                valid && open && windowDrawn ? bounded.Take(MaximumCards).ToArray() : []));
            return true;
        }
    }

    public void ResetWindow()
    {
        lock (gate)
            Volatile.Write(ref current, new(++generation, ++sequence, default,
                false, false, false, false, false, []));
    }
}
