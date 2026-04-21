using System.Buffers;
using System.Text;

namespace LexIndex;

public static class LexIndexBuilder
{
    private const int FormatVersion = 1;
    private const int CurrentNormalizationKind = 1;
    private static ReadOnlySpan<byte> Magic => "LXDX"u8;

    public static async Task BuildAsync(
        IEnumerable<string> keys,
        Stream output,
        LexIndexBuildOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        }

        options ??= new LexIndexBuildOptions();
        ValidateOptions(options);

        var groupedKeys = CollectGroupedKeys(keys, options, ct);
        var builder = new DawgBuilder();
        foreach (var entry in groupedKeys)
        {
            ct.ThrowIfCancellationRequested();
            builder.Add(entry.Normalized, entry.PayloadIndex);
        }

        var frozen = builder.Freeze();
        var serialized = Serialize(frozen, groupedKeys, options);

        await output.WriteAsync(serialized, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static string NormalizeKey(string value, LexIndexBuildOptions? options = null)
    {
        options ??= new LexIndexBuildOptions();
        ValidateOptions(options);
        return value.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    }

    private static void ValidateOptions(LexIndexBuildOptions options)
    {
        if (!string.Equals(
                options.NormalizationId,
                LexIndexBuildOptions.DefaultNormalizationId,
                StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unsupported normalization id '{options.NormalizationId}'.");
        }

        if (options.StringEncoding.CodePage != Encoding.UTF8.CodePage)
        {
            throw new NotSupportedException("Only UTF-8 string encoding is supported.");
        }
    }

    private static List<GroupedKey> CollectGroupedKeys(
        IEnumerable<string> keys,
        LexIndexBuildOptions options,
        CancellationToken ct)
    {
        var groups = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var trimmed = key.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var normalized = NormalizeKey(trimmed, options);
            if (!groups.TryGetValue(normalized, out var originals))
            {
                originals = new SortedSet<string>(StringComparer.Ordinal);
                groups[normalized] = originals;
            }

            originals.Add(trimmed);
        }

        var sorted = groups
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select((pair, index) => new GroupedKey(index, pair.Key, [.. pair.Value]))
            .ToList();

        return sorted;
    }

    private static byte[] Serialize(
        FrozenDawg dawg,
        IReadOnlyList<GroupedKey> groupedKeys,
        LexIndexBuildOptions options)
    {
        var uniqueStrings = new List<string>();
        var stringToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var valueRefs = new List<int>();
        var payloads = new List<PayloadRecord>(groupedKeys.Count);

        foreach (var group in groupedKeys)
        {
            var firstRefIndex = valueRefs.Count;
            foreach (var original in group.Originals)
            {
                if (!stringToIndex.TryGetValue(original, out var stringIndex))
                {
                    stringIndex = uniqueStrings.Count;
                    uniqueStrings.Add(original);
                    stringToIndex.Add(original, stringIndex);
                }

                valueRefs.Add(stringIndex);
            }

            payloads.Add(new PayloadRecord(firstRefIndex, group.Originals.Count));
        }

        var stringOffsets = new int[uniqueStrings.Count + 1];
        var stringBytes = new ArrayBufferWriter<byte>();
        for (int i = 0; i < uniqueStrings.Count; i++)
        {
            stringOffsets[i] = stringBytes.WrittenCount;
            var bytes = options.StringEncoding.GetBytes(uniqueStrings[i]);
            stringBytes.Write(bytes);
        }

        stringOffsets[^1] = stringBytes.WrittenCount;

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, options.StringEncoding, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(CurrentNormalizationKind);
        writer.Write(dawg.States.Length);
        writer.Write(dawg.Edges.Length);
        writer.Write(groupedKeys.Count);
        writer.Write(payloads.Count);
        writer.Write(valueRefs.Count);
        writer.Write(uniqueStrings.Count);
        writer.Write(stringBytes.WrittenCount);

        foreach (var state in dawg.States)
        {
            writer.Write(state.FirstEdgeIndex);
            writer.Write(state.EdgeCount);
            writer.Write(state.PayloadIndex);
        }

        foreach (var edge in dawg.Edges)
        {
            writer.Write(edge.Label);
            writer.Write(edge.TargetStateId);
        }

        foreach (var payload in payloads)
        {
            writer.Write(payload.FirstValueRefIndex);
            writer.Write(payload.ValueCount);
        }

        foreach (var valueRef in valueRefs)
        {
            writer.Write(valueRef);
        }

        foreach (var offset in stringOffsets)
        {
            writer.Write(offset);
        }

        writer.Write(stringBytes.WrittenSpan);
        writer.Flush();
        return memory.ToArray();
    }

    private sealed record GroupedKey(int PayloadIndex, string Normalized, IReadOnlyList<string> Originals);

    private sealed record PayloadRecord(int FirstValueRefIndex, int ValueCount);

    private sealed class DawgBuilder
    {
        private readonly DawgNode _root = new();
        private readonly List<UncheckedNode> _uncheckedNodes = [];
        private readonly Dictionary<NodeSignature, DawgNode> _registry = new();
        private int _nextRegistryId = 1;
        private ReadOnlyMemory<Rune> _previousWord = ReadOnlyMemory<Rune>.Empty;

        public void Add(string normalizedWord, int payloadIndex)
        {
            var runes = normalizedWord.EnumerateRunes().ToArray();
            var commonPrefixLength = GetCommonPrefixLength(_previousWord.Span, runes);

            Minimize(commonPrefixLength);

            var node = commonPrefixLength == 0
                ? _root
                : _uncheckedNodes[commonPrefixLength - 1].Child;

            for (int i = commonPrefixLength; i < runes.Length; i++)
            {
                var child = new DawgNode();
                node.Edges[runes[i].Value] = child;
                _uncheckedNodes.Add(new UncheckedNode(node, runes[i].Value, child));
                node = child;
            }

            node.PayloadIndex = payloadIndex;
            _previousWord = runes;
        }

        public FrozenDawg Freeze()
        {
            Minimize(0);

            var stateIds = new Dictionary<DawgNode, int>(ReferenceEqualityComparer.Instance)
            {
                [_root] = 0
            };
            var queue = new Queue<DawgNode>();
            queue.Enqueue(_root);
            var orderedStates = new List<DawgNode>();

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                orderedStates.Add(node);
                foreach (var edge in node.Edges.OrderBy(pair => pair.Key))
                {
                    if (stateIds.ContainsKey(edge.Value))
                    {
                        continue;
                    }

                    stateIds[edge.Value] = stateIds.Count;
                    queue.Enqueue(edge.Value);
                }
            }

            var states = new FrozenState[orderedStates.Count];
            var edges = new List<FrozenEdge>();

            for (int i = 0; i < orderedStates.Count; i++)
            {
                var node = orderedStates[i];
                var firstEdgeIndex = edges.Count;
                foreach (var edge in node.Edges.OrderBy(pair => pair.Key))
                {
                    edges.Add(new FrozenEdge(edge.Key, stateIds[edge.Value]));
                }

                states[i] = new FrozenState(firstEdgeIndex, edges.Count - firstEdgeIndex, node.PayloadIndex);
            }

            return new FrozenDawg(states, [.. edges]);
        }

        private void Minimize(int downTo)
        {
            for (int i = _uncheckedNodes.Count - 1; i >= downTo; i--)
            {
                var uncheckedNode = _uncheckedNodes[i];
                var signature = NodeSignature.Create(uncheckedNode.Child);

                if (_registry.TryGetValue(signature, out var registered))
                {
                    uncheckedNode.Parent.Edges[uncheckedNode.Label] = registered;
                }
                else
                {
                    uncheckedNode.Child.RegistryId = _nextRegistryId++;
                    _registry[signature] = uncheckedNode.Child;
                }

                _uncheckedNodes.RemoveAt(i);
            }
        }

        private static int GetCommonPrefixLength(ReadOnlySpan<Rune> left, ReadOnlySpan<Rune> right)
        {
            var count = Math.Min(left.Length, right.Length);
            for (int i = 0; i < count; i++)
            {
                if (left[i] != right[i])
                {
                    return i;
                }
            }

            return count;
        }
    }

    private sealed class DawgNode
    {
        public SortedDictionary<int, DawgNode> Edges { get; } = new();

        public int PayloadIndex { get; set; } = -1;

        public int RegistryId { get; set; }
    }

    private sealed record UncheckedNode(DawgNode Parent, int Label, DawgNode Child);

    private readonly record struct NodeSignature(string Value)
    {
        public static NodeSignature Create(DawgNode node)
        {
            var builder = new StringBuilder();
            builder.Append(node.PayloadIndex);
            builder.Append('|');
            foreach (var edge in node.Edges)
            {
                builder.Append(edge.Key);
                builder.Append(':');
                builder.Append(edge.Value.RegistryId);
                builder.Append(';');
            }

            return new NodeSignature(builder.ToString());
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<DawgNode>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public bool Equals(DawgNode? x, DawgNode? y) => ReferenceEquals(x, y);

        public int GetHashCode(DawgNode obj) => obj.GetHashCode();
    }

    internal readonly record struct FrozenState(int FirstEdgeIndex, int EdgeCount, int PayloadIndex);

    internal readonly record struct FrozenEdge(int Label, int TargetStateId);

    internal readonly record struct FrozenDawg(FrozenState[] States, FrozenEdge[] Edges);
}
