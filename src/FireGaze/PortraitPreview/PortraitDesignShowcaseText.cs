namespace FireGaze.PortraitPreview;

/// <summary>Display policy for the compact design showcase.</summary>
internal static class PortraitDesignShowcaseText
{
    internal static string SourceFor(PortraitDesignEntry row, bool hideSource = false)
        => !hideSource && row.Kind == PortraitDesignEntryKind.Appearance ? row.Source : string.Empty;
}
