using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScadaTest.Hmi.Omi;
using Rgb = ScadaTest.Hmi.Omi.ColorClassifier.Rgb;

namespace ScadaTest.Spike.Omi;

/// <summary>Colour statistics for one captured symbol region.</summary>
internal sealed class Pixels
{
    private readonly Rgb[] _pixels;

    public int Width { get; }

    public int Height { get; }

    private Pixels(Rgb[] pixels, int width, int height)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
    }

    public static Pixels From(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var pixels = new Rgb[bmp.Width * bmp.Height];
            for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var i = (y * data.Stride) + (x * 4); // BGRA
                pixels[(y * bmp.Width) + x] = new Rgb(bytes[i + 2], bytes[i + 1], bytes[i]);
            }
            return new Pixels(pixels, bmp.Width, bmp.Height);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public int Count => _pixels.Length;

    /// <summary>Mean colour of the region. This is what <c>OmiViewAppDriver</c> classifies today.</summary>
    public Rgb Average()
    {
        if (_pixels.Length == 0) return default;
        long r = 0, g = 0, b = 0;
        foreach (var p in _pixels) { r += p.R; g += p.G; b += p.B; }
        var n = _pixels.Length;
        return new Rgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>The most common colours, bucketed to 16 levels per channel, with their share of the region.</summary>
    public IReadOnlyList<(Rgb Color, double Share)> Dominant(int top)
    {
        var buckets = new Dictionary<int, (long R, long G, long B, int N)>();
        foreach (var p in _pixels)
        {
            var key = ((p.R >> 4) << 8) | ((p.G >> 4) << 4) | (p.B >> 4);
            buckets.TryGetValue(key, out var acc);
            buckets[key] = (acc.R + p.R, acc.G + p.G, acc.B + p.B, acc.N + 1);
        }
        return buckets.Values
            .OrderByDescending(v => v.N)
            .Take(top)
            .Select(v => (new Rgb((byte)(v.R / v.N), (byte)(v.G / v.N), (byte)(v.B / v.N)), (double)v.N / _pixels.Length))
            .ToList();
    }

    /// <summary>Share of pixels within <paramref name="tolerance"/> of each state's reference colour.</summary>
    public IReadOnlyDictionary<string, double> StateShares(IReadOnlyDictionary<string, string> states, int tolerance)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (state, hex) in states)
        {
            var reference = Rgb.Parse(hex);
            var n = _pixels.Count(p => p.DistanceTo(reference) <= tolerance);
            result[state] = _pixels.Length == 0 ? 0 : (double)n / _pixels.Length;
        }
        return result;
    }

    /// <summary>True when nearly every pixel is black, which is what a locked or minimised RDP desktop returns.</summary>
    public bool LooksBlank() => _pixels.Length > 0 && _pixels.Count(p => p.R < 8 && p.G < 8 && p.B < 8) > _pixels.Length * 0.98;
}

/// <summary>The two classifiers the spike compares.</summary>
internal static class Classifiers
{
    /// <summary>Today's driver: nearest state to the region's mean colour, within tolerance.</summary>
    public static (string State, string Detail) ByAverage(Pixels px, IReadOnlyDictionary<string, string> states, int tolerance)
    {
        var avg = px.Average();
        var (state, distance) = ColorClassifier.Classify(avg, states, tolerance);
        return (state ?? "unknown", $"avg {avg} dist {distance:0}");
    }

    /// <summary>
    /// Alternative: the state whose colour covers the largest share of the region, if at least
    /// <paramref name="minShare"/>. Robust to outlines, labels and background inside the region.
    /// </summary>
    public static (string State, string Detail) ByShare(Pixels px, IReadOnlyDictionary<string, string> states, int tolerance, double minShare)
    {
        var shares = px.StateShares(states, tolerance);
        var best = shares.OrderByDescending(kv => kv.Value).FirstOrDefault();
        var detail = string.Join(" ", shares.Select(kv => $"{kv.Key}={kv.Value:P0}"));
        return (best.Key is not null && best.Value >= minShare ? best.Key : "unknown", detail);
    }
}
