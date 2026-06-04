use std::sync::OnceLock;

use win_fluent::prelude::*;

static I18N: OnceLock<I18n> = OnceLock::new();

pub fn tr(key: &'static str, fallback: &'static str) -> String {
    tr_locale(&current_locale(), key, fallback)
}

pub fn tr_count(key: &'static str, fallback: &'static str, count: usize) -> String {
    tr_count_locale(&current_locale(), key, fallback, count)
}

pub fn tr_locale(locale: &str, key: &'static str, fallback: &'static str) -> String {
    catalog()
        .clone()
        .locale(normalize_locale(locale))
        .resolve(&t(key, fallback))
}

pub fn tr_count_locale(
    locale: &str,
    key: &'static str,
    fallback: &'static str,
    count: usize,
) -> String {
    catalog()
        .clone()
        .locale(normalize_locale(locale))
        .resolve(&t(key, fallback).arg("count", count))
}

fn catalog() -> &'static I18n {
    I18N.get_or_init(|| {
        I18n::new("en-US")
            .fallback_locale("en-US")
            .with_bundle(en_us_bundle())
            .with_bundle(zh_cn_bundle())
    })
}

fn current_locale() -> String {
    ["EASYDICT_PREVIEW_UI_LANGUAGE", "EASYDICT_UI_LANGUAGE"]
        .into_iter()
        .find_map(|key| {
            std::env::var(key)
                .ok()
                .map(|value| value.trim().to_string())
                .filter(|value| !value.is_empty())
        })
        .unwrap_or_else(|| "en-US".to_string())
}

fn normalize_locale(locale: &str) -> String {
    let locale = locale.trim();
    if locale.is_empty() {
        "en-US".to_string()
    } else {
        locale.to_string()
    }
}

fn en_us_bundle() -> I18nBundle {
    I18nBundle::new("en-US")
        .with("app.name", "Easydict")
        .with("app.beta", "beta")
        .with("main.source_text", "Source Text")
        .with(
            "main.source_placeholder",
            "Enter or paste text to translate.",
        )
        .with("main.results", "Translation Results")
        .with("main.completed", "{count} service(s) completed")
        .with("main.auto_detect", "Auto Detect")
        .with("main.target_zh_hans", "Chinese (Simplified)")
        .with("main.language_help", "Language help")
        .with("main.translate", "Translate")
        .with("main.settings", "Settings")
        .with("settings.title", "Settings")
        .with("settings.back", "Back")
        .with("settings.save", "Save Settings")
        .with("settings.error.title", "Settings Error")
        .with("settings.ok", "OK")
        .with("settings.unsaved.title", "Unsaved Settings")
        .with(
            "settings.unsaved.message",
            "Save your settings changes before leaving?",
        )
        .with("settings.unsaved.save", "Save")
        .with("settings.unsaved.discard", "Don't Save")
        .with("settings.unsaved.cancel", "Cancel")
        .with("settings.tab.general", "General")
        .with("settings.tab.services", "Services")
        .with("settings.tab.views", "Views")
        .with("settings.tab.hotkeys", "Hotkeys")
        .with("settings.tab.advanced", "Advanced")
        .with("settings.tab.language", "Language")
        .with("settings.tab.about", "About")
        .with("settings.hotkeys.title", "Hotkeys")
        .with(
            "settings.hotkeys.help",
            "Hotkey changes apply after restart",
        )
        .with("settings.hotkeys.show_window.label", "Show Window")
        .with(
            "settings.hotkeys.translate_selection.label",
            "Translate Selection",
        )
        .with("settings.hotkeys.show_mini.label", "Show Mini Window")
        .with("settings.hotkeys.show_fixed.label", "Show Fixed Window")
        .with(
            "settings.hotkeys.ocr_translate.label",
            "OCR Screenshot Translate",
        )
        .with("settings.hotkeys.silent_ocr.label", "Silent OCR")
        .with(
            "settings.hotkeys.note",
            "Note: Restart app to apply hotkey changes. Toggle hotkeys use the same key with Shift added (e.g., Ctrl+Alt+Shift+M).",
        )
        .with("settings.general.behavior", "Behavior")
        .with("settings.general.app_theme", "App Theme")
        .with(
            "settings.general.app_theme.description",
            "Choose how Easydict appears. Select System to follow Windows theme.",
        )
        .with("settings.general.theme.system", "System")
        .with("settings.general.theme.light", "Light")
        .with("settings.general.theme.dark", "Dark")
        .with("settings.general.theme.minimal", "Minimal")
        .with(
            "settings.general.minimize_to_tray",
            "Minimize to system tray",
        )
        .with(
            "settings.general.start_minimized",
            "Start minimized to tray",
        )
        .with(
            "settings.general.monitor_clipboard",
            "Monitor clipboard for text",
        )
        .with(
            "settings.general.mouse_selection",
            "Mouse selection translate",
        )
        .with("settings.general.excluded_apps", "Excluded apps")
        .with(
            "settings.general.excluded_apps.description",
            "Process names to exclude from mouse selection translate, separated by commas. Example: \"code\" for VS Code.",
        )
        .with("settings.general.always_on_top", "Always on top")
        .with(
            "settings.general.launch_at_startup",
            "Launch at Windows startup",
        )
        .with(
            "settings.general.hide_empty_service_results",
            "Hide dictionaries with no result",
        )
        .with(
            "settings.general.local_dictionary_suggestions",
            "Enable custom dictionary input suggestions",
        )
        .with(
            "settings.general.local_dictionary_suggestions.empty",
            "Import an MDX dictionary to enable local input suggestions.",
        )
        .with(
            "settings.general.local_dictionary_suggestions.ready",
            "Suggest local dictionary entries while typing.",
        )
        .with("settings.general.experimental", "Experimental")
        .with("settings.general.tts.header", "TTS Output Settings")
        .with(
            "settings.general.tts.speed",
            "TTS Reading Speed (0.5x - 3.0x)",
        )
        .with("settings.general.tts.speed.a11y", "TTS speed")
        .with(
            "settings.general.auto_play_translation",
            "Auto play translation",
        )
        .with(
            "settings.general.auto_play_translation.description",
            "Play translated text after a translation finishes.",
        )
        .with("settings.views.title", "Window Results")
        .with(
            "settings.views.description",
            "Choose which results appear in each window and whether each result queries automatically.",
        )
        .with("settings.views.main_window", "Main Window")
        .with("settings.views.mini_window", "Mini Window")
        .with("settings.views.fixed_window", "Fixed Window")
        .with("settings.views.reorder", "Reorder")
        .with("settings.views.done", "Done")
        .with("settings.views.enabled", "enabled")
        .with("settings.views.auto", "Auto")
        .with("settings.views.manual", "Manual")
        .with("settings.views.mini_behavior", "Mini Window behavior")
        .with(
            "settings.views.mini_behavior.description",
            "Close the Mini window automatically after focus moves away.",
        )
        .with("settings.views.auto_close", "Auto close")
        .with("settings.views.fixed_behavior", "Fixed Window behavior")
        .with(
            "settings.views.fixed_behavior.description",
            "Keep the Fixed window above other windows.",
        )
        .with("settings.language.title", "Language")
        .with("settings.language.preferences", "Language Preferences")
        .with("settings.language.first", "First Language")
        .with(
            "settings.language.first.description",
            "Preferred target language when detected source is not the first language.",
        )
        .with("settings.language.second", "Second Language")
        .with(
            "settings.language.second.description",
            "Fallback target language when detected source matches the first language.",
        )
        .with(
            "settings.language.auto_select_target",
            "Auto-select target language",
        )
        .with(
            "settings.language.auto_select_target.description",
            "Use the first/second language rule until a target language is chosen manually.",
        )
        .with(
            "settings.language.auto_select_target.compact",
            "Auto-select target language based on detected source language",
        )
        .with(
            "settings.language.preference_rule.description",
            "When the detected language matches your first language, the target becomes your second language, and vice versa.",
        )
        .with("settings.language.display", "Display language")
        .with(
            "settings.language.display.description",
            "Choose the language used by the app UI. Restart required for full effect.",
        )
        .with(
            "settings.language.translation_languages",
            "Available Languages",
        )
        .with(
            "settings.language.translation_languages.description",
            "Choose which languages appear in Main, Mini, Fixed, and Long Document pickers.",
        )
        .with(
            "settings.language.available.description",
            "Select languages available in source/target pickers. At least 2 required.",
        )
        .with("settings.about.title", "About")
        .with("settings.about.app_name", "Easydict for Windows ᵇᵉᵗᵃ")
        .with("settings.about.version", "Version {version}")
        .with("settings.about.github", "GitHub Repository")
        .with("settings.about.issue_feedback", "Issue Feedback")
        .with("settings.about.inspired_by", "Inspired by")
        .with("settings.about.mac", "Easydict for macOS")
        .with("settings.about.license", "License: GPL-3.0")
        .with("settings.toggle.on", "On")
        .with("settings.toggle.off", "Off")
}

fn zh_cn_bundle() -> I18nBundle {
    I18nBundle::new("zh-CN")
        .with("app.name", "Easydict")
        .with("app.beta", "beta")
        .with("main.source_text", "原文")
        .with("main.source_placeholder", "输入或粘贴要翻译的文本。")
        .with("main.results", "翻译结果")
        .with("main.completed", "已完成 {count} 个服务")
        .with("main.auto_detect", "自动检测")
        .with("main.target_zh_hans", "简体中文")
        .with("main.language_help", "语言帮助")
        .with("main.translate", "翻译")
        .with("main.settings", "设置")
        .with("settings.title", "设置")
        .with("settings.back", "返回")
        .with("settings.save", "保存设置")
        .with("settings.error.title", "设置错误")
        .with("settings.ok", "确定")
        .with("settings.unsaved.title", "未保存的设置")
        .with("settings.unsaved.message", "离开前保存设置更改吗？")
        .with("settings.unsaved.save", "保存")
        .with("settings.unsaved.discard", "不保存")
        .with("settings.unsaved.cancel", "取消")
        .with("settings.tab.general", "常规")
        .with("settings.tab.services", "服务")
        .with("settings.tab.views", "视图")
        .with("settings.tab.hotkeys", "快捷键")
        .with("settings.tab.advanced", "高级")
        .with("settings.tab.language", "语言")
        .with("settings.tab.about", "关于")
        .with("settings.hotkeys.title", "快捷键")
        .with("settings.hotkeys.help", "快捷键更改将在重启后生效")
        .with("settings.hotkeys.show_window.label", "显示窗口")
        .with("settings.hotkeys.translate_selection.label", "翻译选中文本")
        .with("settings.hotkeys.show_mini.label", "显示迷你窗口")
        .with("settings.hotkeys.show_fixed.label", "显示固定窗口")
        .with("settings.hotkeys.ocr_translate.label", "OCR 截图翻译")
        .with("settings.hotkeys.silent_ocr.label", "静默 OCR")
        .with(
            "settings.hotkeys.note",
            "注意：重启应用以应用快捷键更改。切换快捷键使用相同的键加 Shift（例如 Ctrl+Alt+Shift+M）。",
        )
        .with("settings.general.behavior", "行为")
        .with("settings.general.app_theme", "应用主题")
        .with(
            "settings.general.app_theme.description",
            "选择 Easydict 的外观。选择“系统”将跟随 Windows 主题。",
        )
        .with("settings.general.theme.system", "系统")
        .with("settings.general.theme.light", "浅色")
        .with("settings.general.theme.dark", "深色")
        .with("settings.general.theme.minimal", "极简线框")
        .with("settings.general.minimize_to_tray", "最小化到系统托盘")
        .with("settings.general.start_minimized", "启动后最小化到系统托盘")
        .with("settings.general.monitor_clipboard", "监控剪贴板文本")
        .with(
            "settings.general.mouse_selection",
            "划词翻译（选中文本 → 点击浮动图标）",
        )
        .with("settings.general.excluded_apps", "排除应用")
        .with(
            "settings.general.excluded_apps.description",
            "排除划词翻译的进程名称，用逗号分隔。示例：\"code\" 代表 VS Code。",
        )
        .with("settings.general.always_on_top", "始终置顶")
        .with("settings.general.launch_at_startup", "开机自启动")
        .with(
            "settings.general.hide_empty_service_results",
            "隐藏无结果的词典",
        )
        .with(
            "settings.general.local_dictionary_suggestions",
            "启用自定义词典输入建议",
        )
        .with(
            "settings.general.local_dictionary_suggestions.empty",
            "导入 MDX 词典以启用本地输入建议。",
        )
        .with(
            "settings.general.local_dictionary_suggestions.ready",
            "输入时建议本地词典条目。",
        )
        .with("settings.general.experimental", "实验性")
        .with("settings.general.tts.header", "TTS 输出设置")
        .with("settings.general.tts.speed", "TTS 朗读速度 (0.5x - 3.0x)")
        .with("settings.general.tts.speed.a11y", "TTS 速度")
        .with("settings.general.auto_play_translation", "自动朗读译文")
        .with(
            "settings.general.auto_play_translation.description",
            "翻译完成后朗读译文。",
        )
        .with("settings.views.title", "窗口结果显示")
        .with(
            "settings.views.description",
            "选择每个窗口显示哪些结果，以及这些结果是否自动查询。",
        )
        .with("settings.views.main_window", "主窗口")
        .with("settings.views.mini_window", "迷你窗口")
        .with("settings.views.fixed_window", "固定窗口")
        .with("settings.views.reorder", "排序")
        .with("settings.views.done", "完成")
        .with("settings.views.enabled", "已启用")
        .with("settings.views.auto", "自动")
        .with("settings.views.manual", "手动")
        .with("settings.views.mini_behavior", "迷你窗口行为")
        .with(
            "settings.views.mini_behavior.description",
            "焦点移开后自动关闭迷你窗口。",
        )
        .with("settings.views.auto_close", "自动关闭")
        .with("settings.views.fixed_behavior", "固定窗口行为")
        .with(
            "settings.views.fixed_behavior.description",
            "让固定窗口保持在其他窗口之上。",
        )
        .with("settings.language.title", "语言")
        .with("settings.language.preferences", "语言偏好")
        .with("settings.language.first", "第一语言")
        .with(
            "settings.language.first.description",
            "当检测到的源语言不是第一语言时，优先使用的目标语言。",
        )
        .with("settings.language.second", "第二语言")
        .with(
            "settings.language.second.description",
            "当检测到的源语言与第一语言相同时，使用的备用目标语言。",
        )
        .with("settings.language.auto_select_target", "自动选择目标语言")
        .with(
            "settings.language.auto_select_target.description",
            "在手动选择目标语言前，使用第一/第二语言规则。",
        )
        .with(
            "settings.language.auto_select_target.compact",
            "根据检测到的源语言自动选择目标语言",
        )
        .with(
            "settings.language.preference_rule.description",
            "当检测到的语言与您的第一语言匹配时，翻译目标将是您的第二语言，反之亦然。",
        )
        .with("settings.language.display", "界面语言")
        .with(
            "settings.language.display.description",
            "选择应用界面使用的语言。完整生效需要重启。",
        )
        .with("settings.language.translation_languages", "可用语言")
        .with(
            "settings.language.translation_languages.description",
            "选择主窗口、迷你窗口、固定窗口和长文档选择器中显示的语言。",
        )
        .with(
            "settings.language.available.description",
            "选择源语言/目标语言选择器中可用的语言。至少需要 2 种。",
        )
        .with("settings.about.title", "关于")
        .with("settings.about.app_name", "Easydict for Windows ᵇᵉᵗᵃ")
        .with("settings.about.version", "Version {version}")
        .with("settings.about.github", "GitHub Repository")
        .with("settings.about.issue_feedback", "问题反馈")
        .with("settings.about.inspired_by", "Inspired by")
        .with("settings.about.mac", "Easydict for macOS")
        .with("settings.about.license", "License: GPL-3.0")
        .with("settings.toggle.on", "开")
        .with("settings.toggle.off", "关")
}
