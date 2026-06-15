pub const EASYDICT_SYNTHETIC_KEY: isize = 0x4541_5344;
pub const MIN_DRAG_DISTANCE: i32 = 10;
pub const MAX_CLICK_DISTANCE: i32 = 4;
pub const MULTI_CLICK_DELAY_GRACE_MS: u64 = 50;

pub const WM_LBUTTONDOWN: u32 = 0x0201;
pub const WM_LBUTTONUP: u32 = 0x0202;
pub const WM_MOUSEMOVE: u32 = 0x0200;
pub const WM_MOUSEWHEEL: u32 = 0x020A;
pub const WM_RBUTTONDOWN: u32 = 0x0204;
pub const WM_KEYDOWN: u32 = 0x0100;
pub const WM_SYSKEYDOWN: u32 = 0x0104;

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct MouseSelectionPoint {
    pub x: i32,
    pub y: i32,
}

impl MouseSelectionPoint {
    pub const fn new(x: i32, y: i32) -> Self {
        Self { x, y }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DragSelectionResult {
    pub is_drag_selection: bool,
    pub end_point: MouseSelectionPoint,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MultiClickResult {
    pub click_count: u32,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct DragDetector {
    start_point: MouseSelectionPoint,
    is_left_button_down: bool,
    is_dragging: bool,
}

impl DragDetector {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn is_left_button_down(&self) -> bool {
        self.is_left_button_down
    }

    pub fn is_dragging(&self) -> bool {
        self.is_dragging
    }

    pub fn on_left_button_down(&mut self, point: MouseSelectionPoint) {
        self.start_point = point;
        self.is_left_button_down = true;
        self.is_dragging = false;
    }

    pub fn on_mouse_move(&mut self, point: MouseSelectionPoint) {
        if !self.is_left_button_down || self.is_dragging {
            return;
        }

        let dx = point.x.saturating_sub(self.start_point.x);
        let dy = point.y.saturating_sub(self.start_point.y);
        let distance_sq = i64::from(dx) * i64::from(dx) + i64::from(dy) * i64::from(dy);
        let threshold = i64::from(MIN_DRAG_DISTANCE) * i64::from(MIN_DRAG_DISTANCE);
        if distance_sq >= threshold {
            self.is_dragging = true;
        }
    }

    pub fn on_left_button_up(&mut self, point: MouseSelectionPoint) -> DragSelectionResult {
        let was_dragging = self.is_dragging;
        self.is_left_button_down = false;
        self.is_dragging = false;
        DragSelectionResult {
            is_drag_selection: was_dragging,
            end_point: point,
        }
    }

    pub fn reset(&mut self) {
        self.is_left_button_down = false;
        self.is_dragging = false;
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct MultiClickDetector {
    click_count: u32,
    last_click_ticks: i64,
    last_click_point: MouseSelectionPoint,
}

impl MultiClickDetector {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn click_count(&self) -> u32 {
        self.click_count
    }

    pub fn on_click(
        &mut self,
        point: MouseSelectionPoint,
        current_ticks: i64,
        double_click_time_ms: u64,
    ) -> MultiClickResult {
        let elapsed = current_ticks.saturating_sub(self.last_click_ticks);
        let dx = point.x.saturating_sub(self.last_click_point.x);
        let dy = point.y.saturating_sub(self.last_click_point.y);
        let distance_sq = i64::from(dx) * i64::from(dx) + i64::from(dy) * i64::from(dy);
        let max_distance_sq = i64::from(MAX_CLICK_DISTANCE) * i64::from(MAX_CLICK_DISTANCE);
        let within_time = u64::try_from(elapsed)
            .map(|elapsed| elapsed <= double_click_time_ms)
            .unwrap_or(false);

        if within_time && distance_sq <= max_distance_sq {
            self.click_count = self.click_count.saturating_add(1);
        } else {
            self.click_count = 1;
        }

        self.last_click_ticks = current_ticks;
        self.last_click_point = point;

        MultiClickResult {
            click_count: self.click_count,
        }
    }

    pub fn reset(&mut self) {
        self.click_count = 0;
        self.last_click_ticks = 0;
        self.last_click_point = MouseSelectionPoint::default();
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MouseSelectionDismissReason {
    LeftMouseDown,
    MouseScroll,
    RightMouseDown,
    KeyDown,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MouseSelectionTriggerKind {
    Drag,
    MultiClick,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MouseSelectionTrigger {
    pub kind: MouseSelectionTriggerKind,
    pub point: MouseSelectionPoint,
    pub click_count: u32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PendingMultiClickSelection {
    pub point: MouseSelectionPoint,
    pub click_count: u32,
    pub delay_ms: u64,
}

impl PendingMultiClickSelection {
    pub fn complete(self) -> MouseSelectionTrigger {
        MouseSelectionTrigger {
            kind: MouseSelectionTriggerKind::MultiClick,
            point: self.point,
            click_count: self.click_count,
        }
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct MouseSelectionHookOutcome {
    pub dismiss: Option<MouseSelectionDismissReason>,
    pub selection: Option<MouseSelectionTrigger>,
    pub pending_multi_click: Option<PendingMultiClickSelection>,
    pub cancel_pending_multi_click: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MouseSelectionProducerContext {
    pub double_click_time_ms: u64,
    pub current_app_excluded: bool,
    pub left_click_is_pop_button: bool,
}

impl MouseSelectionProducerContext {
    pub const fn new(double_click_time_ms: u64) -> Self {
        Self {
            double_click_time_ms,
            current_app_excluded: false,
            left_click_is_pop_button: false,
        }
    }

    pub const fn current_app_excluded(mut self, value: bool) -> Self {
        self.current_app_excluded = value;
        self
    }

    pub const fn left_click_is_pop_button(mut self, value: bool) -> Self {
        self.left_click_is_pop_button = value;
        self
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MouseSelectionCaptureRequest {
    pub trigger: MouseSelectionTrigger,
    pub generation: u64,
}

impl MouseSelectionCaptureRequest {
    pub const fn anchor_x(&self) -> i32 {
        self.trigger.point.x
    }

    pub const fn anchor_y(&self) -> i32 {
        self.trigger.point.y
    }

    pub fn selection_text_ready(self, text: impl Into<String>) -> MouseSelectionTextReady {
        MouseSelectionTextReady {
            text: text.into(),
            anchor_x: self.anchor_x(),
            anchor_y: self.anchor_y(),
            generation: self.generation,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MouseSelectionTextReady {
    pub text: String,
    pub anchor_x: i32,
    pub anchor_y: i32,
    pub generation: u64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum MouseSelectionProducerAction {
    DismissPopButton {
        reason: MouseSelectionDismissReason,
    },
    CancelPendingMultiClick {
        generation: u64,
    },
    SchedulePendingMultiClick {
        pending: PendingMultiClickSelection,
        generation: u64,
    },
    CaptureSelectionText(MouseSelectionCaptureRequest),
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct MouseSelectionHookState {
    pub drag_detector: DragDetector,
    pub click_detector: MultiClickDetector,
}

impl MouseSelectionHookState {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn process_mouse_message(
        &mut self,
        message: u32,
        point: MouseSelectionPoint,
        current_ticks: i64,
        double_click_time_ms: u64,
        current_app_excluded: bool,
        left_click_is_pop_button: bool,
    ) -> MouseSelectionHookOutcome {
        let mut outcome = MouseSelectionHookOutcome::default();

        match message {
            WM_LBUTTONDOWN => {
                if !left_click_is_pop_button {
                    outcome.dismiss = Some(MouseSelectionDismissReason::LeftMouseDown);
                }
                self.drag_detector.on_left_button_down(point);
            }
            WM_MOUSEMOVE => self.drag_detector.on_mouse_move(point),
            WM_LBUTTONUP => {
                let drag = self.drag_detector.on_left_button_up(point);
                if drag.is_drag_selection {
                    self.click_detector.reset();
                    outcome.cancel_pending_multi_click = true;
                    if !current_app_excluded {
                        outcome.selection = Some(MouseSelectionTrigger {
                            kind: MouseSelectionTriggerKind::Drag,
                            point: drag.end_point,
                            click_count: 1,
                        });
                    }
                } else {
                    let click =
                        self.click_detector
                            .on_click(point, current_ticks, double_click_time_ms);
                    if click.click_count >= 2 && !current_app_excluded {
                        outcome.cancel_pending_multi_click = true;
                        outcome.pending_multi_click = Some(PendingMultiClickSelection {
                            point,
                            click_count: click.click_count,
                            delay_ms: double_click_time_ms
                                .saturating_add(MULTI_CLICK_DELAY_GRACE_MS),
                        });
                    }
                }
            }
            WM_MOUSEWHEEL => outcome.dismiss = Some(MouseSelectionDismissReason::MouseScroll),
            WM_RBUTTONDOWN => outcome.dismiss = Some(MouseSelectionDismissReason::RightMouseDown),
            _ => {}
        }

        outcome
    }

    pub fn reset(&mut self) {
        self.drag_detector.reset();
        self.click_detector.reset();
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MouseSelectionProducer {
    hook_state: MouseSelectionHookState,
    next_generation: u64,
    pending_multi_click: Option<(PendingMultiClickSelection, u64)>,
}

impl Default for MouseSelectionProducer {
    fn default() -> Self {
        Self::new()
    }
}

impl MouseSelectionProducer {
    pub fn new() -> Self {
        Self {
            hook_state: MouseSelectionHookState::new(),
            next_generation: 1,
            pending_multi_click: None,
        }
    }

    pub fn hook_state(&self) -> &MouseSelectionHookState {
        &self.hook_state
    }

    pub fn pending_multi_click_generation(&self) -> Option<u64> {
        self.pending_multi_click
            .map(|(_pending, generation)| generation)
    }

    pub fn process_low_level_input_event(
        &mut self,
        event: easydict_windows_text_selection::LowLevelInputHookEvent,
        context: MouseSelectionProducerContext,
    ) -> Vec<MouseSelectionProducerAction> {
        let outcome = self.hook_state.process_low_level_input_event(
            event,
            context.double_click_time_ms,
            context.current_app_excluded,
            context.left_click_is_pop_button,
        );
        self.process_hook_outcome(outcome)
    }

    pub fn complete_pending_multi_click(
        &mut self,
        generation: u64,
    ) -> Option<MouseSelectionProducerAction> {
        let (pending, pending_generation) = self.pending_multi_click?;
        if pending_generation != generation {
            return None;
        }

        self.pending_multi_click = None;
        Some(MouseSelectionProducerAction::CaptureSelectionText(
            MouseSelectionCaptureRequest {
                trigger: pending.complete(),
                generation,
            },
        ))
    }

    pub fn cancel_pending_multi_click(&mut self) -> Option<MouseSelectionProducerAction> {
        let (_pending, generation) = self.pending_multi_click.take()?;
        Some(MouseSelectionProducerAction::CancelPendingMultiClick { generation })
    }

    fn process_hook_outcome(
        &mut self,
        outcome: MouseSelectionHookOutcome,
    ) -> Vec<MouseSelectionProducerAction> {
        let mut actions = Vec::new();

        if let Some(reason) = outcome.dismiss {
            actions.push(MouseSelectionProducerAction::DismissPopButton { reason });
        }

        if outcome.cancel_pending_multi_click {
            if let Some(action) = self.cancel_pending_multi_click() {
                actions.push(action);
            }
        }

        if let Some(selection) = outcome.selection {
            let generation = self.next_generation();
            actions.push(MouseSelectionProducerAction::CaptureSelectionText(
                MouseSelectionCaptureRequest {
                    trigger: selection,
                    generation,
                },
            ));
        }

        if let Some(pending) = outcome.pending_multi_click {
            let generation = self.next_generation();
            self.pending_multi_click = Some((pending, generation));
            actions.push(MouseSelectionProducerAction::SchedulePendingMultiClick {
                pending,
                generation,
            });
        }

        actions
    }

    fn next_generation(&mut self) -> u64 {
        let generation = self.next_generation;
        self.next_generation = self.next_generation.saturating_add(1);
        generation
    }
}

pub fn keyboard_message_dismiss_reason(
    message: u32,
    extra_info: Option<isize>,
) -> Option<MouseSelectionDismissReason> {
    if extra_info == Some(EASYDICT_SYNTHETIC_KEY) {
        return None;
    }

    matches!(message, WM_KEYDOWN | WM_SYSKEYDOWN).then_some(MouseSelectionDismissReason::KeyDown)
}

impl MouseSelectionHookState {
    pub fn process_low_level_input_event(
        &mut self,
        event: easydict_windows_text_selection::LowLevelInputHookEvent,
        double_click_time_ms: u64,
        current_app_excluded: bool,
        left_click_is_pop_button: bool,
    ) -> MouseSelectionHookOutcome {
        match event {
            easydict_windows_text_selection::LowLevelInputHookEvent::Mouse(event) => self
                .process_mouse_message(
                    event.message,
                    MouseSelectionPoint::new(event.x, event.y),
                    i64::from(event.event_time_ms),
                    double_click_time_ms,
                    current_app_excluded,
                    left_click_is_pop_button,
                ),
            easydict_windows_text_selection::LowLevelInputHookEvent::Keyboard(event) => {
                MouseSelectionHookOutcome {
                    dismiss: keyboard_message_dismiss_reason(event.message, Some(event.extra_info)),
                    ..MouseSelectionHookOutcome::default()
                }
            }
        }
    }
}
