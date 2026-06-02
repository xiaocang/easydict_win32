#![forbid(unsafe_code)]

pub mod a11y;
pub mod action;
pub mod command;
pub mod diff;
pub mod i18n;
pub mod icon;
pub mod loadable;
pub mod motion;
pub mod performance;
pub mod platform;
pub mod prelude;
pub mod runtime;
pub mod schema;
pub mod screenshot;
pub mod state;
pub mod style;
pub mod subscription;
pub mod task;
pub mod theme;
pub mod view;
pub mod window;

pub use a11y::{resolve_accessibility_tree, A11yHint, A11yNode, A11yRole};
pub use action::Action;
pub use command::{command, CommandBuilder, CommandPlacement, CommandToken, KeyboardAccelerator};
pub use diff::{diff_views, ViewChange, ViewChangeKind, ViewPath};
pub use i18n::{t, I18n, I18nArg, I18nBundle, LocaleId, LocalizedText, Localizer};
pub use icon::IconToken;
pub use loadable::Loadable;
pub use motion::{
    Easing, Transition, CONTROL_FASTER_ANIMATION_MS, CONTROL_FAST_ANIMATION_MS,
    CONTROL_NORMAL_ANIMATION_MS,
};
pub use performance::{FrameCoalescer, TextStreamCoalescer};
pub use platform::{
    ClipboardFormat, FileDialogFilter, FileDialogOptions, Hotkey, HotkeyKey, HotkeyModifier,
    NamedEventRegistration, PlatformCommand, ProtocolRegistration, ScreenCaptureRequest,
    ScreenCaptureResult, ScreenRect, ShellVerb, TrayMenu, TrayMenuItem,
};
pub use runtime::{Application, DesktopIntegrationPlan, RuntimeError, RuntimePlan};
pub use schema::{view_schema, SchemaNode, SchemaProperty, ViewSchema, VIEW_SCHEMA_VERSION};
pub use screenshot::{ScreenshotError, WindowScreenshot};
pub use state::{ControlState, ValidationSeverity, ValidationState};
pub use style::{utility_scale, FluentStyle};
pub use subscription::{PlatformEvent, Subscription, SubscriptionKind, WindowEvent};
pub use task::Task;
pub use theme::{
    AccentPalette, BackdropKind, Color, ControlMetrics, CornerRadius, Density, Elevation, Spacing,
    Stroke, ThemeMode, ThemeTokens, Typography, VisualEffects,
};
pub use view::{
    adaptive_switch, busy_overlay, button, card, column, combo_box, command_bar, dialog, expander,
    flyout_button, lazy, navigation_view, overlay, page, pointer_region, primary_button,
    progress_ring, result_card, result_list, row, scroll_view, service_result_card,
    service_result_list, settings_row, slider, spacer, status_badge, text, text_editor, title_bar,
    toggle_switch, wrap, AdaptiveSwitchBuilder, AdaptiveSwitchToken, Alignment, BusyOverlayBuilder,
    BusyOverlayToken, ButtonKind, CardKind, CollapseTraceDirection, CollapseTraceSample,
    CollapseTransition, ComboBoxItem, DialogKind, Edges, ExpanderBuilder, ExpanderToken,
    FlyoutButtonBuilder, FlyoutButtonToken, FlyoutMenuItem, FlyoutMenuItemKind, IntoChildren,
    IntoView, LayoutDistribution, LayoutKind, LazyToken, Length, NavigationItem, OverlayBuilder,
    OverlayLayer, OverlayToken, PointerPosition, PointerRegionAction, PointerRegionActionKind,
    PointerRegionBuilder, PointerRegionToken, PointerWheel, ProgressRingBuilder, ProgressRingToken,
    ResultCardBuilder, ResultCardToken, ResultItem, ResultListBuilder, ResultListToken,
    ResultStatus, ScrollPolicy, SettingsRowKind, SliderBuilder, SliderToken, SpacerBuilder,
    SpacerToken, TextEditorChrome, TextEditorKey, TextEditorKeyBinding, TextEditorKeyModifiers,
    TextStyle, View, ViewToken, WrapBuilder, WrapToken,
};
pub use window::{
    WindowCommand, WindowFrame, WindowId, WindowLevel, WindowOptions, WindowPlacement,
    WindowResizeMode, WindowScreenConstraint, WindowThemePreference,
};
