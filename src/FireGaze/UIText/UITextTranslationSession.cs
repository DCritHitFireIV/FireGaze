namespace FireGaze.UIText;

internal static class UITextTranslationSession
{
    public static async Task<UITextTranslateResult> TranslateAsync(
        IUITextChannel channel, IReadOnlyList<UITextTranslateItem> items,
        Action<int, int>? progress, Action<UITextTranslateResult> checkpoint,
        CancellationToken token, int batchSize = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var result = new UITextTranslateResult();
        for (var start = 0; start < items.Count; start += batchSize)
        {
            if (token.IsCancellationRequested) { result.Error = "已取消"; break; }
            var offset = start;
            var batch = items.Skip(start).Take(batchSize).ToList();
            UITextTranslateResult part;
            try
            {
                part = await channel.TranslateAsync(batch,
                    (done, _) => progress?.Invoke(offset + Math.Min(done, batch.Count), items.Count), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { result.Error = "已取消"; break; }
            catch (Exception e) { result.Error = e.GetBaseException().Message; break; }
            foreach (var pair in part.Translated) result.Translated[pair.Key] = pair.Value;
            result.Failed.AddRange(part.Failed);
            foreach (var note in part.Notes) result.Note(note);
            // Persist each delivered batch before starting another network request.
            checkpoint(part);
            if (part.Error is not null) { result.Error = part.Error; break; }
        }
        if (result.Error is not null)
        {
            foreach (var item in items.Where(i => !result.Translated.ContainsKey(i.Text)))
                if (!result.Failed.Contains(item.Text, StringComparer.Ordinal)) result.Failed.Add(item.Text);
        }
        return result;
    }
}
