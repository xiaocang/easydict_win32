using System.Buffers;
using System.Text;

namespace LexIndex;

public sealed class LexIndex : ILexIndex
{
    private const int FormatVersion = 1;
    private const int CurrentNormalizationKind = 1;
    private static ReadOnlySpan<byte> Magic => "LXDX"u8;
    private static readonly Encoding StringEncoding = Encoding.UTF8;

    private readonly StateRecord[] _states;
    private readonly EdgeRecord[] _edges;
    private readonly PayloadRecord[] _payloads;
    private readonly int[] _valueRefs;
    private readonly int[] _stringOffsets;
    private readonly byte[] _stringBytes;

    private LexIndex(
        LexIndexMetadata metadata,
        StateRecord[] states,
        EdgeRecord[] edges,
        PayloadRecord[] payloads,
        int[] valueRefs,
        int[] stringOffsets,
        byte[] stringBytes)
    {
        Metadata = metadata;
        _states = states;
        _edges = edges;
        _payloads = payloads;
        _valueRefs = valueRefs;
        _stringOffsets = stringOffsets;
        _stringBytes = stringBytes;
    }

    public LexIndexMetadata Metadata { get; }

    public static LexIndex Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Open(stream);
    }

    public static LexIndex Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Open(memory.ToArray());
    }

    public IReadOnlyList<string> Complete(string prefix, int limit)
    {
        if (limit <= 0)
        {
            return Array.Empty<string>();
        }

        var normalized = LexIndexBuilder.NormalizeKey(prefix);
        if (normalized.Length == 0)
        {
            return Array.Empty<string>();
        }

        var runes = normalized.EnumerateRunes().ToArray();
        var stateId = TraverseExact(runes);
        if (stateId < 0)
        {
            return Array.Empty<string>();
        }

        var results = new List<string>(Math.Min(limit, 32));
        CollectCompletions(stateId, results, limit);
        return results;
    }

    public IReadOnlyList<string> Match(string pattern, int limit)
    {
        if (limit <= 0)
        {
            return Array.Empty<string>();
        }

        var normalized = LexIndexBuilder.NormalizeKey(pattern);
        if (normalized.Length == 0)
        {
            return Array.Empty<string>();
        }

        var runes = normalized.EnumerateRunes().ToArray();
        var results = new HashSet<string>(StringComparer.Ordinal);
        var deadEnds = new HashSet<(int StateId, int PatternPosition)>();
        MatchCore(0, runes, 0, results, deadEnds);
        return results
            .OrderBy(value => LexIndexBuilder.NormalizeKey(value), StringComparer.Ordinal)
            .ThenBy(value => value, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private static LexIndex Open(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(memory, StringEncoding, leaveOpen: true);

        var magic = reader.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Invalid LexIndex file header.");
        }

        var version = reader.ReadInt32();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported LexIndex format version {version}.");
        }

        var normalizationKind = reader.ReadInt32();
        if (normalizationKind != CurrentNormalizationKind)
        {
            throw new InvalidDataException($"Unsupported normalization kind {normalizationKind}.");
        }

        var stateCount = reader.ReadInt32();
        var edgeCount = reader.ReadInt32();
        var entryCount = reader.ReadInt32();
        var payloadCount = reader.ReadInt32();
        var valueRefCount = reader.ReadInt32();
        var stringCount = reader.ReadInt32();
        var stringByteCount = reader.ReadInt32();

        if (stateCount <= 0 || edgeCount < 0 || entryCount < 0 || payloadCount < 0 || valueRefCount < 0 || stringCount < 0 || stringByteCount < 0)
        {
            throw new InvalidDataException("LexIndex file contains invalid counts.");
        }

        var states = new StateRecord[stateCount];
        for (int i = 0; i < stateCount; i++)
        {
            states[i] = new StateRecord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        }

        var edges = new EdgeRecord[edgeCount];
        for (int i = 0; i < edgeCount; i++)
        {
            edges[i] = new EdgeRecord(reader.ReadInt32(), reader.ReadInt32());
        }

        var payloads = new PayloadRecord[payloadCount];
        for (int i = 0; i < payloadCount; i++)
        {
            payloads[i] = new PayloadRecord(reader.ReadInt32(), reader.ReadInt32());
        }

        var valueRefs = new int[valueRefCount];
        for (int i = 0; i < valueRefCount; i++)
        {
            valueRefs[i] = reader.ReadInt32();
        }

        var stringOffsets = new int[stringCount + 1];
        for (int i = 0; i < stringOffsets.Length; i++)
        {
            stringOffsets[i] = reader.ReadInt32();
        }

        var stringBytes = reader.ReadBytes(stringByteCount);
        if (stringBytes.Length != stringByteCount)
        {
            throw new InvalidDataException("Unexpected end of LexIndex string pool.");
        }

        Validate(states, edges, payloads, valueRefs, stringOffsets, stringBytes.Length);

        return new LexIndex(
            new LexIndexMetadata
            {
                FormatVersion = version,
                NormalizationId = LexIndexBuildOptions.DefaultNormalizationId,
                StateCount = stateCount,
                EdgeCount = edgeCount,
                EntryCount = entryCount,
                PayloadCount = payloadCount,
                ValueRefCount = valueRefCount,
                StringCount = stringCount
            },
            states,
            edges,
            payloads,
            valueRefs,
            stringOffsets,
            stringBytes);
    }

    private static void Validate(
        IReadOnlyList<StateRecord> states,
        IReadOnlyList<EdgeRecord> edges,
        IReadOnlyList<PayloadRecord> payloads,
        IReadOnlyList<int> valueRefs,
        IReadOnlyList<int> stringOffsets,
        int stringByteCount)
    {
        for (int i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (state.FirstEdgeIndex < 0 || state.EdgeCount < 0 || state.FirstEdgeIndex + state.EdgeCount > edges.Count)
            {
                throw new InvalidDataException($"State {i} has invalid edge bounds.");
            }

            if (state.PayloadIndex >= payloads.Count)
            {
                throw new InvalidDataException($"State {i} points to invalid payload index.");
            }

            var previousLabel = int.MinValue;
            for (int j = state.FirstEdgeIndex; j < state.FirstEdgeIndex + state.EdgeCount; j++)
            {
                var edge = edges[j];
                if (edge.TargetStateId < 0 || edge.TargetStateId >= states.Count)
                {
                    throw new InvalidDataException($"Edge {j} points to invalid target state.");
                }

                if (edge.Label < previousLabel)
                {
                    throw new InvalidDataException($"State {i} edges are not ordered.");
                }

                previousLabel = edge.Label;
            }
        }

        for (int i = 0; i < payloads.Count; i++)
        {
            var payload = payloads[i];
            if (payload.FirstValueRefIndex < 0 || payload.ValueCount < 0 || payload.FirstValueRefIndex + payload.ValueCount > valueRefs.Count)
            {
                throw new InvalidDataException($"Payload {i} has invalid string reference bounds.");
            }
        }

        if (stringOffsets.Count == 0 || stringOffsets[0] != 0 || stringOffsets[^1] != stringByteCount)
        {
            throw new InvalidDataException("String pool offsets are invalid.");
        }

        for (int i = 1; i < stringOffsets.Count; i++)
        {
            if (stringOffsets[i] < stringOffsets[i - 1])
            {
                throw new InvalidDataException("String pool offsets are not monotonic.");
            }
        }

        for (int i = 0; i < valueRefs.Count; i++)
        {
            if (valueRefs[i] < 0 || valueRefs[i] >= stringOffsets.Count - 1)
            {
                throw new InvalidDataException($"String reference {i} is out of range.");
            }
        }
    }

    private int TraverseExact(ReadOnlySpan<Rune> runes)
    {
        var stateId = 0;
        foreach (var rune in runes)
        {
            stateId = FindTransition(stateId, rune.Value);
            if (stateId < 0)
            {
                return -1;
            }
        }

        return stateId;
    }

    private void CollectCompletions(int stateId, List<string> results, int limit)
    {
        if (results.Count >= limit)
        {
            return;
        }

        var state = _states[stateId];
        if (state.PayloadIndex >= 0)
        {
            AddPayloadValues(state.PayloadIndex, results, limit);
            if (results.Count >= limit)
            {
                return;
            }
        }

        for (int i = state.FirstEdgeIndex; i < state.FirstEdgeIndex + state.EdgeCount; i++)
        {
            CollectCompletions(_edges[i].TargetStateId, results, limit);
            if (results.Count >= limit)
            {
                return;
            }
        }
    }

    private bool MatchCore(
        int stateId,
        ReadOnlySpan<Rune> pattern,
        int patternPosition,
        HashSet<string> results,
        HashSet<(int StateId, int PatternPosition)> deadEnds)
    {
        if (deadEnds.Contains((stateId, patternPosition)))
        {
            return false;
        }

        var foundAny = false;
        var state = _states[stateId];

        if (patternPosition == pattern.Length)
        {
            if (state.PayloadIndex >= 0)
            {
                AddPayloadValues(state.PayloadIndex, results);
                foundAny = true;
            }

            if (!foundAny)
            {
                deadEnds.Add((stateId, patternPosition));
            }

            return foundAny;
        }

        var current = pattern[patternPosition];
        if (current.Value == '*')
        {
            foundAny |= MatchCore(stateId, pattern, patternPosition + 1, results, deadEnds);
            for (int i = state.FirstEdgeIndex; i < state.FirstEdgeIndex + state.EdgeCount; i++)
            {
                foundAny |= MatchCore(_edges[i].TargetStateId, pattern, patternPosition, results, deadEnds);
            }
        }
        else if (current.Value == '?')
        {
            for (int i = state.FirstEdgeIndex; i < state.FirstEdgeIndex + state.EdgeCount; i++)
            {
                foundAny |= MatchCore(_edges[i].TargetStateId, pattern, patternPosition + 1, results, deadEnds);
            }
        }
        else
        {
            var nextStateId = FindTransition(stateId, current.Value);
            if (nextStateId >= 0)
            {
                foundAny = MatchCore(nextStateId, pattern, patternPosition + 1, results, deadEnds);
            }
        }

        if (!foundAny)
        {
            deadEnds.Add((stateId, patternPosition));
        }

        return foundAny;
    }

    private int FindTransition(int stateId, int label)
    {
        var state = _states[stateId];
        var low = state.FirstEdgeIndex;
        var high = state.FirstEdgeIndex + state.EdgeCount - 1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var edge = _edges[mid];
            if (edge.Label == label)
            {
                return edge.TargetStateId;
            }

            if (edge.Label < label)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return -1;
    }

    private void AddPayloadValues(int payloadIndex, List<string> results, int limit)
    {
        var payload = _payloads[payloadIndex];
        for (int i = payload.FirstValueRefIndex; i < payload.FirstValueRefIndex + payload.ValueCount; i++)
        {
            results.Add(ReadString(_valueRefs[i]));
            if (results.Count >= limit)
            {
                return;
            }
        }
    }

    private void AddPayloadValues(int payloadIndex, HashSet<string> results)
    {
        var payload = _payloads[payloadIndex];
        for (int i = payload.FirstValueRefIndex; i < payload.FirstValueRefIndex + payload.ValueCount; i++)
        {
            results.Add(ReadString(_valueRefs[i]));
        }
    }

    private string ReadString(int stringIndex)
    {
        var start = _stringOffsets[stringIndex];
        var end = _stringOffsets[stringIndex + 1];
        return StringEncoding.GetString(_stringBytes, start, end - start);
    }

    private readonly record struct StateRecord(int FirstEdgeIndex, int EdgeCount, int PayloadIndex);

    private readonly record struct EdgeRecord(int Label, int TargetStateId);

    private readonly record struct PayloadRecord(int FirstValueRefIndex, int ValueCount);
}
