namespace FireGaze.PortraitPreview;

internal enum PortraitPixelOrder { Rgba, Bgra }
internal enum PortraitPixelAlpha { Straight, Premultiplied }

/// <summary>Thresholds apply inclusively to 0..255 display-encoded luma. Alpha zero is always excluded.</summary>
internal sealed record PortraitAnalysisOptions(int BlackThreshold = 0, int WhiteThreshold = 255,
    int MinimumAlpha = 1, PortraitPixelAlpha AlphaMode = PortraitPixelAlpha.Straight);

/// <summary>Source-pixel rectangle of heuristic contrast, not a fixation or calibrated probability.</summary>
internal readonly record struct PortraitAttentionHotspot(int X, int Y, int Width, int Height, float Score);

internal sealed record PortraitImageAnalysisResult(int Width, int Height, int VisiblePixels, int IgnoredPixels,
    IReadOnlyList<int> Red, IReadOnlyList<int> Green, IReadOnlyList<int> Blue, IReadOnlyList<int> Luma,
    double BlackClipFraction, double WhiteClipFraction,
    double RedHighClipFraction, double GreenHighClipFraction, double BlueHighClipFraction,
    int AttentionWidth, int AttentionHeight, IReadOnlyList<float> Attention,
    IReadOnlyList<PortraitAttentionHotspot> Hotspots);

/// <summary>
/// Detached, bounded analysis of caller-provided 8-bit pixels. No image mutation, game APIs, file I/O or network.
/// Luma uses Rec.709 weights directly on display-encoded RGB; it is not linear luminance, RAW exposure or HDR.
/// Attention is a local color/edge contrast heuristic with a weak center prior. It does not predict eye movements.
/// </summary>
internal static class PortraitImageAnalysis
{
    public const int MaximumPixels = 1_048_576;
    private const int MaximumAttentionSide = 128;

    public static PortraitImageAnalysisResult Analyze(ReadOnlySpan<byte> pixels, int width, int height,
        int stride, PortraitPixelOrder order, PortraitAnalysisOptions? options = null)
    {
        options ??= new();
        Validate(pixels.Length, width, height, stride, order, options);
        var red = new int[256]; var green = new int[256]; var blue = new int[256]; var luma = new int[256];
        var scale = Math.Min(1d, MaximumAttentionSide / (double)Math.Max(width, height));
        var attentionWidth = Math.Max(1, (int)Math.Round(width * scale));
        var attentionHeight = Math.Max(1, (int)Math.Round(height * scale));
        var cellCount = attentionWidth * attentionHeight;
        var cells = new ColorCell[cellCount];
        var visible = 0; var dark = 0; var light = 0;
        var redClip = 0; var greenClip = 0; var blueClip = 0;
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            var cellY = y * attentionHeight / height;
            for (var x = 0; x < width; x++)
            {
                var offset = x * 4; var alpha = row[offset + 3];
                if (alpha < options.MinimumAlpha) continue;
                var r = (int)row[offset + (order == PortraitPixelOrder.Rgba ? 0 : 2)];
                var g = (int)row[offset + 1];
                var b = (int)row[offset + (order == PortraitPixelOrder.Rgba ? 2 : 0)];
                if (options.AlphaMode == PortraitPixelAlpha.Premultiplied)
                {
                    if (r > alpha || g > alpha || b > alpha)
                        throw new ArgumentException("Sampled premultiplied channels must not exceed alpha.", nameof(pixels));
                    r = Unpremultiply(r, alpha); g = Unpremultiply(g, alpha); b = Unpremultiply(b, alpha);
                }
                var brightness = (int)Math.Round((2126d * r + 7152d * g + 722d * b) / 10000d);
                red[r]++; green[g]++; blue[b]++; luma[brightness]++; visible++;
                if (brightness <= options.BlackThreshold) dark++;
                if (brightness >= options.WhiteThreshold) light++;
                if (r >= options.WhiteThreshold) redClip++;
                if (g >= options.WhiteThreshold) greenClip++;
                if (b >= options.WhiteThreshold) blueClip++;
                ref var cell = ref cells[cellY * attentionWidth + x * attentionWidth / width];
                cell.Red += r; cell.Green += g; cell.Blue += b; cell.Count++;
            }
        }
        foreach (ref var cell in cells.AsSpan())
            if (cell.Count > 0) { cell.Red /= cell.Count; cell.Green /= cell.Count; cell.Blue /= cell.Count; }
        var attention = Attention(cells, attentionWidth, attentionHeight);
        var peaks = Hotspots(attention, attentionWidth, attentionHeight, width, height);
        double Ratio(int count) => visible == 0 ? 0 : count / (double)visible;
        return new(width, height, visible, width * height - visible,
            Array.AsReadOnly(red), Array.AsReadOnly(green), Array.AsReadOnly(blue), Array.AsReadOnly(luma),
            Ratio(dark), Ratio(light), Ratio(redClip), Ratio(greenClip), Ratio(blueClip),
            attentionWidth, attentionHeight, Array.AsReadOnly(attention), Array.AsReadOnly(peaks));
    }

    private static void Validate(int bytes, int width, int height, int stride,
        PortraitPixelOrder order, PortraitAnalysisOptions options)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Analysis requires a positive image of at most 1,048,576 pixels.");
        if (stride < width * 4L)
            throw new ArgumentOutOfRangeException(nameof(stride), "Positive stride must hold every pixel in a row.");
        if ((long)(height - 1) * stride + width * 4L > bytes)
            throw new ArgumentException("Pixel data is truncated.", nameof(bytes));
        if (order is not (PortraitPixelOrder.Rgba or PortraitPixelOrder.Bgra))
            throw new ArgumentOutOfRangeException(nameof(order));
        if (options.BlackThreshold is < 0 or > 255 || options.WhiteThreshold is < 0 or > 255
            || options.BlackThreshold >= options.WhiteThreshold)
            throw new ArgumentOutOfRangeException(nameof(options), "Clipping thresholds require 0 <= black < white <= 255.");
        if (options.MinimumAlpha is < 1 or > 255
            || options.AlphaMode is not (PortraitPixelAlpha.Straight or PortraitPixelAlpha.Premultiplied))
            throw new ArgumentOutOfRangeException(nameof(options), "Alpha mode and minimum alpha must be explicit and valid.");
    }

    private static int Unpremultiply(int color, int alpha) => (color * 255 + alpha / 2) / alpha;

    private struct ColorCell
    {
        public double Red, Green, Blue;
        public int Count;
    }

    private static double Difference(ColorCell a, ColorCell b) =>
        (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue)) / (255d * 3d);

    private static float[] Attention(ColorCell[] cells, int width, int height)
    {
        var values = new float[cells.Length];
        var radius = Math.Max(2, Math.Min(width, height) / 16);
        var maximum = 0f;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var cell = cells[y * width + x];
            if (cell.Count == 0) continue;
            var mean = new ColorCell();
            for (var yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy++)
                for (var xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx++)
                {
                    var neighbor = cells[yy * width + xx];
                    if (neighbor.Count == 0) continue;
                    mean.Red += neighbor.Red; mean.Green += neighbor.Green; mean.Blue += neighbor.Blue; mean.Count++;
                }
            mean.Red /= mean.Count; mean.Green /= mean.Count; mean.Blue /= mean.Count;
            var edge = 0d; var neighbors = 0;
            void Edge(int xx, int yy)
            {
                if (xx < 0 || yy < 0 || xx >= width || yy >= height) return;
                var neighbor = cells[yy * width + xx];
                if (neighbor.Count == 0) return;
                edge += Difference(cell, neighbor); neighbors++;
            }
            Edge(x - 1, y); Edge(x + 1, y); Edge(x, y - 1); Edge(x, y + 1);
            if (neighbors > 0) edge /= neighbors;
            var dx = (x + .5d) / width * 2d - 1d;
            var dy = (y + .5d) / height * 2d - 1d;
            // The center can only modulate genuine contrast. A flat scene remains exactly zero.
            var centerPrior = .8d + .2d * Math.Exp(-2d * (dx * dx + dy * dy));
            var value = (float)((.65d * Difference(cell, mean) + .35d * edge) * centerPrior);
            values[y * width + x] = value;
            maximum = Math.Max(maximum, value);
        }
        if (maximum <= 0.000001f) return new float[cells.Length];
        for (var i = 0; i < values.Length; i++) values[i] /= maximum;
        return values;
    }

    private static PortraitAttentionHotspot[] Hotspots(float[] attention, int width, int height,
        int sourceWidth, int sourceHeight)
    {
        var radius = Math.Max(2, Math.Min(width, height) / 8);
        var candidates = Enumerable.Range(0, attention.Length).Where(index => attention[index] >= .2f)
            .OrderByDescending(index => attention[index]).ThenBy(index => index);
        var selected = new List<PortraitAttentionHotspot>(3);
        foreach (var index in candidates)
        {
            var x = index % width; var y = index / width;
            var left = Math.Max(0, x - radius) * sourceWidth / width;
            var top = Math.Max(0, y - radius) * sourceHeight / height;
            var right = Math.Min(width, x + radius + 1) * sourceWidth / width;
            var bottom = Math.Min(height, y + radius + 1) * sourceHeight / height;
            var next = new PortraitAttentionHotspot(left, top, right - left, bottom - top, attention[index]);
            if (selected.Any(previous => Overlap(previous, next))) continue;
            selected.Add(next);
            if (selected.Count == 3) break;
        }
        return selected.ToArray();
    }

    private static bool Overlap(PortraitAttentionHotspot a, PortraitAttentionHotspot b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}
