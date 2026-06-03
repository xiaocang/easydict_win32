use easydict_app::{
    capture_overlay_view, capture_overlay_view_with_state, capture_overlay_window_options,
    easydict_theme_tokens, fixed_window_options, fixed_window_view, main_window_options,
    main_window_view, mini_window_options, mini_window_view, pop_button_view,
    pop_button_view_with_state, pop_button_window_options, settings_view, CaptureInteractionState,
    CapturePhase, CaptureRect, EasydictUiState, GrammarCorrectionPreview, PreviewScenario,
    QuickTranslateSurface, SettingsLink, TranslationResultPreview, HOTKEY_SHOW_MAIN,
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
    assert_control_contains(&snapshot, "QuickInputCard", "kind=Elevated");
    assert!(snapshot.contains("title=\"Source Text\""));
    assert_control_contains(&snapshot, "main.quick.source_content", "width=Fill");
    assert!(snapshot.contains("id=\"InputTextBox\""));
    assert_control_contains(&snapshot, "InputTextBox", "key_bindings=Enter");
    assert_control_contains(&snapshot, "InputTextBox", "focused=true");
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
    assert_control_contains(&snapshot, "QuickOutputCard", "kind=Elevated");
    assert!(snapshot.contains("title=\"Translation Results\""));
    assert!(snapshot.contains("ResultList items=3"));
    assert_control_contains(&snapshot, "google", "actions_visible=false");
    assert_control_contains(
        &snapshot,
        "main.quick.results",
        "collapse_transition_ms=100",
    );
    assert!(snapshot.contains("copy=selection_input"));
    assert!(snapshot.contains("speak=selection_input"));
    assert!(snapshot.contains("replace=selection_input"));
    assert!(snapshot.contains("retry=selection_input"));
    assert!(snapshot.contains("selected=\"auto\""));
    for language in [
        "ar:\"Arabic\"",
        "da:\"Danish\"",
        "de:\"German\"",
        "en:\"English\"",
        "es:\"Spanish\"",
        "fr:\"French\"",
        "hi:\"Hindi\"",
        "id:\"Indonesian\"",
        "it:\"Italian\"",
        "ja:\"Japanese\"",
        "ko:\"Korean\"",
        "ms:\"Malay\"",
        "th:\"Thai\"",
        "vi:\"Vietnamese\"",
        "zh-Hans:\"Chinese (Simplified)\"",
        "zh-Hant:\"Chinese (Traditional)\"",
    ] {
        assert!(
            snapshot.contains(language),
            "missing language picker item {language}"
        );
    }
}

#[test]
fn floating_windows_keep_compact_translate_shape() {
    let state = EasydictUiState::default();
    let mini = win_fluent_testkit::view_snapshot(&mini_window_view(&state.mini));
    let fixed = win_fluent_testkit::view_snapshot(&fixed_window_view(&state.fixed));

    for (prefix, snapshot) in [("mini", &mini), ("fixed", &fixed)] {
        assert!(snapshot.contains("kind=FloatingInput"));
        assert!(snapshot.contains("language_bar"));
        assert_control_contains(
            snapshot,
            &format!("{prefix}.source_language"),
            "width=Fixed(96)",
        );
        assert_control_contains(
            snapshot,
            &format!("{prefix}.target_language"),
            "width=Fixed(96)",
        );
        assert_control_contains(snapshot, &format!("{prefix}.swap"), "width=Fixed(28)");
        assert_control_contains(snapshot, &format!("{prefix}.swap"), "height=Fixed(28)");
        assert_control_contains(snapshot, &format!("{prefix}.close"), "width=Fixed(28)");
        assert_control_contains(snapshot, &format!("{prefix}.close"), "height=Fixed(28)");
        assert!(snapshot.contains("auto:\"Auto\""));
        assert!(snapshot.contains("zh-Hans:\"Chinese\""));
        assert!(!snapshot.contains("auto:\"Auto Detect\""));
        assert!(!snapshot.contains("zh-Hans:\"Chinese (Simplified)\""));
        assert!(snapshot.contains("kind=FloatingAction"));
        assert!(snapshot.contains("ResultList items=1"));
        assert!(snapshot.contains("Button label=\"Close\""));
    }
    assert_control_contains(&mini, "mini.input", "Text value=\"Oh, I am mini window\"");
    assert_control_contains(&mini, "mini.input", "style=Body");
    assert_control_contains(&fixed, "fixed.input", "chrome=Frameless");
    assert_control_contains(&fixed, "fixed.input", "min_height=40");
    assert_control_contains(&fixed, "fixed.input", "max_height=120");
    assert!(mini.contains("id=\"mini.play_source\""));
    assert!(!fixed.contains("id=\"fixed.play_source\""));
    assert!(!mini.contains("Text value=\"?\""));
    assert!(!fixed.contains("Text value=\"?\""));
    assert!(mini.contains("id=\"mini.detected_language_placeholder\""));
    assert!(fixed.contains("id=\"fixed.detected_language_placeholder\""));
    assert!(!mini.contains("Detected: English"));
    assert!(!fixed.contains("Detected: English"));
    assert_control_contains(&mini, "mini.results", "collapse_transition_ms=100");
    assert_control_contains(&fixed, "fixed.results", "collapse_transition_ms=100");

    let mut hover_state = EasydictUiState::default();
    hover_state.mini.translate_button_state = ControlState::default().hovered(true);
    let mini_hover = win_fluent_testkit::view_snapshot(&mini_window_view(&hover_state.mini));
    assert_control_contains(&mini_hover, "mini.translate", "kind=FloatingAction");
    assert_control_contains(&mini_hover, "mini.translate", "hovered=true");
    assert_control_contains(&mini_hover, "mini.translate", "pressed=false");

    let mut pressed_state = EasydictUiState::default();
    pressed_state.fixed.translate_button_state =
        ControlState::default().hovered(true).pressed(true);
    let fixed_pressed = win_fluent_testkit::view_snapshot(&fixed_window_view(&pressed_state.fixed));
    assert_control_contains(&fixed_pressed, "fixed.translate", "kind=FloatingAction");
    assert_control_contains(&fixed_pressed, "fixed.translate", "hovered=true");
    assert_control_contains(&fixed_pressed, "fixed.translate", "pressed=true");

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
fn grammar_result_preview_renders_corrected_text_and_explanation() {
    let result = TranslationResultPreview::new("openai", "OpenAI", "I have an apple.")
        .grammar_capable(true)
        .latency_ms(80);
    let mut result = result;
    result.query_mode = easydict_app::QuickQueryMode::GrammarCorrection;
    result.grammar_result = Some(GrammarCorrectionPreview::new(
        "I has a apple.",
        "I have an apple.",
        Some("Use have with I and an before apple.".to_string()),
        true,
    ));

    let item = result.to_result_item();

    assert!(item.body.contains("Corrected\nI have an apple."));
    assert!(item
        .body
        .contains("Explanation\nUse have with I and an before apple."));
    assert_eq!(item.metadata.as_deref(), Some("Grammar - 80ms"));
}

#[test]
fn settings_view_keeps_category_tiles_and_general_behavior_rows() {
    let state = EasydictUiState::default();
    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert!(snapshot.contains("Page title=\"Settings\""));
    assert!(snapshot.contains("TitleBar title=\"Easydict\""));
    assert!(snapshot.contains("id=\"BackButton\""));
    assert_control_contains(&snapshot, "BackButton", "kind=Primary");
    assert_control_contains(&snapshot, "BackButton", "label=\"\"");
    assert_control_contains(&snapshot, "BackButton", "icon=back");
    assert!(snapshot.contains("id=\"SettingsHeaderText\""));
    assert_control_contains(&snapshot, "SettingsHeaderText", "style=Title");
    assert!(snapshot.contains("id=\"MainScrollViewer\""));
    // The settings content must be capped at 1040 dips and centered — asserted
    // structurally (not via the raw style string) so a silently-dropped
    // `max-w-[1040px] mx-auto` cannot pass this test, unlike the original bug.
    assert_control_contains(&snapshot, "settings.content", "max_width=1040");
    assert_control_contains(&snapshot, "settings.content", "center_x=true");
    assert!(snapshot.contains("id=\"settings.categories\""));
    // Tab tiles are arranged by the framework wrap primitive (7-column cap),
    // not a hand-rolled fixed row split.
    assert_control_contains(&snapshot, "settings.categories", "max_columns=7");
    assert!(snapshot.contains("id=\"SettingsTabSwitchRing\""));
    assert_control_contains(&snapshot, "SettingsTabSwitchRing", "active=false");
    assert_control_contains(&snapshot, "SettingsTabSwitchRing", "size=20");
    for section in [
        "General", "Services", "Views", "Hotkeys", "Advanced", "Language", "About",
    ] {
        assert!(
            snapshot.contains(&format!("label=\"{section}\"")),
            "missing settings category {section}"
        );
        assert!(
            snapshot.contains(&format!("id=\"SettingsTab_{section}\"")),
            "missing settings tab automation id for {section}"
        );
        assert_control_contains(
            &snapshot,
            &format!("SettingsTab_{section}"),
            &format!("tooltip=\"{section}\""),
        );
        assert_control_contains(&snapshot, &format!("SettingsTab_{section}"), "kind=Tile");
        assert_control_contains(
            &snapshot,
            &format!("SettingsTab_{section}"),
            "width=Fixed(128)",
        );
        assert_control_contains(
            &snapshot,
            &format!("SettingsTab_{section}"),
            "height=Fixed(112)",
        );
    }

    // The active tab carries a persistent `selected` state (not keyboard focus),
    // which drives the themed selected-tab surface.
    assert_control_contains(&snapshot, "SettingsTab_General", "selected=true");
    assert_control_contains(&snapshot, "SettingsTab_Services", "selected=false");
    assert_control_contains(&snapshot, "SettingsTab_Services", "hovered=false");
    assert_control_contains(&snapshot, "SettingsTab_Services", "pressed=false");
    assert_control_contains(&snapshot, "settings.general", "spacing=0");
    assert_control_contains(&snapshot, "GeneralTabContent", "spacing=24");
    assert!(snapshot.contains("id=\"BehaviorSection\""));
    assert!(snapshot.contains("id=\"TtsSettingsSection\""));

    let mut hover_state = state.clone();
    hover_state.settings.hovered_section = Some(easydict_app::SettingsSection::Services);
    let hover_snapshot = win_fluent_testkit::view_snapshot(&settings_view(&hover_state.settings));
    assert_control_contains(&hover_snapshot, "SettingsTab_Services", "kind=Tile");
    assert_control_contains(&hover_snapshot, "SettingsTab_Services", "hovered=true");
    assert_control_contains(&hover_snapshot, "SettingsTab_Services", "pressed=false");
    assert_control_contains(&hover_snapshot, "SettingsTab_Services", "selected=false");

    let mut pressed_state = state.clone();
    pressed_state.settings.hovered_section = Some(easydict_app::SettingsSection::Views);
    pressed_state.settings.pressed_section = Some(easydict_app::SettingsSection::Views);
    let pressed_snapshot =
        win_fluent_testkit::view_snapshot(&settings_view(&pressed_state.settings));
    assert_control_contains(&pressed_snapshot, "SettingsTab_Views", "hovered=true");
    assert_control_contains(&pressed_snapshot, "SettingsTab_Views", "pressed=true");
    assert_control_contains(&pressed_snapshot, "SettingsTab_Views", "selected=false");

    let mut switching_state = state.clone();
    switching_state.settings.tab_switching = true;
    let switching_snapshot =
        win_fluent_testkit::view_snapshot(&settings_view(&switching_state.settings));
    assert_control_contains(&switching_snapshot, "SettingsTabSwitchRing", "active=true");
    assert_control_contains(&switching_snapshot, "SettingsTabSwitchRing", "size=20");

    assert!(snapshot.contains("title=\"App Theme\""));
    assert!(snapshot.contains("id=\"AppThemeCombo\""));
    assert!(snapshot.contains("id=\"AppThemeDescriptionText\""));
    assert!(!snapshot.contains("High Contrast"));
    assert!(snapshot.contains("title=\"Minimize to system tray\""));
    assert!(snapshot.contains("id=\"MinimizeToTrayToggle\""));
    assert!(snapshot.contains("title=\"Start minimized to tray\""));
    assert!(snapshot.contains("id=\"MinimizeToTrayOnStartupToggle\""));
    assert!(snapshot.contains("title=\"Monitor clipboard for text\""));
    assert!(snapshot.contains("id=\"ClipboardMonitorToggle\""));
    assert!(snapshot.contains("title=\"Always on top\""));
    assert!(snapshot.contains("id=\"AlwaysOnTopToggle\""));
    assert!(snapshot.contains("title=\"Launch at Windows startup\""));
    assert!(snapshot.contains("id=\"LaunchAtStartupToggle\""));
    assert!(snapshot.contains("title=\"Mouse selection translate\""));
    assert!(snapshot.contains("id=\"SettingsGeneralBehaviorHeader\""));
    assert!(snapshot.contains("id=\"MouseSelectionTranslateToggle\""));
    assert!(snapshot.contains("title=\"Enable custom dictionary input suggestions\""));
    assert!(snapshot.contains("id=\"EnableLocalDictionarySuggestionsLabelText\""));
    assert!(snapshot.contains("id=\"ExperimentalLabelText\""));
    assert!(snapshot.contains("id=\"EnableLocalDictionarySuggestionsHintText\""));
    assert_control_enabled(&snapshot, "EnableLocalDictionarySuggestionsToggle", false);
    assert!(snapshot.contains("title=\"Hide dictionaries with no result\""));
    assert!(snapshot.contains("id=\"HideEmptyServiceResultsToggle\""));
    assert!(snapshot.contains("id=\"TtsSettingsHeaderText\""));
    assert!(snapshot.contains("id=\"TtsSpeedLabelText\""));
    assert!(snapshot.contains("id=\"TtsSpeedSlider\""));
    assert_control_contains(&snapshot, "TtsSpeedSlider", "Slider");
    assert_control_contains(&snapshot, "TtsSpeedSlider", "value=1.00");
    assert_control_contains(&snapshot, "TtsSpeedSlider", "min=0.50");
    assert_control_contains(&snapshot, "TtsSpeedSlider", "max=3.00");
    assert_control_contains(&snapshot, "TtsSpeedSlider", "step=0.50");
    assert_control_contains(&snapshot, "TtsSpeedSlider", "action=number_input");
    assert_control_contains(&snapshot, "TtsSpeedSlider", "hovered=false");
    assert_control_contains(&snapshot, "TtsSpeedSlider", "pressed=false");
    assert!(!snapshot.contains("ComboBox id=\"TtsSpeedSlider\""));
    assert!(snapshot.contains("id=\"TtsSpeedValueText\""));
    assert!(snapshot.contains("id=\"AutoPlayTranslationToggle\""));
    assert_control_contains(&snapshot, "AutoPlayTranslationToggle", "hovered=false");
    assert_control_contains(&snapshot, "AutoPlayTranslationToggle", "pressed=false");
    assert!(!snapshot.contains("id=\"SaveButton\""));
    assert!(snapshot.contains("id=\"SettingsBottomSpacer\""));

    let mut slider_hover_state = state.clone();
    slider_hover_state.settings.tts_speed_slider_state = ControlState::default().hovered(true);
    let slider_hover_snapshot =
        win_fluent_testkit::view_snapshot(&settings_view(&slider_hover_state.settings));
    assert_control_contains(&slider_hover_snapshot, "TtsSpeedSlider", "hovered=true");
    assert_control_contains(&slider_hover_snapshot, "TtsSpeedSlider", "pressed=false");

    let mut toggle_pressed_state = state.clone();
    toggle_pressed_state
        .settings
        .auto_play_translation_toggle_state = ControlState::default().hovered(true).pressed(true);
    let toggle_pressed_snapshot =
        win_fluent_testkit::view_snapshot(&settings_view(&toggle_pressed_state.settings));
    assert_control_contains(
        &toggle_pressed_snapshot,
        "AutoPlayTranslationToggle",
        "hovered=true",
    );
    assert_control_contains(
        &toggle_pressed_snapshot,
        "AutoPlayTranslationToggle",
        "pressed=true",
    );

    let mut dirty_state = EasydictUiState::default();
    dirty_state.settings.unsaved_changes = true;
    let dirty_snapshot = win_fluent_testkit::view_snapshot(&settings_view(&dirty_state.settings));
    assert!(dirty_snapshot.contains("id=\"SaveButton\""));
    assert_control_contains(&dirty_snapshot, "SaveButton", "label=\"Save Settings\"");
    // The save bar floats over the content as an overlay layer (bottom-right,
    // no scrim, pass-through), rather than being a scroll sibling.
    assert_control_contains(&dirty_snapshot, "settings.root", "layers=1");
    assert_control_contains(
        &dirty_snapshot,
        "settings.root",
        "End/End/scrim=None/block=false",
    );
}

#[test]
fn general_settings_mouse_selection_excluded_apps_panel_tracks_toggle() {
    let mut state = EasydictUiState::default();

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert!(snapshot.contains("id=\"MouseSelectionTranslateToggle\""));
    assert_control_contains(&snapshot, "MouseSelectionTranslateToggle", "checked=false");
    assert!(!snapshot.contains("id=\"MouseSelectionExcludedAppsPanel\""));
    assert!(!snapshot.contains("id=\"MouseSelectionExcludedAppsBox\""));

    state.apply(easydict_app::Message::ToggleMouseSelectionTranslate(true));

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert_control_contains(&snapshot, "MouseSelectionTranslateToggle", "checked=true");
    assert!(snapshot.contains("id=\"MouseSelectionExcludedAppsPanel\""));
    assert!(snapshot.contains("id=\"MouseSelectionExcludedAppsBox\""));
    assert_control_contains(&snapshot, "MouseSelectionExcludedAppsBox", "text_len=4");
    assert_control_contains(
        &snapshot,
        "MouseSelectionExcludedAppsBox",
        "action=text_input",
    );
    assert!(snapshot.contains("id=\"MouseSelectionExcludedAppsDescriptionText\""));
    assert!(snapshot.contains("Process names to exclude, separated by commas."));

    state.apply(easydict_app::Message::MouseSelectionExcludedAppsChanged(
        "code, slack".to_string(),
    ));

    assert_eq!(state.settings.mouse_selection_excluded_apps, "code, slack");
    assert!(state.settings.unsaved_changes);
}

#[test]
fn settings_scroll_view_exposes_selected_tab_help_text_hook() {
    let mut state = EasydictUiState::default();
    let a11y = win_fluent_testkit::accessibility_snapshot(&settings_view(&state.settings));
    // UIA hook mirroring WinUI MainScrollViewer.HelpText, reflecting the section.
    assert!(
        a11y.contains("help_text=Some(\"SelectedSettingsTab:general\")"),
        "missing selected-tab help text hook\n{a11y}"
    );

    state.settings.selected_section = easydict_app::SettingsSection::Advanced;
    let a11y = win_fluent_testkit::accessibility_snapshot(&settings_view(&state.settings));
    assert!(a11y.contains("help_text=Some(\"SelectedSettingsTab:advanced\")"));
}

#[test]
fn settings_view_shows_loading_overlay_while_runtime_status_loads() {
    let mut state = EasydictUiState::default();
    assert!(
        !win_fluent_testkit::view_snapshot(&settings_view(&state.settings))
            .contains("id=\"SettingsLoadingRing\"")
    );

    state.settings.settings_runtime = win_fluent::Loadable::Loading;
    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    // Centered 32px ring hosted as an input-blocking, scrimmed overlay layer.
    assert!(snapshot.contains("id=\"SettingsLoadingRing\""));
    assert_control_contains(&snapshot, "SettingsLoadingRing", "size=32");
    assert_control_contains(
        &snapshot,
        "settings.root",
        "Center/Center/scrim=Some(0.3)/block=true",
    );
}

#[test]
fn settings_view_renders_unsaved_changes_dialog_contract() {
    let mut state = EasydictUiState::default();
    state.settings.unsaved_changes = true;
    state.settings.show_unsaved_changes_dialog = true;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert!(snapshot.contains("Dialog title=\"Unsaved Settings\" kind=Confirmation"));
    // The dialog is hosted as a centered, scrimmed, input-blocking modal layer.
    assert_control_contains(
        &snapshot,
        "settings.root",
        "Center/Center/scrim=Some(0.4)/block=true",
    );
    assert!(snapshot.contains("id=\"settings.unsaved_dialog\""));
    assert!(snapshot.contains("Text value=\"Save your settings changes before leaving?\""));
    assert!(snapshot.contains("id=\"settings.unsaved.save\""));
    assert!(snapshot.contains("Button label=\"Save\""));
    assert!(snapshot.contains("id=\"settings.unsaved.discard\""));
    assert!(snapshot.contains("Button label=\"Don't Save\""));
    assert!(snapshot.contains("id=\"settings.unsaved.cancel\""));
    assert!(snapshot.contains("Button label=\"Cancel\""));
}

#[test]
fn advanced_settings_shell_context_menu_toggle_reflects_state() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Advanced;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert!(snapshot.contains("id=\"settings.advanced.shell\""));
    assert!(snapshot.contains("ToggleSwitch label=\"Enabled\" checked=false"));

    state.settings.shell_context_menu = true;
    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert!(snapshot.contains("ToggleSwitch label=\"Enabled\" checked=true"));
}

#[test]
fn advanced_settings_browser_extension_buttons_are_actionable() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Advanced;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert!(snapshot.contains("id=\"settings.advanced.browser\""));
    assert!(snapshot.contains("Button label=\"Install\""));
    assert!(snapshot.contains("Button label=\"Uninstall\""));
}

#[test]
fn advanced_settings_render_ocr_layout_cache_prompt_and_proxy_controls() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Advanced;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    for id in [
        "OcrEngineCombo",
        "OcrLanguageCombo",
        "LayoutDetectionModeCombo",
        "DownloadLayoutModelButton",
        "DeleteLayoutModelButton",
        "LayoutModelStatusText",
        "DownloadCjkFontButton",
        "DeleteCjkFontButton",
        "CjkFontStatusText",
        "FormulaFontPatternBox",
        "FormulaCharPatternBox",
        "TranslationCacheToggle",
        "ClearCacheButton",
        "TranslationCacheStatusText",
        "CustomTranslationPromptBox",
        "ProxyEnabledToggle",
        "ProxyUriBox",
        "ProxyBypassLocalToggle",
    ] {
        assert!(
            snapshot.contains(&format!("id=\"{id}\"")),
            "missing Advanced control {id}\n{snapshot}"
        );
    }

    assert!(snapshot.contains("WindowsNative:\"Default / Windows Native\""));
    assert!(snapshot.contains("id=\"settings.advanced.ocr.language\""));
    assert_control_contains(&snapshot, "OcrLanguageCombo", "selected=\"auto\"");
    assert!(snapshot.contains("OnnxLocal:\"Local ONNX Model\""));
    assert!(snapshot.contains("VisionLLM:\"Vision LLM\""));
    assert_control_enabled(&snapshot, "ProxyUriBox", false);
    assert_control_contains(&snapshot, "CustomTranslationPromptBox", "min_height=120");
    assert!(!snapshot.contains("id=\"OcrEndpointBox\""));

    state.apply(easydict_app::Message::OcrEngineChanged(
        "CustomApi".to_string(),
    ));
    state.apply(easydict_app::Message::OcrApiKeyChanged(
        "ocr-key".to_string(),
    ));
    state.apply(easydict_app::Message::OcrEndpointChanged(
        "https://ocr.example.test/v1/responses".to_string(),
    ));
    state.apply(easydict_app::Message::OcrModelChanged(
        "gpt-vision".to_string(),
    ));
    state.apply(easydict_app::Message::OcrSystemPromptChanged(
        "Extract text.".to_string(),
    ));
    state.settings.selected_section = easydict_app::SettingsSection::Advanced;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    for id in [
        "OcrApiKeyBox",
        "OcrApiKeyRevealButton",
        "OcrEndpointBox",
        "OcrModelBox",
        "OcrSystemPromptBox",
        "TestOcrConnectionButton",
        "OcrTestResultBox",
    ] {
        assert!(snapshot.contains(&format!("id=\"{id}\"")));
    }
    let settings = easydict_app::state::settings_snapshot(&state.settings);
    assert_eq!(settings.ocr_engine.as_deref(), Some("CustomApi"));
    assert_eq!(settings.ocr_api_key.as_deref(), Some("ocr-key"));
    assert_eq!(
        settings.ocr_endpoint.as_deref(),
        Some("https://ocr.example.test/v1/responses")
    );
    assert_eq!(settings.ocr_model.as_deref(), Some("gpt-vision"));
    assert_eq!(settings.ocr_system_prompt.as_deref(), Some("Extract text."));

    state.apply(easydict_app::Message::OcrLanguageChanged("ja".to_string()));
    assert_eq!(state.settings.ocr_language, "ja");
    assert!(state.settings.unsaved_changes);
    let settings = easydict_app::state::settings_snapshot(&state.settings);
    assert_eq!(settings.ocr_language.as_deref(), Some("ja"));

    state.apply(easydict_app::Message::TestOcrConnection);
    assert!(state
        .settings
        .ocr_test_result
        .contains("Connection test requested for Custom API"));

    state.apply(easydict_app::Message::LayoutDetectionModeChanged(
        "VisionLLM".to_string(),
    ));
    state.settings.selected_section = easydict_app::SettingsSection::Advanced;
    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert!(snapshot.contains("id=\"VisionLayoutServiceCombo\""));
    assert!(!snapshot.contains("id=\"DownloadLayoutModelButton\""));

    state.settings_open = true;
    state.apply(easydict_app::Message::ToggleProxyEnabled(true));
    state.apply(easydict_app::Message::ProxyUrlChanged(
        "localhost".to_string(),
    ));
    state.apply(easydict_app::Message::SaveSettingsChanges);

    assert!(state.settings_open);
    assert!(state.settings.unsaved_changes);
    assert_eq!(
        state.settings.save_error_message.as_deref(),
        Some("Proxy URL must be an absolute URI.")
    );

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert!(snapshot.contains("Dialog title=\"Settings Error\" kind=Error"));
    assert!(snapshot.contains("id=\"settings.error.ok\""));

    state.apply(easydict_app::Message::DismissSettingsError);
    state.apply(easydict_app::Message::ProxyUrlChanged(
        "http://127.0.0.1:7890".to_string(),
    ));
    state.apply(easydict_app::Message::SaveSettingsChanges);

    assert_eq!(state.settings.save_error_message, None);
    assert!(!state.settings_open);
}

#[test]
fn services_settings_deepl_expander_exposes_configuration_controls() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert!(snapshot.contains("id=\"DeepLServiceExpander\""));
    assert_control_contains(&snapshot, "DeepLServiceExpander", "kind=Expander");
    assert_control_contains(
        &snapshot,
        "DeepLServiceExpander",
        "description=\"Free API mode\"",
    );
    assert!(snapshot.contains("id=\"DeepLKeyBox\""));
    assert_control_contains(&snapshot, "DeepLKeyBox", "action=text_input");
    assert!(snapshot.contains("id=\"DeepLKeyRevealButton\""));
    assert!(snapshot.contains("id=\"DeepLFreeCheck\""));
    assert_control_contains(&snapshot, "DeepLFreeCheck", "checked=true");
    assert!(snapshot.contains("id=\"DeepLQualityCheck\""));
    assert_control_contains(&snapshot, "DeepLQualityCheck", "checked=false");
    assert!(snapshot.contains("id=\"DeepLDescriptionText\""));
    assert!(snapshot.contains("id=\"TestDeepLButton\""));

    state.apply(easydict_app::Message::ToggleDeepLUseQualityOptimized(true));
    state.apply(easydict_app::Message::DeepLApiKeyChanged(
        "secret-key".to_string(),
    ));
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    assert!(!state.settings.deepl_use_free_api);
    assert!(state.settings.deepl_use_quality_optimized);
    assert_eq!(state.settings.deepl_api_key, "secret-key");
    assert!(state.settings.unsaved_changes);

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert_control_contains(
        &snapshot,
        "DeepLServiceExpander",
        "description=\"Quality-optimized mode\"",
    );
    assert_control_contains(&snapshot, "DeepLFreeCheck", "checked=false");
    assert_control_enabled(&snapshot, "DeepLFreeCheck", false);
    assert_control_contains(&snapshot, "DeepLQualityCheck", "checked=true");
}

#[test]
fn services_settings_local_ai_exposes_provider_configuration() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    for id in [
        "WindowsLocalAIExpander",
        "WindowsLocalAIStatusBadge",
        "LocalAIProviderLabelText",
        "LocalAIProviderCombo",
        "LocalAIProviderWindowsAIItem",
        "LocalAIProviderWindowsAIRatingText",
        "LocalAIProviderFoundryLocalItem",
        "LocalAIProviderFoundryLocalRatingText",
        "LocalAIProviderOpenVINOItem",
        "LocalAIProviderOpenVINORatingText",
        "WindowsLocalAIDescriptionText",
        "WindowsLocalAIConfigPanel",
        "WindowsLocalAISectionTitleText",
        "WindowsLocalAISectionRatingText",
        "WindowsLocalAIStatusBar",
        "WindowsLocalAIPrepareButton",
        "WindowsLocalAIPrepareProgressPanel",
        "WindowsLocalAIPrepareProgressText",
        "WindowsLocalAIPrepareProgressBar",
        "WindowsLocalAIWindowsUpdateLink",
        "FoundryLocalConfigPanel",
        "FoundryLocalTitleText",
        "FoundryLocalRatingText",
        "FoundryLocalEndpointBox",
        "FoundryLocalModelBox",
        "FoundryLocalStatusBar",
        "FoundryLocalStartButton",
        "FoundryLocalInstallLink",
        "FoundryLocalDocsLink",
        "FoundryLocalDescriptionText",
        "OpenVinoConfigPanel",
        "OpenVinoTitleText",
        "OpenVinoRatingText",
        "OpenVinoStatusBadge",
        "OpenVinoDeviceCombo",
        "OpenVinoStatusBar",
        "OpenVinoDownloadProgress",
        "OpenVinoDownloadProgressText",
        "OpenVinoDownloadButton",
        "OpenVinoDescriptionText",
    ] {
        assert!(snapshot.contains(&format!("id=\"{id}\"")), "missing {id}");
    }

    assert_control_contains(&snapshot, "WindowsLocalAIExpander", "kind=Expander");
    assert_control_contains(&snapshot, "LocalAIProviderCombo", "selected=\"Auto\"");
    assert_control_contains(
        &snapshot,
        "LocalAIProviderCombo",
        "Auto (Phi Silica -> Foundry Local -> OpenVINO)",
    );
    assert_control_contains(&snapshot, "LocalAIProviderCombo", "WindowsAI");
    assert_control_contains(
        &snapshot,
        "WindowsLocalAIDescriptionText",
        "Phi Silica first, then Foundry Local, then OpenVINO",
    );
    assert_control_contains(&snapshot, "LocalAIProviderWindowsAIRatingText", "5 stars");
    assert_control_contains(
        &snapshot,
        "LocalAIProviderFoundryLocalRatingText",
        "4 stars",
    );
    assert_control_contains(&snapshot, "LocalAIProviderOpenVINORatingText", "2 stars");
    assert_control_contains(&snapshot, "FoundryLocalEndpointBox", "action=text_input");
    assert_control_contains(&snapshot, "FoundryLocalModelBox", "action=text_input");
    assert_control_contains(&snapshot, "OpenVinoDeviceCombo", "selected=\"Auto\"");

    state.apply(easydict_app::Message::LocalAiProviderChanged(
        "FoundryLocal".to_string(),
    ));
    state.apply(easydict_app::Message::FoundryLocalEndpointChanged(
        "http://127.0.0.1:5273/v1/chat/completions".to_string(),
    ));
    state.apply(easydict_app::Message::FoundryLocalModelChanged(
        "phi-3-mini".to_string(),
    ));
    state.apply(easydict_app::Message::StartFoundryLocal);
    state.apply(easydict_app::Message::InstallFoundryLocal);
    assert_eq!(
        state.settings.foundry_local_status,
        "Install Foundry Local link requested"
    );
    state.apply(easydict_app::Message::OpenFoundryLocalDocs);
    state.apply(easydict_app::Message::OpenVinoDeviceChanged(
        "GPU".to_string(),
    ));
    state.apply(easydict_app::Message::DownloadOpenVinoModel);
    state.apply(easydict_app::Message::PrepareLocalAiModel);
    state.apply(easydict_app::Message::OpenWindowsAiUpdate);

    assert!(state.settings.unsaved_changes);
    assert_eq!(state.settings.local_ai_provider, "FoundryLocal");
    assert_eq!(
        state.settings.local_ai_prepare_progress,
        "Windows Update progress link requested"
    );
    assert_eq!(
        state.settings.foundry_local_status,
        "Starting Foundry Local service..."
    );
    assert_eq!(
        state.settings.open_vino_status,
        "Download queued for NLLB-200 model (~360 MB)"
    );
    assert_eq!(state.settings.open_vino_download_progress, "Queued");

    let settings = easydict_app::state::settings_snapshot(&state.settings);
    assert_eq!(settings.local_ai_provider.as_deref(), Some("FoundryLocal"));
    assert_eq!(
        settings.foundry_local_endpoint.as_deref(),
        Some("http://127.0.0.1:5273/v1/chat/completions")
    );
    assert_eq!(settings.foundry_local_model.as_deref(), Some("phi-3-mini"));
    assert_eq!(settings.open_vino_device.as_deref(), Some("GPU"));

    state.settings.selected_section = easydict_app::SettingsSection::Services;
    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert_control_contains(
        &snapshot,
        "LocalAIProviderCombo",
        "selected=\"FoundryLocal\"",
    );
    assert_control_contains(
        &snapshot,
        "FoundryLocalStatusBar",
        "Starting Foundry Local service...",
    );
    assert_control_contains(&snapshot, "OpenVinoDeviceCombo", "selected=\"GPU\"");
    assert_control_contains(&snapshot, "OpenVinoDownloadProgress", "label=\"Queued\"");
    assert_control_contains(
        &snapshot,
        "WindowsLocalAIPrepareProgressText",
        "Windows Update progress link requested",
    );
}

#[test]
fn services_settings_openai_and_ollama_expose_provider_configuration() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    for id in [
        "OllamaServiceExpander",
        "OllamaEndpointBox",
        "OllamaModelCombo",
        "RefreshOllamaButton",
        "TestOllamaButton",
        "OllamaStatusText",
        "OpenAIServiceExpander",
        "OpenAIKeyHeaderText",
        "OpenAIKeyBox",
        "OpenAIKeyRevealButton",
        "OpenAIEndpointBox",
        "OpenAIApiFormatCombo",
        "OpenAIDetectedFormatText",
        "OpenAIModelCombo",
        "OpenAIStatusText",
        "TestOpenAIButton",
    ] {
        assert!(snapshot.contains(&format!("id=\"{id}\"")), "missing {id}");
    }
    assert_control_contains(&snapshot, "OpenAIKeyBox", "action=text_input");
    assert_control_contains(&snapshot, "OpenAIEndpointBox", "action=text_input");
    assert_control_contains(&snapshot, "OpenAIApiFormatCombo", "selected=\"Auto\"");
    assert_control_contains(&snapshot, "OpenAIDetectedFormatText", "Responses API");
    assert_control_contains(&snapshot, "OpenAIModelCombo", "selected=\"gpt-5.4-mini\"");
    assert_control_contains(&snapshot, "OllamaEndpointBox", "action=text_input");
    assert_control_contains(&snapshot, "OllamaModelCombo", "selected=\"llama3.2\"");

    state.apply(easydict_app::Message::OpenAIApiKeyChanged(
        "sk-test".to_string(),
    ));
    state.apply(easydict_app::Message::OpenAIEndpointChanged(
        "https://api.openai.com/v1/chat/completions".to_string(),
    ));
    state.apply(easydict_app::Message::OpenAIApiFormatChanged(
        "ChatCompletions".to_string(),
    ));
    state.apply(easydict_app::Message::OpenAIModelChanged(
        "gpt-4o-mini".to_string(),
    ));
    state.apply(easydict_app::Message::OllamaEndpointChanged(
        "http://localhost:11434/v1/chat/completions".to_string(),
    ));
    state.apply(easydict_app::Message::OllamaModelChanged(
        "qwen2.5".to_string(),
    ));
    state.apply(easydict_app::Message::RefreshOllamaModels);
    state.apply(easydict_app::Message::TestOllama);
    state.apply(easydict_app::Message::TestOpenAI);
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    assert!(state.settings.unsaved_changes);
    assert_eq!(
        state.settings.open_ai_test_status,
        "Test requested (Chat Completions API)"
    );
    assert_eq!(state.settings.ollama_status, "Test requested for qwen2.5");

    let settings = easydict_app::state::settings_snapshot(&state.settings);
    assert_eq!(settings.open_ai_api_key.as_deref(), Some("sk-test"));
    assert_eq!(
        settings.open_ai_endpoint.as_deref(),
        Some("https://api.openai.com/v1/chat/completions")
    );
    assert_eq!(settings.open_ai_model.as_deref(), Some("gpt-4o-mini"));
    assert_eq!(
        settings.open_ai_api_format_override.as_deref(),
        Some("ChatCompletions")
    );
    assert_eq!(
        settings.ollama_endpoint.as_deref(),
        Some("http://localhost:11434/v1/chat/completions")
    );
    assert_eq!(settings.ollama_model.as_deref(), Some("qwen2.5"));

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert_control_contains(
        &snapshot,
        "OpenAIDetectedFormatText",
        "Pinned format: Chat Completions API",
    );
    assert_control_contains(&snapshot, "OpenAIStatusText", "Test requested");
    assert_control_contains(&snapshot, "OllamaStatusText", "Test requested for qwen2.5");
}

#[test]
fn services_settings_render_llm_provider_configuration_rows() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    for id in [
        "DeepSeekServiceExpander",
        "DeepSeekKeyBox",
        "DeepSeekKeyRevealButton",
        "DeepSeekModelCombo",
        "TestDeepSeekButton",
        "GroqServiceExpander",
        "GroqKeyBox",
        "GroqModelCombo",
        "ZhipuServiceExpander",
        "ZhipuKeyBox",
        "ZhipuModelCombo",
        "GitHubModelsServiceExpander",
        "GitHubModelsTokenBox",
        "GitHubModelsModelCombo",
        "GeminiServiceExpander",
        "GeminiKeyBox",
        "GeminiModelCombo",
        "CustomOpenAIServiceExpander",
        "CustomOpenAIEndpointBox",
        "CustomOpenAIKeyBox",
        "CustomOpenAIModelBox",
        "BuiltInAIServiceExpander",
        "BuiltInApiKeyBox",
        "BuiltInModelCombo",
        "DoubaoServiceExpander",
        "DoubaoEndpointBox",
        "DoubaoKeyBox",
        "DoubaoModelBox",
        "TestDoubaoButton",
    ] {
        assert!(snapshot.contains(&format!("id=\"{id}\"")), "missing {id}");
    }

    assert_control_contains(&snapshot, "DeepSeekKeyBox", "action=text_input");
    assert_control_contains(&snapshot, "CustomOpenAIEndpointBox", "action=text_input");
    assert_control_contains(&snapshot, "DoubaoEndpointBox", "action=text_input");
    assert_control_contains(&snapshot, "GitHubModelsModelCombo", "selected=\"gpt-4.1\"");
    assert_control_contains(
        &snapshot,
        "DoubaoModelBox",
        "selected=\"doubao-seed-translation-250915\"",
    );

    state.apply(easydict_app::Message::ServiceProviderSettingChanged(
        "deepseek".to_string(),
        easydict_app::ServiceProviderField::ApiKey,
        "deepseek-key".to_string(),
    ));
    state.apply(easydict_app::Message::ServiceProviderSettingChanged(
        "deepseek".to_string(),
        easydict_app::ServiceProviderField::Model,
        "deepseek-reasoner".to_string(),
    ));
    state.apply(easydict_app::Message::ServiceProviderSettingChanged(
        "custom-openai".to_string(),
        easydict_app::ServiceProviderField::Endpoint,
        "http://localhost:8000/v1/chat/completions".to_string(),
    ));
    state.apply(easydict_app::Message::ServiceProviderSettingChanged(
        "custom-openai".to_string(),
        easydict_app::ServiceProviderField::ApiKey,
        "custom-key".to_string(),
    ));
    state.apply(easydict_app::Message::ServiceProviderSettingChanged(
        "custom-openai".to_string(),
        easydict_app::ServiceProviderField::Model,
        "qwen2.5".to_string(),
    ));
    state.apply(easydict_app::Message::ServiceProviderSettingChanged(
        "doubao".to_string(),
        easydict_app::ServiceProviderField::Endpoint,
        "https://example.invalid/responses".to_string(),
    ));
    state.apply(easydict_app::Message::ServiceProviderSettingChanged(
        "doubao".to_string(),
        easydict_app::ServiceProviderField::ApiKey,
        "doubao-key".to_string(),
    ));
    state.apply(easydict_app::Message::TestServiceProvider(
        "deepseek".to_string(),
    ));

    assert!(state.settings.unsaved_changes);
    let settings = easydict_app::state::settings_snapshot(&state.settings);
    assert_eq!(settings.deep_seek_api_key.as_deref(), Some("deepseek-key"));
    assert_eq!(
        settings.deep_seek_model.as_deref(),
        Some("deepseek-reasoner")
    );
    assert_eq!(
        settings.custom_open_ai_endpoint.as_deref(),
        Some("http://localhost:8000/v1/chat/completions")
    );
    assert_eq!(
        settings.custom_open_ai_api_key.as_deref(),
        Some("custom-key")
    );
    assert_eq!(settings.custom_open_ai_model.as_deref(), Some("qwen2.5"));
    assert_eq!(settings.doubao_api_key.as_deref(), Some("doubao-key"));
    assert_eq!(
        settings.doubao_endpoint.as_deref(),
        Some("https://example.invalid/responses")
    );
    assert_eq!(
        settings.groq_model.as_deref(),
        Some("llama-3.3-70b-versatile")
    );
    assert_eq!(settings.zhipu_model.as_deref(), Some("glm-4.5-flash"));
    assert_eq!(settings.github_models_model.as_deref(), Some("gpt-4.1"));
    assert_eq!(settings.gemini_model.as_deref(), Some("gemini-2.5-flash"));
    assert_eq!(
        settings.built_in_ai_model.as_deref(),
        Some("glm-4-flash-250414")
    );

    state.settings.selected_section = easydict_app::SettingsSection::Services;
    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert_control_contains(
        &snapshot,
        "DeepSeekStatusText",
        "Test requested for deepseek-reasoner",
    );
}

#[test]
fn services_settings_render_traditional_http_provider_configuration() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    for id in [
        "CaiyunServiceExpander",
        "CaiyunStatusText",
        "CaiyunKeyHeaderText",
        "CaiyunKeyBox",
        "CaiyunKeyRevealButton",
        "TestCaiyunButton",
        "NiuTransServiceExpander",
        "NiuTransStatusText",
        "NiuTransKeyHeaderText",
        "NiuTransKeyBox",
        "NiuTransKeyRevealButton",
        "TestNiuTransButton",
        "YoudaoServiceExpander",
        "YoudaoStatusText",
        "YoudaoAppKeyHeaderText",
        "YoudaoAppKeyBox",
        "YoudaoAppKeyRevealButton",
        "YoudaoAppSecretHeaderText",
        "YoudaoAppSecretBox",
        "YoudaoAppSecretRevealButton",
        "YoudaoUseOfficialApiToggle",
        "TestYoudaoButton",
    ] {
        assert!(snapshot.contains(&format!("id=\"{id}\"")), "missing {id}");
    }

    assert_control_contains(&snapshot, "CaiyunKeyBox", "action=text_input");
    assert_control_contains(&snapshot, "NiuTransKeyBox", "action=text_input");
    assert_control_contains(&snapshot, "YoudaoAppKeyBox", "action=text_input");
    assert_control_contains(&snapshot, "YoudaoAppSecretBox", "action=text_input");
    assert_control_contains(&snapshot, "YoudaoUseOfficialApiToggle", "checked=false");

    state.apply(easydict_app::Message::CaiyunApiKeyChanged(
        "caiyun-token".to_string(),
    ));
    state.apply(easydict_app::Message::NiuTransApiKeyChanged(
        "niu-key".to_string(),
    ));
    state.apply(easydict_app::Message::YoudaoAppKeyChanged(
        "youdao-key".to_string(),
    ));
    state.apply(easydict_app::Message::YoudaoAppSecretChanged(
        "youdao-secret".to_string(),
    ));
    state.apply(easydict_app::Message::ToggleYoudaoUseOfficialApi(true));
    state.apply(easydict_app::Message::TestCaiyun);
    state.apply(easydict_app::Message::TestNiuTrans);
    state.apply(easydict_app::Message::TestYoudao);

    assert!(state.settings.unsaved_changes);
    assert_eq!(state.settings.caiyun_status, "Test requested for Caiyun");
    assert_eq!(
        state.settings.niu_trans_status,
        "Test requested for NiuTrans"
    );
    assert_eq!(
        state.settings.youdao_status,
        "Test requested for Official API"
    );

    let settings = easydict_app::state::settings_snapshot(&state.settings);
    assert_eq!(settings.caiyun_token.as_deref(), Some("caiyun-token"));
    assert_eq!(settings.niu_trans_api_key.as_deref(), Some("niu-key"));
    assert_eq!(settings.youdao_app_key.as_deref(), Some("youdao-key"));
    assert_eq!(settings.youdao_app_secret.as_deref(), Some("youdao-secret"));
    assert_eq!(settings.youdao_use_official_api, Some(true));

    state.settings.selected_section = easydict_app::SettingsSection::Services;
    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert_control_contains(&snapshot, "YoudaoUseOfficialApiToggle", "checked=true");
    assert_control_contains(
        &snapshot,
        "YoudaoStatusText",
        "Test requested for Official API",
    );
}

#[test]
fn services_settings_render_no_config_service_section() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    for id in [
        "settings.services.free_services",
        "FreeServicesHeaderText",
        "FreeServiceGoogleTranslateRow",
        "FreeServiceGoogleDictRow",
        "FreeServicesDescriptionText",
    ] {
        assert!(snapshot.contains(&format!("id=\"{id}\"")), "missing {id}");
    }

    assert_control_contains(
        &snapshot,
        "FreeServicesHeaderText",
        "Free Services (No Configuration Required)",
    );
    assert!(snapshot.contains("Text value=\"Google Translate\""));
    assert!(snapshot.contains("Text value=\"Google Dict\""));
    assert_control_contains(&snapshot, "FreeServicesDescriptionText", "without API keys");

    #[cfg(feature = "enable-linguee-service")]
    assert!(snapshot.contains("id=\"LingueeFreeServicePanel\""));

    #[cfg(not(feature = "enable-linguee-service"))]
    assert!(!snapshot.contains("id=\"LingueeFreeServicePanel\""));
}

#[test]
fn views_settings_registers_migration_list_translation_services() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Views;

    let mut expected_ids = vec![
        "google",
        "google_web",
        "bing",
        "deepl",
        "youdao",
        "openai",
        "ollama",
        "builtin",
        "deepseek",
        "groq",
        "zhipu",
        "github",
        "custom-openai",
        "gemini",
        "doubao",
        "caiyun",
        "niutrans",
        "volcano",
    ];
    #[cfg(feature = "enable-linguee-service")]
    expected_ids.push("linguee");
    expected_ids.push("windows-local-ai");

    let actual_ids = state
        .settings
        .main_window_services
        .iter()
        .map(|service| service.service_id.as_str())
        .collect::<Vec<_>>();
    assert_eq!(actual_ids, expected_ids);
    assert!(window_service(&state, "google").enabled);
    assert!(window_service(&state, "bing").enabled);
    assert!(window_service(&state, "openai").enabled);
    assert!(!window_service(&state, "google_web").enabled);
    assert!(!window_service(&state, "openai").configured);
    assert!(!actual_ids.contains(&"foundry-local"));
    assert!(!actual_ids.contains(&"openvino-local-ai"));

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    for id in [
        "main.service.google_web",
        "main.service.deepl",
        "main.service.youdao",
        "main.service.ollama",
        "main.service.builtin",
        "main.service.deepseek",
        "main.service.groq",
        "main.service.zhipu",
        "main.service.github",
        "main.service.custom_openai",
        "main.service.gemini",
        "main.service.doubao",
        "main.service.caiyun",
        "main.service.niutrans",
        "main.service.volcano",
        "main.service.windows_local_ai",
    ] {
        assert!(snapshot.contains(&format!("id=\"{id}\"")), "missing {id}");
    }
    assert_control_contains(&snapshot, "main.deepseek.enabled", "checked=false");
    assert!(!snapshot.contains("id=\"main.deepseek.enabled_query\""));

    state.apply(easydict_app::Message::ToggleWindowService(
        QuickTranslateSurface::Main,
        "deepseek".to_string(),
        true,
    ));
    state.apply(easydict_app::Message::SaveSettingsChanges);

    let deepseek_result = state
        .results
        .iter()
        .find(|result| result.id == "deepseek")
        .expect("enabling DeepSeek should add a result row on save");
    assert!(deepseek_result.enabled_query);
    assert!(deepseek_result.streaming_capable);
    assert!(deepseek_result.grammar_capable);
}

#[test]
fn views_settings_reorder_mode_exposes_window_specific_controls() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Views;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert!(snapshot.contains("Text value=\"Window Results\""));
    assert!(snapshot.contains("id=\"MainWindowReorderModeButton\""));
    assert!(snapshot.contains("id=\"MiniWindowReorderModeButton\""));
    assert!(snapshot.contains("id=\"FixedWindowReorderModeButton\""));
    assert!(snapshot.contains("id=\"main.service_list\""));
    assert!(snapshot.contains("id=\"main.service.google\""));
    assert!(snapshot.contains("id=\"main.google.enabled\""));
    assert!(snapshot.contains("id=\"main.google.enabled_query\""));
    assert!(snapshot.contains("id=\"main.service.openai\""));
    assert!(snapshot.contains("ToggleSwitch label=\"EnabledQuery\" checked=true"));
    assert_control_contains(
        &snapshot,
        "MainWindowReorderModeButton",
        "label=\"Reorder\"",
    );
    assert!(!snapshot.contains("id=\"main.google.move_up\""));
    assert!(!snapshot.contains("id=\"main.google.move_down\""));

    state.apply(easydict_app::Message::ToggleWindowServiceQuery(
        QuickTranslateSurface::Main,
        "google".to_string(),
        false,
    ));
    state.apply(easydict_app::Message::ToggleWindowService(
        QuickTranslateSurface::Main,
        "bing".to_string(),
        false,
    ));
    assert!(state.settings.unsaved_changes);
    assert!(!state.settings.main_window_services[0].enabled_query);
    assert!(!window_service(&state, "bing").enabled);

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert_control_contains(&snapshot, "main.google.enabled_query", "checked=false");
    assert_control_contains(&snapshot, "main.bing.enabled", "checked=false");
    assert!(!snapshot.contains("id=\"main.bing.enabled_query\""));

    state.settings.unsaved_changes = false;
    state.apply(easydict_app::Message::ToggleWindowReorderMode(
        QuickTranslateSurface::Main,
    ));
    assert!(state.settings.main_window_reorder_mode);
    assert!(!state.settings.mini_window_reorder_mode);
    assert!(!state.settings.fixed_window_reorder_mode);
    assert!(!state.settings.unsaved_changes);

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert_control_contains(&snapshot, "MainWindowReorderModeButton", "label=\"Done\"");
    assert!(snapshot.contains("id=\"main.google.move_up\""));
    assert!(snapshot.contains("id=\"main.google.move_down\""));
    assert!(snapshot.contains("id=\"main.openai.move_up\""));
    assert!(!snapshot.contains("id=\"mini.google.move_up\""));
    assert!(!snapshot.contains("id=\"fixed.google.move_up\""));

    state.apply(easydict_app::Message::MoveWindowService(
        QuickTranslateSurface::Main,
        "openai".to_string(),
        -1,
    ));
    let openai_index = main_window_service_index(&state, "openai")
        .expect("OpenAI should remain in the Main Window service list");
    let youdao_index = main_window_service_index(&state, "youdao")
        .expect("Youdao should precede OpenAI before the move");
    assert_eq!(openai_index + 1, youdao_index);

    state.apply(easydict_app::Message::SaveSettingsChanges);
    assert!(!state.settings.main_window_reorder_mode);
}

#[test]
fn about_settings_renders_required_links_with_automation_ids() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::About;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert!(snapshot.contains("id=\"AboutHeaderText\""));
    assert!(snapshot.contains("id=\"AboutAppNameText\""));
    for (id, label, url) in [
        (
            "GitHubRepositoryLink",
            "GitHub Repository",
            "https://github.com/xiaocang/easydict_win32",
        ),
        (
            "IssueFeedbackLink",
            "Issue Feedback",
            "https://github.com/xiaocang/easydict_win32/issues",
        ),
        (
            "InspiredByLink",
            "Easydict for macOS",
            "https://github.com/tisfeng/Easydict",
        ),
    ] {
        assert!(snapshot.contains(&format!("id=\"{id}\"")));
        assert_control_contains(&snapshot, id, &format!("label=\"{label}\""));
        assert_control_contains(&snapshot, id, "kind=Link");
        assert_control_contains(&snapshot, id, &format!("tooltip=\"{url}\""));
        assert_control_contains(&snapshot, id, "action=message");
    }

    state.apply(easydict_app::Message::OpenSettingsLink(
        SettingsLink::IssueFeedback,
    ));

    assert_eq!(
        state.last_opened_settings_link,
        Some(SettingsLink::IssueFeedback)
    );
}

#[test]
fn hotkey_settings_render_configurable_shortcuts() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Hotkeys;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert!(snapshot.contains("id=\"settings.hotkeys.show_window\""));
    assert!(snapshot.contains("title=\"Show Window\""));
    assert!(snapshot.contains("description=\"Ctrl+Alt+T - applied after saving settings.\""));
    assert!(snapshot.contains("id=\"ShowHotkeyBox\""));
    assert!(snapshot.contains("id=\"ShowHotkeyEnabledToggle\""));
    assert_control_contains(&snapshot, "ShowHotkeyBox", "text_len=10");
    assert_control_contains(&snapshot, "ShowHotkeyBox", "action=text_input");
    assert_control_contains(&snapshot, "ShowHotkeyEnabledToggle", "checked=true");
    assert_control_contains(&snapshot, "ShowHotkeyEnabledToggle", "action=bool_input");
    assert!(snapshot.contains("id=\"TranslateClipboardHotkeyBox\""));
    assert!(snapshot.contains("id=\"ShowMiniHotkeyBox\""));
    assert!(snapshot
        .contains("description=\"Ctrl+Alt+M - toggle hotkey adds Shift after saving settings.\""));
    assert!(snapshot.contains("id=\"ShowFixedHotkeyBox\""));
    assert!(snapshot
        .contains("description=\"Ctrl+Alt+F - toggle hotkey adds Shift after saving settings.\""));
    assert!(snapshot.contains("id=\"OcrTranslateHotkeyBox\""));
    assert!(snapshot.contains("id=\"SilentOcrHotkeyBox\""));
    assert!(!snapshot.contains("Button label=\"Record\""));

    state.apply(easydict_app::Message::ToggleHotkey(
        HOTKEY_SHOW_MAIN.to_string(),
        false,
    ));
    state.settings.selected_section = easydict_app::SettingsSection::Hotkeys;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert_control_contains(&snapshot, "ShowHotkeyEnabledToggle", "checked=false");
}

#[test]
fn services_settings_mdx_import_reflects_imported_dictionaries() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    let empty_snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert!(empty_snapshot.contains("id=\"settings.services.mdx\""));
    assert!(empty_snapshot.contains("id=\"ImportMdxDictionaryButton\""));
    assert!(empty_snapshot.contains("id=\"ImportedMdxSummaryText\""));
    assert_control_contains(
        &empty_snapshot,
        "ImportedMdxSummaryText",
        "No MDX dictionaries imported",
    );
    assert!(empty_snapshot.contains("id=\"ImportedMdxConfigPanel\""));
    assert!(empty_snapshot.contains("id=\"EnableInternationalServicesHeaderText\""));
    assert!(empty_snapshot.contains("id=\"EnableInternationalServicesDescriptionText\""));
    assert!(empty_snapshot.contains("id=\"EnableInternationalServicesToggle\""));
    assert!(!empty_snapshot.contains("id=\"settings.services.local_dictionary_suggestions\""));

    state.apply(easydict_app::Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Demo Dictionary.mdx".to_string(),
    )));
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert_control_contains(
        &snapshot,
        "ImportedMdxSummaryText",
        "1 MDX dictionary imported",
    );
    assert!(snapshot.contains("id=\"ImportedMdxDictionaryExpander.mdx::demo-dictionary\""));
    assert!(snapshot.contains("id=\"MdxFilePathText.mdx::demo-dictionary\""));
    assert!(snapshot.contains("id=\"MdxMddPathsText.mdx::demo-dictionary\""));

    state.settings.selected_section = easydict_app::SettingsSection::General;
    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert_control_enabled(&snapshot, "EnableLocalDictionarySuggestionsToggle", true);
}

#[test]
fn services_settings_mdx_dynamic_config_edits_rescans_and_deletes() {
    let temp_dir = std::env::temp_dir().join(format!(
        "easydict-mdx-ui-{}-{}",
        std::process::id(),
        "dynamic"
    ));
    let _ = fs::remove_dir_all(&temp_dir);
    fs::create_dir_all(&temp_dir).expect("temp MDX directory should be created");
    let mdx_path = temp_dir.join("Secure Dict.mdx");
    let mdd_path = temp_dir.join("Secure Dict.mdd");
    let mdd_numbered_path = temp_dir.join("Secure Dict.1.mdd");
    fs::write(&mdx_path, b"mdx").expect("MDX file should be created");
    fs::write(&mdd_path, b"mdd").expect("MDD file should be created");

    let mut state = EasydictUiState::default();
    state.apply(easydict_app::Message::MdxDictionarySelected(Some(
        mdx_path.to_string_lossy().into_owned(),
    )));
    let service_id = state.settings.imported_mdx_dictionaries[0]
        .service_id
        .clone();
    state.settings.imported_mdx_dictionaries[0].is_encrypted = true;
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert!(snapshot.contains(&format!("id=\"MdxEmailBox.{service_id}\"")));
    assert!(snapshot.contains(&format!("id=\"MdxRegcodeBox.{service_id}\"")));
    assert!(snapshot.contains(&format!("id=\"RescanMdxMddFilesButton.{service_id}\"")));
    assert!(snapshot.contains(&format!("id=\"DeleteMdxDictionaryButton.{service_id}\"")));
    assert_eq!(
        state.settings.imported_mdx_dictionaries[0]
            .mdd_file_paths
            .len(),
        1
    );

    fs::write(&mdd_numbered_path, b"mdd1").expect("numbered MDD file should be created");
    state.apply(easydict_app::Message::RescanMdxMddFiles(service_id.clone()));
    state.apply(easydict_app::Message::MdxDictionaryEmailChanged(
        service_id.clone(),
        "owner@example.com".to_string(),
    ));
    state.apply(easydict_app::Message::MdxDictionaryRegcodeChanged(
        service_id.clone(),
        "reg-123".to_string(),
    ));

    let dictionary = &state.settings.imported_mdx_dictionaries[0];
    assert_eq!(dictionary.email.as_deref(), Some("owner@example.com"));
    assert_eq!(dictionary.regcode.as_deref(), Some("reg-123"));
    assert_eq!(dictionary.mdd_file_paths.len(), 2);
    assert!(state.settings.unsaved_changes);

    let settings = easydict_app::state::settings_snapshot(&state.settings);
    let snapshot_dictionary = settings
        .imported_mdx_dictionaries
        .as_ref()
        .and_then(|dictionaries| dictionaries.first())
        .expect("imported dictionary should be snapshotted");
    assert_eq!(
        snapshot_dictionary.email.as_deref(),
        Some("owner@example.com")
    );
    assert_eq!(snapshot_dictionary.regcode.as_deref(), Some("reg-123"));
    assert_eq!(snapshot_dictionary.mdd_file_paths.len(), 2);

    state.apply(easydict_app::Message::RequestDeleteMdxDictionary(
        service_id.clone(),
    ));
    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert!(snapshot.contains("id=\"MdxDeleteConfirmDialog\""));
    assert!(snapshot.contains("id=\"MdxDeleteConfirmButton\""));
    assert!(snapshot.contains("id=\"MdxDeleteCancelButton\""));

    state.apply(easydict_app::Message::CancelDeleteMdxDictionary);
    assert_eq!(state.settings.pending_mdx_delete_service_id, None);
    assert_eq!(state.settings.imported_mdx_dictionaries.len(), 1);

    state.apply(easydict_app::Message::RequestDeleteMdxDictionary(
        service_id.clone(),
    ));
    state.apply(easydict_app::Message::ConfirmDeleteMdxDictionary);

    assert!(state.settings.imported_mdx_dictionaries.is_empty());
    assert!(!state.results.iter().any(|result| result.id == service_id));
    assert!(!state
        .settings
        .main_window_services
        .iter()
        .any(|service| service.service_id == service_id));

    let _ = fs::remove_dir_all(&temp_dir);
}

#[test]
fn services_settings_mdx_key_info_encrypted_dictionary_hides_credentials() {
    let temp_dir = unique_temp_dir("easydict-mdx-ui-key-info-encrypted");
    fs::create_dir_all(&temp_dir).expect("temp MDX directory should be created");
    let mdx_path = temp_dir.join("Key Info Dict.mdx");
    write_mdx_header(
        &mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="2" />"#,
    );

    let mut state = EasydictUiState::default();
    state.apply(easydict_app::Message::MdxDictionarySelected(Some(
        mdx_path.to_string_lossy().into_owned(),
    )));
    let service_id = state.settings.imported_mdx_dictionaries[0]
        .service_id
        .clone();
    assert!(state.settings.imported_mdx_dictionaries[0].is_encrypted);
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert!(snapshot.contains(&format!(
        "id=\"ImportedMdxDictionaryExpander.{service_id}\""
    )));
    assert!(!snapshot.contains(&format!("id=\"MdxEmailBox.{service_id}\"")));
    assert!(!snapshot.contains(&format!("id=\"MdxRegcodeBox.{service_id}\"")));
    assert!(!snapshot.contains("Credential-encrypted dictionaries require email"));

    let _ = fs::remove_dir_all(&temp_dir);
}

#[test]
fn services_settings_mdx_record_encrypted_dictionary_keeps_credentials() {
    let temp_dir = unique_temp_dir("easydict-mdx-ui-record-encrypted");
    fs::create_dir_all(&temp_dir).expect("temp MDX directory should be created");
    let mdx_path = temp_dir.join("Secure Dict.mdx");
    write_mdx_header(
        &mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="1" RegisterBy="EMail" />"#,
    );

    let mut state = EasydictUiState::default();
    state.apply(easydict_app::Message::MdxDictionarySelected(Some(
        mdx_path.to_string_lossy().into_owned(),
    )));
    let service_id = state.settings.imported_mdx_dictionaries[0]
        .service_id
        .clone();
    assert!(state.settings.imported_mdx_dictionaries[0].is_encrypted);
    state.settings.selected_section = easydict_app::SettingsSection::Services;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));
    assert!(snapshot.contains(&format!("id=\"MdxEmailBox.{service_id}\"")));
    assert!(snapshot.contains(&format!("id=\"MdxRegcodeBox.{service_id}\"")));
    assert!(snapshot.contains("Credential-encrypted dictionaries require email"));

    let _ = fs::remove_dir_all(&temp_dir);
}

#[test]
fn language_settings_render_selected_language_toggles() {
    let mut state = EasydictUiState::default();
    state.settings.selected_section = easydict_app::SettingsSection::Language;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert!(snapshot.contains("id=\"settings.language.first\""));
    assert!(snapshot.contains("id=\"FirstLanguageCombo\""));
    assert!(snapshot.contains("selected=\"zh-Hans\""));
    assert!(snapshot.contains("id=\"settings.language.second\""));
    assert!(snapshot.contains("id=\"SecondLanguageCombo\""));
    assert!(snapshot.contains("id=\"settings.language.auto_select_target\""));
    assert!(snapshot.contains("id=\"AutoSelectTargetToggle\""));
    assert!(snapshot.contains("ToggleSwitch label=\"Auto-select target language\" checked=true"));
    assert!(snapshot.contains("id=\"UILanguageCombo\""));
    assert!(snapshot.contains("selected=\"en-US\""));
    assert!(snapshot.contains("Restart required for full effect"));
    assert!(!snapshot.contains("id=\"OcrLanguageCombo\""));
    assert!(!snapshot.contains("id=\"settings.language.ocr\""));
    for ui_language in [
        "en-US:\"English\"",
        "zh-CN:\"Chinese (Simplified)\"",
        "zh-TW:\"Chinese (Traditional)\"",
        "ja-JP:\"Japanese\"",
        "ko-KR:\"Korean\"",
        "fr-FR:\"French\"",
        "de-DE:\"German\"",
        "vi-VN:\"Vietnamese\"",
        "th-TH:\"Thai\"",
        "ar-SA:\"Arabic\"",
        "id-ID:\"Indonesian\"",
        "it-IT:\"Italian\"",
        "ms-MY:\"Malay\"",
        "hi-IN:\"Hindi\"",
        "da-DK:\"Danish\"",
    ] {
        assert!(
            snapshot.contains(ui_language),
            "missing UI language item {ui_language}"
        );
    }
    assert!(snapshot.contains("id=\"settings.language.translation_languages\""));
    assert_control_contains(
        &snapshot,
        "settings.language.translation_languages",
        "Expander",
    );
    assert_control_contains(
        &snapshot,
        "settings.language.translation_languages",
        "expanded=false",
    );
    assert_control_contains(
        &snapshot,
        "settings.language.translation_languages",
        "action=bool_input",
    );
    assert!(!snapshot.contains("id=\"settings.language.selected_languages\""));

    state.apply(easydict_app::Message::ToggleTranslationLanguagesExpanded(
        true,
    ));
    state.settings.selected_section = easydict_app::SettingsSection::Language;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert_control_contains(
        &snapshot,
        "settings.language.translation_languages",
        "expanded=true",
    );
    assert!(snapshot.contains("id=\"settings.language.selected_languages\""));
    assert_control_contains(
        &snapshot,
        "settings.language.selected_languages",
        "max_columns=4",
    );
    assert!(snapshot.contains("id=\"settings.language.selected.fr\""));
    assert!(snapshot.contains("id=\"settings.language.selected.fr.toggle\""));
    assert!(snapshot.contains("ToggleSwitch label=\"French\" checked=true"));
    assert!(snapshot.contains("id=\"settings.language.selected.zh-Hans\""));
    assert!(snapshot.contains("ToggleSwitch label=\"Chinese (Simplified)\" checked=true"));
    assert!(snapshot.contains("id=\"settings.language.selected.pt\""));
    assert!(snapshot.contains("ToggleSwitch label=\"Portuguese\" checked=true"));
    assert!(snapshot.contains("id=\"settings.language.selected.zh-classical\""));
    assert!(snapshot.contains("ToggleSwitch label=\"Classical Chinese\" checked=true"));

    state.apply(easydict_app::Message::ToggleSelectedLanguage(
        "fr".to_string(),
        false,
    ));
    state.settings.selected_section = easydict_app::SettingsSection::Language;
    state.settings.translation_languages_expanded = true;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert!(snapshot.contains("ToggleSwitch label=\"French\" checked=false"));

    state.settings.selected_languages = vec!["en".to_string(), "ja".to_string()];
    state.settings.first_language = "en".to_string();
    state.settings.second_language = "ja".to_string();
    state.settings.selected_section = easydict_app::SettingsSection::Language;
    state.settings.translation_languages_expanded = true;

    let snapshot = win_fluent_testkit::view_snapshot(&settings_view(&state.settings));

    assert_control_enabled(&snapshot, "settings.language.selected.en.toggle", false);
    assert_control_enabled(&snapshot, "settings.language.selected.ja.toggle", false);
}

#[test]
fn main_quick_translate_renders_local_dictionary_suggestions() {
    let mut state = EasydictUiState::default();
    state.source_text = "please app".to_string();
    state.local_dictionary_suggestions = vec![easydict_app::LocalDictionarySuggestion {
        key: "apple".to_string(),
        dictionary_name: "Demo Dictionary".to_string(),
    }];
    state.local_dictionary_suggestion_active_index = Some(0);

    let snapshot = win_fluent_testkit::view_snapshot(&main_window_view(&state));

    assert!(snapshot.contains("id=\"main.local_dictionary_suggestions\""));
    assert!(snapshot.contains("id=\"main.local_dictionary_suggestions.item.0\""));
    assert!(snapshot.contains("id=\"main.local_dictionary_suggestions.header.0\""));
    assert!(snapshot.contains("Button label=\"apple · Demo Dictionary\""));
    assert_control_focused(&snapshot, "main.local_dictionary_suggestions.item.0", true);
    assert_control_contains(
        &snapshot,
        "InputTextBox",
        "key_bindings=Enter,Tab,Shift+Tab,ArrowDown,ArrowUp,Escape",
    );
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
    for label in [
        "🌐 Source",
        "🎯 Target",
        "🤖 Service",
        "📄 Input",
        "📝 Output",
        "⚡ Threads",
        "📑 Pages",
        "Output Folder",
    ] {
        assert!(
            snapshot.contains(&format!("Text value=\"{label}\"")),
            "missing long document label {label}"
        );
    }
    assert!(snapshot.contains("id=\"main.long-doc.output_folder_row\""));
    assert!(snapshot.contains("id=\"main.long-doc.output_browse\""));
    assert!(snapshot.contains("id=\"main.long-doc.output_naming_hint\""));
    assert!(snapshot.contains("selected=\"pdf\""));
    assert!(snapshot.contains("selected=\"mono\""));
    assert_control_contains(&snapshot, "main.long-doc.service", "windows-local-ai");
    assert_control_contains(&snapshot, "main.long-doc.service", "deepseek");
    assert_control_contains(&snapshot, "main.long-doc.service", "gemini");
    assert_control_contains(&snapshot, "main.long-doc.service", "niutrans");
    assert_control_not_contains(&snapshot, "main.long-doc.service", "google_web");
    assert_control_not_contains(&snapshot, "main.long-doc.service", "mdx::");
    assert!(snapshot.contains("ToggleSwitch label=\"Use document context pass\""));

    let mut text_bilingual = state;
    text_bilingual.long_document.input_mode = "plaintext".to_string();
    text_bilingual.long_document.output_mode = "bilingual".to_string();
    let text_bilingual_snapshot =
        win_fluent_testkit::view_snapshot(&main_window_view(&text_bilingual));
    assert_control_contains(
        &text_bilingual_snapshot,
        "main.long-doc.input_mode",
        "selected=\"plaintext\"",
    );
    assert_control_contains(
        &text_bilingual_snapshot,
        "main.long-doc.output_mode",
        "selected=\"bilingual\"",
    );

    let mut service_hover = text_bilingual;
    service_hover.long_document.service_combo_state = ControlState::default().hovered(true);
    let service_hover_snapshot =
        win_fluent_testkit::view_snapshot(&main_window_view(&service_hover));
    assert_control_contains(
        &service_hover_snapshot,
        "main.long-doc.service",
        "hovered=true",
    );
    assert_control_contains(
        &service_hover_snapshot,
        "main.long-doc.service",
        "pressed=false",
    );
}

#[test]
fn long_document_mode_locks_settings_while_translating() {
    let mut state = EasydictUiState::default();
    state.mode = easydict_app::AppMode::LongDocument;
    state.long_document.is_translating = true;

    let snapshot = win_fluent_testkit::view_snapshot(&main_window_view(&state));

    for id in [
        "main.long-doc.browse",
        "main.long-doc.source_text",
        "main.long-doc.source_language",
        "main.long-doc.target_language",
        "main.long-doc.service",
        "main.long-doc.input_mode",
        "main.long-doc.output_mode",
        "main.long-doc.concurrency",
        "main.long-doc.page_range",
        "main.long-doc.output_browse",
        "main.long-doc.two_pass",
        "main.long-doc.translate",
    ] {
        assert_control_enabled(&snapshot, id, false);
    }
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
    assert_control_contains(&overlay, "ModeSwitchOverlay", "fade_transition_ms=180");

    let long_doc_running = win_fluent_testkit::view_snapshot(&main_window_view(
        &EasydictUiState::preview(PreviewScenario::LongDocumentRunning, ThemeMode::Light),
    ));
    assert!(long_doc_running.contains("Progress: 42%"));
    assert!(long_doc_running.contains("Translating page 8 of 18 with OpenAI"));
    assert!(long_doc_running.contains("Latest block: Abstract and introduction completed"));
    assert!(long_doc_running.contains("Translating document"));
    assert_control_enabled(&long_doc_running, "main.long-doc.translate", false);
    assert_control_enabled(&long_doc_running, "main.long-doc.service", false);
    assert_control_enabled(&long_doc_running, "main.long-doc.retry", false);

    let long_doc_error = win_fluent_testkit::view_snapshot(&main_window_view(
        &EasydictUiState::preview(PreviewScenario::LongDocumentError, ThemeMode::Light),
    ));
    assert!(long_doc_error.contains("Progress: 67%"));
    assert!(long_doc_error.contains("Failed: page 12 layout detection timed out"));
    assert!(long_doc_error.contains("Retry failed blocks after checking OCR/Layout settings."));
    assert!(long_doc_error.contains("status=Error"));
    assert_control_enabled(&long_doc_error, "main.long-doc.retry", true);
    assert_control_enabled(&long_doc_error, "main.long-doc.translate", true);

    let primary_hover = win_fluent_testkit::view_snapshot(&main_window_view(
        &EasydictUiState::preview(PreviewScenario::PrimaryHover, ThemeMode::Light),
    ));
    assert_control_contains(&primary_hover, "TranslateButton", "hovered=true");
    assert_control_contains(&primary_hover, "TranslateButton", "pressed=false");
    assert_control_contains(&primary_hover, "TranslateButtonNarrow", "hovered=true");
    assert_control_contains(&primary_hover, "TranslateButtonNarrow", "pressed=false");

    let primary_pressed = win_fluent_testkit::view_snapshot(&main_window_view(
        &EasydictUiState::preview(PreviewScenario::PrimaryPressed, ThemeMode::Light),
    ));
    assert_control_contains(&primary_pressed, "TranslateButton", "hovered=true");
    assert_control_contains(&primary_pressed, "TranslateButton", "pressed=true");
    assert_control_contains(&primary_pressed, "TranslateButtonNarrow", "hovered=true");
    assert_control_contains(&primary_pressed, "TranslateButtonNarrow", "pressed=true");

    let source_input_hover = win_fluent_testkit::view_snapshot(&main_window_view(
        &EasydictUiState::preview(PreviewScenario::SourceInputHover, ThemeMode::Light),
    ));
    assert_control_contains(&source_input_hover, "InputTextBox", "hovered=true");
    assert_control_contains(&source_input_hover, "InputTextBox", "pressed=false");
    assert_control_contains(&source_input_hover, "InputTextBox", "focused=false");

    let source_input_focused = win_fluent_testkit::view_snapshot(&main_window_view(
        &EasydictUiState::preview(PreviewScenario::SourceInputFocused, ThemeMode::Light),
    ));
    assert_control_contains(&source_input_focused, "InputTextBox", "hovered=false");
    assert_control_contains(&source_input_focused, "InputTextBox", "pressed=false");
    assert_control_contains(&source_input_focused, "InputTextBox", "focused=true");

    let result_header_hover = win_fluent_testkit::view_snapshot(&main_window_view(
        &EasydictUiState::preview(PreviewScenario::ResultHeaderHover, ThemeMode::Light),
    ));
    assert_control_contains(&result_header_hover, "google", "header_state=");
    assert_control_contains(&result_header_hover, "google", "hovered=true");
    assert_control_contains(&result_header_hover, "google", "pressed=false");
    assert_control_contains(&result_header_hover, "google", "actions_visible=true");

    let result_collapsed = win_fluent_testkit::view_snapshot(&main_window_view(
        &EasydictUiState::preview(PreviewScenario::ResultCollapsed, ThemeMode::Light),
    ));
    assert_control_contains(
        &result_collapsed,
        "main.quick.results",
        "collapse_transition_ms=100",
    );
    assert_control_contains(&result_collapsed, "google", "expanded=false");
}

#[test]
fn easydict_theme_tokens_match_light_dark_minimal_contract() {
    let light = win_fluent_testkit::theme_snapshot(&easydict_theme_tokens(ThemeMode::Light));
    let dark = win_fluent_testkit::theme_snapshot(&easydict_theme_tokens(ThemeMode::Dark));
    let minimal = win_fluent_testkit::theme_snapshot(&easydict_theme_tokens(ThemeMode::Minimal));

    assert!(light.contains("background=#f7f9fc"));
    assert!(light.contains("selected_surface=#eaf3ff"));
    assert!(light.contains("selected_foreground=#174e8b"));
    assert!(light.contains("selected_border=#5c8fc7"));
    assert!(light.contains("result_header_hover=#f1f4f8"));
    assert!(light.contains("button_hover=#eef3f8"));
    assert!(light.contains("button_pressed=#e5ebf3"));
    assert!(light.contains("floating_input_surface=#f1f4f8"));
    assert!(light.contains("floating_input_border=#e1e7ef"));
    assert!(light.contains("floating_action_surface=#f7fbff"));
    assert!(light.contains("floating_action_border=#7aa7d9"));
    assert!(light.contains("accent_hover=#106ebe"));
    assert!(light.contains("accent_pressed=#005a9e"));
    assert!(light.contains("floating_action_rest_opacity=0.75"));
    assert!(light.contains("floating_action_hover_opacity=1"));
    assert!(light.contains("floating_action_pressed_opacity=0.85"));
    assert!(light.contains("result_action_button=24"));
    assert!(light.contains("primary_round_button=40"));
    assert!(dark.contains("background=#1f2229"));
    assert!(dark.contains("selected_surface=#243247"));
    assert!(dark.contains("selected_foreground=#d8e8ff"));
    assert!(dark.contains("selected_border=#5b7fa6"));
    assert!(dark.contains("result_header_hover=#323946"));
    assert!(dark.contains("button_hover=#323946"));
    assert!(dark.contains("button_pressed=#2a2f39"));
    assert!(dark.contains("floating_input_surface=#2a2f39"));
    assert!(dark.contains("floating_input_border=#3a4250"));
    assert!(dark.contains("accent_hover=#3a99e6"));
    assert!(dark.contains("accent_pressed=#1f6fb3"));
    assert!(dark.contains("floating_action_rest_opacity=0.94"));
    assert!(dark.contains("floating_action_hover_opacity=1"));
    assert!(dark.contains("floating_action_pressed_opacity=0.85"));
    assert!(minimal.contains("background=#ffffff"));
    assert!(minimal.contains("selected_surface=#e0e0e0"));
    assert!(minimal.contains("selected_foreground=#000000"));
    assert!(minimal.contains("selected_border=#000000"));
    assert!(minimal.contains("radius_control=0"));
    assert!(minimal.contains("floating_action_rest_opacity=1"));
    assert!(minimal.contains("floating_action_hover_opacity=1"));
    assert!(minimal.contains("floating_action_pressed_opacity=0.85"));
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
    assert!(capture.contains("PointerRegion"));
    assert!(capture.contains("id=\"capture.pointer\""));
    assert!(capture.contains("move=position"));
    assert!(capture.contains("double_click=position"));
    assert!(capture.contains("wheel=wheel"));
    assert!(capture.contains("escape=message"));
    assert!(capture.contains("id=\"capture.overlay.layers\""));
    assert!(capture.contains("layers=1"));
    assert_control_contains(&capture, "capture.status_panel", "width=Fixed(460)");
    assert!(capture.contains("phase=Detecting"));
    assert!(capture.contains("Button label=\"Confirm\""));
    assert!(capture.contains("Button label=\"Cancel\""));
    assert!(capture.contains("id=\"capture.nudge_commands\""));
    assert_control_contains(&capture, "capture.confirm", "enabled=false");
    assert_control_contains(&capture, "capture.copy", "enabled=false");
    assert_control_contains(&capture, "capture.nudge.left", "enabled=false");

    let mut detecting_state = CaptureInteractionState::new();
    detecting_state.detected_region = Some(CaptureRect::new(96, 118, 720, 458));
    let detecting =
        win_fluent_testkit::view_snapshot(&capture_overlay_view_with_state(&detecting_state, None));
    assert!(detecting.contains("CaptureOverlay phase=\"Detecting\""));
    assert!(detecting.contains("detected_rect=(96,118 624x340)"));
    assert!(detecting.contains("handles_visible=false"));
    assert!(detecting.contains("magnifier_visible=false"));
    assert!(detecting.contains("id=\"capture.detected_region\""));

    let mut selected_state = CaptureInteractionState::new();
    selected_state.phase = CapturePhase::Selecting;
    selected_state.selection = Some(CaptureRect::new(180, 164, 604, 386));
    let selected = win_fluent_testkit::view_snapshot(&capture_overlay_view_with_state(
        &selected_state,
        selected_state.selection,
    ));
    assert!(selected.contains("CaptureOverlay phase=\"Selecting\""));
    assert!(selected.contains("selection_rect=(180,164 424x222)"));
    assert!(selected.contains("handles_visible=true"));
    assert!(selected.contains("magnifier_visible=true"));
    assert!(selected.contains("id=\"capture.selection_rect\""));
    assert!(selected.contains("id=\"capture.magnifier\""));
    assert_control_contains(&selected, "capture.confirm", "enabled=false");
    assert_control_contains(&selected, "capture.copy", "enabled=false");
    assert_control_contains(&selected, "capture.nudge.left", "enabled=false");

    let mut adjusting_state = CaptureInteractionState::new();
    adjusting_state.set_adjusting_selection(CaptureRect::new(180, 164, 604, 386));
    let adjusting = win_fluent_testkit::view_snapshot(&capture_overlay_view_with_state(
        &adjusting_state,
        adjusting_state.selection,
    ));
    assert!(adjusting.contains("CaptureOverlay phase=\"Adjusting\""));
    assert!(adjusting.contains("selection_rect=(180,164 424x222)"));
    assert!(adjusting.contains("handles_visible=true"));
    assert!(adjusting.contains("magnifier_visible=true"));
    assert_control_contains(&adjusting, "capture.confirm", "enabled=true");
    assert_control_contains(&adjusting, "capture.copy", "enabled=true");
    assert_control_contains(&adjusting, "capture.nudge.left", "enabled=true");
    assert_control_contains(&adjusting, "capture.nudge.up", "action=message");

    let pop = win_fluent_testkit::view_snapshot(&pop_button_view());
    assert!(pop.contains("Page title=\"Selection Translate\""));
    assert!(pop.contains("id=\"pop-button.window\""));
    assert!(pop.contains("Button label=\"Translate selection\""));
    assert!(pop.contains("kind=FloatingAction"));
    assert!(pop.contains("icon=translate"));
    assert_control_contains(&pop, "pop-button.translate", "width=Fixed(30)");
    assert_control_contains(&pop, "pop-button.translate", "height=Fixed(30)");

    let pop_hover = win_fluent_testkit::view_snapshot(&pop_button_view_with_state(
        ControlState::default().hovered(true),
    ));
    assert_control_contains(&pop_hover, "pop-button.translate", "hovered=true");
    assert_control_contains(&pop_hover, "pop-button.translate", "pressed=false");

    let pop_pressed = win_fluent_testkit::view_snapshot(&pop_button_view_with_state(
        ControlState::default().hovered(true).pressed(true),
    ));
    assert_control_contains(&pop_pressed, "pop-button.translate", "hovered=true");
    assert_control_contains(&pop_pressed, "pop-button.translate", "pressed=true");

    let capture_options = capture_overlay_window_options();
    assert_eq!(capture_options.level, WindowLevel::TopMost);
    assert_eq!(capture_options.frame, WindowFrame::Borderless);
    assert_eq!(capture_options.placement, WindowPlacement::Monitor);
    assert_eq!(capture_options.min_width, Some(1.0));
    assert_eq!(capture_options.min_height, Some(1.0));
    assert!(capture_options.skip_taskbar);

    let pop_options = pop_button_window_options();
    assert_eq!(pop_options.level, WindowLevel::ToolWindow);
    assert_eq!(pop_options.frame, WindowFrame::Borderless);
    assert!(pop_options.skip_taskbar);
    assert!(pop_options.no_activate);
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

fn window_service<'a>(
    state: &'a EasydictUiState,
    service_id: &str,
) -> &'a easydict_app::state::WindowServiceSetting {
    state
        .settings
        .main_window_services
        .iter()
        .find(|service| service.service_id == service_id)
        .unwrap_or_else(|| panic!("missing main window service {service_id}"))
}

fn main_window_service_index(state: &EasydictUiState, service_id: &str) -> Option<usize> {
    state
        .settings
        .main_window_services
        .iter()
        .position(|service| service.service_id == service_id)
}

fn unique_temp_dir(prefix: &str) -> PathBuf {
    let nonce = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .expect("system clock should be after UNIX epoch")
        .as_nanos();
    std::env::temp_dir().join(format!("{prefix}-{}-{nonce}", std::process::id()))
}

fn write_mdx_header(path: &Path, header_xml: &str) {
    let mut header_bytes = header_xml
        .encode_utf16()
        .flat_map(u16::to_le_bytes)
        .collect::<Vec<_>>();
    header_bytes.extend_from_slice(&[0, 0]);

    let mut file_bytes = Vec::new();
    file_bytes.extend_from_slice(&(header_bytes.len() as u32).to_be_bytes());
    file_bytes.extend_from_slice(&header_bytes);
    file_bytes.extend_from_slice(&[0, 0, 0, 0]);
    fs::write(path, file_bytes).expect("MDX header should be written");
}

fn assert_control_enabled(snapshot: &str, id: &str, enabled: bool) {
    let line = snapshot
        .lines()
        .find(|line| line.contains(&format!("id=\"{id}\"")))
        .unwrap_or_else(|| panic!("missing control id {id}\n{snapshot}"));
    let expected = format!("state=enabled={enabled},");
    assert!(
        line.contains(&expected),
        "control {id} did not contain {expected}; line was {line}"
    );
}

fn assert_control_focused(snapshot: &str, id: &str, focused: bool) {
    let line = snapshot
        .lines()
        .find(|line| line.contains(&format!("id=\"{id}\"")))
        .unwrap_or_else(|| panic!("missing control id {id}\n{snapshot}"));
    let expected = format!("focused={focused}");
    assert!(
        line.contains(&expected),
        "control {id} did not contain {expected}; line was {line}"
    );
}

fn assert_control_contains(snapshot: &str, id: &str, expected: &str) {
    let line = snapshot
        .lines()
        .find(|line| line.contains(&format!("id=\"{id}\"")))
        .unwrap_or_else(|| panic!("missing control id {id}\n{snapshot}"));
    assert!(
        line.contains(expected),
        "control {id} did not contain {expected}; line was {line}"
    );
}

fn assert_control_not_contains(snapshot: &str, id: &str, unexpected: &str) {
    let line = snapshot
        .lines()
        .find(|line| line.contains(&format!("id=\"{id}\"")))
        .unwrap_or_else(|| panic!("missing control id {id}\n{snapshot}"));
    assert!(
        !line.contains(unexpected),
        "control {id} unexpectedly contained {unexpected}; line was {line}"
    );
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
