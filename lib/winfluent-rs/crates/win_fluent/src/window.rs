use crate::view::View;

#[derive(Clone, Debug, Eq, PartialEq, Hash)]
pub struct WindowId(String);

impl WindowId {
    pub fn new(value: impl Into<String>) -> Self {
        Self(value.into())
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl From<&str> for WindowId {
    fn from(value: &str) -> Self {
        Self::new(value)
    }
}

impl From<String> for WindowId {
    fn from(value: String) -> Self {
        Self::new(value)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WindowLevel {
    Normal,
    TopMost,
    ToolWindow,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WindowFrame {
    Standard,
    Borderless,
    Acrylic,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WindowResizeMode {
    CanResize,
    CanMinimize,
    Fixed,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WindowThemePreference {
    System,
    Light,
    Dark,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum WindowPlacement {
    Center,
    CursorOffset { x: f32, y: f32 },
    TopRight { margin_x: f32, margin_y: f32 },
    Explicit { x: f32, y: f32 },
}

#[derive(Clone, Debug, PartialEq)]
pub struct WindowOptions {
    pub id: WindowId,
    pub title: String,
    pub width: f32,
    pub height: f32,
    pub min_width: Option<f32>,
    pub min_height: Option<f32>,
    pub level: WindowLevel,
    pub frame: WindowFrame,
    pub resize_mode: WindowResizeMode,
    pub placement: WindowPlacement,
    pub theme: WindowThemePreference,
    pub visible_on_start: bool,
    pub skip_taskbar: bool,
}

impl WindowOptions {
    pub fn new(id: impl Into<WindowId>, title: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            title: title.into(),
            width: 900.0,
            height: 640.0,
            min_width: Some(480.0),
            min_height: Some(320.0),
            level: WindowLevel::Normal,
            frame: WindowFrame::Standard,
            resize_mode: WindowResizeMode::CanResize,
            placement: WindowPlacement::Center,
            theme: WindowThemePreference::System,
            visible_on_start: true,
            skip_taskbar: false,
        }
    }

    pub fn size(mut self, width: f32, height: f32) -> Self {
        self.width = width;
        self.height = height;
        self
    }

    pub fn min_size(mut self, width: f32, height: f32) -> Self {
        self.min_width = Some(width);
        self.min_height = Some(height);
        self
    }

    pub fn level(mut self, level: WindowLevel) -> Self {
        self.level = level;
        self
    }

    pub fn frame(mut self, frame: WindowFrame) -> Self {
        self.frame = frame;
        self
    }

    pub fn resize_mode(mut self, resize_mode: WindowResizeMode) -> Self {
        self.resize_mode = resize_mode;
        self
    }

    pub fn placement(mut self, placement: WindowPlacement) -> Self {
        self.placement = placement;
        self
    }

    pub fn hidden(mut self) -> Self {
        self.visible_on_start = false;
        self
    }

    pub fn skip_taskbar(mut self, skip_taskbar: bool) -> Self {
        self.skip_taskbar = skip_taskbar;
        self
    }
}

#[derive(Clone, Debug)]
pub enum WindowCommand<Message> {
    Open {
        options: WindowOptions,
        view: View<Message>,
    },
    ReplaceView {
        id: WindowId,
        view: View<Message>,
    },
    Close(WindowId),
    Show(WindowId),
    Hide(WindowId),
    Focus(WindowId),
    SetTitle {
        id: WindowId,
        title: String,
    },
    SetAlwaysOnTop {
        id: WindowId,
        enabled: bool,
    },
}
