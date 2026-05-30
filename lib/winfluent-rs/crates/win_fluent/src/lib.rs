#![forbid(unsafe_code)]

pub mod a11y;
pub mod action;
pub mod command;
pub mod diff;
pub mod icon;
pub mod performance;
pub mod platform;
pub mod prelude;
pub mod runtime;
pub mod schema;
pub mod state;
pub mod subscription;
pub mod task;
pub mod theme;
pub mod view;
pub mod window;

pub use a11y::{resolve_accessibility_tree, A11yHint, A11yNode, A11yRole};
pub use action::Action;
pub use command::{command, CommandBuilder, CommandPlacement, CommandToken, KeyboardAccelerator};
pub use diff::{diff_views, ViewChange, ViewChangeKind, ViewPath};
pub use icon::IconToken;
pub use performance::{FrameCoalescer, TextStreamCoalescer};
pub use platform::{
    ClipboardFormat, Hotkey, HotkeyKey, HotkeyModifier, ShellVerb, TrayMenu, TrayMenuItem,
};
pub use runtime::{Application, RuntimeError};
pub use schema::{view_schema, SchemaNode, SchemaProperty, ViewSchema, VIEW_SCHEMA_VERSION};
pub use state::{ControlState, ValidationSeverity, ValidationState};
pub use subscription::{PlatformEvent, Subscription, SubscriptionKind, WindowEvent};
pub use task::Task;
pub use theme::{
    AccentPalette, Color, CornerRadius, Density, Spacing, ThemeMode, ThemeTokens, Typography,
};
pub use view::{
    button, column, combo_box, command_bar, dialog, lazy, navigation_view, page, primary_button,
    row, scroll_view, service_result_card, service_result_list, settings_row, text, text_editor,
    toggle_switch, Alignment, ButtonKind, ComboBoxItem, DialogKind, IntoChildren, IntoView,
    LayoutKind, LazyToken, Length, NavigationItem, ResultItem, ResultStatus, ScrollPolicy,
    SettingsRowKind, TextStyle, View, ViewToken,
};
pub use window::{
    WindowCommand, WindowFrame, WindowId, WindowLevel, WindowOptions, WindowPlacement,
    WindowResizeMode, WindowThemePreference,
};
