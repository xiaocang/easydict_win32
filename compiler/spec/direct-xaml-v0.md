# Direct XAML — language contract v0

Status: **frozen for v0**. Any change to this document is an IR-breaking change and must bump
`ir_version` in `schemas/dxir-v0.schema.json`.

Direct XAML is a strict subset of WinUI 3 XAML. A `.xaml` file that compiles under Direct XAML
must also compile under the stock WinUI XAML compiler and produce the same visual result. The
converse does not hold — most WinUI XAML is outside this subset.

The compiler's contract is **total**: every construct is either explicitly supported below, or it
is a hard compile error. There is no silently-ignored syntax, and no partial output. A file either
produces a complete, valid `.dxir.json` or it produces diagnostics and no artifact.

## Scope of v0

v0 exists to compile `Views/Controls/MinimalServiceResultItem.xaml` — the smallest real
translation-result card in the app. The supported surface below was derived from that file plus
`ServiceResultItem.xaml`, not invented ahead of demand.

## Document shape

The root element must be `UserControl` with an `x:Class` directive. Exactly one root; exactly one
child element under the root.

Required namespace declarations on the root:

| Prefix | URI | Meaning |
|---|---|---|
| *(default)* | `http://schemas.microsoft.com/winfx/2006/xaml/presentation` | control types |
| `x` | `http://schemas.microsoft.com/winfx/2006/xaml` | XAML directives |

Optional and ignored: `d` (`.../expression/blend/2008`) and `mc`
(`.../markup-compatibility/2006`). Any attribute in a namespace listed by `mc:Ignorable` is
dropped before analysis. Any *other* prefix is `DX2003` (unknown namespace).

## Supported elements

| Element | Content | Notes |
|---|---|---|
| `UserControl` | exactly 1 child | root only |
| `Border` | 0..1 child | |
| `Grid` | 0..n children | plus `Grid.RowDefinitions` / `Grid.ColumnDefinitions` |
| `StackPanel` | 0..n children | |
| `Button` | none | string `Content`; simple command binding |
| `TextBlock` | text only | no inlines (`Run`, `Bold`, `Hyperlink`) in v0 |
| `RowDefinition` | none | only inside `Grid.RowDefinitions` |
| `ColumnDefinition` | none | only inside `Grid.ColumnDefinitions` |

`Grid.RowDefinitions` and `Grid.ColumnDefinitions` are the only property elements v0 accepts. Any
other `Owner.Property` element is `DX3003`.

Any element not in this table is `DX3001`. `FontIcon`, `Image`, `ProgressRing`, `ScrollViewer`
and `HyperlinkButton` — used by `ServiceResultItem.xaml` — remain outside the vertical slice.

## Supported properties

Value types: `Dbl` double · `Len` double or `Auto` · `Grd` grid length (`Auto` \| *n* \| *n*`*`) ·
`Thk` thickness (1, 2 or 4 numbers) · `Cnr` corner radius (1 or 4 numbers) · `Brs` brush ·
`Str` string · `Bool` boolean · `Enum` enumeration.

Numbers may be separated by commas or whitespace. Any property may instead be written as a
resource reference — see *Markup extensions* below — including `Thk` and `Cnr`, which the real
cards do use that way.

| Property | Type | Applies to | Invalidation |
|---|---|---|---|
| `Width`, `Height`, `MinWidth`, `MinHeight`, `MaxWidth`, `MaxHeight` | `Len` | all | Measure \| Paint |
| `Margin` | `Thk` | all | Measure \| Paint |
| `Padding` | `Thk` | `Border`, `Button`, `Grid`, `StackPanel`, `TextBlock` | Measure \| Paint |
| `Background` | `Brs` | `Border`, `Button`, `Grid`, `StackPanel` | Paint |
| `BorderBrush` | `Brs` | `Border`, `Button` | Paint |
| `BorderThickness` | `Thk` | `Border`, `Button` | Measure \| Paint |
| `CornerRadius` | `Cnr` | `Border`, `Button` | Paint |
| `Spacing` | `Dbl` | `StackPanel` | Measure \| Paint |
| `Orientation` | `Enum` | `StackPanel` | Measure \| Paint |
| `Content` | `Str` | `Button` | Measure \| Paint \| Semantics |
| `Command` | simple `{x:Bind CommandName}` | `Button` | Semantics |
| `Text` | `Str` | `TextBlock` | Measure \| Paint |
| `FontSize` | `Dbl` | `TextBlock`, `Button` | Measure \| Paint |
| `FontWeight` | `Enum` | `TextBlock`, `Button` | Measure \| Paint |
| `Foreground` | `Brs` | `TextBlock`, `Button` | Paint |
| `TextWrapping` | `Enum` | `TextBlock` | Measure \| Paint |
| `TextTrimming` | `Enum` | `TextBlock` | Measure \| Paint |
| `IsTextSelectionEnabled` | `Bool` | `TextBlock` | Semantics |
| `HorizontalAlignment`, `VerticalAlignment` | `Enum` | all | Arrange \| Paint |
| `Visibility` | `Enum` | all | Measure \| Paint \| Semantics |
| `Opacity` | `Dbl` | all | Paint |
| `Height`, `Width` | `Len` | `RowDefinition` / `ColumnDefinition` — see below | Measure |

`RowDefinition.Height` and `ColumnDefinition.Width` take `Grd`, not `Len`.

Attached properties — the complete v0 set:

| Attached property | Type | Valid on |
|---|---|---|
| `Grid.Row`, `Grid.Column`, `Grid.RowSpan`, `Grid.ColumnSpan` | `Int` | direct children of a `Grid` |

Any other attached property is `DX3002`. Notably `ToolTipService.ToolTip` and
`AutomationProperties.*` are out of v0; automation identity is supplied at runtime by
`ServiceResultViewHost.ApplyAutomationProperties`, so nothing is lost by excluding them.

### Enumerations

| Enum | Accepted values |
|---|---|
| `Visibility` | `Visible`, `Collapsed` |
| `Orientation` | `Horizontal`, `Vertical` |
| `TextWrapping` | `NoWrap`, `Wrap`, `WrapWholeWords` |
| `TextTrimming` | `None`, `CharacterEllipsis`, `WordEllipsis`, `Clip` |
| `HorizontalAlignment` | `Left`, `Center`, `Right`, `Stretch` |
| `VerticalAlignment` | `Top`, `Center`, `Bottom`, `Stretch` |
| `FontWeight` | `Thin`, `ExtraLight`, `Light`, `Normal`, `Medium`, `SemiBold`, `Bold`, `ExtraBold`, `Black` |

An unrecognised variant is `DX2005`, and the diagnostic lists the accepted values.

## Markup extensions

Resource properties accept exactly two extensions:

- `{ThemeResource Key}` — compiled to a **runtime theme slot**, never folded to a literal colour.
  Light / Dark / HighContrast must remain switchable at runtime, and the C# side resolves the key
  through the existing `Services/ThemeResourceService.cs`.
- `{StaticResource Key}` — same slot mechanism; the distinction is preserved in the IR so the
  runtime may cache static lookups.

`Button.Command` accepts one constrained form: `{x:Bind CommandName}`. For `TextBlock.Text` and
`Button.Content`, v0 also accepts `{x:Bind PropertyName}` or `{x:Bind Path=PropertyName,
Mode=OneTime|OneWay}`. These string bindings require a valid root `x:DataType`; the compiler
resolves a `using:Namespace` prefix and emits the binding context type into the IR. `OneTime` is
applied when the generated binding context is set; `OneWay` additionally subscribes to
`INotifyPropertyChanged` and marshals updates to the owning UI dispatcher. All paths are single
identifiers and all bound targets carry the property's normal invalidation class. `{Binding}`,
`{RelativeSource}`, `{TemplateBinding}`, `{x:Null}` and other extensions are `DX3004`.

Resource keys are **not** resolved at compile time. The compiler records the key; a missing key is
a runtime concern, because the app merges theme dictionaries dynamically
(`MinimalThemeService.ApplyResources`).
## Directives

| Directive | Effect |
|---|---|
| `x:Class` | recorded in the IR header; the generated accessor type is derived from it |
| `x:Name` | creates a **named slot** — see below |
| `x:DataType` | declares the root binding context type for typed `{x:Bind}` |

`x:DataType` is valid only on the root `UserControl`. A value may be a qualified C# type name or
an alias-qualified name whose alias maps to a `using:Namespace` declaration. The resolved name is
stored as `binding_context_type`; it is required whenever the IR contains typed bindings.

`x:Key`, `x:Uid`, `x:Load`, `x:DeferLoadStrategy`, `x:Phase` and `x:FieldModifier` are `DX3005`.

## Named slots and the v0 binding model

The shipping view primarily uses `x:Name` plus imperative code-behind. v0 compiles each `x:Name`
into a *named slot* and generates typed C# setters. Typed string bindings are compiled separately:
`TextBlock.Text` and `Button.Content` become binding targets, while
`Button.Command="{x:Bind CopyCommand}"` lowers directly to a `click` action.

A named slot records the node it addresses and the set of properties that may be mutated at
runtime, each with its invalidation class. A slot's mutable set is the intersection of the
properties supported on that element type and the properties v0 declares runtime-mutable:

`Text`, `Content`, `Visibility`, `Foreground`, `Background`, `FontSize`, `Opacity`

That set is exactly what `MinimalServiceResultItem.UpdateUI()` writes, which is the point: the
existing method ports onto the direct backend without being rewritten.

Slot names must be unique within a document (`DX2006` otherwise) and must be valid C# identifiers
(`DX2007`), because they become members of the generated accessor type.

## Events

An attribute whose name matches a known routed event compiles to an **action**: the event name
plus the handler method name, recorded for later codegen. v0 recognises `PointerPressed`, `Click`,
`PointerEntered`, `PointerExited` and `Tapped`. A simple `Button.Command` x:Bind also becomes a
`click` action whose handler is the command-property name.

The compiler cannot see the C# member and therefore cannot verify it; generated/accessing C# code
provides the compile-time contract, while the Direct host maps the action to the existing command.

## Text content

Literal text is permitted only inside `TextBlock`, and only when the element has no `Text`
attribute (`DX2008` if both). Whitespace-only text is discarded. XML entities are unescaped.

## Diagnostics

Format is MSBuild-parseable so that a later `<Exec>` integration surfaces errors in the IDE with
no extra work:

```
<path>(<line>,<col>): error DX3001: control 'ProgressRing' is not in the Direct XAML v0 subset
```

Line and column are 1-based. Columns are byte offsets within the line, which matches ASCII XAML
exactly and may drift on lines containing non-ASCII text — acceptable for v0.

| Range | Class |
|---|---|
| `DX1xxx` | XML syntax / well-formedness |
| `DX2xxx` | name, type and value resolution |
| `DX3xxx` | construct outside the v0 subset |
| `DX4xxx` | lowering and IR validation |

Full list:

| Code | Meaning |
|---|---|
| `DX1001` | XML parse error |
| `DX1002` | no root element |
| `DX1003` | more than one root element |
| `DX2001` | root element must be `UserControl` |
| `DX2002` | missing required `x:Class` on root |
| `DX2003` | unknown XML namespace prefix |
| `DX2004` | property is not valid on this element |
| `DX2005` | malformed or out-of-range property value |
| `DX2006` | duplicate `x:Name` |
| `DX2007` | `x:Name` is not a valid identifier |
| `DX2008` | `TextBlock` has both a `Text` attribute and text content |
| `DX2009` | wrong child count for this element |
| `DX2010` | element is not valid in this position |
| `DX3001` | control not in the v0 subset |
| `DX3002` | attached property not in the v0 subset |
| `DX3003` | property element not in the v0 subset |
| `DX3004` | markup extension not in the v0 subset |
| `DX3005` | XAML directive not in the v0 subset |
| `DX4001` | IR validation failure (internal) |
