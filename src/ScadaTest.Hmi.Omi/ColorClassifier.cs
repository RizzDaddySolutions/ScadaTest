using System.Globalization;

namespace ScadaTest.Hmi.Omi;

/// <summary>Picks the configured state whose reference colour is nearest the sampled colour.</summary>
public static class ColorClassifier
{
    public readonly record struct Rgb(byte R, byte G, byte B)
    {
        public static Rgb Parse(string hex)
        {
            var h = hex.TrimStart('#');
            if (h.Length != 6)
                throw new FormatException($"Colour '{hex}' must be #RRGGBB.");
            return new Rgb(
                byte.Parse(h[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(h[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(h[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        public double DistanceTo(Rgb o) => Math.Sqrt(Math.Pow(R - o.R, 2) + Math.Pow(G - o.G, 2) + Math.Pow(B - o.B, 2));

        public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
    }

    /// <summary>Returns the nearest state within tolerance, or <c>null</c> when nothing is close enough.</summary>
    public static (string? State, double Distance) Classify(Rgb sample, IReadOnlyDictionary<string, string> states, int tolerance)
    {
        string? best = null;
        var bestDistance = double.MaxValue;
        foreach (var (state, hex) in states)
        {
            var d = sample.DistanceTo(Rgb.Parse(hex));
            if (d < bestDistance)
            {
                best = state;
                bestDistance = d;
            }
        }
        return bestDistance <= tolerance ? (best, bestDistance) : (null, bestDistance);
    }
}
