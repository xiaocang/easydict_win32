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

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CommonVisualState {
    Normal,
    PointerOver,
    Pressed,
    Disabled,
}

impl CommonVisualState {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Normal => "Normal",
            Self::PointerOver => "PointerOver",
            Self::Pressed => "Pressed",
            Self::Disabled => "Disabled",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FocusVisualState {
    Focused,
    Unfocused,
}

impl FocusVisualState {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Focused => "Focused",
            Self::Unfocused => "Unfocused",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SelectionVisualState {
    Selected,
    Unselected,
}

impl SelectionVisualState {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Selected => "Selected",
            Self::Unselected => "Unselected",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct VisualStateSnapshot {
    pub common: CommonVisualState,
    pub focus: FocusVisualState,
    pub selection: SelectionVisualState,
    pub validation: Option<ValidationSeverity>,
}

impl VisualStateSnapshot {
    pub fn automation_key(self) -> String {
        format!(
            "CommonStates:{},FocusStates:{},SelectionStates:{},Validation:{:?}",
            self.common.as_str(),
            self.focus.as_str(),
            self.selection.as_str(),
            self.validation
        )
    }
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

    pub fn visual_state_snapshot(&self) -> VisualStateSnapshot {
        let common = if !self.enabled {
            CommonVisualState::Disabled
        } else if self.pressed {
            CommonVisualState::Pressed
        } else if self.hovered {
            CommonVisualState::PointerOver
        } else {
            CommonVisualState::Normal
        };

        VisualStateSnapshot {
            common,
            focus: if self.focused {
                FocusVisualState::Focused
            } else {
                FocusVisualState::Unfocused
            },
            selection: if self.selected {
                SelectionVisualState::Selected
            } else {
                SelectionVisualState::Unselected
            },
            validation: self.validation.severity,
        }
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn visual_state_snapshot_matches_winui_vsm_groups() {
        let state = ControlState::default()
            .hovered(true)
            .pressed(true)
            .focused(true)
            .selected(true)
            .validation(ValidationState::warning("check"));

        let snapshot = state.visual_state_snapshot();

        assert_eq!(snapshot.common, CommonVisualState::Pressed);
        assert_eq!(snapshot.focus, FocusVisualState::Focused);
        assert_eq!(snapshot.selection, SelectionVisualState::Selected);
        assert_eq!(snapshot.validation, Some(ValidationSeverity::Warning));
        assert_eq!(
            snapshot.automation_key(),
            "CommonStates:Pressed,FocusStates:Focused,SelectionStates:Selected,Validation:Some(Warning)"
        );
    }

    #[test]
    fn disabled_state_wins_over_pointer_and_press_states() {
        let snapshot = ControlState::default()
            .hovered(true)
            .pressed(true)
            .disabled()
            .visual_state_snapshot();

        assert_eq!(snapshot.common, CommonVisualState::Disabled);
        assert_eq!(snapshot.focus, FocusVisualState::Unfocused);
    }
}
