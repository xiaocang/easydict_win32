use easydict_app::{
    capture_overlay_view, fixed_window_view, main_window_view, mini_window_view, pop_button_view,
    settings_view, EasydictUiState,
};
use win_fluent::view_schema;

fn main() {
    let state = EasydictUiState::default();

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
            view_schema(&capture_overlay_view()).snapshot(),
        ),
        ("pop-button", view_schema(&pop_button_view()).snapshot()),
    ] {
        println!("== {name} ==");
        println!("{snapshot}");
    }
}
