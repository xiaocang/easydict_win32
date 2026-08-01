using System.Reflection;
using System.Text.Json;

namespace Easydict.DirectXaml.Ir;

/// <summary>Raised when IR cannot be trusted. Loading never degrades — it either succeeds fully or throws.</summary>
public sealed class IrLoadException : Exception
{
    public IrLoadException(string message) : base(message)
    {
    }

    public IrLoadException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Deserializes and validates a compiled Direct XAML document.
///
/// The compiler guarantees a total translation — a document either compiled completely or not at
/// all — and the loader upholds the other half of that contract: unknown IR versions and unknown
/// features are refused outright rather than partially honoured.
/// </summary>
public static class IrLoader
{
    public const string SupportedIrVersion = "0.1.0";

    private static readonly HashSet<string> KnownFeatures = new(StringComparer.Ordinal)
    {
        "named-slots",
        "theme-resources",
        "actions",
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        // Every property carries an explicit [JsonPropertyName]; nothing is inferred.
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    public static IrDocument Load(string json)
    {
        IrDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<IrDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new IrLoadException($"IR is not valid JSON: {ex.Message}", ex);
        }

        if (document is null)
        {
            throw new IrLoadException("IR document is null");
        }

        Validate(document);
        return document;
    }

    /// <summary>Loads IR embedded in an assembly, which is how the vertical slice ships it.</summary>
    public static IrDocument LoadFromResource(Assembly assembly, string resourceName)
    {
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            string available = string.Join(", ", assembly.GetManifestResourceNames());
            throw new IrLoadException(
                $"embedded resource '{resourceName}' not found; available: {available}");
        }

        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    }

    /// <summary>Maps an IR <c>kind</c> string onto <see cref="NodeKind"/>.</summary>
    public static NodeKind ParseNodeKind(string kind) => kind switch
    {
        "userControl" => NodeKind.UserControl,
        "border" => NodeKind.Border,
        "grid" => NodeKind.Grid,
        "stackPanel" => NodeKind.StackPanel,
        "textBlock" => NodeKind.TextBlock,
        "rowDefinition" => NodeKind.RowDefinition,
        "columnDefinition" => NodeKind.ColumnDefinition,
        _ => throw new IrLoadException($"unknown node kind '{kind}'"),
    };

    public static Invalidation ParseInvalidation(IEnumerable<string> names)
    {
        Invalidation result = Invalidation.None;
        foreach (string name in names)
        {
            result |= name switch
            {
                "measure" => Invalidation.Measure,
                "arrange" => Invalidation.Arrange,
                "paint" => Invalidation.Paint,
                "semantics" => Invalidation.Semantics,
                _ => throw new IrLoadException($"unknown invalidation '{name}'"),
            };
        }

        return result;
    }

    private static void Validate(IrDocument document)
    {
        if (document.IrVersion != SupportedIrVersion)
        {
            throw new IrLoadException(
                $"IR version '{document.IrVersion}' is not supported; this runtime implements '{SupportedIrVersion}'");
        }

        foreach (string feature in document.Features)
        {
            if (!KnownFeatures.Contains(feature))
            {
                throw new IrLoadException(
                    $"IR declares feature '{feature}', which this runtime does not implement");
            }
        }

        if (document.Nodes.Count == 0)
        {
            throw new IrLoadException("IR contains no nodes");
        }

        int rootCount = 0;
        for (int index = 0; index < document.Nodes.Count; index++)
        {
            IrNode node = document.Nodes[index];
            if (node.Id != index)
            {
                throw new IrLoadException($"node at index {index} declares id {node.Id}");
            }

            // Throws for an unrecognised kind, which is the point: fail at load, not at paint.
            ParseNodeKind(node.Kind);

            if (node.Parent is null)
            {
                rootCount++;
            }
            else if (node.Parent.Value < 0 || node.Parent.Value >= document.Nodes.Count)
            {
                throw new IrLoadException($"node {node.Id} has out-of-range parent {node.Parent.Value}");
            }

            foreach (int child in node.Children)
            {
                if (child < 0 || child >= document.Nodes.Count)
                {
                    throw new IrLoadException($"node {node.Id} has out-of-range child {child}");
                }

                if (document.Nodes[child].Parent != node.Id)
                {
                    throw new IrLoadException($"node {node.Id} lists child {child}, which does not point back at it");
                }
            }
        }

        if (rootCount != 1)
        {
            throw new IrLoadException($"expected exactly one root node, found {rootCount}");
        }

        foreach (IrProperty property in document.Properties)
        {
            RequireNode(document, property.Node, $"property '{property.Name}'");
            if (property.Value is IrResourceValue resource
                && (resource.Resource < 0 || resource.Resource >= document.Resources.Count))
            {
                throw new IrLoadException(
                    $"property '{property.Name}' references unknown resource {resource.Resource}");
            }
        }

        for (int index = 0; index < document.Resources.Count; index++)
        {
            if (document.Resources[index].Id != index)
            {
                throw new IrLoadException($"resource at index {index} declares id {document.Resources[index].Id}");
            }
        }

        var seenSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach (IrNamedSlot slot in document.NamedSlots)
        {
            RequireNode(document, slot.Node, $"named slot '{slot.Name}'");
            if (!seenSlots.Add(slot.Name))
            {
                throw new IrLoadException($"named slot '{slot.Name}' is declared twice");
            }

            foreach (IrMutableProperty mutable in slot.Mutable)
            {
                ParseInvalidation(mutable.Invalidation);
            }
        }

        foreach (IrAction action in document.Actions)
        {
            RequireNode(document, action.Node, $"action '{action.Event}'");
        }

        foreach (IrSemantics semantics in document.Semantics)
        {
            RequireNode(document, semantics.Node, "semantics entry");
        }
    }

    private static void RequireNode(IrDocument document, int node, string what)
    {
        if (node < 0 || node >= document.Nodes.Count)
        {
            throw new IrLoadException($"{what} references unknown node {node}");
        }
    }
}
