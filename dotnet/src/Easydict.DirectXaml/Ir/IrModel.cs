using System.Text.Json.Serialization;

namespace Easydict.DirectXaml.Ir;

/// <summary>
/// C# mirror of <c>compiler/schemas/dxir-v0.schema.json</c>.
///
/// Property names are spelled out explicitly rather than relying on a naming policy: the document
/// level uses snake_case (<c>ir_version</c>, <c>named_slots</c>) while value payloads use
/// camelCase (<c>gridLength</c>), so no single policy covers both.
/// </summary>
public sealed record IrDocument
{
    [JsonPropertyName("ir_version")]
    public string IrVersion { get; init; } = string.Empty;

    [JsonPropertyName("compiler_version")]
    public string CompilerVersion { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public IrSource Source { get; init; } = new();

    [JsonPropertyName("class_name")]
    public string ClassName { get; init; } = string.Empty;

    [JsonPropertyName("features")]
    public IReadOnlyList<string> Features { get; init; } = Array.Empty<string>();

    [JsonPropertyName("nodes")]
    public IReadOnlyList<IrNode> Nodes { get; init; } = Array.Empty<IrNode>();

    [JsonPropertyName("properties")]
    public IReadOnlyList<IrProperty> Properties { get; init; } = Array.Empty<IrProperty>();

    [JsonPropertyName("named_slots")]
    public IReadOnlyList<IrNamedSlot> NamedSlots { get; init; } = Array.Empty<IrNamedSlot>();

    [JsonPropertyName("resources")]
    public IReadOnlyList<IrResource> Resources { get; init; } = Array.Empty<IrResource>();

    [JsonPropertyName("actions")]
    public IReadOnlyList<IrAction> Actions { get; init; } = Array.Empty<IrAction>();

    [JsonPropertyName("semantics")]
    public IReadOnlyList<IrSemantics> Semantics { get; init; } = Array.Empty<IrSemantics>();
}

public sealed record IrSource
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; init; } = string.Empty;
}

public sealed record IrNode
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("parent")]
    public int? Parent { get; init; }

    /// <summary>
    /// For a grid this includes its rowDefinition and columnDefinition nodes alongside the visual
    /// children; consumers separate them by <see cref="Kind"/>.
    /// </summary>
    [JsonPropertyName("children")]
    public IReadOnlyList<int> Children { get; init; } = Array.Empty<int>();

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

public sealed record IrProperty
{
    [JsonPropertyName("node")]
    public int Node { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public IrValue Value { get; init; } = new IrNullValue();
}

public sealed record IrNamedSlot
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("node")]
    public int Node { get; init; }

    [JsonPropertyName("mutable")]
    public IReadOnlyList<IrMutableProperty> Mutable { get; init; } = Array.Empty<IrMutableProperty>();
}

public sealed record IrMutableProperty
{
    [JsonPropertyName("property")]
    public string Property { get; init; } = string.Empty;

    [JsonPropertyName("invalidation")]
    public IReadOnlyList<string> Invalidation { get; init; } = Array.Empty<string>();
}

public sealed record IrResource
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary><c>themeResource</c> or <c>staticResource</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;
}

public sealed record IrAction
{
    [JsonPropertyName("node")]
    public int Node { get; init; }

    [JsonPropertyName("event")]
    public string Event { get; init; } = string.Empty;

    [JsonPropertyName("handler")]
    public string Handler { get; init; } = string.Empty;
}

public sealed record IrSemantics
{
    [JsonPropertyName("node")]
    public int Node { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("focusable")]
    public bool Focusable { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(IrResourceValue), "resource")]
[JsonDerivedType(typeof(IrDoubleValue), "double")]
[JsonDerivedType(typeof(IrLengthValue), "length")]
[JsonDerivedType(typeof(IrGridLengthValue), "gridLength")]
[JsonDerivedType(typeof(IrThicknessValue), "thickness")]
[JsonDerivedType(typeof(IrCornerRadiusValue), "cornerRadius")]
[JsonDerivedType(typeof(IrColorValue), "color")]
[JsonDerivedType(typeof(IrStringValue), "string")]
[JsonDerivedType(typeof(IrBoolValue), "bool")]
[JsonDerivedType(typeof(IrEnumValue), "enum")]
[JsonDerivedType(typeof(IrIntValue), "int")]
public abstract record IrValue;

/// <summary>Placeholder for an absent value; never produced by the compiler.</summary>
public sealed record IrNullValue : IrValue;

public sealed record IrResourceValue : IrValue
{
    [JsonPropertyName("resource")]
    public int Resource { get; init; }
}

public sealed record IrDoubleValue : IrValue
{
    [JsonPropertyName("value")]
    public double Value { get; init; }
}

public sealed record IrLengthValue : IrValue
{
    [JsonPropertyName("value")]
    public IrLength Value { get; init; } = new IrAutoLength();
}

public sealed record IrGridLengthValue : IrValue
{
    [JsonPropertyName("value")]
    public IrGridLength Value { get; init; } = new IrAutoGridLength();
}

public sealed record IrThicknessValue : IrValue
{
    /// <summary>left, top, right, bottom</summary>
    [JsonPropertyName("value")]
    public double[] Value { get; init; } = new double[4];
}

public sealed record IrCornerRadiusValue : IrValue
{
    /// <summary>topLeft, topRight, bottomRight, bottomLeft</summary>
    [JsonPropertyName("value")]
    public double[] Value { get; init; } = new double[4];
}

public sealed record IrColorValue : IrValue
{
    [JsonPropertyName("argb")]
    public string Argb { get; init; } = string.Empty;
}

public sealed record IrStringValue : IrValue
{
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;
}

public sealed record IrBoolValue : IrValue
{
    [JsonPropertyName("value")]
    public bool Value { get; init; }
}

public sealed record IrEnumValue : IrValue
{
    [JsonPropertyName("enum")]
    public string EnumName { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;
}

public sealed record IrIntValue : IrValue
{
    [JsonPropertyName("value")]
    public long Value { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(IrAutoLength), "auto")]
[JsonDerivedType(typeof(IrDipLength), "dip")]
public abstract record IrLength;

public sealed record IrAutoLength : IrLength;

public sealed record IrDipLength : IrLength
{
    [JsonPropertyName("value")]
    public double Value { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(IrAutoGridLength), "auto")]
[JsonDerivedType(typeof(IrDipGridLength), "dip")]
[JsonDerivedType(typeof(IrStarGridLength), "star")]
public abstract record IrGridLength;

public sealed record IrAutoGridLength : IrGridLength;

public sealed record IrDipGridLength : IrGridLength
{
    [JsonPropertyName("value")]
    public double Value { get; init; }
}

public sealed record IrStarGridLength : IrGridLength
{
    [JsonPropertyName("value")]
    public double Value { get; init; }
}
