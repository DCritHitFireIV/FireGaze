using System.Text;
using System.Text.Json;
using Lumina.Excel.Sheets;

internal static class TimelineDataAudit
{
    public static int Run(string sqpack, string output)
    {
        using var game = new Lumina.GameData(sqpack, new Lumina.LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified });
        var root = Encoding.UTF8.GetString(game.GetFile("exd/root.exl")!.Data);
        var identities = game.GetExcelSheet<Emote>()!.Where(row => row.RowId > 0 && row.RowId <= ushort.MaxValue)
            .Select(row => new { row.RowId, Name = row.Name.ToString(), TimelineIds = row.ActionTimeline.Select(reference => reference.RowId).Where(id => id > 0).ToArray() }).ToArray();
        var timelines = game.GetExcelSheet<ActionTimeline>()!.Select(row => new { row.RowId, Key = row.Key.ToString(), row.KillUpper, row.Slot, row.Priority }).ToArray();
        var replacements = game.GetExcelSheet<ActionTimelineReplace>()!.Select(row => new { row.RowId, Old = row.Old.RowId, New = row.New.RowId }).ToArray();
        File.WriteAllText(output, JsonSerializer.Serialize(new {
            Schema = 1, RootMatches = root.Split('\n').Where(line => line.StartsWith("ActionTimeline", StringComparison.Ordinal)).ToArray(),
            Emotes = identities, Timelines = timelines, Replacements = replacements,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"TimelineDataAudit: {identities.Length} emotes, {timelines.Length} timelines, {replacements.Length} replacements; disk sqpack read only.");
        return 0;
    }
}
