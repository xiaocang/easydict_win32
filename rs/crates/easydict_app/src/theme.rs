use win_fluent::prelude::*;

pub fn easydict_theme_tokens(mode: ThemeMode) -> ThemeTokens {
    match mode {
        ThemeMode::System | ThemeMode::Light => easydict_light(),
        ThemeMode::Dark => easydict_dark(),
        ThemeMode::Minimal => easydict_minimal(),
        ThemeMode::HighContrast => ThemeTokens::high_contrast(),
    }
}

fn easydict_light() -> ThemeTokens {
    ThemeTokens {
        mode: ThemeMode::Light,
        accent: AccentPalette {
            base: Color::rgb(0, 120, 212),
            light_1: Color::rgb(16, 110, 190),
            light_2: Color::rgb(234, 243, 255),
            dark_1: Color::rgb(0, 90, 158),
            dark_2: Color::rgb(0, 64, 128),
        },
        typography: Typography::default(),
        spacing: Spacing::default(),
        radius: CornerRadius {
            control: 10.0,
            overlay: 10.0,
            window: 8.0,
        },
        stroke: Stroke::default(),
        elevation: Elevation {
            rest: 0.0,
            raised: 1.0,
            overlay: 6.0,
            flyout: 12.0,
        },
        control: ControlMetrics {
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
        },
        effects: VisualEffects {
            disabled_opacity: 0.5,
            dimmed_opacity: 0.5,
            floating_action_rest_opacity: 0.75,
            floating_action_hover_opacity: 1.0,
            floating_action_pressed_opacity: 0.85,
        },
        density: Density::Comfortable,
        backdrop: BackdropKind::Mica,
        background: Color::rgb(247, 249, 252),
        surface: Color::rgb(255, 255, 255),
        surface_alt: Color::rgb(241, 244, 248),
        selected_surface: Color::rgb(234, 243, 255),
        input_surface: Color::rgb(241, 244, 248),
        result_surface: Color::rgb(255, 255, 255),
        result_header: Color::rgb(251, 252, 254),
        result_header_hover: Color::rgb(241, 244, 248),
        button_hover: Color::rgb(238, 243, 248),
        button_pressed: Color::rgb(229, 235, 243),
        floating_action_surface: Color::rgb(247, 251, 255),
        floating_action_border: Color::rgb(122, 167, 217),
        accent_hover: Color::rgb(16, 110, 190),
        accent_pressed: Color::rgb(0, 90, 158),
        accent_foreground: Color::rgb(255, 255, 255),
        status_connected: Color::rgb(16, 124, 16),
        status_disconnected: Color::rgb(121, 119, 117),
        status_error: Color::rgb(209, 52, 56),
        text_primary: Color::rgb(38, 38, 38),
        text_secondary: Color::rgb(95, 102, 112),
        border: Color::rgb(221, 228, 238),
        focus: Color::rgb(0, 122, 255),
        error: Color::rgb(209, 52, 56),
        warning: Color::rgb(157, 93, 0),
        success: Color::rgb(16, 124, 16),
    }
}

fn easydict_dark() -> ThemeTokens {
    ThemeTokens {
        mode: ThemeMode::Dark,
        accent: AccentPalette {
            base: Color::rgb(43, 136, 216),
            light_1: Color::rgb(58, 153, 230),
            light_2: Color::rgb(36, 50, 71),
            dark_1: Color::rgb(31, 111, 179),
            dark_2: Color::rgb(23, 78, 139),
        },
        typography: Typography::default(),
        spacing: Spacing::default(),
        radius: CornerRadius {
            control: 10.0,
            overlay: 10.0,
            window: 8.0,
        },
        stroke: Stroke::default(),
        elevation: Elevation {
            rest: 0.0,
            raised: 1.0,
            overlay: 6.0,
            flyout: 12.0,
        },
        control: ControlMetrics {
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
        },
        effects: VisualEffects {
            disabled_opacity: 0.5,
            dimmed_opacity: 0.5,
            floating_action_rest_opacity: 0.94,
            floating_action_hover_opacity: 1.0,
            floating_action_pressed_opacity: 0.85,
        },
        density: Density::Comfortable,
        backdrop: BackdropKind::Mica,
        background: Color::rgb(31, 34, 41),
        surface: Color::rgb(34, 39, 49),
        surface_alt: Color::rgb(42, 47, 57),
        selected_surface: Color::rgb(36, 50, 71),
        input_surface: Color::rgb(42, 47, 57),
        result_surface: Color::rgb(34, 39, 49),
        result_header: Color::rgb(40, 45, 55),
        result_header_hover: Color::rgb(50, 57, 70),
        button_hover: Color::rgb(50, 57, 70),
        button_pressed: Color::rgb(42, 47, 57),
        floating_action_surface: Color::rgb(37, 42, 51),
        floating_action_border: Color::rgb(107, 117, 132),
        accent_hover: Color::rgb(58, 153, 230),
        accent_pressed: Color::rgb(31, 111, 179),
        accent_foreground: Color::rgb(255, 255, 255),
        status_connected: Color::rgb(121, 184, 115),
        status_disconnected: Color::rgb(138, 141, 147),
        status_error: Color::rgb(229, 138, 149),
        text_primary: Color::rgb(226, 228, 233),
        text_secondary: Color::rgb(200, 206, 216),
        border: Color::rgb(58, 66, 80),
        focus: Color::rgb(94, 181, 255),
        error: Color::rgb(229, 138, 149),
        warning: Color::rgb(252, 225, 0),
        success: Color::rgb(121, 184, 115),
    }
}

fn easydict_minimal() -> ThemeTokens {
    let mut theme = ThemeTokens::minimal();
    theme.mode = ThemeMode::Minimal;
    theme.background = Color::rgb(255, 255, 255);
    theme.surface = Color::rgb(255, 255, 255);
    theme.surface_alt = Color::rgb(247, 247, 247);
    theme.input_surface = Color::rgb(255, 255, 255);
    theme.result_surface = Color::rgb(255, 255, 255);
    theme.result_header = Color::rgb(255, 255, 255);
    theme.result_header_hover = Color::rgb(224, 224, 224);
    theme.button_hover = Color::rgb(224, 224, 224);
    theme.button_pressed = Color::rgb(192, 192, 192);
    theme.floating_action_surface = Color::rgb(255, 255, 255);
    theme.floating_action_border = Color::rgb(0, 0, 0);
    theme.effects.floating_action_rest_opacity = 1.0;
    theme.effects.floating_action_hover_opacity = 1.0;
    theme.effects.floating_action_pressed_opacity = 0.85;
    theme.border = Color::rgb(153, 153, 153);
    theme
}
