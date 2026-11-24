using System.Text.RegularExpressions;

namespace ScadaTest.Core;

/// <summary>Refuses tag writes the environment does not allow. Every test write goes through it.</summary>
public sealed class WriteGuard
{
    private readonly Regex[] _allowed;

    public WriteGuard(bool allowWrites, IEnumerable<string>? writablePatterns = null)
    {
        AllowWrites = allowWrites;
        _allowed = (writablePatterns ?? ["*"]).Select(GlobToRegex).ToArray();
    }

    public bool AllowWrites { get; }

    public static WriteGuard ReadOnly { get; } = new(false);

    public static WriteGuard AllowAll { get; } = new(true);

    public void EnsureWritable(TagRef tag)
    {
        if (!AllowWrites)
            throw new WriteNotAllowedException($"Writes are disabled for this environment (tried to write '{tag}').");
        if (!_allowed.Any(r => r.IsMatch(tag.Name)))
            throw new WriteNotAllowedException($"Tag '{tag}' does not match any writable pattern for this environment.");
    }

    private static Regex GlobToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

public sealed class WriteNotAllowedException(string message) : InvalidOperationException(message);
