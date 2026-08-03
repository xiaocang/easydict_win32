using System.Runtime.CompilerServices;
using System.Threading;
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
    private readonly Invalidation[] _nodeDirty;
    private readonly bool[] _dynamicNodes;
    private readonly bool[] _dynamicSubtrees;
    private readonly Dictionary<string, IrNamedSlot> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Node, string Property), string> _stringOverrides = new();
    private readonly Dictionary<(int Node, string Property), double> _doubleOverrides = new();
    private readonly Dictionary<(int Node, string Property), Color> _colorOverrides = new();
    private readonly Dictionary<(int Node, string Property), Visibility> _visibilityOverrides = new();
    private readonly Dictionary<(int Node, string Property), Invalidation> _bindingInvalidation = new();

    private Func<Action, bool>? _uiDispatcher;
    private int _uiThreadId;
    private int _dispatcherCleared;

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
        _nodeDirty = new Invalidation[ir.Nodes.Count];
        Array.Fill(_nodeDirty, _dirty);

        foreach (IrProperty property in ir.Properties)
        {
            _properties[(property.Node, property.Name)] = property.Value;
        }

        _dynamicNodes = new bool[ir.Nodes.Count];
        _dynamicSubtrees = new bool[ir.Nodes.Count];
        foreach (IrNamedSlot slot in ir.NamedSlots)
        {
            _slots[slot.Name] = slot;
            _dynamicNodes[slot.Node] = true;
        }
        foreach (IrBinding binding in ir.Bindings)
        {
            _bindingInvalidation[(binding.TargetNode, binding.TargetProperty)] =
                IrLoader.ParseInvalidation(binding.Invalidation);
            _dynamicNodes[binding.TargetNode] = true;
        }

        RootNode = ir.Nodes.First(node => node.Parent is null).Id;
        foreach (IrNamedSlot slot in ir.NamedSlots)
        {
            MarkDynamicSubtree(slot.Node);
        }
        foreach (IrBinding binding in ir.Bindings)
        {
            MarkDynamicSubtree(binding.TargetNode);
        }
    }

    public IrDocument Ir => _ir;

    public int RootNode { get; }

    public int NodeCount => _ir.Nodes.Count;


    /// <summary>Returns invalidation flags propagated to one node and its layout ancestors.</summary>
    public Invalidation DirtyOf(int node) => _nodeDirty[node];

    /// <summary>Whether a node has a named runtime slot and can change without recompilation.</summary>
    public bool IsDynamicNode(int node) => _dynamicNodes[node];

    /// <summary>Whether this node or any visual descendant carries a runtime slot.</summary>
    public bool HasDynamicDescendant(int node) => _dynamicSubtrees[node];
    public Invalidation Dirty => _dirty;

    /// <summary>Raised after a runtime value changes and the view needs layout or painting.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Attaches the UI dispatcher that owns this view. Generated one-way bindings use it to marshal
    /// model notifications before touching the view's mutable state.
    /// </summary>
    public void ConfigureUiDispatcher(Func<Action, bool> tryEnqueue)
    {
        ArgumentNullException.ThrowIfNull(tryEnqueue);
        if (Volatile.Read(ref _dispatcherCleared) != 0)
        {
            throw new InvalidOperationException("The Direct XAML UI dispatcher has been cleared.");
        }
        if (Interlocked.CompareExchange(ref _uiDispatcher, tryEnqueue, null) is not null)
        {
            throw new InvalidOperationException("A UI dispatcher is already configured for this view.");
        }

        Volatile.Write(ref _uiThreadId, Environment.CurrentManagedThreadId);
    }

    /// <summary>Runs immediately on the owning UI thread, otherwise queues work to that thread.</summary>
    public void Dispatch(Action action)
    {
        if (!TryDispatch(action))
        {
            throw new InvalidOperationException("The Direct XAML UI dispatcher is unavailable.");
        }
    }

    /// <summary>Attempts to marshal work to the owning UI thread without throwing during teardown.</summary>
    public bool TryDispatch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Volatile.Read(ref _dispatcherCleared) != 0)
        {
            return false;
        }
        Func<Action, bool>? dispatcher = Volatile.Read(ref _uiDispatcher);
        if (dispatcher is null || Environment.CurrentManagedThreadId == Volatile.Read(ref _uiThreadId))
        {
            if (Volatile.Read(ref _dispatcherCleared) != 0)
            {
                return false;
            }

            action();
            return true;
        }

        if (Volatile.Read(ref _dispatcherCleared) != 0)
        {
            return false;
        }

        return dispatcher(action);
    }

    /// <summary>Detaches the UI dispatcher during surface teardown.</summary>
    public void ClearUiDispatcher()
    {
        if (Volatile.Read(ref _dispatcherCleared) != 0)
        {
            return;
        }

        Volatile.Write(ref _dispatcherCleared, 1);
        Interlocked.Exchange(ref _uiDispatcher, null);
        Volatile.Write(ref _uiThreadId, 0);
    }

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

    public void MarkClean()
    {
        Array.Fill(_nodeDirty, Invalidation.None);
        _dirty = Invalidation.None;
    }

    /// <summary>Clears measure and arrange work while retaining paint and semantic updates.</summary>
    public void MarkLayoutClean()
    {
        for (int index = 0; index < _nodeDirty.Length; index++)
        {
            _nodeDirty[index] &= ~(Invalidation.Measure | Invalidation.Arrange);
        }

        _dirty &= ~(Invalidation.Measure | Invalidation.Arrange);
    }

    /// <summary>Invalidates every node, used for a device or theme-wide change.</summary>
    public void Invalidate(Invalidation invalidation)
    {
        if (invalidation == Invalidation.None)
        {
            return;
        }

        for (int index = 0; index < _nodeDirty.Length; index++)
        {
            _nodeDirty[index] |= invalidation;
        }

        _dirty |= invalidation;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateNode(int node, Invalidation invalidation)
    {
        if (invalidation == Invalidation.None)
        {
            return;
        }

        _nodeDirty[node] |= invalidation;
        Invalidation ancestorInvalidation = invalidation & (Invalidation.Measure | Invalidation.Arrange);
        if ((ancestorInvalidation & Invalidation.Measure) != Invalidation.None)
        {
            ancestorInvalidation |= Invalidation.Arrange;
        }

        int? parent = _ir.Nodes[node].Parent;
        while (parent is int parentNode)
        {
            _nodeDirty[parentNode] |= ancestorInvalidation;
            parent = _ir.Nodes[parentNode].Parent;
        }

        _dirty |= invalidation;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Call when the active theme changes. Resource slots back thicknesses and corner radii as well
    /// as colours, so a theme switch can change layout, not only paint.
    /// </summary>
    public void OnThemeChanged(IResourceResolver resources)
    {
        _resources = resources;
        Invalidate(Invalidation.Measure | Invalidation.Arrange | Invalidation.Paint);
    }

    // ---- slot writes -------------------------------------------------------------------------

    public void SetText(string slotName, string? value) =>
        SetTypedSlotValue(slotName, PropertyNames.Text, value ?? string.Empty, _stringOverrides);

    public void SetContent(string slotName, string? value) =>
        SetTypedSlotValue(slotName, PropertyNames.Content, value ?? string.Empty, _stringOverrides);


    /// <summary>Writes a compiler-declared string binding target by node and property.</summary>
    public void SetBoundString(int node, string property, string? value)
    {
        if (!_bindingInvalidation.TryGetValue((node, property), out Invalidation invalidation))
        {
            throw new InvalidOperationException(
                $"'{_ir.ClassName}' has no string binding target '{node}.{property}'");
        }

        string normalized = value ?? string.Empty;
        var key = (node, property);
        if (_stringOverrides.TryGetValue(key, out string? existing)
            && string.Equals(existing, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _stringOverrides[key] = normalized;
        InvalidateNode(node, invalidation);
    }
    public void SetVisibility(string slotName, Visibility value) =>
        SetTypedSlotValue(slotName, PropertyNames.Visibility, value, _visibilityOverrides);

    public void SetOpacity(string slotName, double value) =>
        SetTypedSlotValue(slotName, PropertyNames.Opacity, value, _doubleOverrides);

    public void SetFontSize(string slotName, double value) =>
        SetTypedSlotValue(slotName, PropertyNames.FontSize, value, _doubleOverrides);

    public void SetForeground(string slotName, Color value) =>
        SetTypedSlotValue(slotName, PropertyNames.Foreground, value, _colorOverrides);

    public void SetBackground(string slotName, Color value) =>
        SetTypedSlotValue(slotName, PropertyNames.Background, value, _colorOverrides);

    /// <summary>Clears an override so the value falls back to what the IR declared.</summary>
    public void ResetSlotProperty(string slotName, string property)
    {
        if (!_slots.TryGetValue(slotName, out IrNamedSlot? slot))
        {
            return;
        }

        var key = (slot.Node, property);
        bool removed = property switch
        {
            PropertyNames.Text or PropertyNames.Content => _stringOverrides.Remove(key),
            PropertyNames.Visibility => _visibilityOverrides.Remove(key),
            PropertyNames.Opacity or PropertyNames.FontSize => _doubleOverrides.Remove(key),
            PropertyNames.Foreground or PropertyNames.Background => _colorOverrides.Remove(key),
            _ => false,
        };
        if (removed)
        {
            InvalidateNode(slot.Node, InvalidationFor(slot, property));
        }
    }

    private void SetTypedSlotValue<T>(
        string slotName,
        string property,
        T value,
        Dictionary<(int Node, string Property), T> overrides)
    {
        if (!_slots.TryGetValue(slotName, out IrNamedSlot? slot))
        {
            throw new ArgumentException(
                $"'{_ir.ClassName}' has no named slot '{slotName}'",
                nameof(slotName));
        }

        IrMutableProperty? mutable = FindMutable(slot, property);
        if (mutable is null)
        {
            throw new InvalidOperationException(
                $"slot '{slotName}' cannot write '{property}'; it allows: {string.Join(", ", slot.Mutable.Select(m => m.Property))}");
        }

        var key = (slot.Node, property);
        if (overrides.TryGetValue(key, out T? existing)
            && EqualityComparer<T>.Default.Equals(existing, value))
        {
            return;
        }

        overrides[key] = value;
        InvalidateNode(slot.Node, IrLoader.ParseInvalidation(mutable.Invalidation));
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

    private void MarkDynamicSubtree(int node)
    {
        int? currentNode = node;
        while (currentNode is int current && !_dynamicSubtrees[current])
        {
            _dynamicSubtrees[current] = true;
            currentNode = _ir.Nodes[current].Parent;
        }
    }

    // ---- resolved reads ----------------------------------------------------------------------


    private IrValue? Declared(int node, string property) =>
        _properties.TryGetValue((node, property), out IrValue? value) ? value : null;

    private string? ResourceKey(IrValue value) =>
        value is IrResourceValue resource ? _ir.Resources[resource.Resource].Key : null;

    public string GetString(int node, string property, string fallback = "")
    {
        return _stringOverrides.TryGetValue((node, property), out string? value)
            ? value
            : Declared(node, property) is IrStringValue declared ? declared.Value : fallback;
    }

    public double GetDouble(int node, string property, double fallback)
    {
        if (_doubleOverrides.TryGetValue((node, property), out double value))
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
        if (_colorOverrides.TryGetValue((node, property), out Color value))
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
        if (typeof(TEnum) == typeof(Visibility)
            && _visibilityOverrides.TryGetValue((node, property), out Visibility visibility))
        {
            return Unsafe.As<Visibility, TEnum>(ref visibility);
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

    /// <summary>Effective string content for a button node.</summary>
    public string GetContent(int node) => GetString(node, PropertyNames.Content);
}

/// <summary>Property names as the compiler spells them. Centralised so a rename is one edit.</summary>
public static class PropertyNames
{
    public const string Text = "Text";
    public const string Content = "Content";
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
