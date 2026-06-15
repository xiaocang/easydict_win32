#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ThemeMode {
    System,
    Light,
    Dark,
    Minimal,
    HighContrast,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Color {
    pub r: u8,
    pub g: u8,
    pub b: u8,
    pub a: u8,
}

impl Color {
    pub const fn rgb(r: u8, g: u8, b: u8) -> Self {
        Self { r, g, b, a: 255 }
    }

    pub const fn rgba(r: u8, g: u8, b: u8, a: u8) -> Self {
        Self { r, g, b, a }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AccentPalette {
    pub base: Color,
    pub light_1: Color,
    pub light_2: Color,
    pub dark_1: Color,
    pub dark_2: Color,
}

impl Default for AccentPalette {
    fn default() -> Self {
        Self {
            base: Color::rgb(0, 95, 184),
            light_1: Color::rgb(38, 140, 230),
            light_2: Color::rgb(210, 232, 255),
            dark_1: Color::rgb(0, 64, 128),
            dark_2: Color::rgb(0, 42, 87),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Typography {
    pub caption: f32,
    pub body: f32,
    pub body_large: f32,
    pub body_strong: f32,
    pub subtitle: f32,
    pub title: f32,
    pub title_large: f32,
}

impl Default for Typography {
    fn default() -> Self {
        Self {
            caption: 12.0,
            body: 14.0,
            body_large: 15.0,
            body_strong: 14.0,
            subtitle: 20.0,
            title: 28.0,
            title_large: 40.0,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Spacing {
    pub xxs: f32,
    pub xs: f32,
    pub sm: f32,
    pub md: f32,
    pub lg: f32,
    pub xl: f32,
}

impl Default for Spacing {
    fn default() -> Self {
        Self {
            xxs: 2.0,
            xs: 4.0,
            sm: 8.0,
            md: 12.0,
            lg: 16.0,
            xl: 24.0,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CornerRadius {
    pub control: f32,
    pub overlay: f32,
    pub window: f32,
}

impl Default for CornerRadius {
    fn default() -> Self {
        Self {
            control: 10.0,
            overlay: 10.0,
            window: 8.0,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Density {
    Compact,
    Comfortable,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackdropKind {
    Solid,
    Mica,
    Acrylic,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Stroke {
    pub divider: f32,
    pub control: f32,
    pub focus: f32,
}

impl Default for Stroke {
    fn default() -> Self {
        Self {
            divider: 1.0,
            control: 1.0,
            focus: 2.0,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Elevation {
    pub rest: f32,
    pub raised: f32,
    pub overlay: f32,
    pub flyout: f32,
}

impl Default for Elevation {
    fn default() -> Self {
        Self {
            rest: 0.0,
            raised: 2.0,
            overlay: 8.0,
            flyout: 16.0,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ControlMetrics {
    pub height: f32,
    pub compact_height: f32,
    pub icon_button: f32,
    pub compact_icon_button: f32,
    pub result_action_button: f32,
    pub primary_round_button: f32,
    pub floating_action_button: f32,
    pub min_touch_target: f32,
    pub title_bar_height: f32,
    pub caption_button_width: f32,
    pub card_padding: f32,
    pub result_header_height: f32,
}

impl Default for ControlMetrics {
    fn default() -> Self {
        Self {
            height: 32.0,
            compact_height: 28.0,
            icon_button: 36.0,
            compact_icon_button: 28.0,
            result_action_button: 24.0,
            primary_round_button: 40.0,
            floating_action_button: 30.0,
            min_touch_target: 40.0,
            title_bar_height: 28.0,
            caption_button_width: 48.0,
            card_padding: 12.0,
            result_header_height: 30.0,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct VisualEffects {
    pub disabled_opacity: f32,
    pub dimmed_opacity: f32,
    pub floating_action_rest_opacity: f32,
    pub floating_action_hover_opacity: f32,
    pub floating_action_pressed_opacity: f32,
}

impl Default for VisualEffects {
    fn default() -> Self {
        Self {
            disabled_opacity: 0.5,
            dimmed_opacity: 0.5,
            floating_action_rest_opacity: 1.0,
            floating_action_hover_opacity: 1.0,
            floating_action_pressed_opacity: 0.85,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct ThemeTokens {
    pub mode: ThemeMode,
    pub accent: AccentPalette,
    pub typography: Typography,
    pub spacing: Spacing,
    pub radius: CornerRadius,
    pub stroke: Stroke,
    pub elevation: Elevation,
    pub control: ControlMetrics,
    pub effects: VisualEffects,
    pub density: Density,
    pub backdrop: BackdropKind,
    pub background: Color,
    pub surface: Color,
    pub surface_alt: Color,
    /// Background for persistently selected controls (e.g. the active tab).
    pub selected_surface: Color,
    /// Foreground for persistently selected controls (e.g. the active tab).
    pub selected_foreground: Color,
    /// Border for persistently selected controls (e.g. the active tab).
    pub selected_border: Color,
    /// Background for unselected tile controls.
    pub tile_surface: Color,
    /// Foreground for unselected tile controls.
    pub tile_foreground: Color,
    /// Border for unselected tile controls.
    pub tile_border: Color,
    pub input_surface: Color,
    pub result_surface: Color,
    pub result_header: Color,
    pub result_header_foreground: Color,
    pub result_header_hover: Color,
    pub button_hover: Color,
    pub button_pressed: Color,
    pub floating_input_surface: Color,
    pub floating_input_border: Color,
    pub floating_action_surface: Color,
    pub floating_action_border: Color,
    pub accent_hover: Color,
    pub accent_pressed: Color,
    pub accent_foreground: Color,
    pub status_connected: Color,
    pub status_disconnected: Color,
    pub status_error: Color,
    pub text_primary: Color,
    pub text_secondary: Color,
    pub border: Color,
    pub focus: Color,
    pub error: Color,
    pub warning: Color,
    pub success: Color,
}

impl ThemeTokens {
    pub fn fluent_light() -> Self {
        Self {
            mode: ThemeMode::Light,
            accent: AccentPalette::default(),
            typography: Typography::default(),
            spacing: Spacing::default(),
            radius: CornerRadius::default(),
            stroke: Stroke::default(),
            elevation: Elevation::default(),
            control: ControlMetrics::default(),
            effects: VisualEffects::default(),
            density: Density::Comfortable,
            backdrop: BackdropKind::Mica,
            background: Color::rgb(243, 243, 243),
            surface: Color::rgb(255, 255, 255),
            surface_alt: Color::rgb(250, 250, 250),
            selected_surface: Color::rgb(234, 243, 255),
            selected_foreground: Color::rgb(23, 78, 139),
            selected_border: Color::rgb(92, 143, 199),
            tile_surface: Color::rgba(255, 255, 255, 0),
            tile_foreground: Color::rgb(42, 47, 54),
            tile_border: Color::rgb(214, 221, 232),
            input_surface: Color::rgb(250, 250, 250),
            result_surface: Color::rgb(255, 255, 255),
            result_header: Color::rgb(250, 250, 250),
            result_header_foreground: Color::rgb(32, 32, 32),
            result_header_hover: Color::rgb(243, 243, 243),
            button_hover: Color::rgb(243, 243, 243),
            button_pressed: Color::rgb(230, 230, 230),
            floating_input_surface: Color::rgb(250, 250, 250),
            floating_input_border: Color::rgb(218, 220, 224),
            floating_action_surface: Color::rgb(255, 255, 255),
            floating_action_border: Color::rgb(218, 220, 224),
            accent_hover: Color::rgb(38, 140, 230),
            accent_pressed: Color::rgb(0, 64, 128),
            accent_foreground: Color::rgb(255, 255, 255),
            status_connected: Color::rgb(16, 124, 16),
            status_disconnected: Color::rgb(121, 119, 117),
            status_error: Color::rgb(196, 43, 28),
            text_primary: Color::rgb(32, 32, 32),
            text_secondary: Color::rgb(96, 96, 96),
            border: Color::rgb(218, 220, 224),
            focus: Color::rgb(0, 95, 184),
            error: Color::rgb(196, 43, 28),
            warning: Color::rgb(157, 93, 0),
            success: Color::rgb(16, 124, 16),
        }
    }

    pub fn fluent_dark() -> Self {
        Self {
            mode: ThemeMode::Dark,
            accent: AccentPalette::default(),
            typography: Typography::default(),
            spacing: Spacing::default(),
            radius: CornerRadius::default(),
            stroke: Stroke::default(),
            elevation: Elevation::default(),
            control: ControlMetrics::default(),
            effects: VisualEffects::default(),
            density: Density::Comfortable,
            backdrop: BackdropKind::Mica,
            background: Color::rgb(32, 32, 32),
            surface: Color::rgb(45, 45, 45),
            surface_alt: Color::rgb(56, 56, 56),
            selected_surface: Color::rgb(36, 50, 71),
            selected_foreground: Color::rgb(216, 232, 255),
            selected_border: Color::rgb(91, 127, 166),
            tile_surface: Color::rgb(32, 33, 39),
            tile_foreground: Color::rgb(230, 235, 242),
            tile_border: Color::rgb(58, 66, 80),
            input_surface: Color::rgb(56, 56, 56),
            result_surface: Color::rgb(45, 45, 45),
            result_header: Color::rgb(56, 56, 56),
            result_header_foreground: Color::rgb(255, 255, 255),
            result_header_hover: Color::rgb(72, 72, 72),
            button_hover: Color::rgb(72, 72, 72),
            button_pressed: Color::rgb(88, 88, 88),
            floating_input_surface: Color::rgb(56, 56, 56),
            floating_input_border: Color::rgb(72, 72, 72),
            floating_action_surface: Color::rgb(45, 45, 45),
            floating_action_border: Color::rgb(72, 72, 72),
            accent_hover: Color::rgb(38, 140, 230),
            accent_pressed: Color::rgb(0, 64, 128),
            accent_foreground: Color::rgb(255, 255, 255),
            status_connected: Color::rgb(84, 227, 70),
            status_disconnected: Color::rgb(138, 141, 147),
            status_error: Color::rgb(255, 153, 164),
            text_primary: Color::rgb(255, 255, 255),
            text_secondary: Color::rgb(205, 205, 205),
            border: Color::rgb(72, 72, 72),
            focus: Color::rgb(96, 205, 255),
            error: Color::rgb(255, 153, 164),
            warning: Color::rgb(252, 225, 0),
            success: Color::rgb(84, 227, 70),
        }
    }

    pub fn high_contrast() -> Self {
        Self {
            mode: ThemeMode::HighContrast,
            accent: AccentPalette {
                base: Color::rgb(255, 255, 0),
                light_1: Color::rgb(255, 255, 128),
                light_2: Color::rgb(255, 255, 204),
                dark_1: Color::rgb(192, 192, 0),
                dark_2: Color::rgb(128, 128, 0),
            },
            typography: Typography::default(),
            spacing: Spacing::default(),
            radius: CornerRadius::default(),
            stroke: Stroke::default(),
            elevation: Elevation {
                rest: 0.0,
                raised: 0.0,
                overlay: 0.0,
                flyout: 0.0,
            },
            control: ControlMetrics::default(),
            effects: VisualEffects::default(),
            density: Density::Comfortable,
            backdrop: BackdropKind::Solid,
            background: Color::rgb(0, 0, 0),
            surface: Color::rgb(0, 0, 0),
            surface_alt: Color::rgb(16, 16, 16),
            selected_surface: Color::rgb(0, 0, 128),
            selected_foreground: Color::rgb(255, 255, 255),
            selected_border: Color::rgb(255, 255, 255),
            tile_surface: Color::rgb(255, 255, 255),
            tile_foreground: Color::rgb(0, 0, 0),
            tile_border: Color::rgb(0, 0, 0),
            input_surface: Color::rgb(0, 0, 0),
            result_surface: Color::rgb(0, 0, 0),
            result_header: Color::rgb(0, 0, 0),
            result_header_foreground: Color::rgb(255, 255, 255),
            result_header_hover: Color::rgb(32, 32, 32),
            button_hover: Color::rgb(32, 32, 32),
            button_pressed: Color::rgb(64, 64, 64),
            floating_input_surface: Color::rgb(0, 0, 0),
            floating_input_border: Color::rgb(255, 255, 255),
            floating_action_surface: Color::rgb(0, 0, 0),
            floating_action_border: Color::rgb(255, 255, 255),
            accent_hover: Color::rgb(255, 255, 128),
            accent_pressed: Color::rgb(192, 192, 0),
            accent_foreground: Color::rgb(0, 0, 0),
            status_connected: Color::rgb(0, 255, 0),
            status_disconnected: Color::rgb(255, 255, 255),
            status_error: Color::rgb(255, 128, 128),
            text_primary: Color::rgb(255, 255, 255),
            text_secondary: Color::rgb(255, 255, 255),
            border: Color::rgb(255, 255, 255),
            focus: Color::rgb(255, 255, 0),
            error: Color::rgb(255, 128, 128),
            warning: Color::rgb(255, 255, 0),
            success: Color::rgb(0, 255, 0),
        }
    }

    pub fn minimal() -> Self {
        Self {
            mode: ThemeMode::Minimal,
            accent: AccentPalette {
                base: Color::rgb(0, 0, 0),
                light_1: Color::rgb(224, 224, 224),
                light_2: Color::rgb(247, 247, 247),
                dark_1: Color::rgb(64, 64, 64),
                dark_2: Color::rgb(32, 32, 32),
            },
            typography: Typography::default(),
            spacing: Spacing::default(),
            radius: CornerRadius {
                control: 0.0,
                overlay: 0.0,
                window: 0.0,
            },
            stroke: Stroke::default(),
            elevation: Elevation {
                rest: 0.0,
                raised: 0.0,
                overlay: 0.0,
                flyout: 0.0,
            },
            control: ControlMetrics::default(),
            effects: VisualEffects::default(),
            density: Density::Comfortable,
            backdrop: BackdropKind::Solid,
            background: Color::rgb(255, 255, 255),
            surface: Color::rgb(255, 255, 255),
            surface_alt: Color::rgb(247, 247, 247),
            selected_surface: Color::rgb(224, 224, 224),
            selected_foreground: Color::rgb(0, 0, 0),
            selected_border: Color::rgb(0, 0, 0),
            tile_surface: Color::rgb(255, 255, 255),
            tile_foreground: Color::rgb(17, 17, 17),
            tile_border: Color::rgb(153, 153, 153),
            input_surface: Color::rgb(255, 255, 255),
            result_surface: Color::rgb(255, 255, 255),
            result_header: Color::rgb(255, 255, 255),
            result_header_foreground: Color::rgb(0, 0, 0),
            result_header_hover: Color::rgb(224, 224, 224),
            button_hover: Color::rgb(224, 224, 224),
            button_pressed: Color::rgb(192, 192, 192),
            floating_input_surface: Color::rgb(255, 255, 255),
            floating_input_border: Color::rgb(153, 153, 153),
            floating_action_surface: Color::rgb(255, 255, 255),
            floating_action_border: Color::rgb(153, 153, 153),
            accent_hover: Color::rgb(224, 224, 224),
            accent_pressed: Color::rgb(192, 192, 192),
            accent_foreground: Color::rgb(255, 255, 255),
            status_connected: Color::rgb(16, 124, 16),
            status_disconnected: Color::rgb(121, 119, 117),
            status_error: Color::rgb(209, 52, 56),
            text_primary: Color::rgb(0, 0, 0),
            text_secondary: Color::rgb(0, 0, 0),
            border: Color::rgb(153, 153, 153),
            focus: Color::rgb(0, 0, 0),
            error: Color::rgb(209, 52, 56),
            warning: Color::rgb(0, 0, 0),
            success: Color::rgb(16, 124, 16),
        }
    }

    pub fn resolve(mode: ThemeMode) -> Self {
        match mode {
            ThemeMode::System | ThemeMode::Light => Self::fluent_light(),
            ThemeMode::Dark => Self::fluent_dark(),
            ThemeMode::Minimal => Self::minimal(),
            ThemeMode::HighContrast => Self::high_contrast(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolves_visual_tokens_for_all_supported_modes() {
        let light = ThemeTokens::resolve(ThemeMode::Light);
        let dark = ThemeTokens::resolve(ThemeMode::Dark);
        let minimal = ThemeTokens::resolve(ThemeMode::Minimal);
        let high_contrast = ThemeTokens::resolve(ThemeMode::HighContrast);

        assert_eq!(light.backdrop, BackdropKind::Mica);
        assert_eq!(dark.backdrop, BackdropKind::Mica);
        assert_eq!(minimal.backdrop, BackdropKind::Solid);
        assert_eq!(minimal.radius.control, 0.0);
        assert_eq!(high_contrast.backdrop, BackdropKind::Solid);
        assert_eq!(light.control.height, 32.0);
        assert_eq!(light.control.result_action_button, 24.0);
        assert_eq!(light.effects.dimmed_opacity, 0.5);
        assert_eq!(dark.stroke.focus, 2.0);
        assert_eq!(high_contrast.elevation.overlay, 0.0);
    }

    #[test]
    fn system_theme_resolves_to_light_visual_contract() {
        assert_eq!(
            ThemeTokens::resolve(ThemeMode::System),
            ThemeTokens::fluent_light()
        );
    }
}
