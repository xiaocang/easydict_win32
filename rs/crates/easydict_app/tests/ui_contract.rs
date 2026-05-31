use easydict_app::{
    capture_overlay_view, capture_overlay_window_options, easydict_theme_tokens,
    fixed_window_options, fixed_window_view, main_window_options, main_window_view,
    mini_window_options, mini_window_view, pop_button_view, settings_view, EasydictUiState,
    PreviewScenario,
};
use std::fs;
use std::path::{Path, PathBuf};
use win_fluent::prelude::*;

#[test]
fn main_quick_translate_matches_current_xaml_surface() {
    let state = EasydictUiState::default();
    let snapshot = win_fluent_testkit::view_snapshot(&main_window_view(&state));

    assert!(snapshot.contains("Page title=\"Easydict\""));
    assert!(snapshot.contains("id=\"main.header\""));
    assert!(snapshot.contains("id=\"ModeMenuButton\""));
    assert!(snapshot.contains("id=\"ModeTitleText\""));
    assert!(snapshot
        .contains("Text value=\"Easydict\" style=Subtitle selectable=false id=\"ModeTitleText\""));
    assert!(snapshot.contains("quick:\"🌐  Translate\":Radio:checked=true"));
    assert!(snapshot.contains("id=\"SettingsButton\""));
    assert!(snapshot.contains("id=\"QuickInputCard\""));
    assert!(snapshot.contains("title=\"Source Text\""));
    assert!(snapshot.contains("id=\"InputTextBox\""));
    assert!(snapshot.contains("AdaptiveSwitch breakpoint_width=500"));
    assert!(snapshot.contains("id=\"SourceLangCombo\""));
    assert!(snapshot.contains("id=\"SourceLangComboNarrow\""));
    assert!(snapshot.contains("id=\"SwapLanguageButton\""));
    assert!(snapshot.contains("id=\"SwapLanguageButtonNarrow\""));
    assert!(snapshot.contains("id=\"TargetLangCombo\""));
    assert!(snapshot.contains("id=\"TargetLangComboNarrow\""));
    assert!(snapshot.contains("id=\"TranslateButton\""));
    assert!(snapshot.contains("id=\"TranslateButtonNarrow\""));
    assert!(snapshot.contains("id=\"QuickOutputCard\""));
    assert!(snapshot.contains("title=\"Translation Results\""));
    assert!(snapshot.contains("ResultList items=3"));
    assert!(snapshot.contains("selected=\"auto\""));
    assert!(snapshot.contains("selected=\"zh-Hans\""));
}

#[test]
fn floating_windows_keep_compact_translate_shape() {
    let state = EasydictUiState::default();
    let mini = win_fluent_testkit::view_snapshot(&mini_window_view(&state.mini));
    let fixed = win_fluent_testkit::view_snapshot(&fixed_window_view(&state.fixed));

    for snapshot in [mini, fixed] {
        assert!(snapshot.contains("Source Text"));
        assert!(snapshot.contains("TextEditor"));
        assert!(snapshot.contains("language_bar"));
        assert!(snapshot.contains("ResultList items=1"));
        assert!(snapshot.contains("Button label=\"Close\""));
    }

    let mini_options = mini_window_options();
    assert_eq!(mini_options.id.as_str(), "mini");
    assert_eq!(mini_options.width, 320.0);
    assert_eq!(mini_options.height, 200.0);
    assert_eq!(mini_options.level, WindowLevel::TopMost);
    assert_eq!(mini_options.frame, WindowFrame::Acrylic);
    assert!(mini_options.skip_taskbar);

    let fixed_options = fixed_window_options();
    assert_eq!(fixed_options.id.as_str(), "fixed");
    assert_eq!(fixed_options.width, 320.0);
    assert_eq!(fixed_options.height, 280.0);
    assert_eq!(fixed_options.level, WindowLevel::TopMost);
    assert_eq!(fixed_options.frame, WindowFrame::Acrylic);
    assert!(fixed_options.skip_taskbar);
}

#[test]
fn settings_view_keeps_category_tiles_and_general_behavior_rows() {
    let state = EasydictUiState::default();
    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert!(snapshot.contains("Page title=\"Settings\""));
    assert!(snapshot.contains("id=\"settings.categories\""));
    for section in [
        "General", "Services", "Views", "Hotkeys", "Advanced", "Language", "About",
    ] {
        assert!(
            snapshot.contains(&format!("label=\"{section}\"")),
            "missing settings category {section}"
        );
    }

    assert!(snapshot.contains("title=\"App Theme\""));
    assert!(snapshot.contains("title=\"Minimize to system tray\""));
    assert!(snapshot.contains("title=\"Start minimized to tray\""));
    assert!(snapshot.contains("title=\"Monitor clipboard for text\""));
    assert!(snapshot.contains("title=\"Mouse selection translate\""));
}

#[test]
fn long_document_mode_keeps_file_controls_output_and_history() {
    let mut state = EasydictUiState::default();
    state.mode = easydict_app::AppMode::LongDocument;

    let snapshot = win_fluent_testkit::view_snapshot(&main_window_view(&state));

    assert!(snapshot.contains("Text value=\"📄\""));
    assert!(snapshot
        .contains("Text value=\"Easydict\" style=Subtitle selectable=false id=\"ModeTitleText\""));
    assert!(snapshot.contains("Text value=\"Long Document\" style=Caption"));
    assert!(snapshot.contains("long-document:\"📄  Long Document\":Radio:checked=true"));
    assert!(snapshot.contains("title=\"📝 Source Text\""));
    assert!(snapshot.contains("title=\"⚡ Translation Result\""));
    assert!(snapshot.contains("title=\"📑 History\""));
    assert!(snapshot.contains("id=\"main.long-doc.input_card\""));
    assert!(snapshot.contains("id=\"main.long-doc.control_bar\""));
    assert!(snapshot.contains("id=\"main.long-doc.output_card\""));
    assert!(snapshot.contains("id=\"main.long-doc.history\""));
    assert!(snapshot.contains("selected=\"pdf\""));
    assert!(snapshot.contains("selected=\"bilingual\""));
    assert!(snapshot.contains("ToggleSwitch label=\"Use document context pass\""));
}

#[test]
fn main_window_preview_scenarios_cover_translation_states() {
    for scenario in PreviewScenario::ALL {
        let state = EasydictUiState::preview(scenario, ThemeMode::Light);
        let snapshot = win_fluent_testkit::view_snapshot(&main_window_view(&state));

        assert!(snapshot.contains("Page title=\"Easydict\""));
        assert!(snapshot.contains("id=\"ModeMenuButton\""));
        assert!(
            snapshot.contains("ResultList") || snapshot.contains("main.long-doc"),
            "scenario {:?} did not render a recognized main surface",
            scenario
        );
    }

    let loading = win_fluent_testkit::view_snapshot(&main_window_view(&EasydictUiState::preview(
        PreviewScenario::Loading,
        ThemeMode::Light,
    )));
    assert!(loading.contains("status=Loading"));
    assert!(loading.contains("status=Streaming"));
    assert!(loading.contains("ProgressRing active=true size=20"));

    let error = win_fluent_testkit::view_snapshot(&main_window_view(&EasydictUiState::preview(
        PreviewScenario::Error,
        ThemeMode::Light,
    )));
    assert!(error.contains("status=Error"));
    assert!(error.contains("pending_hint=\"Click to query this service\""));

    let overlay = win_fluent_testkit::view_snapshot(&main_window_view(&EasydictUiState::preview(
        PreviewScenario::ModeOverlay,
        ThemeMode::Light,
    )));
    assert!(overlay.contains("BusyOverlay active=true opacity=0.86"));
}

#[test]
fn easydict_theme_tokens_match_light_dark_minimal_contract() {
    let light = win_fluent_testkit::theme_snapshot(&easydict_theme_tokens(ThemeMode::Light));
    let dark = win_fluent_testkit::theme_snapshot(&easydict_theme_tokens(ThemeMode::Dark));
    let minimal = win_fluent_testkit::theme_snapshot(&easydict_theme_tokens(ThemeMode::Minimal));

    assert!(light.contains("background=#f7f9fc"));
    assert!(light.contains("result_header_hover=#f1f4f8"));
    assert!(light.contains("floating_action_surface=#f7fbff"));
    assert!(light.contains("floating_action_border=#7aa7d9"));
    assert!(light.contains("floating_action_rest_opacity=0.75"));
    assert!(light.contains("result_action_button=24"));
    assert!(light.contains("primary_round_button=40"));
    assert!(dark.contains("background=#1f2229"));
    assert!(dark.contains("result_header_hover=#323946"));
    assert!(dark.contains("floating_action_rest_opacity=0.94"));
    assert!(minimal.contains("background=#ffffff"));
    assert!(minimal.contains("radius_control=0"));
    assert!(minimal.contains("floating_action_rest_opacity=1"));
}

#[test]
fn minimal_theme_reduces_decorative_chrome_without_losing_controls() {
    let state = EasydictUiState::preview(PreviewScenario::Initial, ThemeMode::Minimal);
    let snapshot = win_fluent_testkit::view_snapshot(&main_window_view(&state));

    assert!(!snapshot.contains("Text value=\"🌐\""));
    assert!(!snapshot.contains("🌐  Translate"));
    assert!(!snapshot.contains("id=\"main.quick.play_source\""));
    assert!(!snapshot.contains("id=\"LanguageHelpButton\""));
    assert!(!snapshot.contains("service(s) completed"));
    assert!(snapshot.contains("id=\"ModeMenuButton\""));
    assert!(snapshot.contains("id=\"TranslateButton\""));
}

#[test]
fn capture_and_pop_button_match_utility_window_contracts() {
    let capture = win_fluent_testkit::view_snapshot(&capture_overlay_view());
    assert!(capture.contains("Page title=\"Capture Overlay\""));
    assert!(capture.contains("Button label=\"Confirm\""));
    assert!(capture.contains("Button label=\"Cancel\""));

    let pop = win_fluent_testkit::view_snapshot(&pop_button_view());
    assert!(pop.contains("Page title=\"Selection Translate\""));
    assert!(pop.contains("Button label=\"Translate selection\""));
    assert!(pop.contains("kind=FloatingAction"));
    assert!(pop.contains("icon=translate"));

    let capture_options = capture_overlay_window_options();
    assert_eq!(capture_options.level, WindowLevel::TopMost);
    assert_eq!(capture_options.frame, WindowFrame::Borderless);
    assert!(capture_options.skip_taskbar);
}

#[test]
fn all_reference_views_pass_accessibility_audit() {
    let state = EasydictUiState::default();
    let views = [
        main_window_view(&state),
        settings_view(&state.settings),
        mini_window_view(&state.mini),
        fixed_window_view(&state.fixed),
        capture_overlay_view(),
        pop_button_view(),
    ];

    for view in views {
        let audit = win_fluent_testkit::accessibility_audit(&view);
        assert!(audit.passed(), "{:?}", audit.issues);
    }
}

#[test]
fn app_views_do_not_depend_on_backend_types() {
    let state = EasydictUiState::default();
    let snapshots = [
        win_fluent_testkit::view_snapshot(&main_window_view(&state)),
        win_fluent_testkit::view_snapshot(&settings_view(&state.settings)),
        win_fluent_testkit::view_snapshot(&mini_window_view(&state.mini)),
        win_fluent_testkit::view_snapshot(&fixed_window_view(&state.fixed)),
    ];

    for snapshot in snapshots {
        assert!(!snapshot.contains("iced"));
        assert!(!snapshot.contains("windows::"));
        assert!(!snapshot.contains("Win32"));
        assert!(!snapshot.contains("HWND"));
        assert!(!snapshot.contains("COM"));
    }
}

#[test]
fn app_crate_source_does_not_call_backend_or_native_apis() {
    let manifest_dir = Path::new(env!("CARGO_MANIFEST_DIR"));
    let mut files = vec![manifest_dir.join("Cargo.toml")];
    collect_rust_files(&manifest_dir.join("src"), &mut files);

    for file in files {
        let content = fs::read_to_string(&file).expect("source file must be readable");
        for forbidden in [
            "iced",
            "windows::",
            "windows_sys",
            "winapi",
            "user32",
            "dwmapi",
            "HWND",
            "Win32",
        ] {
            assert!(
                !content.contains(forbidden),
                "{} must not reference backend/native API marker {forbidden:?}",
                file.display()
            );
        }
    }
}

#[test]
fn win_fluent_crates_do_not_contain_app_specific_names() {
    let manifest_dir = Path::new(env!("CARGO_MANIFEST_DIR"));
    let repo_root = manifest_dir
        .ancestors()
        .nth(3)
        .expect("crate must live under repo/rs/crates/easydict_app");
    let winfluent_root = repo_root.join("lib").join("winfluent-rs");
    let mut files = Vec::new();
    collect_rust_files(&winfluent_root.join("crates"), &mut files);
    files.push(winfluent_root.join("README.md"));

    for file in files {
        if !file.exists() {
            continue;
        }
        let content = fs::read_to_string(&file).expect("win_fluent file must be readable");
        assert!(
            !content.to_ascii_lowercase().contains("easydict"),
            "{} must not contain app-specific Easydict names",
            file.display()
        );
    }
}

#[test]
fn main_window_keeps_saved_default_size_contract() {
    let options = main_window_options();
    assert_eq!(options.id.as_str(), "main");
    assert_eq!(options.width, 940.0);
    assert_eq!(options.height, 1220.0);
    assert_eq!(options.min_width, Some(640.0));
    assert_eq!(options.min_height, Some(720.0));
    assert_eq!(options.frame, WindowFrame::Borderless);
}

fn collect_rust_files(dir: &Path, files: &mut Vec<PathBuf>) {
    for entry in fs::read_dir(dir).expect("source directory must be readable") {
        let entry = entry.expect("source directory entry must be readable");
        let path = entry.path();

        if path.is_dir() {
            collect_rust_files(&path, files);
        } else if path.extension().is_some_and(|extension| extension == "rs") {
            files.push(path);
        }
    }
}
