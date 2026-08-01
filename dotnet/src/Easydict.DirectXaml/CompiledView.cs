using Easydict.DirectXaml.Ir;
using Easydict.DirectXaml.Theming;

namespace Easydict.DirectXaml;

/// <summary>
/// A loaded IR document plus its mutable runtime state.
///
/// This is the object that replaces a per-card <c>FrameworkElement</c> subtree: structure is fixed
/// at compile time, and only the values behind named slots vary. Writes go through the slot API so
/// the declared invalidation is applied — that is what lets a colour change repaint without
/// re-running layout.
/// </summary>
public sealed class CompiledView
{
    private readonly IrDocument _ir;
    private readonly NodeKind[] _kinds;
    private readonly Dictionary<(int Node, string Property), IrValue> _properties = new();
    private readonly Dictionary<string, IrNamedSlot> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Node, string Property), object> _overrides = new();

    private IResourceResolver _resources;
    private Invalidation _dirty =
        Invalidation.Measure | Invalidation.Arrange | Invalidation.Paint | Invalidation.Semantics;

    public CompiledView(IrDocument ir, IResourceResolver resources)
    {
        _ir = ir;
        _resources = resources;

        _kinds = new NodeKind[ir.Nodes.Count];
        for (int index = 0; index < ir.Nodes.Count; index++)
        {
            _kinds[index] = IrLoader.ParseNodeKind(ir.Nodes[index].Kind);
        }

        foreach (IrProperty property in ir.Properties)
        {
            _properties[(property.Node, property.Name)] = property.Value;
        }

        foreach (IrNamedSlot slot in ir.NamedSlots)
        {
            _slots[slot.Name] = slot;
        }

        RootNode = ir.Nodes.First(node => node.Parent is null).Id;
    }

    public IrDocument Ir => _ir;

    public int RootNode { get; }

    public int NodeCount => _ir.Nodes.Count;

    public Invalidation Dirty => _dirty;

    public string ClassName => _ir.ClassName;

    public NodeKind KindOf(int node) => _kinds[node];

    public IReadOnlyList<int> ChildrenOf(int node) => _ir.Nodes[node].Children;

    /// <summary>
    /// The containing node, or <c>null</c> at the root. Hit testing walks this upwards so a click
    /// on a leaf still reaches a handler declared on an ancestor, as routed events would.
    /// </summary>
    public int? ParentOf(int node) => _ir.Nodes[node].Parent;

    /// <summary>Literal text baked into the IR, before any slot override.</summary>
    public string? LiteralTextOf(int node) => _ir.Nodes[node].Text;

    public IReadOnlyList<string> SlotNames => _ir.NamedSlots.Select(slot => slot.Name).ToArray();

    public bool TryGetSlotNode(string slotName, out int node)
    {
        if (_slots.TryGetValue(slotName, out IrNamedSlot? slot))
        {
            node = slot.Node;
            return true;
        }

        node = -1;
        return false;
    }

    /// <summary>The handler name bound to an event on a node, if any.</summary>
    public string? FindActionHandler(int node, string @event)
    {
        foreach (IrAction action in _ir.Actions)
        {
            if (action.Node == node && action.Event == @event)
            {
                return action.Handler;
            }
        }

        return null;
    }

    public void MarkClean() => _dirty = Invalidation.None;

    public void Invalidate(Invalidation invalidation) => _dirty |= invalidation;

    /// <summary>
    /// Call when the active theme changes. Resource slots back thicknesses and corner radii as well
    /// as colours, so a theme switch can change layout, not only paint.
    /// </summary>
    public void OnThemeChanged(IResourceResolver resources)
    {
        _resources = resources;
        _dirty |= Invalidation.Measure | Invalidation.Arrange | Invalidation.Paint;
    }

    // ---- slot writes -------------------------------------------------------------------------

    public void SetText(string slotName, string? value) =>
        SetSlotValue(slotName, PropertyNames.Text, value ?? string.Empty);

    public void SetVisibility(string slotName, Visibility value) =>
        SetSlotValue(slotName, PropertyNames.Visibility, value);

    public void SetOpacity(string slotName, double value) =>
        SetSlotValue(slotName, PropertyNames.Opacity, value);

    public void SetFontSize(string slotName, double value) =>
        SetSlotValue(slotName, PropertyNames.FontSize, value);

    public void SetForeground(string slotName, Color value) =>
        SetSlotValue(slotName, PropertyNames.Foreground, value);

    public void SetBackground(string slotName, Color value) =>
        SetSlotValue(slotName, PropertyNames.Background, value);

    /// <summary>Clears an override so the value falls back to what the IR declared.</summary>
    public void ResetSlotProperty(string slotName, string property)
    {
        if (_slots.TryGetValue(slotName, out IrNamedSlot? slot)
            && _overrides.Remove((slot.Node, property)))
        {
            _dirty |= InvalidationFor(slot, property);
        }
    }

    private void SetSlotValue(string slotName, string property, object value)
    {
        if (!_slots.TryGetValue(slotName, out IrNamedSlot? slot))
        {
            throw new ArgumentException(
                $"'{_ir.ClassName}' has no named slot '{slotName}'", nameof(slotName));
        }

        IrMutableProperty? mutable = FindMutable(slot, property);
        if (mutable is null)
        {
            // A typo would otherwise be a silent no-op that shows up as a rendering bug.
            throw new InvalidOperationException(
                $"slot '{slotName}' cannot write '{property}'; it allows: {string.Join(", ", slot.Mutable.Select(m => m.Property))}");
        }

        var key = (slot.Node, property);
        if (_overrides.TryGetValue(key, out object? existing) && Equals(existing, value))
        {
            // UpdateUI rewrites the same values on every notification; do not dirty for a no-op.
            return;
        }

        _overrides[key] = value;
        _dirty |= IrLoader.ParseInvalidation(mutable.Invalidation);
    }

    private static IrMutableProperty? FindMutable(IrNamedSlot slot, string property)
    {
        foreach (IrMutableProperty mutable in slot.Mutable)
        {
            if (mutable.Property == property)
            {
                return mutable;
            }
        }

        return null;
    }

    private static Invalidation InvalidationFor(IrNamedSlot slot, string property)
    {
        IrMutableProperty? mutable = FindMutable(slot, property);
        return mutable is null ? Invalidation.None : IrLoader.ParseInvalidation(mutable.Invalidation);
    }

    // ---- resolved reads ----------------------------------------------------------------------

    private bool TryOverride<T>(int node, string property, out T value)
    {
        if (_overrides.TryGetValue((node, property), out object? stored) && stored is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    private IrValue? Declared(int node, string property) =>
        _properties.TryGetValue((node, property), out IrValue? value) ? value : null;

    private string? ResourceKey(IrValue value) =>
        value is IrResourceValue resource ? _ir.Resources[resource.Resource].Key : null;

    public string GetString(int node, string property, string fallback = "")
    {
        if (TryOverride(node, property, out string value))
        {
            return value;
        }

        return Declared(node, property) is IrStringValue declared ? declared.Value : fallback;
    }

    public double GetDouble(int node, string property, double fallback)
    {
        if (TryOverride(node, property, out double value))
        {
            return value;
        }

        IrValue? declared = Declared(node, property);
        if (declared is IrDoubleValue number)
        {
            return number.Value;
        }

        if (ResourceKey(declared!) is { } key && _resources.TryGetDouble(key, out double resolved))
        {
            return resolved;
        }

        return fallback;
    }

    public int GetInt(int node, string property, int fallback = 0) =>
        Declared(node, property) is IrIntValue value ? (int)value.Value : fallback;

    public bool GetBool(int node, string property, bool fallback = false) =>
        Declared(node, property) is IrBoolValue value ? value.Value : fallback;

    public Color GetColor(int node, string property, Color fallback)
    {
        if (TryOverride(node, property, out Color value))
        {
            return value;
        }

        IrValue? declared = Declared(node, property);
        if (declared is IrColorValue literal && Color.TryParseArgbHex(literal.Argb, out Color parsed))
        {
            return parsed;
        }

        if (ResourceKey(declared!) is { } key && _resources.TryGetColor(key, out Color resolved))
        {
            return resolved;
        }

        return fallback;
    }

    public Thickness GetThickness(int node, string property, Thickness fallback = default)
    {
        IrValue? declared = Declared(node, property);
        if (declared is IrThicknessValue literal && literal.Value.Length == 4)
        {
            double[] v = literal.Value;
            return new Thickness(v[0], v[1], v[2], v[3]);
        }

        if (ResourceKey(declared!) is { } key && _resources.TryGetThickness(key, out Thickness resolved))
        {
            return resolved;
        }

        return fallback;
    }

    public CornerRadius GetCornerRadius(int node, string property, CornerRadius fallback = default)
    {
        IrValue? declared = Declared(node, property);
        if (declared is IrCornerRadiusValue literal && literal.Value.Length == 4)
        {
            double[] v = literal.Value;
            return new CornerRadius(v[0], v[1], v[2], v[3]);
        }

        if (ResourceKey(declared!) is { } key && _resources.TryGetCornerRadius(key, out CornerRadius resolved))
        {
            return resolved;
        }

        return fallback;
    }

    public TEnum GetEnum<TEnum>(int node, string property, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (TryOverride(node, property, out TEnum value))
        {
            return value;
        }

        if (Declared(node, property) is IrEnumValue declared
            && Enum.TryParse(declared.Value, out TEnum parsed))
        {
            return parsed;
        }

        return fallback;
    }

    public LengthValue GetLength(int node, string property)
    {
        if (Declared(node, property) is IrLengthValue declared)
        {
            return declared.Value switch
            {
                IrDipLength dip => LengthValue.Fixed(dip.Value),
                _ => LengthValue.Auto,
            };
        }

        return LengthValue.Auto;
    }

    public GridLengthValue GetGridLength(int node, string property)
    {
        if (Declared(node, property) is IrGridLengthValue declared)
        {
            return declared.Value switch
            {
                IrDipGridLength dip => GridLengthValue.Dip(dip.Value),
                IrStarGridLength star => GridLengthValue.Star(star.Value),
                _ => GridLengthValue.Auto,
            };
        }

        return GridLengthValue.Auto;
    }

    /// <summary>Effective text for a node: slot override first, then literal IR content.</summary>
    public string GetText(int node) => GetString(node, PropertyNames.Text, LiteralTextOf(node) ?? string.Empty);
}

/// <summary>Property names as the compiler spells them. Centralised so a rename is one edit.</summary>
public static class PropertyNames
{
    public const string Text = "Text";
    public const string Visibility = "Visibility";
    public const string Opacity = "Opacity";
    public const string FontSize = "FontSize";
    public const string FontWeight = "FontWeight";
    public const string Foreground = "Foreground";
    public const string Background = "Background";
    public const string BorderBrush = "BorderBrush";
    public const string BorderThickness = "BorderThickness";
    public const string CornerRadius = "CornerRadius";
    public const string Padding = "Padding";
    public const string Margin = "Margin";
    public const string Spacing = "Spacing";
    public const string Orientation = "Orientation";
    public const string TextWrapping = "TextWrapping";
    public const string TextTrimming = "TextTrimming";
    public const string HorizontalAlignment = "HorizontalAlignment";
    public const string VerticalAlignment = "VerticalAlignment";
    public const string Width = "Width";
    public const string Height = "Height";
    public const string MinWidth = "MinWidth";
    public const string MinHeight = "MinHeight";
    public const string MaxWidth = "MaxWidth";
    public const string MaxHeight = "MaxHeight";
    public const string GridRow = "Grid.Row";
    public const string GridColumn = "Grid.Column";
    public const string GridRowSpan = "Grid.RowSpan";
    public const string GridColumnSpan = "Grid.ColumnSpan";
}
