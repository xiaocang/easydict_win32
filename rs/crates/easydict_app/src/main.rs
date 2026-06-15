use easydict_app::{
    capture_overlay_view_with_state, fixed_window_view, main_window_view, mini_window_view,
    pop_button_view_with_state, preview_control_state_from_id, settings_view, EasydictUiState,
    PreviewScenario,
};
use win_fluent::view_schema;

fn main() {
    let state = EasydictUiState::preview_from_env();

    for (name, snapshot) in [
        ("main", view_schema(&main_window_view(&state)).snapshot()),
        (
            "settings",
            view_schema(&settings_view(&state.settings)).snapshot(),
        ),
        (
            "mini",
            view_schema(&mini_window_view(&state.mini)).snapshot(),
        ),
        (
            "fixed",
            view_schema(&fixed_window_view(&state.fixed)).snapshot(),
        ),
        (
            "capture-overlay",
            view_schema(&capture_overlay_view_with_state(
                &state.capture_interaction,
                state.capture_selection,
                state.capture_background.as_ref(),
            ))
            .snapshot(),
        ),
        (
            "pop-button",
            view_schema(&pop_button_view_with_state(pop_button_preview_state())).snapshot(),
        ),
    ] {
        println!("== {name} ==");
        println!("{snapshot}");
    }

    for scenario in PreviewScenario::ALL {
        let state = EasydictUiState::preview(scenario, state.settings.theme);
        println!("== main:{} ==", scenario.id());
        println!("{}", view_schema(&main_window_view(&state)).snapshot());
    }
}

fn pop_button_preview_state() -> win_fluent::state::ControlState {
    std::env::var("EASYDICT_PREVIEW_POPBUTTON_STATE")
        .ok()
        .map(|value| preview_control_state_from_id(&value))
        .unwrap_or_default()
}
