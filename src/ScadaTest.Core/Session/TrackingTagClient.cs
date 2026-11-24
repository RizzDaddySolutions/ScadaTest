using System.Collections.Concurrent;

namespace ScadaTest.Core;

/// <summary>
/// Wraps a tag client so every write is guarded, logged, and undone at teardown:
/// the original value of each written tag is captured before its first write.
/// </summary>
public sealed class TrackingTagClient(ITagClient inner, WriteGuard guard, Action<WriteRecord>? onWrite = null) : ITagClient
{
    private readonly ConcurrentDictionary<string, TagValue> _originals = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _writeOrder = [];

    public IReadOnlyCollection<TagRef> WrittenTags => _writeOrder.Select(n => new TagRef(n)).ToArray();

    public Task<TagValue> ReadAsync(TagRef tag, CancellationToken ct) => inner.ReadAsync(tag, ct);

    public IAsyncEnumerable<TagValue> SubscribeAsync(TagRef tag, CancellationToken ct) => inner.SubscribeAsync(tag, ct);

    public async Task WriteAsync(TagRef tag, object? value, CancellationToken ct)
    {
        guard.EnsureWritable(tag);
        TagValue? before = null;
        if (!_originals.ContainsKey(tag.Name))
        {
            before = await inner.ReadAsync(tag, ct).ConfigureAwait(false);
            if (_originals.TryAdd(tag.Name, before))
                lock (_writeOrder) _writeOrder.Add(tag.Name);
        }
        before ??= _originals[tag.Name];
        await inner.WriteAsync(tag, value, ct).ConfigureAwait(false);
        onWrite?.Invoke(new WriteRecord(tag, before.Value, value, DateTimeOffset.UtcNow));
    }

    /// <summary>Writes original values back, last-written first. Errors are collected, not thrown midway.</summary>
    public async Task<IReadOnlyList<Exception>> RestoreAsync(CancellationToken ct)
    {
        var errors = new List<Exception>();
        string[] order;
        lock (_writeOrder) order = [.. _writeOrder];
        foreach (var name in order.Reverse())
        {
            try
            {
                await inner.WriteAsync(name, _originals[name].Value, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(new InvalidOperationException($"Could not restore '{name}': {ex.Message}", ex));
            }
        }
        _originals.Clear();
        lock (_writeOrder) _writeOrder.Clear();
        return errors;
    }
}

public sealed record WriteRecord(TagRef Tag, object? OldValue, object? NewValue, DateTimeOffset Timestamp);
