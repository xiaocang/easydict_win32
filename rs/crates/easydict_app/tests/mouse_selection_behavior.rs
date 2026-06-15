use easydict_app::{
    keyboard_message_dismiss_reason, DragDetector, MouseSelectionDismissReason,
    MouseSelectionHookState, MouseSelectionPoint, MouseSelectionProducer,
    MouseSelectionProducerAction, MouseSelectionProducerContext, MouseSelectionTriggerKind,
    MultiClickDetector, EASYDICT_SYNTHETIC_KEY, MAX_CLICK_DISTANCE, MIN_DRAG_DISTANCE,
    MULTI_CLICK_DELAY_GRACE_MS, WM_KEYDOWN, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MOUSEMOVE,
    WM_MOUSEWHEEL, WM_RBUTTONDOWN, WM_SYSKEYDOWN,
};
use easydict_windows_text_selection::{
    LowLevelInputHookEvent, LowLevelKeyboardHookEvent, LowLevelMouseHookEvent,
};

fn point(x: i32, y: i32) -> MouseSelectionPoint {
    MouseSelectionPoint::new(x, y)
}

#[test]
fn drag_detector_matches_dotnet_distance_threshold() {
    let mut detector = DragDetector::new();
    detector.on_left_button_down(point(0, 0));
    detector.on_mouse_move(point(MIN_DRAG_DISTANCE - 1, 0));
    assert!(!detector.is_dragging());
    assert!(!detector.on_left_button_up(point(9, 0)).is_drag_selection);

    detector.on_left_button_down(point(0, 0));
    detector.on_mouse_move(point(MIN_DRAG_DISTANCE, 0));
    assert!(detector.is_dragging());
    let exact = detector.on_left_button_up(point(MIN_DRAG_DISTANCE, 0));
    assert!(exact.is_drag_selection);
    assert_eq!(exact.end_point, point(MIN_DRAG_DISTANCE, 0));

    detector.on_left_button_down(point(0, 0));
    detector.on_mouse_move(point(7, 7));
    assert!(!detector.on_left_button_up(point(7, 7)).is_drag_selection);

    detector.on_left_button_down(point(0, 0));
    detector.on_mouse_move(point(8, 8));
    assert!(detector.on_left_button_up(point(8, 8)).is_drag_selection);
}

#[test]
fn drag_detector_resets_after_drag_or_manual_reset() {
    let mut detector = DragDetector::new();
    detector.on_left_button_down(point(100, 100));
    detector.on_mouse_move(point(200, 100));
    assert!(detector.is_left_button_down());
    assert!(detector.is_dragging());

    assert!(
        detector
            .on_left_button_up(point(200, 100))
            .is_drag_selection
    );
    assert!(!detector.is_left_button_down());
    assert!(!detector.is_dragging());

    detector.on_left_button_down(point(0, 0));
    detector.on_mouse_move(point(50, 0));
    detector.reset();
    assert!(!detector.is_left_button_down());
    assert!(!detector.is_dragging());
}

#[test]
fn multi_click_detector_tracks_timing_and_distance() {
    let mut detector = MultiClickDetector::new();
    detector.on_click(point(100, 100), 1_000, 500);
    assert_eq!(
        detector.on_click(point(100, 100), 1_300, 500).click_count,
        2
    );
    assert_eq!(
        detector.on_click(point(100, 100), 1_400, 500).click_count,
        3
    );

    assert_eq!(
        detector.on_click(point(100, 100), 2_000, 500).click_count,
        1
    );

    detector.reset();
    detector.on_click(point(100, 100), 1_000, 500);
    assert_eq!(
        detector
            .on_click(point(100 + MAX_CLICK_DISTANCE + 1, 100), 1_200, 500)
            .click_count,
        1
    );

    detector.reset();
    detector.on_click(point(100, 100), 1_000, 500);
    assert_eq!(
        detector
            .on_click(point(100 + MAX_CLICK_DISTANCE - 1, 100), 1_200, 500)
            .click_count,
        2
    );
}

#[test]
fn hook_state_detects_drag_selection_and_cancels_pending_multi_click() {
    let mut state = MouseSelectionHookState::new();

    let down =
        state.process_mouse_message(WM_LBUTTONDOWN, point(100, 100), 1_000, 500, false, false);
    assert_eq!(
        down.dismiss,
        Some(MouseSelectionDismissReason::LeftMouseDown)
    );

    let move_outcome =
        state.process_mouse_message(WM_MOUSEMOVE, point(200, 100), 1_010, 500, false, false);
    assert_eq!(move_outcome.selection, None);

    let up = state.process_mouse_message(WM_LBUTTONUP, point(200, 100), 1_020, 500, false, false);
    let selection = up.selection.expect("drag should select");
    assert_eq!(selection.kind, MouseSelectionTriggerKind::Drag);
    assert_eq!(selection.point, point(200, 100));
    assert_eq!(selection.click_count, 1);
    assert!(up.cancel_pending_multi_click);
    assert_eq!(state.click_detector.click_count(), 0);
}

#[test]
fn hook_state_schedules_double_and_triple_click_after_grace_delay() {
    let mut state = MouseSelectionHookState::new();

    state.process_mouse_message(WM_LBUTTONDOWN, point(100, 100), 1_000, 500, false, false);
    let first =
        state.process_mouse_message(WM_LBUTTONUP, point(100, 100), 1_000, 500, false, false);
    assert_eq!(first.pending_multi_click, None);

    state.process_mouse_message(WM_LBUTTONDOWN, point(100, 100), 1_200, 500, false, false);
    let second =
        state.process_mouse_message(WM_LBUTTONUP, point(100, 100), 1_200, 500, false, false);
    let pending = second
        .pending_multi_click
        .expect("double click should schedule delayed selection");
    assert_eq!(pending.click_count, 2);
    assert_eq!(pending.point, point(100, 100));
    assert_eq!(pending.delay_ms, 500 + MULTI_CLICK_DELAY_GRACE_MS);
    assert_eq!(
        pending.complete().kind,
        MouseSelectionTriggerKind::MultiClick
    );

    state.process_mouse_message(WM_LBUTTONDOWN, point(100, 100), 1_350, 500, false, false);
    let third =
        state.process_mouse_message(WM_LBUTTONUP, point(100, 100), 1_350, 500, false, false);
    let pending = third
        .pending_multi_click
        .expect("triple click should restart delayed selection");
    assert_eq!(pending.click_count, 3);
    assert!(third.cancel_pending_multi_click);
}

#[test]
fn hook_state_suppresses_selection_for_excluded_apps_but_keeps_dismissals() {
    let mut state = MouseSelectionHookState::new();

    state.process_mouse_message(WM_LBUTTONDOWN, point(0, 0), 1_000, 500, true, false);
    state.process_mouse_message(WM_MOUSEMOVE, point(20, 0), 1_010, 500, true, false);
    let drag = state.process_mouse_message(WM_LBUTTONUP, point(20, 0), 1_020, 500, true, false);
    assert_eq!(drag.selection, None);
    assert!(drag.cancel_pending_multi_click);

    state.process_mouse_message(WM_LBUTTONDOWN, point(10, 10), 2_000, 500, true, false);
    state.process_mouse_message(WM_LBUTTONUP, point(10, 10), 2_000, 500, true, false);
    state.process_mouse_message(WM_LBUTTONDOWN, point(10, 10), 2_100, 500, true, false);
    let second = state.process_mouse_message(WM_LBUTTONUP, point(10, 10), 2_100, 500, true, false);
    assert_eq!(second.pending_multi_click, None);
    assert_eq!(state.click_detector.click_count(), 2);
}

#[test]
fn hook_state_dismisses_on_scroll_right_click_and_non_pop_button_left_click() {
    let mut state = MouseSelectionHookState::new();

    let pop_click =
        state.process_mouse_message(WM_LBUTTONDOWN, point(1, 1), 1_000, 500, false, true);
    assert_eq!(pop_click.dismiss, None);

    let other_click =
        state.process_mouse_message(WM_LBUTTONDOWN, point(2, 2), 1_100, 500, false, false);
    assert_eq!(
        other_click.dismiss,
        Some(MouseSelectionDismissReason::LeftMouseDown)
    );

    let scroll = state.process_mouse_message(WM_MOUSEWHEEL, point(2, 2), 1_200, 500, false, false);
    assert_eq!(
        scroll.dismiss,
        Some(MouseSelectionDismissReason::MouseScroll)
    );

    let right = state.process_mouse_message(WM_RBUTTONDOWN, point(2, 2), 1_300, 500, false, false);
    assert_eq!(
        right.dismiss,
        Some(MouseSelectionDismissReason::RightMouseDown)
    );
}

#[test]
fn keyboard_dismiss_ignores_synthetic_ctrl_c_marker() {
    assert_eq!(
        keyboard_message_dismiss_reason(WM_KEYDOWN, None),
        Some(MouseSelectionDismissReason::KeyDown)
    );
    assert_eq!(
        keyboard_message_dismiss_reason(WM_SYSKEYDOWN, Some(0)),
        Some(MouseSelectionDismissReason::KeyDown)
    );
    assert_eq!(
        keyboard_message_dismiss_reason(WM_KEYDOWN, Some(EASYDICT_SYNTHETIC_KEY)),
        None
    );
    assert_eq!(keyboard_message_dismiss_reason(0x0101, None), None);
}

#[test]
fn low_level_mouse_events_feed_existing_drag_selection_state_machine() {
    let mut state = MouseSelectionHookState::new();

    let down = state.process_low_level_input_event(
        mouse_event(WM_LBUTTONDOWN, 100, 100, 1_000),
        500,
        false,
        false,
    );
    assert_eq!(
        down.dismiss,
        Some(MouseSelectionDismissReason::LeftMouseDown)
    );

    state.process_low_level_input_event(
        mouse_event(WM_MOUSEMOVE, 120, 100, 1_010),
        500,
        false,
        false,
    );
    let up = state.process_low_level_input_event(
        mouse_event(WM_LBUTTONUP, 120, 100, 1_020),
        500,
        false,
        false,
    );

    let selection = up.selection.expect("drag should select through hook event");
    assert_eq!(selection.kind, MouseSelectionTriggerKind::Drag);
    assert_eq!(selection.point, point(120, 100));
    assert!(up.cancel_pending_multi_click);
}

#[test]
fn low_level_keyboard_events_preserve_synthetic_marker_dismiss_guard() {
    let mut state = MouseSelectionHookState::new();

    let real_key = state.process_low_level_input_event(
        keyboard_event(WM_KEYDOWN, 0x41, 1_000, 0),
        500,
        false,
        false,
    );
    assert_eq!(real_key.dismiss, Some(MouseSelectionDismissReason::KeyDown));

    let synthetic = state.process_low_level_input_event(
        keyboard_event(WM_KEYDOWN, 0x43, 1_010, EASYDICT_SYNTHETIC_KEY),
        500,
        false,
        false,
    );
    assert_eq!(synthetic.dismiss, None);
}

#[test]
fn producer_maps_drag_hook_events_to_pop_button_capture_request() {
    let mut producer = MouseSelectionProducer::new();
    let context = MouseSelectionProducerContext::new(500);

    let down = producer
        .process_low_level_input_event(mouse_event(WM_LBUTTONDOWN, 100, 100, 1_000), context);
    assert_eq!(
        down,
        vec![MouseSelectionProducerAction::DismissPopButton {
            reason: MouseSelectionDismissReason::LeftMouseDown
        }]
    );

    assert!(producer
        .process_low_level_input_event(mouse_event(WM_MOUSEMOVE, 140, 100, 1_010), context)
        .is_empty());
    let actions =
        producer.process_low_level_input_event(mouse_event(WM_LBUTTONUP, 140, 100, 1_020), context);

    let [MouseSelectionProducerAction::CaptureSelectionText(request)] = actions.as_slice() else {
        panic!("drag should request selected-text capture, got {actions:?}");
    };
    assert_eq!(request.generation, 1);
    assert_eq!(request.trigger.kind, MouseSelectionTriggerKind::Drag);
    assert_eq!(request.anchor_x(), 140);
    assert_eq!(request.anchor_y(), 100);

    let ready = request.selection_text_ready("selected text");
    assert_eq!(ready.text, "selected text");
    assert_eq!(ready.anchor_x, 140);
    assert_eq!(ready.anchor_y, 100);
    assert_eq!(ready.generation, 1);
}

#[test]
fn producer_schedules_and_completes_latest_multi_click_only() {
    let mut producer = MouseSelectionProducer::new();
    let context = MouseSelectionProducerContext::new(500);

    producer.process_low_level_input_event(mouse_event(WM_LBUTTONDOWN, 10, 10, 1_000), context);
    assert!(producer
        .process_low_level_input_event(mouse_event(WM_LBUTTONUP, 10, 10, 1_000), context)
        .is_empty());

    producer.process_low_level_input_event(mouse_event(WM_LBUTTONDOWN, 10, 10, 1_200), context);
    let second =
        producer.process_low_level_input_event(mouse_event(WM_LBUTTONUP, 10, 10, 1_200), context);
    let [MouseSelectionProducerAction::SchedulePendingMultiClick {
        pending,
        generation,
    }] = second.as_slice()
    else {
        panic!("double-click should schedule pending selection, got {second:?}");
    };
    assert_eq!(pending.click_count, 2);
    assert_eq!(*generation, 1);

    producer.process_low_level_input_event(mouse_event(WM_LBUTTONDOWN, 10, 10, 1_350), context);
    let third =
        producer.process_low_level_input_event(mouse_event(WM_LBUTTONUP, 10, 10, 1_350), context);
    assert_eq!(
        third.first(),
        Some(&MouseSelectionProducerAction::CancelPendingMultiClick { generation: 1 })
    );
    let Some(MouseSelectionProducerAction::SchedulePendingMultiClick {
        pending,
        generation,
    }) = third.get(1)
    else {
        panic!("triple-click should schedule a replacement pending selection, got {third:?}");
    };
    assert_eq!(pending.click_count, 3);
    assert_eq!(*generation, 2);

    assert_eq!(producer.complete_pending_multi_click(1), None);
    let Some(MouseSelectionProducerAction::CaptureSelectionText(request)) =
        producer.complete_pending_multi_click(2)
    else {
        panic!("latest pending generation should complete into capture request");
    };
    assert_eq!(request.generation, 2);
    assert_eq!(request.trigger.kind, MouseSelectionTriggerKind::MultiClick);
    assert_eq!(request.trigger.click_count, 3);
    assert_eq!(request.anchor_x(), 10);
    assert_eq!(request.anchor_y(), 10);
}

#[test]
fn producer_preserves_excluded_app_and_pop_button_click_suppression() {
    let mut producer = MouseSelectionProducer::new();
    let excluded = MouseSelectionProducerContext::new(500).current_app_excluded(true);

    producer.process_low_level_input_event(mouse_event(WM_LBUTTONDOWN, 1, 1, 1_000), excluded);
    producer.process_low_level_input_event(mouse_event(WM_MOUSEMOVE, 30, 1, 1_010), excluded);
    let drag =
        producer.process_low_level_input_event(mouse_event(WM_LBUTTONUP, 30, 1, 1_020), excluded);
    assert!(drag.is_empty(), "excluded app should suppress selection");

    let pop_button_click = MouseSelectionProducerContext::new(500).left_click_is_pop_button(true);
    let actions = producer
        .process_low_level_input_event(mouse_event(WM_LBUTTONDOWN, 5, 5, 2_000), pop_button_click);
    assert!(
        actions.is_empty(),
        "clicking the pop button itself should not request dismiss"
    );
}

fn mouse_event(message: u32, x: i32, y: i32, event_time_ms: u32) -> LowLevelInputHookEvent {
    LowLevelInputHookEvent::Mouse(LowLevelMouseHookEvent {
        message,
        x,
        y,
        mouse_data: 0,
        flags: 0,
        event_time_ms,
        extra_info: 0,
    })
}

fn keyboard_event(
    message: u32,
    virtual_key: u32,
    event_time_ms: u32,
    extra_info: isize,
) -> LowLevelInputHookEvent {
    LowLevelInputHookEvent::Keyboard(LowLevelKeyboardHookEvent {
        message,
        virtual_key,
        scan_code: 0,
        flags: 0,
        event_time_ms,
        extra_info,
    })
}
