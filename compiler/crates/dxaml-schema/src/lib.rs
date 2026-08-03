//! The authoritative Direct XAML v0 subset tables.
//!
//! `schemas/direct-xaml-v0.subset.json` mirrors this file for external tooling; a test at the
//! bottom of this module asserts the two agree. Prose lives in `spec/direct-xaml-v0.md`.

pub const NS_PRESENTATION: &str = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
pub const NS_DIRECTIVES: &str = "http://schemas.microsoft.com/winfx/2006/xaml";
pub const NS_BLEND: &str = "http://schemas.microsoft.com/expression/blend/2008";
pub const NS_MARKUP_COMPAT: &str = "http://schemas.openxmlformats.org/markup-compatibility/2006";

/// What kind of content an element accepts.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ContentKind {
    /// No children and no text.
    None,
    /// Exactly one child element.
    Single,
    /// Any number of child elements.
    Many,
    /// Text only.
    Text,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ControlKind {
    UserControl,
    Border,
    Grid,
    StackPanel,
    Button,
    TextBlock,
    RowDefinition,
    ColumnDefinition,
}

impl ControlKind {
    pub fn from_name(name: &str) -> Option<Self> {
        Some(match name {
            "UserControl" => Self::UserControl,
            "Border" => Self::Border,
            "Grid" => Self::Grid,
            "StackPanel" => Self::StackPanel,
            "TextBlock" => Self::TextBlock,
            "Button" => Self::Button,
            "RowDefinition" => Self::RowDefinition,
            "ColumnDefinition" => Self::ColumnDefinition,
            _ => return None,
        })
    }

    pub fn name(self) -> &'static str {
        match self {
            Self::UserControl => "UserControl",
            Self::Border => "Border",
            Self::Grid => "Grid",
            Self::StackPanel => "StackPanel",
            Self::TextBlock => "TextBlock",
            Self::Button => "Button",
            Self::RowDefinition => "RowDefinition",
            Self::ColumnDefinition => "ColumnDefinition",
        }
    }

    /// The `kind` written into the IR, matching `dxir-v0.schema.json`.
    pub fn ir_name(self) -> &'static str {
        match self {
            Self::UserControl => "userControl",
            Self::Border => "border",
            Self::Grid => "grid",
            Self::StackPanel => "stackPanel",
            Self::TextBlock => "textBlock",
            Self::Button => "button",
            Self::RowDefinition => "rowDefinition",
            Self::ColumnDefinition => "columnDefinition",
        }
    }

    pub fn content(self) -> ContentKind {
        match self {
            Self::UserControl | Self::Border => ContentKind::Single,
            Self::Grid | Self::StackPanel => ContentKind::Many,
            Self::TextBlock => ContentKind::Text,
            Self::Button | Self::RowDefinition | Self::ColumnDefinition => ContentKind::None,
        }
    }

    /// Elements that may appear as an ordinary child in the visual tree.
    pub fn is_visual(self) -> bool {
        !matches!(self, Self::RowDefinition | Self::ColumnDefinition)
    }

    pub const ALL: &[ControlKind] = &[
        ControlKind::UserControl,
        ControlKind::Border,
        ControlKind::Grid,
        ControlKind::StackPanel,
        ControlKind::TextBlock,
        ControlKind::Button,
        ControlKind::RowDefinition,
        ControlKind::ColumnDefinition,
    ];
}

/// What a runtime write to a property must invalidate.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Invalidation(u8);

impl Invalidation {
    pub const NONE: Self = Self(0);
    pub const MEASURE: Self = Self(1);
    pub const ARRANGE: Self = Self(2);
    pub const PAINT: Self = Self(4);
    pub const SEMANTICS: Self = Self(8);

    pub const MEASURE_PAINT: Self = Self(1 | 4);
    pub const ARRANGE_PAINT: Self = Self(2 | 4);
    pub const MEASURE_ARRANGE_PAINT: Self = Self(1 | 2 | 4);
    pub const MEASURE_PAINT_SEMANTICS: Self = Self(1 | 4 | 8);

    pub fn contains(self, other: Self) -> bool {
        self.0 & other.0 == other.0
    }

    pub fn bits(self) -> u8 {
        self.0
    }

    /// IR names, always in this order so serialized output is deterministic.
    pub fn names(self) -> Vec<&'static str> {
        let mut names = Vec::new();
        if self.contains(Self::MEASURE) {
            names.push("measure");
        }
        if self.contains(Self::ARRANGE) {
            names.push("arrange");
        }
        if self.contains(Self::PAINT) {
            names.push("paint");
        }
        if self.contains(Self::SEMANTICS) {
            names.push("semantics");
        }
        names
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EnumKind {
    Visibility,
    Orientation,
    TextWrapping,
    TextTrimming,
    HorizontalAlignment,
    VerticalAlignment,
    FontWeight,
}

impl EnumKind {
    pub fn name(self) -> &'static str {
        match self {
            Self::Visibility => "Visibility",
            Self::Orientation => "Orientation",
            Self::TextWrapping => "TextWrapping",
            Self::TextTrimming => "TextTrimming",
            Self::HorizontalAlignment => "HorizontalAlignment",
            Self::VerticalAlignment => "VerticalAlignment",
            Self::FontWeight => "FontWeight",
        }
    }

    pub fn variants(self) -> &'static [&'static str] {
        match self {
            Self::Visibility => &["Visible", "Collapsed"],
            Self::Orientation => &["Horizontal", "Vertical"],
            Self::TextWrapping => &["NoWrap", "Wrap", "WrapWholeWords"],
            Self::TextTrimming => &["None", "CharacterEllipsis", "WordEllipsis", "Clip"],
            Self::HorizontalAlignment => &["Left", "Center", "Right", "Stretch"],
            Self::VerticalAlignment => &["Top", "Center", "Bottom", "Stretch"],
            Self::FontWeight => &[
                "Thin",
                "ExtraLight",
                "Light",
                "Normal",
                "Medium",
                "SemiBold",
                "Bold",
                "ExtraBold",
                "Black",
            ],
        }
    }

    /// Resolves a written variant to its canonical spelling. Matching is exact: XAML enum
    /// values are case-sensitive.
    pub fn resolve(self, value: &str) -> Option<&'static str> {
        self.variants().iter().copied().find(|v| *v == value)
    }

    pub const ALL: &[EnumKind] = &[
        EnumKind::Visibility,
        EnumKind::Orientation,
        EnumKind::TextWrapping,
        EnumKind::TextTrimming,
        EnumKind::HorizontalAlignment,
        EnumKind::VerticalAlignment,
        EnumKind::FontWeight,
    ];
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ValueType {
    Double,
    Length,
    GridLength,
    Thickness,
    CornerRadius,
    Brush,
    Str,
    Bool,
    Int,
    Enumeration(EnumKind),
}

#[derive(Debug, Clone, Copy)]
pub enum Applies {
    /// Every element that participates in layout — that is, everything except
    /// `RowDefinition` and `ColumnDefinition`, which carry their own sizing properties.
    Layout,
    Only(&'static [ControlKind]),
}

impl Applies {
    fn matches(&self, control: ControlKind) -> bool {
        match self {
            Applies::Layout => control.is_visual(),
            Applies::Only(kinds) => kinds.contains(&control),
        }
    }
}

#[derive(Debug, Clone, Copy)]
pub struct PropertyDef {
    pub name: &'static str,
    pub value_type: ValueType,
    pub applies: Applies,
    pub invalidation: Invalidation,
    /// Whether a named slot may write this property at runtime.
    pub mutable: bool,
}

const BORDER: &[ControlKind] = &[ControlKind::Border, ControlKind::Button];
const BUTTON: &[ControlKind] = &[ControlKind::Button];
const STACK: &[ControlKind] = &[ControlKind::StackPanel];
const TEXT: &[ControlKind] = &[ControlKind::TextBlock];
const TEXTUAL: &[ControlKind] = &[ControlKind::TextBlock, ControlKind::Button];
const ROW: &[ControlKind] = &[ControlKind::RowDefinition];
const COLUMN: &[ControlKind] = &[ControlKind::ColumnDefinition];
const PANELS: &[ControlKind] = &[
    ControlKind::Border,
    ControlKind::Button,
    ControlKind::Grid,
    ControlKind::StackPanel,
];
const PADDABLE: &[ControlKind] = &[
    ControlKind::Border,
    ControlKind::Button,
    ControlKind::Grid,
    ControlKind::StackPanel,
    ControlKind::TextBlock,
];

static PROPERTIES: &[PropertyDef] = &[
    // Sizing on the definition elements is a grid length, so these must precede nothing —
    // `Applies::Layout` already excludes them, keeping lookup order-independent.
    PropertyDef {
        name: "Height",
        value_type: ValueType::GridLength,
        applies: Applies::Only(ROW),
        invalidation: Invalidation::MEASURE,
        mutable: false,
    },
    PropertyDef {
        name: "Width",
        value_type: ValueType::GridLength,
        applies: Applies::Only(COLUMN),
        invalidation: Invalidation::MEASURE,
        mutable: false,
    },
    PropertyDef {
        name: "Width",
        value_type: ValueType::Length,
        applies: Applies::Layout,
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "Height",
        value_type: ValueType::Length,
        applies: Applies::Layout,
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "MinWidth",
        value_type: ValueType::Length,
        applies: Applies::Layout,
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "MinHeight",
        value_type: ValueType::Length,
        applies: Applies::Layout,
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "MaxWidth",
        value_type: ValueType::Length,
        applies: Applies::Layout,
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "MaxHeight",
        value_type: ValueType::Length,
        applies: Applies::Layout,
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "Margin",
        value_type: ValueType::Thickness,
        applies: Applies::Layout,
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "Opacity",
        value_type: ValueType::Double,
        applies: Applies::Layout,
        invalidation: Invalidation::PAINT,
        mutable: true,
    },
    PropertyDef {
        name: "Visibility",
        value_type: ValueType::Enumeration(EnumKind::Visibility),
        applies: Applies::Layout,
        invalidation: Invalidation::MEASURE_PAINT_SEMANTICS,
        mutable: true,
    },
    PropertyDef {
        name: "HorizontalAlignment",
        value_type: ValueType::Enumeration(EnumKind::HorizontalAlignment),
        applies: Applies::Layout,
        invalidation: Invalidation::ARRANGE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "VerticalAlignment",
        value_type: ValueType::Enumeration(EnumKind::VerticalAlignment),
        applies: Applies::Layout,
        invalidation: Invalidation::ARRANGE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "Padding",
        value_type: ValueType::Thickness,
        applies: Applies::Only(PADDABLE),
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "Background",
        value_type: ValueType::Brush,
        applies: Applies::Only(PANELS),
        invalidation: Invalidation::PAINT,
        mutable: true,
    },
    PropertyDef {
        name: "BorderBrush",
        value_type: ValueType::Brush,
        applies: Applies::Only(BORDER),
        invalidation: Invalidation::PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "BorderThickness",
        value_type: ValueType::Thickness,
        applies: Applies::Only(BORDER),
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "CornerRadius",
        value_type: ValueType::CornerRadius,
        applies: Applies::Only(BORDER),
        invalidation: Invalidation::PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "Spacing",
        value_type: ValueType::Double,
        applies: Applies::Only(STACK),
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "Orientation",
        value_type: ValueType::Enumeration(EnumKind::Orientation),
        applies: Applies::Only(STACK),
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "Content",
        value_type: ValueType::Str,
        applies: Applies::Only(BUTTON),
        invalidation: Invalidation::MEASURE_PAINT_SEMANTICS,
        mutable: true,
    },
    PropertyDef {
        name: "Text",
        value_type: ValueType::Str,
        applies: Applies::Only(TEXT),
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: true,
    },
    PropertyDef {
        name: "FontSize",
        value_type: ValueType::Double,
        applies: Applies::Only(TEXTUAL),
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: true,
    },
    PropertyDef {
        name: "FontWeight",
        value_type: ValueType::Enumeration(EnumKind::FontWeight),
        applies: Applies::Only(TEXTUAL),
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "Foreground",
        value_type: ValueType::Brush,
        applies: Applies::Only(TEXTUAL),
        invalidation: Invalidation::PAINT,
        mutable: true,
    },
    PropertyDef {
        name: "TextWrapping",
        value_type: ValueType::Enumeration(EnumKind::TextWrapping),
        applies: Applies::Only(TEXT),
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "TextTrimming",
        value_type: ValueType::Enumeration(EnumKind::TextTrimming),
        applies: Applies::Only(TEXT),
        invalidation: Invalidation::MEASURE_PAINT,
        mutable: false,
    },
    PropertyDef {
        name: "IsTextSelectionEnabled",
        value_type: ValueType::Bool,
        applies: Applies::Only(TEXT),
        invalidation: Invalidation::SEMANTICS,
        mutable: false,
    },
];

pub fn lookup_property(control: ControlKind, name: &str) -> Option<&'static PropertyDef> {
    PROPERTIES
        .iter()
        .find(|def| def.name == name && def.applies.matches(control))
}

/// Every property a named slot addressing `control` may write at runtime.
pub fn mutable_properties(control: ControlKind) -> Vec<&'static PropertyDef> {
    PROPERTIES
        .iter()
        .filter(|def| def.mutable && def.applies.matches(control))
        .collect()
}

/// True when `name` is a property of some element, even if not this one. Lets the compiler say
/// "not valid here" instead of "unknown".
pub fn property_exists(name: &str) -> bool {
    PROPERTIES.iter().any(|def| def.name == name)
}

#[derive(Debug, Clone, Copy)]
pub struct AttachedPropertyDef {
    pub owner: &'static str,
    pub name: &'static str,
    pub value_type: ValueType,
    /// The attached property is only meaningful on a direct child of this element.
    pub parent: ControlKind,
    pub invalidation: Invalidation,
}

static ATTACHED_PROPERTIES: &[AttachedPropertyDef] = &[
    AttachedPropertyDef {
        owner: "Grid",
        name: "Row",
        value_type: ValueType::Int,
        parent: ControlKind::Grid,
        invalidation: Invalidation::MEASURE_ARRANGE_PAINT,
    },
    AttachedPropertyDef {
        owner: "Grid",
        name: "Column",
        value_type: ValueType::Int,
        parent: ControlKind::Grid,
        invalidation: Invalidation::MEASURE_ARRANGE_PAINT,
    },
    AttachedPropertyDef {
        owner: "Grid",
        name: "RowSpan",
        value_type: ValueType::Int,
        parent: ControlKind::Grid,
        invalidation: Invalidation::MEASURE_ARRANGE_PAINT,
    },
    AttachedPropertyDef {
        owner: "Grid",
        name: "ColumnSpan",
        value_type: ValueType::Int,
        parent: ControlKind::Grid,
        invalidation: Invalidation::MEASURE_ARRANGE_PAINT,
    },
];

pub fn lookup_attached(owner: &str, name: &str) -> Option<&'static AttachedPropertyDef> {
    ATTACHED_PROPERTIES
        .iter()
        .find(|def| def.owner == owner && def.name == name)
}

/// The property elements v0 accepts, all of them on `Grid`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PropertyElementKind {
    RowDefinitions,
    ColumnDefinitions,
}

impl PropertyElementKind {
    /// The element type its children must have.
    pub fn child_kind(self) -> ControlKind {
        match self {
            Self::RowDefinitions => ControlKind::RowDefinition,
            Self::ColumnDefinitions => ControlKind::ColumnDefinition,
        }
    }
}

pub fn lookup_property_element(owner: ControlKind, name: &str) -> Option<PropertyElementKind> {
    match (owner, name) {
        (ControlKind::Grid, "RowDefinitions") => Some(PropertyElementKind::RowDefinitions),
        (ControlKind::Grid, "ColumnDefinitions") => Some(PropertyElementKind::ColumnDefinitions),
        _ => None,
    }
}

/// Routed events v0 compiles into actions, paired with their IR spelling.
static EVENTS: &[(&str, &str)] = &[
    ("PointerPressed", "pointerPressed"),
    ("Click", "click"),
    ("PointerEntered", "pointerEntered"),
    ("PointerExited", "pointerExited"),
    ("Tapped", "tapped"),
];

pub fn lookup_event(name: &str) -> Option<&'static str> {
    EVENTS
        .iter()
        .find(|(xaml, _)| *xaml == name)
        .map(|(_, ir)| *ir)
}

pub fn event_names() -> Vec<&'static str> {
    EVENTS.iter().map(|(xaml, _)| *xaml).collect()
}

/// XAML directives accepted by the v0 compiler.
pub fn is_supported_directive(name: &str) -> bool {
    matches!(name, "Class" | "Name" | "DataType")
}

/// The markup extensions v0 accepts.
pub fn is_supported_markup_extension(name: &str) -> bool {
    matches!(name, "ThemeResource" | "StaticResource")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn definition_sizing_is_a_grid_length() {
        let height = lookup_property(ControlKind::RowDefinition, "Height").expect("Height");
        assert!(matches!(height.value_type, ValueType::GridLength));

        let width = lookup_property(ControlKind::ColumnDefinition, "Width").expect("Width");
        assert!(matches!(width.value_type, ValueType::GridLength));
    }

    #[test]
    fn layout_sizing_stays_a_plain_length() {
        let width = lookup_property(ControlKind::Border, "Width").expect("Width");
        assert!(matches!(width.value_type, ValueType::Length));
    }

    #[test]
    fn definitions_do_not_inherit_layout_properties() {
        assert!(lookup_property(ControlKind::RowDefinition, "Margin").is_none());
        assert!(lookup_property(ControlKind::ColumnDefinition, "Opacity").is_none());
    }

    #[test]
    fn text_properties_are_confined_to_textblock() {
        assert!(lookup_property(ControlKind::TextBlock, "TextWrapping").is_some());
        assert!(lookup_property(ControlKind::Border, "TextWrapping").is_none());
        assert!(property_exists("TextWrapping"));
    }

    #[test]
    fn mutable_set_matches_the_spec() {
        let mut names: Vec<&str> = mutable_properties(ControlKind::TextBlock)
            .iter()
            .map(|def| def.name)
            .collect();
        names.sort_unstable();
        assert_eq!(
            names,
            vec!["FontSize", "Foreground", "Opacity", "Text", "Visibility"]
        );

        let mut border: Vec<&str> = mutable_properties(ControlKind::Border)
            .iter()
            .map(|def| def.name)
            .collect();
        border.sort_unstable();
        assert_eq!(border, vec!["Background", "Opacity", "Visibility"]);
    }

    #[test]
    fn invalidation_names_are_ordered() {
        assert_eq!(
            Invalidation::MEASURE_PAINT_SEMANTICS.names(),
            vec!["measure", "paint", "semantics"]
        );
        assert_eq!(Invalidation::PAINT.names(), vec!["paint"]);
        assert!(Invalidation::NONE.names().is_empty());
    }

    #[test]
    fn enum_resolution_is_case_sensitive() {
        assert_eq!(EnumKind::Visibility.resolve("Collapsed"), Some("Collapsed"));
        assert_eq!(EnumKind::Visibility.resolve("collapsed"), None);
    }

    /// `schemas/direct-xaml-v0.subset.json` is documentation for external tooling. If it drifts
    /// from these tables it is worse than having no file at all, so the two are pinned together.
    #[test]
    fn subset_json_mirrors_these_tables() {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../schemas/direct-xaml-v0.subset.json"
        );
        let raw = std::fs::read_to_string(path).expect("read subset json");
        let json: serde_json::Value = serde_json::from_str(&raw).expect("parse subset json");

        let controls = json["controls"].as_object().expect("controls object");
        for name in controls.keys() {
            assert!(
                ControlKind::from_name(name).is_some(),
                "subset json lists control '{name}' that the tables do not support"
            );
        }
        for kind in ControlKind::ALL {
            assert!(
                controls.contains_key(kind.name()),
                "control '{}' is supported but missing from the subset json",
                kind.name()
            );
        }

        let enums = json["enums"].as_object().expect("enums object");
        for kind in EnumKind::ALL {
            let listed: Vec<&str> = enums[kind.name()]
                .as_array()
                .unwrap_or_else(|| panic!("enum '{}' missing from subset json", kind.name()))
                .iter()
                .map(|value| value.as_str().expect("enum variant is a string"))
                .collect();
            assert_eq!(
                listed,
                kind.variants(),
                "enum '{}' differs between the tables and the subset json",
                kind.name()
            );
        }

        let events: Vec<&str> = json["events"]
            .as_array()
            .expect("events array")
            .iter()
            .map(|value| value.as_str().expect("event is a string"))
            .collect();
        assert_eq!(events, event_names());

        let extensions: Vec<&str> = json["markup_extensions"]
            .as_array()
            .expect("markup_extensions array")
            .iter()
            .map(|value| value.as_str().expect("extension is a string"))
            .collect();
        for extension in &extensions {
            assert!(is_supported_markup_extension(extension));
        }
    }
}
