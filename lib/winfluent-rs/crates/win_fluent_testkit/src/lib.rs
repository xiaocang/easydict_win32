#![forbid(unsafe_code)]

use std::fmt::Write;

use win_fluent::a11y::{resolve_accessibility_tree, A11yNode, A11yRole};
use win_fluent::action::ActionKind;
use win_fluent::schema::{view_schema, ViewSchema};
use win_fluent::theme::{ThemeMode, ThemeTokens};
use win_fluent::view::{LayoutKind, View, ViewToken};

pub fn view_schema_tree<Message>(view: &View<Message>) -> ViewSchema {
    view_schema(view)
}

pub fn view_snapshot<Message>(view: &View<Message>) -> String {
    view_schema(view).snapshot()
}

pub fn layout_snapshot<Message>(view: &View<Message>) -> String {
    let mut output = String::new();
    write_layout(&mut output, view, 0);
    output
}

pub fn accessibility_tree<Message>(view: &View<Message>) -> A11yNode {
    resolve_accessibility_tree(view)
}

pub fn accessibility_snapshot<Message>(view: &View<Message>) -> String {
    let tree = accessibility_tree(view);
    let mut output = String::new();
    write_a11y(&mut output, &tree, 0);
    output
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum A11ySeverity {
    Warning,
    Error,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct A11yIssue {
    pub path: String,
    pub role: A11yRole,
    pub severity: A11ySeverity,
    pub message: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct A11yAudit {
    pub issues: Vec<A11yIssue>,
}

impl A11yAudit {
    pub fn passed(&self) -> bool {
        self.error_count() == 0
    }

    pub fn error_count(&self) -> usize {
        self.issues
            .iter()
            .filter(|issue| issue.severity == A11ySeverity::Error)
            .count()
    }

    pub fn warning_count(&self) -> usize {
        self.issues
            .iter()
            .filter(|issue| issue.severity == A11ySeverity::Warning)
            .count()
    }
}

pub fn audit_accessibility_tree(root: &A11yNode) -> A11yAudit {
    let mut issues = Vec::new();
    audit_a11y_node(root, "root", &mut issues);
    A11yAudit { issues }
}

pub fn accessibility_audit<Message>(view: &View<Message>) -> A11yAudit {
    audit_accessibility_tree(&accessibility_tree(view))
}

pub fn accessibility_audit_snapshot<Message>(view: &View<Message>) -> String {
    let audit = accessibility_audit(view);
    let mut output = String::new();
    write_a11y_audit(&mut output, &audit);
    output
}

pub fn theme_snapshot(theme: &ThemeTokens) -> String {
    format!(
        "ResolvedTheme mode={:?} background=#{:02x}{:02x}{:02x} surface=#{:02x}{:02x}{:02x} surface_alt=#{:02x}{:02x}{:02x} input_surface=#{:02x}{:02x}{:02x} result_surface=#{:02x}{:02x}{:02x} result_header=#{:02x}{:02x}{:02x} result_header_hover=#{:02x}{:02x}{:02x} button_hover=#{:02x}{:02x}{:02x} button_pressed=#{:02x}{:02x}{:02x} floating_action_surface=#{:02x}{:02x}{:02x} floating_action_border=#{:02x}{:02x}{:02x} accent_hover=#{:02x}{:02x}{:02x} accent_pressed=#{:02x}{:02x}{:02x} accent_foreground=#{:02x}{:02x}{:02x} status_connected=#{:02x}{:02x}{:02x} status_disconnected=#{:02x}{:02x}{:02x} status_error=#{:02x}{:02x}{:02x} text_primary=#{:02x}{:02x}{:02x} text_secondary=#{:02x}{:02x}{:02x} border=#{:02x}{:02x}{:02x} focus=#{:02x}{:02x}{:02x} accent=#{:02x}{:02x}{:02x} radius_control={} spacing_md={} density={:?} backdrop={:?} stroke_control={} stroke_focus={} elevation_rest={} elevation_raised={} elevation_overlay={} elevation_flyout={} disabled_opacity={} dimmed_opacity={} floating_action_rest_opacity={} floating_action_hover_opacity={} floating_action_pressed_opacity={} control_height={} control_compact_height={} control_icon_button={} control_compact_icon_button={} result_action_button={} primary_round_button={} floating_action_button={} control_min_touch_target={} title_bar_height={} caption_button_width={} card_padding={} result_header_height={}",
        theme.mode,
        theme.background.r,
        theme.background.g,
        theme.background.b,
        theme.surface.r,
        theme.surface.g,
        theme.surface.b,
        theme.surface_alt.r,
        theme.surface_alt.g,
        theme.surface_alt.b,
        theme.input_surface.r,
        theme.input_surface.g,
        theme.input_surface.b,
        theme.result_surface.r,
        theme.result_surface.g,
        theme.result_surface.b,
        theme.result_header.r,
        theme.result_header.g,
        theme.result_header.b,
        theme.result_header_hover.r,
        theme.result_header_hover.g,
        theme.result_header_hover.b,
        theme.button_hover.r,
        theme.button_hover.g,
        theme.button_hover.b,
        theme.button_pressed.r,
        theme.button_pressed.g,
        theme.button_pressed.b,
        theme.floating_action_surface.r,
        theme.floating_action_surface.g,
        theme.floating_action_surface.b,
        theme.floating_action_border.r,
        theme.floating_action_border.g,
        theme.floating_action_border.b,
        theme.accent_hover.r,
        theme.accent_hover.g,
        theme.accent_hover.b,
        theme.accent_pressed.r,
        theme.accent_pressed.g,
        theme.accent_pressed.b,
        theme.accent_foreground.r,
        theme.accent_foreground.g,
        theme.accent_foreground.b,
        theme.status_connected.r,
        theme.status_connected.g,
        theme.status_connected.b,
        theme.status_disconnected.r,
        theme.status_disconnected.g,
        theme.status_disconnected.b,
        theme.status_error.r,
        theme.status_error.g,
        theme.status_error.b,
        theme.text_primary.r,
        theme.text_primary.g,
        theme.text_primary.b,
        theme.text_secondary.r,
        theme.text_secondary.g,
        theme.text_secondary.b,
        theme.border.r,
        theme.border.g,
        theme.border.b,
        theme.focus.r,
        theme.focus.g,
        theme.focus.b,
        theme.accent.base.r,
        theme.accent.base.g,
        theme.accent.base.b,
        theme.radius.control,
        theme.spacing.md,
        theme.density,
        theme.backdrop,
        theme.stroke.control,
        theme.stroke.focus,
        theme.elevation.rest,
        theme.elevation.raised,
        theme.elevation.overlay,
        theme.elevation.flyout,
        theme.effects.disabled_opacity,
        theme.effects.dimmed_opacity,
        theme.effects.floating_action_rest_opacity,
        theme.effects.floating_action_hover_opacity,
        theme.effects.floating_action_pressed_opacity,
        theme.control.height,
        theme.control.compact_height,
        theme.control.icon_button,
        theme.control.compact_icon_button,
        theme.control.result_action_button,
        theme.control.primary_round_button,
        theme.control.floating_action_button,
        theme.control.min_touch_target,
        theme.control.title_bar_height,
        theme.control.caption_button_width,
        theme.control.card_padding,
        theme.control.result_header_height,
    )
}

pub fn theme_matrix_snapshot() -> String {
    let mut output = String::new();
    for mode in [
        ThemeMode::Light,
        ThemeMode::Dark,
        ThemeMode::Minimal,
        ThemeMode::HighContrast,
    ] {
        let theme = ThemeTokens::resolve(mode);
        let _ = writeln!(output, "{}", theme_snapshot(&theme));
    }
    output
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct VisualFrame {
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
}

impl VisualFrame {
    pub fn from_rgba(width: u32, height: u32, rgba: Vec<u8>) -> Result<Self, String> {
        let expected = expected_rgba_len(width, height)?;
        if rgba.len() != expected {
            return Err(format!(
                "invalid rgba buffer length: expected {expected}, got {}",
                rgba.len()
            ));
        }

        Ok(Self {
            width,
            height,
            rgba,
        })
    }

    pub fn solid_rgba(width: u32, height: u32, rgba: [u8; 4]) -> Self {
        let len = expected_rgba_len(width, height).expect("solid frame dimensions must fit usize");
        let mut pixels = Vec::with_capacity(len);
        let pixel_count = len / 4;
        for _ in 0..pixel_count {
            pixels.extend_from_slice(&rgba);
        }

        Self {
            width,
            height,
            rgba: pixels,
        }
    }

    pub fn diff(&self, after: &Self) -> Result<VisualDiff, String> {
        if self.width != after.width || self.height != after.height {
            return Err(format!(
                "visual frame size mismatch: before={}x{}, after={}x{}",
                self.width, self.height, after.width, after.height
            ));
        }

        if self.rgba.len() != after.rgba.len() {
            return Err(format!(
                "visual frame buffer mismatch: before={}, after={}",
                self.rgba.len(),
                after.rgba.len()
            ));
        }

        let mut changed_pixels = 0usize;
        let mut total_delta = 0u64;
        let mut max_channel_delta = 0u8;

        for (before, after) in self.rgba.chunks_exact(4).zip(after.rgba.chunks_exact(4)) {
            let mut pixel_changed = false;
            for channel in 0..4 {
                let delta = before[channel].abs_diff(after[channel]);
                if delta > 0 {
                    pixel_changed = true;
                    total_delta += u64::from(delta);
                    max_channel_delta = max_channel_delta.max(delta);
                }
            }

            if pixel_changed {
                changed_pixels += 1;
            }
        }

        Ok(VisualDiff {
            width: self.width,
            height: self.height,
            changed_pixels,
            total_delta,
            max_channel_delta,
        })
    }

    pub fn to_ppm_rgb(&self) -> Vec<u8> {
        let mut output = format!("P6\n{} {}\n255\n", self.width, self.height).into_bytes();
        output.reserve(self.rgba.len() / 4 * 3);

        for pixel in self.rgba.chunks_exact(4) {
            output.extend_from_slice(&pixel[..3]);
        }

        output
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct VisualDiff {
    pub width: u32,
    pub height: u32,
    pub changed_pixels: usize,
    pub total_delta: u64,
    pub max_channel_delta: u8,
}

impl VisualDiff {
    pub fn passes(self, tolerance: VisualDiffTolerance) -> bool {
        self.changed_pixels <= tolerance.max_changed_pixels
            && self.total_delta <= tolerance.max_total_delta
            && self.max_channel_delta <= tolerance.max_channel_delta
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct VisualDiffTolerance {
    pub max_changed_pixels: usize,
    pub max_total_delta: u64,
    pub max_channel_delta: u8,
}

impl VisualDiffTolerance {
    pub const EXACT: Self = Self {
        max_changed_pixels: 0,
        max_total_delta: 0,
        max_channel_delta: 0,
    };
}

fn expected_rgba_len(width: u32, height: u32) -> Result<usize, String> {
    let pixels = u64::from(width)
        .checked_mul(u64::from(height))
        .ok_or_else(|| "visual frame dimensions overflow".to_string())?;
    let bytes = pixels
        .checked_mul(4)
        .ok_or_else(|| "visual frame byte length overflows u64".to_string())?;
    usize::try_from(bytes).map_err(|_| "visual frame byte length overflows usize".to_string())
}

fn audit_a11y_node(node: &A11yNode, path: &str, issues: &mut Vec<A11yIssue>) {
    if requires_accessible_name(&node.role) && is_blank(node.name.as_deref()) {
        issues.push(A11yIssue {
            path: path.to_string(),
            role: node.role.clone(),
            severity: A11ySeverity::Error,
            message: "missing required accessible name".to_string(),
        });
    }

    if node.focusable && is_blank(node.name.as_deref()) {
        issues.push(A11yIssue {
            path: path.to_string(),
            role: node.role.clone(),
            severity: A11ySeverity::Error,
            message: "focusable node must have an accessible name".to_string(),
        });
    }

    if matches!(node.role, A11yRole::List | A11yRole::Navigation) && node.children.is_empty() {
        issues.push(A11yIssue {
            path: path.to_string(),
            role: node.role.clone(),
            severity: A11ySeverity::Warning,
            message: "container has no accessible children".to_string(),
        });
    }

    for (index, child) in node.children.iter().enumerate() {
        let child_path = format!("{path}/{index}");
        audit_a11y_node(child, &child_path, issues);
    }
}

fn requires_accessible_name(role: &A11yRole) -> bool {
    matches!(
        role,
        A11yRole::Application
            | A11yRole::Button
            | A11yRole::CheckBox
            | A11yRole::ComboBox
            | A11yRole::Dialog
            | A11yRole::TextInput
    )
}

fn is_blank(value: Option<&str>) -> bool {
    match value {
        Some(value) => value.trim().is_empty(),
        None => true,
    }
}

fn write_layout<Message>(output: &mut String, view: &View<Message>, indent: usize) {
    let pad = " ".repeat(indent);
    match view.token() {
        ViewToken::Layout(token) => {
            let label = match token.kind {
                LayoutKind::Column => "Column",
                LayoutKind::Row => "Row",
            };
            let _ = writeln!(
                output,
                "{pad}{label} id={:?} children={} padding={} spacing={} width={:?} height={:?} align={:?} distribution={:?}",
                token.id,
                token.children.len(),
                token.padding,
                token.spacing,
                token.width,
                token.height,
                token.align,
                token.distribution
            );
            if !token.style.classes().is_empty() {
                let _ = writeln!(output, "{pad}  style={:?}", token.style.summary());
            }
            for child in &token.children {
                write_layout(output, child, indent + 2);
            }
        }
        ViewToken::Page(token) => {
            if let Some(content) = &token.content {
                write_layout(output, content, indent);
            }
        }
        ViewToken::TitleBar(token) => {
            let _ = writeln!(
                output,
                "{pad}TitleBar id={:?} commands={} caption_controls={} minimize={:?} toggle_maximize={:?} close={:?}",
                token.id,
                token.commands.len(),
                token.show_caption_controls,
                token.minimize_action.kind(),
                token.toggle_maximize_action.kind(),
                token.close_action.kind()
            );
            for child in &token.commands {
                write_layout(output, child, indent + 2);
            }
        }
        ViewToken::FlyoutButton(token) => {
            let _ = writeln!(
                output,
                "{pad}FlyoutButton id={:?} items={} selected={:?}",
                token.id,
                token.items.len(),
                token.selected
            );
        }
        ViewToken::ProgressRing(token) => {
            let _ = writeln!(
                output,
                "{pad}ProgressRing id={:?} active={} size={}",
                token.id, token.active, token.size
            );
        }
        ViewToken::BusyOverlay(token) => {
            let _ = writeln!(
                output,
                "{pad}BusyOverlay id={:?} active={} opacity={:.2} blocks_input={}",
                token.id, token.active, token.opacity, token.blocks_input
            );
            write_layout(output, &token.content, indent + 2);
        }
        ViewToken::CommandBar(token) => {
            let _ = writeln!(
                output,
                "{pad}CommandBar id={:?} items={} compact={} width={:?} align={:?} distribution={:?}",
                token.id,
                token.items.len(),
                token.compact,
                token.width,
                token.align,
                token.distribution
            );
            for child in &token.items {
                write_layout(output, child, indent + 2);
            }
        }
        ViewToken::NavigationView(token) => {
            if let Some(content) = &token.content {
                write_layout(output, content, indent);
            }
        }
        ViewToken::Dialog(token) => {
            if let Some(content) = &token.content {
                write_layout(output, content, indent);
            }
        }
        ViewToken::Lazy(token) => write_layout(output, &token.content, indent),
        ViewToken::AdaptiveSwitch(token) => {
            let _ = writeln!(
                output,
                "{pad}AdaptiveSwitch id={:?} breakpoint_width={}",
                token.id, token.breakpoint_width
            );
            write_layout(output, &token.wide, indent + 2);
            write_layout(output, &token.narrow, indent + 2);
        }
        ViewToken::ScrollView(token) => {
            let _ = writeln!(
                output,
                "{pad}ScrollView id={:?} horizontal={:?} vertical={:?}",
                token.id, token.horizontal, token.vertical
            );
            if let Some(content) = &token.content {
                write_layout(output, content, indent + 2);
            }
        }
        ViewToken::Card(token) => {
            let _ = writeln!(
                output,
                "{pad}Card id={:?} trailing={}",
                token.id,
                token.trailing.len()
            );
            if let Some(content) = &token.content {
                write_layout(output, content, indent + 2);
            }
            for child in &token.trailing {
                write_layout(output, child, indent + 2);
            }
        }
        ViewToken::Spacer(token) => {
            let _ = writeln!(
                output,
                "{pad}Spacer id={:?} width={:?} height={:?}",
                token.id, token.width, token.height
            );
        }
        ViewToken::SettingsRow(token) => {
            let _ = writeln!(
                output,
                "{pad}SettingsRow id={:?} trailing={}",
                token.id,
                token.trailing.len()
            );
            if let Some(content) = &token.content {
                write_layout(output, content, indent + 2);
            }
            for child in &token.trailing {
                write_layout(output, child, indent + 2);
            }
        }
        ViewToken::Custom(token) => {
            let _ = writeln!(
                output,
                "{pad}Custom id={:?} control={:?} children={}",
                token.id,
                token.control,
                token.children.len()
            );
            for child in &token.children {
                write_layout(output, child, indent + 2);
            }
        }
        ViewToken::Text(_)
        | ViewToken::Button(_)
        | ViewToken::StatusBadge(_)
        | ViewToken::TextEditor(_)
        | ViewToken::ToggleSwitch(_)
        | ViewToken::ComboBox(_)
        | ViewToken::ResultCard(_)
        | ViewToken::ResultList(_) => {}
    }
}

fn write_a11y(output: &mut String, node: &A11yNode, indent: usize) {
    let pad = " ".repeat(indent);
    let _ = writeln!(
        output,
        "{pad}{:?} name={:?} focusable={}",
        node.role, node.name, node.focusable
    );
    for child in &node.children {
        write_a11y(output, child, indent + 2);
    }
}

fn write_a11y_audit(output: &mut String, audit: &A11yAudit) {
    let _ = writeln!(
        output,
        "A11yAudit passed={} errors={} warnings={}",
        audit.passed(),
        audit.error_count(),
        audit.warning_count()
    );

    for issue in &audit.issues {
        let _ = writeln!(
            output,
            "{:?} path={} role={:?} message={}",
            issue.severity, issue.path, issue.role, issue.message
        );
    }
}

pub fn assert_action_kind<Message>(action: &win_fluent::Action<Message>, expected: ActionKind) {
    assert_eq!(action.kind(), expected);
}

#[macro_export]
macro_rules! assert_view_snapshot {
    ($name:literal, $view:expr) => {{
        let snapshot = $crate::view_snapshot(&$view);
        assert!(
            !snapshot.trim().is_empty(),
            "view snapshot `{}` was empty",
            $name
        );
        snapshot
    }};
}

#[cfg(test)]
mod tests {
    use win_fluent::prelude::*;

    #[allow(dead_code)]
    #[derive(Clone)]
    enum Msg {
        Save,
        Changed(String),
    }

    #[test]
    fn snapshots_token_tree_with_schema_version() {
        let view = page("Settings")
            .content(column((
                settings_row("Mode")
                    .trailing((toggle_switch("Enabled", true).on_toggle(|_| Msg::Save),)),
                text_editor("value")
                    .focused(true)
                    .validation(ValidationState::error("Invalid value"))
                    .on_input(Msg::Changed),
            )))
            .into_view();

        let snapshot = crate::view_snapshot(&view);

        assert!(snapshot.contains("ViewSchema version=1"));
        assert!(snapshot.contains("Page title=\"Settings\""));
        assert!(snapshot.contains("ToggleSwitch"));
        assert!(snapshot.contains("TextEditor"));
        assert!(snapshot.contains("validation=Error"));
    }

    #[test]
    fn snapshots_layout_tokens_separately() {
        let view: View<Msg> = page("Layout")
            .content(column((text("A"), text("B"))).padding(24).spacing(12))
            .into_view();

        let snapshot = crate::layout_snapshot(&view);

        assert!(snapshot.contains("Column"));
        assert!(snapshot.contains("padding=24"));
        assert!(snapshot.contains("spacing=12"));
    }

    #[test]
    fn audits_accessible_views_without_issues() {
        let view = page("Settings")
            .content(column((
                button("Save").on_press(Msg::Save),
                text_editor("").placeholder("Input").on_input(Msg::Changed),
            )))
            .into_view();

        let audit = crate::accessibility_audit(&view);
        let snapshot = crate::accessibility_audit_snapshot(&view);

        assert!(audit.passed());
        assert_eq!(audit.error_count(), 0);
        assert!(snapshot.contains("A11yAudit passed=true errors=0 warnings=0"));
    }

    #[test]
    fn reports_missing_names_for_focusable_or_interactive_nodes() {
        let mut node = A11yNode::new(A11yRole::Button);
        node.focusable = true;

        let audit = crate::audit_accessibility_tree(&node);

        assert!(!audit.passed());
        assert_eq!(audit.error_count(), 2);
        assert!(audit
            .issues
            .iter()
            .any(|issue| issue.message == "missing required accessible name"));
        assert!(audit
            .issues
            .iter()
            .any(|issue| issue.message == "focusable node must have an accessible name"));
    }

    #[test]
    fn reports_empty_accessible_containers_as_warning() {
        let node = A11yNode::new(A11yRole::List);

        let audit = crate::audit_accessibility_tree(&node);

        assert!(audit.passed());
        assert_eq!(audit.error_count(), 0);
        assert_eq!(audit.warning_count(), 1);
    }

    #[test]
    fn snapshots_resolved_visual_theme_matrix() {
        let snapshot = crate::theme_matrix_snapshot();

        assert!(snapshot.contains("mode=Light"));
        assert!(snapshot.contains("mode=Dark"));
        assert!(snapshot.contains("mode=Minimal"));
        assert!(snapshot.contains("mode=HighContrast"));
        assert!(snapshot.contains("backdrop=Mica"));
        assert!(snapshot.contains("backdrop=Solid"));
        assert!(snapshot.contains("result_header_hover="));
        assert!(snapshot.contains("elevation_overlay=8"));
        assert!(snapshot.contains("control_min_touch_target=40"));
    }

    #[test]
    fn visual_diff_detects_changed_pixels_with_tolerance() {
        let before = crate::VisualFrame::solid_rgba(2, 1, [10, 20, 30, 255]);
        let after =
            crate::VisualFrame::from_rgba(2, 1, vec![10, 20, 30, 255, 20, 25, 30, 255]).unwrap();

        let diff = before.diff(&after).unwrap();

        assert_eq!(diff.changed_pixels, 1);
        assert_eq!(diff.total_delta, 15);
        assert_eq!(diff.max_channel_delta, 10);
        assert!(diff.passes(crate::VisualDiffTolerance {
            max_changed_pixels: 1,
            max_total_delta: 15,
            max_channel_delta: 10,
        }));
        assert!(!diff.passes(crate::VisualDiffTolerance::EXACT));
    }

    #[test]
    fn visual_frame_rejects_invalid_or_mismatched_buffers() {
        let invalid = crate::VisualFrame::from_rgba(2, 1, vec![0, 1, 2]);
        assert!(invalid.is_err());

        let before = crate::VisualFrame::solid_rgba(2, 1, [0, 0, 0, 255]);
        let after = crate::VisualFrame::solid_rgba(1, 2, [0, 0, 0, 255]);

        assert!(before.diff(&after).is_err());
    }

    #[test]
    fn visual_frame_emits_ppm_rgb_bytes_for_artifacts() {
        let frame = crate::VisualFrame::from_rgba(1, 2, vec![1, 2, 3, 255, 4, 5, 6, 128]).unwrap();

        let ppm = frame.to_ppm_rgb();

        assert!(ppm.starts_with(b"P6\n1 2\n255\n"));
        assert!(ppm.ends_with(&[1, 2, 3, 4, 5, 6]));
    }
}
