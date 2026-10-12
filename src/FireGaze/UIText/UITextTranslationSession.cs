namespace FireGaze.UIText;

internal static class UITextTranslationSession
{
    public static bool IsFailure(UITextTranslateResult result, int applied, int rejected, int unchanged)
        => result.Error != "已取消" && (result.Error is not null
            || (applied == 0 && result.Failed.Count > 0 && unchanged + rejected <= result.Failed.Count));

    public static async Task<UITextTranslateResult> TranslateAsync(
        IUITextChannel channel, IReadOnlyList<UITextTranslateItem> items,
        Action<int, int>? progress, Action<UITextTranslateResult> checkpoint,
        CancellationToken token, int batchSize = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var result = new UITextTranslateResult();
        var reported = 0;
        for (var start = 0; start < items.Count; start += batchSize)
        {
            if (token.IsCancellationRequested) { result.Error = "已取消"; break; }
            var offset = start;
            var batch = items.Skip(start).Take(batchSize).ToList();
            var requested = batch;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (token.IsCancellationRequested) { result.Error = "已取消"; break; }
                UITextTranslateResult part;
                try
                {
                    part = await channel.TranslateAsync(requested,
                        (done, _) =>
                        {
                            reported = Math.Max(reported, offset + Math.Min(done, batch.Count));
                            progress?.Invoke(reported, items.Count);
                        }, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { result.Error = "已取消"; break; }
                catch (Exception e) { result.Error = e.GetBaseException().Message; break; }
                var allowed = requested.Select(i => i.Text).ToHashSet(StringComparer.Ordinal);
                foreach (var key in part.Translated.Keys.Where(k => !allowed.Contains(k)).ToArray()) part.Translated.Remove(key);
                part.Failed.RemoveAll(k => !allowed.Contains(k));
                foreach (var pair in part.Translated) result.Translated[pair.Key] = pair.Value;
                foreach (var note in part.Notes) result.Note(note);
                // Save delivered text before retrying; never treat a disk error as a network failure.
                checkpoint(part);
                if (part.Error is not null) { result.Error = part.Error; break; }
                var missing = requested.Where(i => !result.Translated.ContainsKey(i.Text)).ToList();
                if (missing.Count == 0) break;
                if (attempt == 1)
                {
                    result.Failed.AddRange(missing.Select(i => i.Text).Distinct(StringComparer.Ordinal));
                    break;
                }
                requested = missing;
            }
            if (result.Error is not null) break;
        }
        if (result.Error is not null)
        {
            foreach (var item in items.Where(i => !result.Translated.ContainsKey(i.Text)))
                if (!result.Failed.Contains(item.Text, StringComparer.Ordinal)) result.Failed.Add(item.Text);
        }
        return result;
    }
}
