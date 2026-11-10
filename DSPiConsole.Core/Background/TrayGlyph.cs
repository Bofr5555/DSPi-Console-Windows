namespace DSPiConsole.Core.Background;

/// <summary>
/// The notification-area glyph, drawn in code at the exact size Windows asks
/// for (the app ships no icon files): a waveform in a circle, outlined while
/// idle and filled while sharing, the Windows counterpart of the macOS
/// Console's menu bar symbol (waveform.circle / waveform.circle.fill).
/// </summary>
public static class TrayGlyph
{
    private const int Samples = 4;   // per axis, for anti-aliasing

    /// <summary>32-bit BGRA pixels with straight (not premultiplied) alpha, as
    /// an icon's colour bitmap holds them, top row first:
    /// <paramref name="size"/> by <paramref name="size"/>.</summary>
    public static uint[] Render(int size, bool filled, byte r, byte g, byte b)
    {
        if (size < 8) size = 8;
        var bars = Bars(size);
        // The ring is at least a pixel wide at any size.
        double ring = Math.Max(1.15 / size, 0.085);
        const double outer = 0.48;
        var pixels = new uint[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int hits = 0;
            for (int sy = 0; sy < Samples; sy++)
            for (int sx = 0; sx < Samples; sx++)
            {
                double px = (x + (sx + 0.5) / Samples) / size;
                double py = (y + (sy + 0.5) / Samples) / size;
                double d = Math.Sqrt((px - 0.5) * (px - 0.5) + (py - 0.5) * (py - 0.5));
                bool inBar = InAnyBar(bars, px, py);
                bool on = filled
                    ? d <= outer && !inBar
                    : (d <= outer && d >= outer - ring) || inBar;
                if (on) hits++;
            }
            if (hits == 0) continue;
            int a = hits * 255 / (Samples * Samples);
            pixels[y * size + x] = (uint)a << 24 | (uint)r << 16 | (uint)g << 8 | b;
        }
        return pixels;
    }

    /// <summary>The waveform's bars as capsules (centre x, half height, radius),
    /// in unit coordinates: three at the smallest sizes, where five would
    /// blur together, else five. Widths are whole pixels and centres sit on
    /// the pixel grid, so the bars stay sharp rather than smeared grey.</summary>
    internal static (double X, double HalfHeight, double Radius)[] Bars(int size)
    {
        var layout = size < 20
            ? new[] { (0.30, 0.10), (0.50, 0.20), (0.70, 0.10) }
            : new[] { (0.24, 0.06), (0.37, 0.14), (0.50, 0.22), (0.63, 0.14), (0.76, 0.06) };
        int width = size < 20 ? 2 : Math.Max(2, (int)Math.Round(0.07 * size));
        double radius = width / 2.0 / size;
        return layout.Select(b =>
        {
            double px = b.Item1 * size;
            double snapped = width % 2 == 0 ? Math.Round(px) : Math.Floor(px) + 0.5;
            return (snapped / size, b.Item2, radius);
        }).ToArray();
    }

    private static bool InAnyBar((double X, double HalfHeight, double Radius)[] bars, double px, double py)
    {
        foreach (var (x, half, radius) in bars)
        {
            // Distance to the vertical segment from (x, 0.5 - half) to (x, 0.5 + half).
            double cy = Math.Clamp(py, 0.5 - half, 0.5 + half);
            double dx = px - x, dy = py - cy;
            if (dx * dx + dy * dy <= radius * radius) return true;
        }
        return false;
    }
}
