use std::fmt;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ControlState {
    pub enabled: bool,
    pub hovered: bool,
    pub pressed: bool,
    pub focused: bool,
    /// Whether the control is in a persistent "selected"/"checked" state — e.g.
    /// the active tab. Distinct from `focused` (keyboard focus ring).
    pub selected: bool,
    pub validation: ValidationState,
}

impl ControlState {
    pub fn enabled(mut self, enabled: bool) -> Self {
        self.enabled = enabled;
        self
    }

    pub fn disabled(self) -> Self {
        self.enabled(false)
    }

    pub fn hovered(mut self, hovered: bool) -> Self {
        self.hovered = hovered;
        self
    }

    pub fn pressed(mut self, pressed: bool) -> Self {
        self.pressed = pressed;
        self
    }

    pub fn focused(mut self, focused: bool) -> Self {
        self.focused = focused;
        self
    }

    pub fn selected(mut self, selected: bool) -> Self {
        self.selected = selected;
        self
    }

    pub fn validation(mut self, validation: ValidationState) -> Self {
        self.validation = validation;
        self
    }

    pub fn is_focusable(&self) -> bool {
        self.enabled
    }
}

impl Default for ControlState {
    fn default() -> Self {
        Self {
            enabled: true,
            hovered: false,
            pressed: false,
            focused: false,
            selected: false,
            validation: ValidationState::default(),
        }
    }
}

impl fmt::Display for ControlState {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            formatter,
            "enabled={},hovered={},pressed={},focused={},selected={},validation={}",
            self.enabled, self.hovered, self.pressed, self.focused, self.selected, self.validation
        )
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ValidationState {
    pub severity: Option<ValidationSeverity>,
    pub message: Option<String>,
}

impl ValidationState {
    pub fn none() -> Self {
        Self::default()
    }

    pub fn info(message: impl Into<String>) -> Self {
        Self::message(ValidationSeverity::Info, message)
    }

    pub fn warning(message: impl Into<String>) -> Self {
        Self::message(ValidationSeverity::Warning, message)
    }

    pub fn error(message: impl Into<String>) -> Self {
        Self::message(ValidationSeverity::Error, message)
    }

    pub fn success(message: impl Into<String>) -> Self {
        Self::message(ValidationSeverity::Success, message)
    }

    fn message(severity: ValidationSeverity, message: impl Into<String>) -> Self {
        Self {
            severity: Some(severity),
            message: Some(message.into()),
        }
    }
}

impl Default for ValidationState {
    fn default() -> Self {
        Self {
            severity: None,
            message: None,
        }
    }
}

impl fmt::Display for ValidationState {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match (&self.severity, &self.message) {
            (Some(severity), Some(message)) => write!(formatter, "{severity:?}:{message:?}"),
            (Some(severity), None) => write!(formatter, "{severity:?}"),
            (None, Some(message)) => write!(formatter, "none:{message:?}"),
            (None, None) => formatter.write_str("none"),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ValidationSeverity {
    Info,
    Warning,
    Error,
    Success,
}
