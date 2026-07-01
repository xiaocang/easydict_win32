use win_fluent::prelude::*;
use win_fluent_platform_win::{WindowsPlatformAdapter, WindowsTrayItemKind};

#[derive(Clone, Debug, PartialEq)]
enum Msg {
    Open,
    Logs,
}

#[test]
fn tray_plan_preserves_fluent_presenter_style_and_nested_submenus() {
    let style = TrayMenuPresenterStyle::winui()
        .presenter_max_height(Some(360))
        .submenu_arrow_column_width(26)
        .hover_inset(5, 4)
        .light_palette(
            TrayMenuColor::rgb(0xFA, 0xFA, 0xFA),
            TrayMenuColor::rgb(0x1A, 0x1A, 0x1A),
            TrayMenuColor::rgb(0xE5, 0xE5, 0xE5),
        )
        .dark_palette(
            TrayMenuColor::system_menu(),
            TrayMenuColor::rgb(0xF3, 0xF3, 0xF3),
            TrayMenuColor::rgb(0x3A, 0x3A, 0x3A),
        )
        .hover_foreground_mix_percent(15);

    let menu = TrayMenu::new("WinFluent")
        .presenter_kind(TrayMenuPresenterKind::Fluent)
        .presenter_min_width(280)
        .presenter_style(style)
        .default_item("logs")
        .item(
            TrayMenuItem::new("open", "Open")
                .tooltip("Open app")
                .on_invoke(Msg::Open),
        )
        .item(
            TrayMenuItem::submenu("tools", "Tools")
                .tooltip("Tool actions")
                .item(
                    TrayMenuItem::submenu("diagnostics", "Diagnostics")
                        .tooltip("Diagnostics")
                        .item(
                            TrayMenuItem::new("logs", "Logs")
                                .tooltip("Open logs")
                                .on_invoke(Msg::Logs),
                        ),
                ),
        );

    let plan = WindowsPlatformAdapter::plan_tray(&menu).expect("tray menu should plan");

    assert_eq!(plan.presenter_kind, TrayMenuPresenterKind::Fluent);
    assert_eq!(plan.presenter_min_width, Some(280));
    assert_eq!(plan.presenter_style, style);
    assert_eq!(plan.default_command_id, Some(1001));
    assert_eq!(plan.item_count, 2);

    assert_eq!(plan.items[0].kind, WindowsTrayItemKind::Command);
    assert_eq!(plan.items[0].tooltip.as_deref(), Some("Open app"));
    assert_eq!(plan.items[1].kind, WindowsTrayItemKind::Submenu);
    assert_eq!(plan.items[1].tooltip.as_deref(), Some("Tool actions"));

    let diagnostics = &plan.items[1].children[0];
    assert_eq!(diagnostics.kind, WindowsTrayItemKind::Submenu);
    assert_eq!(diagnostics.tooltip.as_deref(), Some("Diagnostics"));
    assert_eq!(diagnostics.children[0].id, "logs");
    assert_eq!(diagnostics.children[0].command_id, 1001);
    assert_eq!(
        diagnostics.children[0].tooltip.as_deref(),
        Some("Open logs")
    );
}
