namespace FireGaze.PortraitPreview;

internal sealed class OwnedRetainerRosterState
{
    private OwnedCharacterKey? owner;
    private OwnedRetainerRoster? roster;

    internal OwnedRetainerRoster? Observe(OwnedCharacterKey? character, IReadOnlyList<ulong>? ids)
    {
        if (character is not { AccountID: > 0, ContentID: > 0 }) { owner = null; roster = null; return null; }
        if (owner != character) { owner = character; roster = null; }
        if (ids is not null && ids.All(id => id != 0) && ids.Distinct().Count() == ids.Count)
            roster = new(character.Value, Array.AsReadOnly(ids.ToArray()), OwnedReadStatus.Current);
        else if (roster is not null) roster = roster with { Status = OwnedReadStatus.PossiblyStale };
        return roster;
    }
}
