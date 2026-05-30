#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ThemeMode {
    System,
    Light,
    Dark,
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
            control: 4.0,
            overlay: 8.0,
            window: 8.0,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Density {
    Compact,
    Comfortable,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ThemeTokens {
    pub mode: ThemeMode,
    pub accent: AccentPalette,
    pub typography: Typography,
    pub spacing: Spacing,
    pub radius: CornerRadius,
    pub density: Density,
    pub background: Color,
    pub surface: Color,
    pub surface_alt: Color,
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
            density: Density::Comfortable,
            background: Color::rgb(243, 243, 243),
            surface: Color::rgb(255, 255, 255),
            surface_alt: Color::rgb(250, 250, 250),
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
            density: Density::Comfortable,
            background: Color::rgb(32, 32, 32),
            surface: Color::rgb(45, 45, 45),
            surface_alt: Color::rgb(56, 56, 56),
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
            density: Density::Comfortable,
            background: Color::rgb(0, 0, 0),
            surface: Color::rgb(0, 0, 0),
            surface_alt: Color::rgb(16, 16, 16),
            text_primary: Color::rgb(255, 255, 255),
            text_secondary: Color::rgb(255, 255, 255),
            border: Color::rgb(255, 255, 255),
            focus: Color::rgb(255, 255, 0),
            error: Color::rgb(255, 128, 128),
            warning: Color::rgb(255, 255, 0),
            success: Color::rgb(0, 255, 0),
        }
    }

    pub fn resolve(mode: ThemeMode) -> Self {
        match mode {
            ThemeMode::System | ThemeMode::Light => Self::fluent_light(),
            ThemeMode::Dark => Self::fluent_dark(),
            ThemeMode::HighContrast => Self::high_contrast(),
        }
    }
}
