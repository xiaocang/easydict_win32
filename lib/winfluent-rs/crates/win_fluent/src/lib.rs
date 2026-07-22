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

#[cfg(feature = "parity-diagnostics")]
pub mod provenance;

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
pub use i18n::{t, I18n, I18nArg, I18nBundle, LocaleId, LocalizedText, Localizer, TextDirection};
pub use icon::{fluent_icon_glyph, IconToken, STANDARD_ICON_NAMES};
pub use loadable::Loadable;
pub use motion::{
    Easing, Transition, CONTROL_FASTER_ANIMATION_MS, CONTROL_FAST_ANIMATION_MS,
    CONTROL_NORMAL_ANIMATION_MS,
};
pub use performance::{FrameCoalescer, TextStreamCoalescer};
pub use platform::{
    ClipboardFormat, FileDialogFilter, FileDialogOptions, FolderDialogOptions, Hotkey, HotkeyKey,
    HotkeyModifier, NamedEventRegistration, PlatformCommand, ProtocolRegistration,
    ScreenCaptureRequest, ScreenCaptureResult, ScreenRect, ScreenWindow,
    ScreenWindowSnapshotRequest, ShellVerb, TrayMenu, TrayMenuColor, TrayMenuItem,
    TrayMenuPopupAnimation, TrayMenuPresenterKind, TrayMenuPresenterStyle,
};
pub use runtime::{Application, DesktopIntegrationPlan, RuntimeError, RuntimePlan};

#[cfg(feature = "parity-diagnostics")]
pub use provenance::{PropertyProvenance, SourceLocation, ViewProvenance};
#[cfg(feature = "parity-diagnostics")]
pub use schema::{
    diagnostic_diff_views, diagnostic_view_schema, DiagnosticChange, DiagnosticNode,
    DiagnosticViewDiff, DiagnosticViewSchema,
};
pub use schema::{view_schema, SchemaNode, SchemaProperty, ViewSchema, VIEW_SCHEMA_VERSION};
pub use screenshot::{ScreenshotError, WindowScreenshot};
pub use state::{
    CommonVisualState, ControlState, FocusVisualState, SelectionVisualState, ValidationSeverity,
    ValidationState, VisualStateSnapshot,
};
pub use style::{utility_scale, FluentStyle};
pub use subscription::{PlatformEvent, Subscription, SubscriptionKind, WindowEvent};
pub use task::Task;
pub use theme::{
    AccentPalette, BackdropKind, Color, ControlMetrics, CornerRadius, Density, Elevation, Spacing,
    Stroke, ThemeMode, ThemeTokenCoverageReport, ThemeTokens, Typography, VisualEffects,
};
pub use view::{
    adaptive_switch, auto_suggest_box, border, busy_overlay, button, capture_overlay, card,
    checkbox, column, combo_box, command_bar, control_template, custom_control, dialog, expander,
    flyout, flyout_button, grid, image, image_bgra_file, info_bar, lazy, list_view,
    navigation_view, number_box, overlay, page, pointer_region, primary_button, progress_bar,
    progress_ring, radio_group, result_card, result_list, row, scroll_view, service_result_card,
    service_result_list, settings_row, slider, spacer, split_button, status_badge, tab_view, text,
    text_editor, text_runs, title_bar, toggle_button, toggle_switch, tray_menu_presenter,
    tray_menu_presenter_with_animation_offset, tree_view, viewbox, web_view_html, web_view_url,
    wrap, AdaptiveSwitchBuilder, AdaptiveSwitchToken, Alignment, AutoSuggestBoxBuilder,
    AutoSuggestBoxToken, BorderBuilder, BorderToken, BusyOverlayBuilder, BusyOverlayToken,
    ButtonKind, CaptureOverlayBackground, CaptureOverlayBuilder, CaptureOverlayPhase,
    CaptureOverlayPoint, CaptureOverlayRect, CaptureOverlayToken, CardKind, CheckBoxBuilder,
    CheckBoxToken, CollapseTraceDirection, CollapseTraceSample, CollapseTransition, ComboBoxItem,
    CustomControlKind, CustomToken, DialogKind, Edges, ExpanderBuilder, ExpanderToken,
    FlyoutBuilder, FlyoutButtonBuilder, FlyoutButtonToken, FlyoutFocusBehavior, FlyoutLightDismiss,
    FlyoutMenuItem, FlyoutMenuItemKind, FlyoutPlacement, FlyoutToken, GridBuilder, GridChild,
    GridToken, ImageBuilder, ImageSourceKind, ImageStretch, ImageToken, InfoBarBuilder,
    InfoBarToken, IntoChildren, IntoView, LayoutDistribution, LayoutKind, LazyToken, Length,
    ListContractKind, ListViewBuilder, ListViewItem, ListViewToken, NavigationItem,
    NumberBoxBuilder, NumberBoxToken, Orientation, OverlayBuilder, OverlayLayer, OverlayToken,
    PaneDisplayMode, PointerPosition, PointerRegionAction, PointerRegionActionKind,
    PointerRegionBuilder, PointerRegionToken, PointerWheel, ProgressBarBuilder, ProgressBarToken,
    ProgressRingBuilder, ProgressRingToken, RadioGroupBuilder, RadioGroupToken, RadioOption,
    ResultCardBuilder, ResultCardToken, ResultItem, ResultListBuilder, ResultListToken,
    ResultStatus, RichTextBuilder, RichTextToken, ScrollPolicy, SettingsRowKind, SliderBuilder,
    SliderToken, SpacerBuilder, SpacerToken, SplitButtonBuilder, SplitButtonToken, StatusBadgeKind,
    TabItem, TabViewBuilder, TabViewToken, TextEditorChrome, TextEditorKey, TextEditorKeyBinding,
    TextEditorKeyModifiers, TextRun, TextRunKind, TextStyle, ToggleButtonBuilder,
    ToggleButtonToken, TooltipPlacement, TreeNode, TreeViewBuilder, TreeViewToken, View, ViewToken,
    ViewboxBuilder, ViewboxToken, WebViewBuilder, WebViewSource, WebViewToken, WrapBuilder,
    WrapToken,
};
#[allow(deprecated)]
pub use view::{
    ServiceResultCardBuilder, ServiceResultCardToken, ServiceResultListBuilder,
    ServiceResultListToken,
};
pub use window::{
    WindowCommand, WindowFrame, WindowId, WindowLevel, WindowOptions, WindowPlacement,
    WindowResizeMode, WindowScreenConstraint, WindowThemePreference,
};
