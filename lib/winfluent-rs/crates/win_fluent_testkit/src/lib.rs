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

#[cfg(feature = "parity-diagnostics")]
pub fn diagnostic_snapshot<Message>(
    view: &View<Message>,
) -> win_fluent::DiagnosticViewSchema {
    win_fluent::diagnostic_view_schema(view)
}

#[cfg(feature = "parity-diagnostics")]
pub fn diagnostic_diff<Message>(
    before: &View<Message>,
    after: &View<Message>,
) -> win_fluent::DiagnosticViewDiff {
    win_fluent::diagnostic_diff_views(before, after)
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
        "ResolvedTheme mode={:?} background=#{:02x}{:02x}{:02x} surface=#{:02x}{:02x}{:02x} surface_alt=#{:02x}{:02x}{:02x} selected_surface=#{:02x}{:02x}{:02x} selected_foreground=#{:02x}{:02x}{:02x} selected_border=#{:02x}{:02x}{:02x} tile_surface=#{:02x}{:02x}{:02x}{:02x} tile_foreground=#{:02x}{:02x}{:02x} tile_border=#{:02x}{:02x}{:02x} input_surface=#{:02x}{:02x}{:02x} result_surface=#{:02x}{:02x}{:02x} result_header=#{:02x}{:02x}{:02x} result_header_foreground=#{:02x}{:02x}{:02x} result_header_hover=#{:02x}{:02x}{:02x} button_hover=#{:02x}{:02x}{:02x} button_pressed=#{:02x}{:02x}{:02x} floating_input_surface=#{:02x}{:02x}{:02x} floating_input_border=#{:02x}{:02x}{:02x} floating_action_surface=#{:02x}{:02x}{:02x} floating_action_border=#{:02x}{:02x}{:02x} accent_hover=#{:02x}{:02x}{:02x} accent_pressed=#{:02x}{:02x}{:02x} accent_foreground=#{:02x}{:02x}{:02x} status_connected=#{:02x}{:02x}{:02x} status_disconnected=#{:02x}{:02x}{:02x} status_error=#{:02x}{:02x}{:02x} text_primary=#{:02x}{:02x}{:02x} text_secondary=#{:02x}{:02x}{:02x} border=#{:02x}{:02x}{:02x} focus=#{:02x}{:02x}{:02x} accent=#{:02x}{:02x}{:02x} radius_control={} spacing_md={} density={:?} backdrop={:?} stroke_control={} stroke_focus={} elevation_rest={} elevation_raised={} elevation_overlay={} elevation_flyout={} disabled_opacity={} dimmed_opacity={} floating_action_rest_opacity={} floating_action_hover_opacity={} floating_action_pressed_opacity={} control_height={} control_compact_height={} control_icon_button={} control_compact_icon_button={} result_action_button={} primary_round_button={} floating_action_button={} control_min_touch_target={} title_bar_height={} caption_button_width={} card_padding={} result_header_height={}",
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
        theme.selected_surface.r,
        theme.selected_surface.g,
        theme.selected_surface.b,
        theme.selected_foreground.r,
        theme.selected_foreground.g,
        theme.selected_foreground.b,
        theme.selected_border.r,
        theme.selected_border.g,
        theme.selected_border.b,
        theme.tile_surface.a,
        theme.tile_surface.r,
        theme.tile_surface.g,
        theme.tile_surface.b,
        theme.tile_foreground.r,
        theme.tile_foreground.g,
        theme.tile_foreground.b,
        theme.tile_border.r,
        theme.tile_border.g,
        theme.tile_border.b,
        theme.input_surface.r,
        theme.input_surface.g,
        theme.input_surface.b,
        theme.result_surface.r,
        theme.result_surface.g,
        theme.result_surface.b,
        theme.result_header.r,
        theme.result_header.g,
        theme.result_header.b,
        theme.result_header_foreground.r,
        theme.result_header_foreground.g,
        theme.result_header_foreground.b,
        theme.result_header_hover.r,
        theme.result_header_hover.g,
        theme.result_header_hover.b,
        theme.button_hover.r,
        theme.button_hover.g,
        theme.button_hover.b,
        theme.button_pressed.r,
        theme.button_pressed.g,
        theme.button_pressed.b,
        theme.floating_input_surface.r,
        theme.floating_input_surface.g,
        theme.floating_input_surface.b,
        theme.floating_input_border.r,
        theme.floating_input_border.g,
        theme.floating_input_border.b,
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
            | A11yRole::Hyperlink
            | A11yRole::MenuItem
            | A11yRole::ProgressBar
            | A11yRole::RadioButton
            | A11yRole::Slider
            | A11yRole::TabItem
            | A11yRole::TextInput
            | A11yRole::TreeItem
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
                "{pad}{label} id={:?} children={} padding={} spacing={} width={:?} height={:?} max_width={:?} max_height={:?} center_x={} margin={:?} align={:?} distribution={:?}",
                token.id,
                token.children.len(),
                layout_padding(token.padding, token.padding_edges),
                token.spacing,
                token.width,
                token.height,
                token.max_width,
                token.max_height,
                token.center_x,
                token.margin,
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
        ViewToken::Grid(token) => {
            let _ = writeln!(
                output,
                "{pad}Grid id={:?} rows={:?} columns={:?} row_spacing={} column_spacing={} cells={}",
                token.id,
                token.rows,
                token.columns,
                token.row_spacing,
                token.column_spacing,
                token.children.len()
            );
            for child in &token.children {
                let _ = writeln!(
                    output,
                    "{pad}  GridCell row={} column={} row_span={} column_span={}",
                    child.row, child.column, child.row_span, child.column_span
                );
                write_layout(output, &child.view, indent + 2);
            }
        }
        ViewToken::ListView(token) => {
            let _ = writeln!(
                output,
                "{pad}ListView id={:?} items={} selected={:?} spacing={} max_height={:?} virtualized={}",
                token.id,
                token.items.len(),
                token.selected,
                token.spacing,
                token.max_height,
                token.virtualized
            );
            for item in &token.items {
                write_layout(output, &item.view, indent + 2);
            }
        }
        ViewToken::Wrap(token) => {
            let _ = writeln!(
                output,
                "{pad}Wrap id={:?} children={} max_columns={} spacing={} run_spacing={}",
                token.id,
                token.children.len(),
                token.max_columns,
                token.spacing,
                token.run_spacing
            );
            for child in &token.children {
                write_layout(output, child, indent + 2);
            }
        }
        ViewToken::Border(token) => {
            let _ = writeln!(
                output,
                "{pad}Border id={:?} radius={} stroke={} filled={} padding={:?} width={:?} height={:?}",
                token.id,
                token.corner_radius,
                token.stroke_width,
                token.filled,
                token.padding,
                token.width,
                token.height
            );
            write_layout(output, &token.content, indent + 2);
        }
        ViewToken::Viewbox(token) => {
            let _ = writeln!(
                output,
                "{pad}Viewbox id={:?} stretch={:?} width={:?} height={:?}",
                token.id, token.stretch, token.width, token.height
            );
            write_layout(output, &token.content, indent + 2);
        }
        ViewToken::TabView(token) => {
            let _ = writeln!(
                output,
                "{pad}TabView id={:?} tabs={} selected={:?}",
                token.id,
                token.tabs.len(),
                token.selected
            );
            for tab in &token.tabs {
                write_layout(output, &tab.content, indent + 2);
            }
        }
        ViewToken::Flyout(token) => {
            let _ = writeln!(
                output,
                "{pad}Flyout id={:?} open={} placement={:?} light_dismiss={:?} focus_behavior={:?}",
                token.id, token.open, token.placement, token.light_dismiss, token.focus_behavior
            );
            write_layout(output, &token.anchor, indent + 2);
            if token.open {
                write_layout(output, &token.content, indent + 2);
            }
        }
        ViewToken::Overlay(token) => {
            let _ = writeln!(
                output,
                "{pad}Overlay id={:?} layers={} blocking_layers={} scrim_layers={}",
                token.id,
                token.layers.len(),
                token.blocking_layer_count(),
                token.scrim_layer_count()
            );
            write_layout(output, &token.base, indent + 2);
            for (index, layer) in token.layers.iter().enumerate() {
                let _ = writeln!(
                    output,
                    "{pad}  OverlayLayer index={} align={:?}/{:?} scrim={:?} blocks_input={}",
                    index, layer.align_x, layer.align_y, layer.scrim, layer.blocks_input
                );
                write_layout(output, &layer.content, indent + 2);
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
                "{pad}FlyoutButton id={:?} label={:?} items={} selected={:?} min_width={:?} min_height={:?} padding={:?} border_width={:?} radius={:?} align_y={:?} text_style={:?} font_size={:?}",
                token.id,
                token.label,
                token.items.len(),
                token.selected,
                token.min_width,
                token.min_height,
                token.padding,
                token.border_width,
                token.radius,
                token.align_y,
                token.text_style,
                token.font_size
            );
        }
        ViewToken::ProgressRing(token) => {
            let _ = writeln!(
                output,
                "{pad}ProgressRing id={:?} active={} size={}",
                token.id, token.active, token.size
            );
        }
        ViewToken::ProgressBar(token) => {
            let _ = writeln!(
                output,
                "{pad}ProgressBar id={:?} active={} value={:?} width={:?} height=Fixed({})",
                token.id,
                token.active,
                token.normalized_value(),
                token.width,
                token.height
            );
        }
        ViewToken::BusyOverlay(token) => {
            let _ = writeln!(
                output,
                "{pad}BusyOverlay id={:?} active={} opacity={:.2} fade_transition_ms={} blocks_input={}",
                token.id, token.active, token.opacity, token.fade_transition_ms, token.blocks_input
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
            let _ = writeln!(
                output,
                "{pad}NavigationView id={:?} items={} footer_items={} selected={:?} pane_display_mode={:?} settings_visible={} back_button_visible={}",
                token.id,
                token.items.len(),
                token.footer_items.len(),
                token.selected,
                token.pane_display_mode,
                token.settings_visible,
                token.back_button_visible
            );
            if let Some(content) = &token.content {
                write_layout(output, content, indent + 2);
            }
        }
        ViewToken::Dialog(token) => {
            if let Some(content) = &token.content {
                write_layout(output, content, indent);
            }
        }
        ViewToken::Lazy(token) => {
            let _ = writeln!(output, "{pad}Lazy id={:?} key={:?}", token.id, token.key);
            write_layout(output, &token.content, indent + 2);
        }
        ViewToken::AdaptiveSwitch(token) => {
            let _ = writeln!(
                output,
                "{pad}AdaptiveSwitch id={:?} breakpoint_width={} resolved_width={:?} resolved_branch={}",
                token.id,
                token.breakpoint_width,
                token.resolved_width,
                token.resolved_branch_name()
            );
            match token.resolved_branch() {
                Some(branch) => write_layout(output, branch, indent + 2),
                None => {
                    write_layout(output, &token.wide, indent + 2);
                    write_layout(output, &token.narrow, indent + 2);
                }
            }
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
                "{pad}Card id={:?} trailing={} padding={:?} content_spacing={} margin={:?} max_height={:?}",
                token.id,
                token.trailing.len(),
                token.padding,
                token.content_spacing,
                token.margin,
                token.max_height
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
        ViewToken::Expander(token) => {
            let _ = writeln!(
                output,
                "{pad}Expander id={:?} trailing={} expanded={} motion=expand-collapse-reveal",
                token.id,
                token.trailing.len(),
                token.expanded
            );
            if token.expanded {
                if let Some(content) = &token.content {
                    write_layout(output, content, indent + 2);
                }
            }
            for child in &token.trailing {
                write_layout(output, child, indent + 2);
            }
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
        ViewToken::PointerRegion(token) => {
            let _ = writeln!(
                output,
                "{pad}PointerRegion id={:?} move={:?} left_down={:?} left_up={:?} double_click={:?} right_down={:?} wheel={:?} escape={:?}",
                token.id,
                token.move_action.kind(),
                token.left_down_action.kind(),
                token.left_up_action.kind(),
                token.double_click_action.kind(),
                token.right_down_action.kind(),
                token.wheel_action.kind(),
                token.escape_action.kind()
            );
            write_layout(output, &token.content, indent + 2);
        }
        ViewToken::CaptureOverlay(token) => {
            let _ = writeln!(
                output,
                "{pad}CaptureOverlay id={:?} phase=\"{}\" depth={} dragging={} detected={:?} selection={:?} handles={} magnifier={} background_pixels={:?} cursor={:?}",
                token.id,
                token.phase,
                token.detection_depth,
                token.dragging,
                token.detected_rect,
                token.selection_rect,
                token.handles_visible,
                token.magnifier_visible,
                token.background_pixel_size(),
                token.cursor
            );
        }
        ViewToken::Image(token) => {
            let _ = writeln!(
                output,
                "{pad}Image id={:?} source_kind={:?} fallback={} bgra_path={:?} pixels={}x{} raster={:?} stretch={:?} width={:?} height={:?}",
                token.id,
                token.source_kind(),
                token.fallback_behavior(),
                token.bgra_path,
                token.pixel_width,
                token.pixel_height,
                token.raster_path,
                token.stretch,
                token.width,
                token.height
            );
        }
        ViewToken::WebView(token) => {
            let _ = writeln!(
                output,
                "{pad}WebView id={:?} source={:?} width={:?} height={:?}",
                token.id, token.source, token.width, token.height
            );
        }
        ViewToken::RichText(token) => {
            let _ = writeln!(
                output,
                "{pad}RichText id={:?} runs={} style={:?} wrapping={:?}",
                token.id,
                token.runs.len(),
                token.style,
                token.wrapping
            );
        }
        ViewToken::ToggleButton(token) => {
            let _ = writeln!(
                output,
                "{pad}ToggleButton id={:?} pressed={} enabled={}",
                token.id, token.pressed, token.state.enabled
            );
        }
        ViewToken::SplitButton(token) => {
            let disabled_items = token.items.iter().filter(|item| !item.enabled).count();
            let _ = writeln!(
                output,
                "{pad}SplitButton id={:?} items={} disabled_items={} open={} enabled={}",
                token.id,
                token.items.len(),
                disabled_items,
                token.open,
                token.state.enabled
            );
        }
        ViewToken::InfoBar(token) => {
            let _ = writeln!(
                output,
                "{pad}InfoBar id={:?} severity={:?} title={:?}",
                token.id, token.severity, token.title
            );
        }
        ViewToken::TextEditor(token) => {
            let _ = writeln!(
                output,
                "{pad}TextEditor id={:?} secure={} read_only={} chrome={:?} width={:?} min_height={:?} max_height={:?} key_bindings={}",
                token.id,
                token.secure,
                token.read_only,
                token.chrome,
                token.width,
                token.min_height,
                token.max_height,
                token.key_bindings.len()
            );
        }
        ViewToken::CheckBox(token) => {
            let _ = writeln!(
                output,
                "{pad}CheckBox id={:?} checked={} indeterminate={} enabled={}",
                token.id, token.checked, token.indeterminate, token.state.enabled
            );
        }
        ViewToken::RadioGroup(token) => {
            let _ = writeln!(
                output,
                "{pad}RadioGroup id={:?} options={} selected={:?} orientation={:?} enabled={}",
                token.id,
                token.options.len(),
                token.selected,
                token.orientation,
                token.state.enabled
            );
        }
        ViewToken::NumberBox(token) => {
            let _ = writeln!(
                output,
                "{pad}NumberBox id={:?} value={} min={:?} max={:?} step={} spin_buttons={} enabled={}",
                token.id,
                token.value,
                token.min,
                token.max,
                token.step,
                token.spin_buttons,
                token.state.enabled
            );
        }
        ViewToken::AutoSuggestBox(token) => {
            let _ = writeln!(
                output,
                "{pad}AutoSuggestBox id={:?} suggestions={} open={} highlighted_index={:?} width={:?} enabled={}",
                token.id,
                token.suggestions.len(),
                token.open,
                token.highlighted_index,
                token.width,
                token.state.enabled
            );
        }
        ViewToken::Slider(token) => {
            let _ = writeln!(
                output,
                "{pad}Slider id={:?} value={:.2} min={:.2} max={:.2} step={:.2} preview_active={} width={:?} enabled={}",
                token.id,
                token.value,
                token.min,
                token.max,
                token.step,
                token.preview_active(),
                token.width,
                token.state.enabled
            );
        }
        ViewToken::ComboBox(token) => {
            let _ = writeln!(
                output,
                "{pad}ComboBox id={:?} items={} selected={:?} selected_label={:?} width={:?} height={:?} enabled={}",
                token.id,
                token.items.len(),
                token.selected,
                token.selected_label(),
                token.width,
                token.height,
                token.state.enabled
            );
        }
        ViewToken::TreeView(token) => {
            let _ = writeln!(
                output,
                "{pad}TreeView id={:?} roots={} selected={:?}",
                token.id,
                token.roots.len(),
                token.selected
            );
            write_tree_nodes(output, &token.roots, indent + 2);
        }
        ViewToken::TrayMenu(token) => {
            let item_height = (token.style.item_font_size + token.style.item_vertical_padding)
                .max(token.style.item_min_height);
            let _ = writeln!(
                output,
                "{pad}TrayMenu id={:?} items={} min_width={} max_height={:?} radius={} shadow_margin={} animation_offset_y={} item_height={} item_padding={}x{} submenu_arrow={} hover_inset={}x{} separator={}x{} inset={} light={}/{}/{} dark={}/{}/{} hover_mix={}",
                token.id,
                token.items.len(),
                token.min_width,
                token.style.presenter_max_height,
                token.style.presenter_corner_radius,
                token.style.presenter_shadow_margin,
                token.animation_offset_y,
                item_height,
                token.style.item_horizontal_padding,
                token.style.item_vertical_padding,
                token.style.submenu_arrow_column_width,
                token.style.hover_inset_x,
                token.style.hover_inset_y,
                token.style.separator_height,
                token.style.separator_line_thickness,
                token.style.separator_horizontal_inset,
                tray_color(token.style.light_surface),
                tray_color(token.style.light_foreground),
                tray_color(token.style.light_separator),
                tray_color(token.style.dark_surface),
                tray_color(token.style.dark_foreground),
                tray_color(token.style.dark_separator),
                token.style.hover_foreground_mix_percent
            );
            write_tray_menu_items(output, &token.items, indent + 2);
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
        | ViewToken::ToggleSwitch(_)
        | ViewToken::ResultCard(_)
        | ViewToken::ResultList(_) => {}
    }
}

fn write_tree_nodes(output: &mut String, roots: &[win_fluent::view::TreeNode], indent: usize) {
    let pad = " ".repeat(indent);
    for node in roots {
        let _ = writeln!(
            output,
            "{pad}TreeNode id={:?} label={:?} expanded={} children={}",
            node.id,
            node.label,
            node.expanded,
            node.children.len()
        );
        write_tree_nodes(output, &node.children, indent + 2);
    }
}

fn write_tray_menu_items<Message>(
    output: &mut String,
    items: &[win_fluent::platform::TrayMenuItem<Message>],
    indent: usize,
) {
    let pad = " ".repeat(indent);
    for item in items {
        if item.is_separator() {
            let _ = writeln!(output, "{pad}TrayMenuSeparator");
            continue;
        }

        let _ = writeln!(
            output,
            "{pad}TrayMenuItem id={:?} label={:?} tooltip={:?} enabled={} submenu={} children={}",
            item.id,
            item.label,
            item.tooltip,
            item.enabled,
            item.is_submenu(),
            item.children.len()
        );
        write_tray_menu_items(output, &item.children, indent + 2);
    }
}

fn tray_color(color: win_fluent::platform::TrayMenuColor) -> String {
    match color {
        win_fluent::platform::TrayMenuColor::SystemMenu => "SystemMenu".to_string(),
        win_fluent::platform::TrayMenuColor::Rgb(red, green, blue) => {
            format!("#{red:02X}{green:02X}{blue:02X}")
        }
    }
}

fn layout_padding(uniform: u16, edges: Option<win_fluent::view::Edges>) -> String {
    edges
        .map(|value| format!("{value:?}"))
        .unwrap_or_else(|| uniform.to_string())
}

fn write_a11y(output: &mut String, node: &A11yNode, indent: usize) {
    let pad = " ".repeat(indent);
    let _ = writeln!(
        output,
        "{pad}{:?} name={:?} focusable={} help_text={:?}",
        node.role, node.name, node.focusable, node.help_text
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
    fn reports_missing_names_for_extended_interactive_roles() {
        for role in [
            A11yRole::Hyperlink,
            A11yRole::MenuItem,
            A11yRole::ProgressBar,
            A11yRole::RadioButton,
            A11yRole::Slider,
            A11yRole::TabItem,
            A11yRole::TreeItem,
        ] {
            let audit = crate::audit_accessibility_tree(&A11yNode::new(role.clone()));

            assert!(
                audit.issues.iter().any(|issue| issue.role == role
                    && issue.message == "missing required accessible name"),
                "expected missing-name audit issue for {role:?}"
            );
        }
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

    #[cfg(feature = "parity-diagnostics")]
    fn diagnostic_source_line(node: &win_fluent::DiagnosticNode, property: &str) -> u32 {
        node.provenance
            .source_for(property)
            .unwrap_or_else(|| panic!("missing provenance for {property}"))
            .line
    }

    #[cfg(feature = "parity-diagnostics")]
    #[test]
    fn diagnostics_track_layout_constructor_defaults_setters_and_style_classes() {
        let constructor_line = line!() + 1;
        let builder = column::<Msg, _>(());
        let width_line = line!() + 1;
        let builder = builder.width(Length::Fill);
        let style_line = line!() + 1;
        let view = builder.tw("gap-3 items-center").into_view();

        let diagnostic = crate::diagnostic_snapshot(&view);

        assert_eq!(diagnostic.root.provenance.constructor.line, constructor_line);
        assert_eq!(
            diagnostic_source_line(&diagnostic.root, "height"),
            constructor_line
        );
        assert_eq!(
            diagnostic_source_line(&diagnostic.root, "width"),
            width_line
        );
        assert_eq!(
            diagnostic_source_line(&diagnostic.root, "style"),
            style_line
        );
        assert_eq!(
            diagnostic_source_line(&diagnostic.root, "spacing"),
            style_line
        );
        assert_eq!(
            diagnostic.root.style_classes,
            vec!["gap-3".to_string(), "items-center".to_string()]
        );
    }

    #[cfg(feature = "parity-diagnostics")]
    #[test]
    fn diagnostics_track_setters_across_previously_uninstrumented_builders() {
        let card_constructor_line = line!() + 1;
        let card_builder = card::<Msg>("Result");
        let card_padding_line = line!() + 1;
        let card = card_builder.padding(16).into_view();
        let card_diagnostic = crate::diagnostic_snapshot(&card);
        assert_eq!(
            card_diagnostic.root.provenance.constructor.line,
            card_constructor_line
        );
        assert_eq!(
            diagnostic_source_line(&card_diagnostic.root, "padding"),
            card_padding_line
        );

        let editor_constructor_line = line!() + 1;
        let editor_builder = text_editor::<Msg>("value");
        let editor_height_line = line!() + 1;
        let editor = editor_builder.min_height(72).into_view();
        let editor_diagnostic = crate::diagnostic_snapshot(&editor);
        assert_eq!(
            editor_diagnostic.root.provenance.constructor.line,
            editor_constructor_line
        );
        assert_eq!(
            diagnostic_source_line(&editor_diagnostic.root, "height"),
            editor_height_line
        );
        assert_eq!(
            diagnostic_source_line(&editor_diagnostic.root, "min_height"),
            editor_height_line
        );

        let grid_constructor_line = line!() + 1;
        let grid_builder = grid::<Msg>();
        let grid_rows_line = line!() + 1;
        let grid_builder = grid_builder.rows([Length::Shrink, Length::Fill]);
        let grid_spacing_line = line!() + 1;
        let grid = grid_builder.spacing(8).into_view();
        let grid_diagnostic = crate::diagnostic_snapshot(&grid);
        assert_eq!(
            grid_diagnostic.root.provenance.constructor.line,
            grid_constructor_line
        );
        assert_eq!(
            diagnostic_source_line(&grid_diagnostic.root, "rows"),
            grid_rows_line
        );
        assert_eq!(
            diagnostic_source_line(&grid_diagnostic.root, "row_spacing"),
            grid_spacing_line
        );
        assert_eq!(
            diagnostic_source_line(&grid_diagnostic.root, "column_spacing"),
            grid_spacing_line
        );

        let flyout_constructor_line = line!() + 1;
        let flyout_builder = flyout::<Msg, _, _>(text("anchor"), text("content"));
        let flyout_placement_line = line!() + 2;
        let flyout = flyout_builder
            .placement(FlyoutPlacement::Top)
            .into_view();
        let flyout_diagnostic = crate::diagnostic_snapshot(&flyout);
        assert_eq!(
            flyout_diagnostic.root.provenance.constructor.line,
            flyout_constructor_line
        );
        assert_eq!(
            diagnostic_source_line(&flyout_diagnostic.root, "placement"),
            flyout_placement_line
        );

        let overlay_constructor_line = line!() + 1;
        let overlay_builder = overlay::<Msg, _>(text("base"));
        let overlay_layer_line = line!() + 2;
        let overlay = overlay_builder
            .layer(OverlayLayer::new(text("layer")).blocks_input(true))
            .into_view();
        let overlay_diagnostic = crate::diagnostic_snapshot(&overlay);
        assert_eq!(
            overlay_diagnostic.root.provenance.constructor.line,
            overlay_constructor_line
        );
        assert_eq!(
            diagnostic_source_line(&overlay_diagnostic.root, "layers"),
            overlay_layer_line
        );
        assert_eq!(
            diagnostic_source_line(&overlay_diagnostic.root, "blocking_layers"),
            overlay_layer_line
        );

        let list_constructor_line = line!() + 1;
        let list_builder = result_list::<Msg>([ResultItem::new("one", "One", "Body")]);
        let list_height_line = line!() + 1;
        let list = list_builder.height(Length::Fill).into_view();
        let list_diagnostic = crate::diagnostic_snapshot(&list);
        assert_eq!(
            list_diagnostic.root.provenance.constructor.line,
            list_constructor_line
        );
        assert_eq!(
            diagnostic_source_line(&list_diagnostic.root, "height"),
            list_height_line
        );

        let result_card_constructor_line = line!() + 2;
        let result_card_builder =
            result_card::<Msg>(ResultItem::new("card", "Card", "Body"));
        let result_card_transition_line = line!() + 1;
        let result_card = result_card_builder.collapse_transition_ms(180).into_view();
        let result_card_diagnostic = crate::diagnostic_snapshot(&result_card);
        assert_eq!(
            result_card_diagnostic.root.provenance.constructor.line,
            result_card_constructor_line
        );
        assert_eq!(
            diagnostic_source_line(
                &result_card_diagnostic.root,
                "collapse_transition_ms"
            ),
            result_card_transition_line
        );
    }

    #[cfg(feature = "parity-diagnostics")]
    #[test]
    fn diagnostics_keep_duplicate_ids_distinct_by_structural_path() {
        let first = card::<Msg>("First").id("duplicate").into_view();
        let second = card::<Msg>("Second").id("duplicate").into_view();
        let view = column((first, second)).into_view();

        let diagnostic = crate::diagnostic_snapshot(&view);

        assert_eq!(diagnostic.root.children.len(), 2);
        assert_eq!(
            diagnostic.root.children[0].id.as_deref(),
            Some("duplicate")
        );
        assert_eq!(
            diagnostic.root.children[1].id.as_deref(),
            Some("duplicate")
        );
        assert_ne!(
            diagnostic.root.children[0].path,
            diagnostic.root.children[1].path
        );
        assert!(diagnostic
            .warnings
            .iter()
            .any(|warning| warning.contains("duplicate-id:duplicate")));
    }
}
