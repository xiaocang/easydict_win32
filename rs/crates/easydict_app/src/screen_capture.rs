const DRAG_THRESHOLD: i32 = 5;
const MIN_SELECTION_SIZE: i32 = 3;

use win_fluent::platform::{ScreenRect, ScreenWindow};

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct CapturePoint {
    pub x: i32,
    pub y: i32,
}

impl CapturePoint {
    pub const fn new(x: i32, y: i32) -> Self {
        Self { x, y }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CaptureRect {
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
}

impl CaptureRect {
    pub const fn new(left: i32, top: i32, right: i32, bottom: i32) -> Self {
        Self {
            left,
            top,
            right,
            bottom,
        }
    }

    pub const fn from_point(point: CapturePoint) -> Self {
        Self::new(point.x, point.y, point.x, point.y)
    }

    pub const fn width(self) -> i32 {
        self.right - self.left
    }

    pub const fn height(self) -> i32 {
        self.bottom - self.top
    }

    pub const fn contains(self, point: CapturePoint) -> bool {
        point.x >= self.left && point.x < self.right && point.y >= self.top && point.y < self.bottom
    }

    pub const fn normalized(self) -> Self {
        Self {
            left: min_i32(self.left, self.right),
            top: min_i32(self.top, self.bottom),
            right: max_i32(self.left, self.right),
            bottom: max_i32(self.top, self.bottom),
        }
    }

    pub const fn is_confirmable(self) -> bool {
        let normalized = self.normalized();
        normalized.width() >= MIN_SELECTION_SIZE && normalized.height() >= MIN_SELECTION_SIZE
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DetectedWindow {
    pub id: isize,
    pub rect: CaptureRect,
    pub children: Vec<DetectedWindow>,
}

impl DetectedWindow {
    pub fn new(id: isize, rect: CaptureRect) -> Self {
        Self {
            id,
            rect,
            children: Vec::new(),
        }
    }

    pub fn with_children(mut self, children: impl IntoIterator<Item = DetectedWindow>) -> Self {
        self.children.extend(children);
        self
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct WindowDetector {
    windows: Vec<DetectedWindow>,
}

impl WindowDetector {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn from_windows(windows: impl IntoIterator<Item = DetectedWindow>) -> Self {
        Self {
            windows: windows.into_iter().collect(),
        }
    }

    pub fn add_window(&mut self, window: DetectedWindow) {
        self.windows.push(window);
    }

    pub fn clear(&mut self) {
        self.windows.clear();
    }

    pub fn find_region_at_point(&self, point: CapturePoint, depth: usize) -> Option<CaptureRect> {
        let window = self
            .windows
            .iter()
            .find(|window| window.rect.contains(point))?;
        let mut chain = vec![window];
        build_child_chain(&window.children, point, &mut chain);

        let target_index = chain.len().saturating_sub(1 + depth);
        Some(chain[target_index].rect)
    }

    pub fn max_depth_at_point(&self, point: CapturePoint) -> usize {
        let Some(window) = self
            .windows
            .iter()
            .find(|window| window.rect.contains(point))
        else {
            return 0;
        };

        let mut chain = vec![window];
        build_child_chain(&window.children, point, &mut chain);
        chain.len().saturating_sub(1)
    }
}

pub fn detected_windows_from_screen_windows(
    windows: impl IntoIterator<Item = ScreenWindow>,
) -> Vec<DetectedWindow> {
    let windows: Vec<ScreenWindow> = windows.into_iter().collect();
    build_detected_window_tree(None, &windows)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CapturePhase {
    Detecting,
    Selecting,
    Adjusting,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CaptureInteraction {
    None,
    Redraw,
    Confirm(CaptureRect),
    Cancel,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CaptureInteractionState {
    pub phase: CapturePhase,
    pub detected_region: Option<CaptureRect>,
    pub selection: Option<CaptureRect>,
    pub detection_depth: usize,
    is_mouse_down: bool,
    is_drag_selecting: bool,
    ignore_next_mouse_up: bool,
    mouse_down_point: CapturePoint,
}

impl Default for CaptureInteractionState {
    fn default() -> Self {
        Self {
            phase: CapturePhase::Detecting,
            detected_region: None,
            selection: None,
            detection_depth: 0,
            is_mouse_down: false,
            is_drag_selecting: false,
            ignore_next_mouse_up: false,
            mouse_down_point: CapturePoint::default(),
        }
    }
}

impl CaptureInteractionState {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn is_drag_selecting(&self) -> bool {
        self.is_drag_selecting
    }

    pub fn on_mouse_move(
        &mut self,
        point: CapturePoint,
        detector: &WindowDetector,
    ) -> CaptureInteraction {
        match self.phase {
            CapturePhase::Detecting => {
                if self.is_mouse_down && drag_exceeds_threshold(self.mouse_down_point, point) {
                    self.is_mouse_down = false;
                    self.is_drag_selecting = true;
                    self.selection = Some(CaptureRect::new(
                        self.mouse_down_point.x,
                        self.mouse_down_point.y,
                        point.x,
                        point.y,
                    ));
                    self.detected_region = None;
                    self.phase = CapturePhase::Selecting;
                    return CaptureInteraction::Redraw;
                }

                let previous = self.detected_region;
                let detected = detector.find_region_at_point(point, self.detection_depth);
                self.detected_region = detected;

                if previous != detected || detected.is_none() {
                    CaptureInteraction::Redraw
                } else {
                    CaptureInteraction::None
                }
            }
            CapturePhase::Selecting => {
                if let Some(selection) = self.selection.as_mut() {
                    selection.right = point.x;
                    selection.bottom = point.y;
                }
                CaptureInteraction::Redraw
            }
            CapturePhase::Adjusting => CaptureInteraction::None,
        }
    }

    pub fn on_left_button_down(&mut self, point: CapturePoint) -> CaptureInteraction {
        match self.phase {
            CapturePhase::Detecting => {
                self.is_mouse_down = true;
                self.mouse_down_point = point;
                CaptureInteraction::None
            }
            CapturePhase::Selecting if !self.is_drag_selecting => {
                let mut selection = self.selection.unwrap_or(CaptureRect::from_point(point));
                selection.right = point.x;
                selection.bottom = point.y;
                self.selection = Some(selection.normalized());
                self.confirm_or_reset()
            }
            CapturePhase::Selecting => CaptureInteraction::None,
            CapturePhase::Adjusting => CaptureInteraction::None,
        }
    }

    pub fn on_left_button_up(&mut self, point: CapturePoint) -> CaptureInteraction {
        if self.ignore_next_mouse_up {
            self.ignore_next_mouse_up = false;
            return CaptureInteraction::None;
        }

        if self.is_mouse_down && self.phase == CapturePhase::Detecting {
            self.is_mouse_down = false;
            return CaptureInteraction::None;
        }

        if self.phase == CapturePhase::Selecting && self.is_drag_selecting {
            self.is_drag_selecting = false;
            if let Some(selection) = self.selection.as_mut() {
                selection.right = point.x;
                selection.bottom = point.y;
                *selection = selection.normalized();
            }
            return self.confirm_or_reset();
        }

        CaptureInteraction::None
    }

    pub fn on_double_click(&mut self, point: CapturePoint) -> CaptureInteraction {
        if self.phase != CapturePhase::Detecting {
            return CaptureInteraction::None;
        }

        self.is_mouse_down = false;
        self.ignore_next_mouse_up = true;

        if let Some(detected_region) = self.detected_region {
            self.selection = Some(detected_region);
            return CaptureInteraction::Confirm(detected_region);
        }

        self.selection = Some(CaptureRect::from_point(point));
        self.detected_region = None;
        self.is_drag_selecting = false;
        self.phase = CapturePhase::Selecting;
        CaptureInteraction::Redraw
    }

    pub fn on_right_button_down(&mut self) -> CaptureInteraction {
        if matches!(
            self.phase,
            CapturePhase::Selecting | CapturePhase::Adjusting
        ) {
            self.reset_to_detecting(false);
            CaptureInteraction::Redraw
        } else {
            CaptureInteraction::Cancel
        }
    }

    pub fn on_escape(&mut self) -> CaptureInteraction {
        if matches!(
            self.phase,
            CapturePhase::Selecting | CapturePhase::Adjusting
        ) {
            self.reset_to_detecting(true);
            CaptureInteraction::Redraw
        } else {
            CaptureInteraction::Cancel
        }
    }

    pub fn on_mouse_wheel(
        &mut self,
        delta: i32,
        point: CapturePoint,
        detector: &WindowDetector,
    ) -> CaptureInteraction {
        if self.phase != CapturePhase::Detecting {
            return CaptureInteraction::None;
        }

        let max_depth = detector.max_depth_at_point(point);
        if delta > 0 {
            self.detection_depth = self.detection_depth.saturating_sub(1);
        } else {
            self.detection_depth = (self.detection_depth + 1).min(max_depth);
        }

        self.detected_region = None;
        self.on_mouse_move(point, detector)
    }

    pub fn set_adjusting_selection(&mut self, selection: CaptureRect) -> CaptureInteraction {
        let selection = selection.normalized();
        if selection.is_confirmable() {
            self.phase = CapturePhase::Adjusting;
            self.detected_region = None;
            self.selection = Some(selection);
            self.is_mouse_down = false;
            self.is_drag_selecting = false;
            self.ignore_next_mouse_up = false;
            CaptureInteraction::Redraw
        } else {
            self.reset_to_detecting(false);
            CaptureInteraction::Redraw
        }
    }

    pub fn nudge_selection(&mut self, delta_x: i32, delta_y: i32) -> CaptureInteraction {
        if self.phase != CapturePhase::Adjusting {
            return CaptureInteraction::None;
        }

        let Some(selection) = self.selection.map(CaptureRect::normalized) else {
            self.reset_to_detecting(false);
            return CaptureInteraction::Redraw;
        };

        self.selection = Some(CaptureRect::new(
            selection.left.saturating_add(delta_x),
            selection.top.saturating_add(delta_y),
            selection.right.saturating_add(delta_x),
            selection.bottom.saturating_add(delta_y),
        ));
        CaptureInteraction::Redraw
    }

    fn confirm_or_reset(&mut self) -> CaptureInteraction {
        let Some(selection) = self.selection.map(CaptureRect::normalized) else {
            self.reset_to_detecting(false);
            return CaptureInteraction::Redraw;
        };

        if selection.is_confirmable() {
            self.set_adjusting_selection(selection)
        } else {
            self.reset_to_detecting(false);
            CaptureInteraction::Redraw
        }
    }

    fn reset_to_detecting(&mut self, reset_depth: bool) {
        self.phase = CapturePhase::Detecting;
        self.detected_region = None;
        self.selection = None;
        self.is_mouse_down = false;
        self.is_drag_selecting = false;
        self.ignore_next_mouse_up = false;
        if reset_depth {
            self.detection_depth = 0;
        }
    }
}

fn build_child_chain<'a>(
    children: &'a [DetectedWindow],
    point: CapturePoint,
    chain: &mut Vec<&'a DetectedWindow>,
) {
    if let Some(child) = children.iter().find(|child| child.rect.contains(point)) {
        chain.push(child);
        build_child_chain(&child.children, point, chain);
    }
}

fn build_detected_window_tree(
    parent_id: Option<isize>,
    windows: &[ScreenWindow],
) -> Vec<DetectedWindow> {
    windows
        .iter()
        .filter(|window| window.parent_id == parent_id)
        .map(|window| {
            DetectedWindow::new(window.id, capture_rect_from_screen_rect(window.rect))
                .with_children(build_detected_window_tree(Some(window.id), windows))
        })
        .collect()
}

fn capture_rect_from_screen_rect(rect: ScreenRect) -> CaptureRect {
    let width = i32::try_from(rect.width).unwrap_or(i32::MAX);
    let height = i32::try_from(rect.height).unwrap_or(i32::MAX);
    CaptureRect::new(
        rect.x,
        rect.y,
        rect.x.saturating_add(width),
        rect.y.saturating_add(height),
    )
}

const fn min_i32(left: i32, right: i32) -> i32 {
    if left < right {
        left
    } else {
        right
    }
}

const fn max_i32(left: i32, right: i32) -> i32 {
    if left > right {
        left
    } else {
        right
    }
}

fn drag_exceeds_threshold(start: CapturePoint, current: CapturePoint) -> bool {
    (current.x - start.x).abs() > DRAG_THRESHOLD || (current.y - start.y).abs() > DRAG_THRESHOLD
}
