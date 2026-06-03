use easydict_app::{
    detected_windows_from_screen_windows, CaptureInteraction, CaptureInteractionState,
    CapturePhase, CapturePoint, CaptureRect, DetectedWindow, WindowDetector,
};
use win_fluent::prelude::{ScreenRect, ScreenWindow};

#[test]
fn capture_rect_contains_uses_exclusive_bottom_right_edges() {
    let rect = CaptureRect::new(10, 20, 110, 70);

    assert_eq!(rect.width(), 100);
    assert_eq!(rect.height(), 50);
    assert!(rect.contains(CapturePoint::new(10, 20)));
    assert!(rect.contains(CapturePoint::new(50, 30)));
    assert!(!rect.contains(CapturePoint::new(110, 70)));
    assert!(!rect.contains(CapturePoint::new(0, 0)));
}

#[test]
fn window_detector_returns_z_order_window_and_nested_depth_regions() {
    let front = DetectedWindow::new(1, CaptureRect::new(50, 50, 300, 200));
    let grandchild = DetectedWindow::new(4, CaptureRect::new(100, 100, 150, 130));
    let child =
        DetectedWindow::new(3, CaptureRect::new(50, 50, 200, 180)).with_children([grandchild]);
    let back = DetectedWindow::new(2, CaptureRect::new(0, 0, 800, 600)).with_children([child]);

    let detector = WindowDetector::from_windows([front, back]);

    assert_eq!(
        detector.find_region_at_point(CapturePoint::new(100, 100), 0),
        Some(CaptureRect::new(50, 50, 300, 200))
    );

    let detector = WindowDetector::from_windows([back_window()]);
    assert_eq!(detector.max_depth_at_point(CapturePoint::new(120, 110)), 2);
    assert_eq!(
        detector.find_region_at_point(CapturePoint::new(120, 110), 0),
        Some(CaptureRect::new(100, 100, 150, 130))
    );
    assert_eq!(
        detector.find_region_at_point(CapturePoint::new(120, 110), 1),
        Some(CaptureRect::new(50, 50, 200, 180))
    );
    assert_eq!(
        detector.find_region_at_point(CapturePoint::new(120, 110), 99),
        Some(CaptureRect::new(0, 0, 800, 600))
    );
    assert_eq!(
        detector.find_region_at_point(CapturePoint::new(900, 900), 0),
        None
    );
}

#[test]
fn capture_interaction_detects_windows_and_scrolls_depth() {
    let detector = WindowDetector::from_windows([back_window()]);
    let mut state = CaptureInteractionState::new();

    assert_eq!(
        state.on_mouse_move(CapturePoint::new(120, 110), &detector),
        CaptureInteraction::Redraw
    );
    assert_eq!(
        state.detected_region,
        Some(CaptureRect::new(100, 100, 150, 130))
    );

    assert_eq!(
        state.on_mouse_wheel(-120, CapturePoint::new(120, 110), &detector),
        CaptureInteraction::Redraw
    );
    assert_eq!(state.detection_depth, 1);
    assert_eq!(
        state.detected_region,
        Some(CaptureRect::new(50, 50, 200, 180))
    );

    assert_eq!(
        state.on_mouse_wheel(120, CapturePoint::new(120, 110), &detector),
        CaptureInteraction::Redraw
    );
    assert_eq!(state.detection_depth, 0);
    assert_eq!(
        state.detected_region,
        Some(CaptureRect::new(100, 100, 150, 130))
    );
}

#[test]
fn detected_windows_from_screen_windows_restores_parent_child_snapshot_tree() {
    let windows = detected_windows_from_screen_windows([
        ScreenWindow::new(1, None, ScreenRect::new(0, 0, 800, 600)).class_name("Main"),
        ScreenWindow::new(2, Some(1), ScreenRect::new(50, 50, 200, 180)).class_name("Child"),
        ScreenWindow::new(3, Some(2), ScreenRect::new(100, 100, 50, 30)).class_name("Grandchild"),
        ScreenWindow::new(4, None, ScreenRect::new(900, 20, 120, 90)).class_name("Other"),
    ]);

    assert_eq!(
        windows,
        vec![
            DetectedWindow::new(1, CaptureRect::new(0, 0, 800, 600)).with_children([
                DetectedWindow::new(2, CaptureRect::new(50, 50, 250, 230))
                    .with_children([DetectedWindow::new(3, CaptureRect::new(100, 100, 150, 130))])
            ]),
            DetectedWindow::new(4, CaptureRect::new(900, 20, 1020, 110)),
        ]
    );
}

#[test]
fn capture_interaction_drag_selects_and_enters_adjusting_with_normalized_region() {
    let detector = WindowDetector::new();
    let mut state = CaptureInteractionState::new();

    assert_eq!(
        state.on_left_button_down(CapturePoint::new(100, 100)),
        CaptureInteraction::None
    );
    assert_eq!(
        state.on_mouse_move(CapturePoint::new(104, 104), &detector),
        CaptureInteraction::Redraw
    );
    assert_eq!(state.phase, CapturePhase::Detecting);

    assert_eq!(
        state.on_mouse_move(CapturePoint::new(80, 70), &detector),
        CaptureInteraction::Redraw
    );
    assert_eq!(state.phase, CapturePhase::Selecting);
    assert!(state.is_drag_selecting());

    assert_eq!(
        state.on_left_button_up(CapturePoint::new(80, 70)),
        CaptureInteraction::Redraw
    );
    assert_eq!(state.phase, CapturePhase::Adjusting);
    assert_eq!(state.selection, Some(CaptureRect::new(80, 70, 100, 100)));
}

#[test]
fn capture_interaction_tiny_drag_returns_to_detecting() {
    let detector = WindowDetector::new();
    let mut state = CaptureInteractionState::new();

    state.on_left_button_down(CapturePoint::new(10, 10));
    state.on_mouse_move(CapturePoint::new(16, 10), &detector);

    assert_eq!(
        state.on_left_button_up(CapturePoint::new(12, 12)),
        CaptureInteraction::Redraw
    );
    assert_eq!(state.phase, CapturePhase::Detecting);
    assert_eq!(state.selection, None);
}

#[test]
fn capture_interaction_double_click_confirms_detected_region() {
    let detector = WindowDetector::from_windows([back_window()]);
    let mut state = CaptureInteractionState::new();
    state.on_mouse_move(CapturePoint::new(120, 110), &detector);

    assert_eq!(
        state.on_double_click(CapturePoint::new(120, 110)),
        CaptureInteraction::Confirm(CaptureRect::new(100, 100, 150, 130))
    );
    assert_eq!(state.phase, CapturePhase::Detecting);
}

#[test]
fn capture_interaction_double_click_blank_enters_track_mouse_selection() {
    let mut state = CaptureInteractionState::new();

    assert_eq!(
        state.on_double_click(CapturePoint::new(20, 20)),
        CaptureInteraction::Redraw
    );
    assert_eq!(state.phase, CapturePhase::Selecting);
    assert!(!state.is_drag_selecting());

    assert_eq!(
        state.on_mouse_move(CapturePoint::new(80, 60), &WindowDetector::new()),
        CaptureInteraction::Redraw
    );
    assert_eq!(
        state.on_left_button_down(CapturePoint::new(80, 60)),
        CaptureInteraction::Redraw
    );
    assert_eq!(state.phase, CapturePhase::Adjusting);
    assert_eq!(state.selection, Some(CaptureRect::new(20, 20, 80, 60)));
}

#[test]
fn capture_interaction_right_click_and_escape_match_legacy_phase_rules() {
    let detector = WindowDetector::new();
    let mut state = CaptureInteractionState::new();

    state.on_left_button_down(CapturePoint::new(10, 10));
    state.on_mouse_move(CapturePoint::new(30, 30), &detector);

    assert_eq!(state.on_right_button_down(), CaptureInteraction::Redraw);
    assert_eq!(state.phase, CapturePhase::Detecting);

    assert_eq!(state.on_right_button_down(), CaptureInteraction::Cancel);

    state.on_double_click(CapturePoint::new(10, 10));
    state.detection_depth = 2;
    assert_eq!(state.on_escape(), CaptureInteraction::Redraw);
    assert_eq!(state.phase, CapturePhase::Detecting);
    assert_eq!(state.detection_depth, 0);

    assert_eq!(state.on_escape(), CaptureInteraction::Cancel);
}

#[test]
fn capture_interaction_adjusting_nudges_selection_by_pixel() {
    let mut state = CaptureInteractionState::new();

    assert_eq!(
        state.set_adjusting_selection(CaptureRect::new(100, 80, 220, 160)),
        CaptureInteraction::Redraw
    );
    assert_eq!(state.phase, CapturePhase::Adjusting);

    assert_eq!(state.nudge_selection(1, 0), CaptureInteraction::Redraw);
    assert_eq!(state.selection, Some(CaptureRect::new(101, 80, 221, 160)));

    assert_eq!(state.nudge_selection(0, -1), CaptureInteraction::Redraw);
    assert_eq!(state.selection, Some(CaptureRect::new(101, 79, 221, 159)));
}

#[test]
fn capture_interaction_adjusting_cancel_returns_to_detecting() {
    let mut state = CaptureInteractionState::new();
    state.set_adjusting_selection(CaptureRect::new(100, 80, 220, 160));

    assert_eq!(state.on_escape(), CaptureInteraction::Redraw);
    assert_eq!(state.phase, CapturePhase::Detecting);
    assert_eq!(state.selection, None);

    state.set_adjusting_selection(CaptureRect::new(100, 80, 220, 160));
    assert_eq!(state.on_right_button_down(), CaptureInteraction::Redraw);
    assert_eq!(state.phase, CapturePhase::Detecting);
    assert_eq!(state.selection, None);
}

#[test]
fn capture_interaction_blank_mouse_moves_keep_redrawing_for_magnifier_parity() {
    let detector = WindowDetector::new();
    let mut state = CaptureInteractionState::new();

    assert_eq!(
        state.on_mouse_move(CapturePoint::new(10, 10), &detector),
        CaptureInteraction::Redraw
    );
    assert_eq!(state.detected_region, None);
    assert_eq!(
        state.on_mouse_move(CapturePoint::new(11, 10), &detector),
        CaptureInteraction::Redraw
    );
}

fn back_window() -> DetectedWindow {
    let grandchild = DetectedWindow::new(3, CaptureRect::new(100, 100, 150, 130));
    let child =
        DetectedWindow::new(2, CaptureRect::new(50, 50, 200, 180)).with_children([grandchild]);
    DetectedWindow::new(1, CaptureRect::new(0, 0, 800, 600)).with_children([child])
}
