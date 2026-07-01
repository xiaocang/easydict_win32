use win_fluent::prelude::*;

#[derive(Clone, Debug, PartialEq)]
enum Msg {
    Number(f32),
    Selected(String),
}

#[test]
fn layout_snapshot_covers_first_wave_winui_controls() {
    let view = page("Layout Contract")
        .content(column(vec![
            grid()
                .columns([Length::Shrink, Length::Fill])
                .rows([Length::Shrink])
                .cell(0, 0, text("Label"))
                .cell(0, 1, text_editor("value"))
                .cell_span(1, 0, 1, 2, text("Help"))
                .into_view(),
            list_view([
                ListViewItem::new("a", text("Alpha")),
                ListViewItem::new("b", text("Beta")),
            ])
            .selected("a")
            .max_height(200)
            .on_select(Msg::Selected),
            radio_group()
                .option("left", "Left")
                .option("right", "Right")
                .selected("left")
                .on_select(Msg::Selected),
            number_box(2.0).range(0.0, 10.0).on_change(Msg::Number),
            auto_suggest_box("a")
                .suggestions(["Alpha"])
                .highlighted_index(Some(0))
                .on_submit(Msg::Selected),
            image_bgra_file("missing.raw", 4, 4)
                .width(Length::Fixed(16))
                .height(Length::Fixed(16))
                .into_view(),
            flyout(button("More"), text("Flyout content"))
                .open(true)
                .focus_behavior(FlyoutFocusBehavior::MoveFocusToContent)
                .into_view(),
            split_button("Save")
                .items([
                    FlyoutMenuItem::command("legacy", "Legacy").enabled(false),
                    FlyoutMenuItem::command("save", "Save"),
                ])
                .on_select(Msg::Selected),
            tab_view([TabItem::new("tab", "Tab", text("Tab content"))]).on_select(Msg::Selected),
            tree_view([TreeNode::branch(
                "root",
                "Root",
                [TreeNode::leaf("child", "Child")],
            )])
            .on_select(Msg::Selected),
            border(viewbox(text("Scaled")))
                .padding(Edges {
                    top: 4,
                    right: 4,
                    bottom: 4,
                    left: 4,
                })
                .width(Length::Fixed(120))
                .height(Length::Fixed(40))
                .into_view(),
            tray_menu_presenter(
                TrayMenu::<Msg>::new("Test")
                    .presenter_kind(TrayMenuPresenterKind::Fluent)
                    .item(TrayMenuItem::new("open", "Open").tooltip("Open app")),
            ),
        ]))
        .into_view();

    let snapshot = win_fluent_testkit::layout_snapshot(&view);

    for expected in [
        "Grid",
        "ListView",
        "RadioGroup",
        "NumberBox",
        "AutoSuggestBox",
        "source_kind=Bgra",
        "fallback=empty-layout-slot-on-read-error",
        "highlighted_index=Some(0)",
        "Flyout",
        "SplitButton",
        "TabView",
        "TreeView",
        "TrayMenu",
        "light=#F9F9F9/#1A1A1A/#E5E5E5",
        "dark=SystemMenu/#F3F3F3/#3A3A3A",
        "hover_mix=10",
        "tooltip=Some(\"Open app\")",
        "focus_behavior=MoveFocusToContent",
        "disabled_items=1",
        "max_height=Some(200)",
        "GridCell row=1 column=0 row_span=1 column_span=2",
        "padding=Edges",
        "Viewbox",
    ] {
        assert!(
            snapshot.contains(expected),
            "layout snapshot missing {expected}\n{snapshot}"
        );
    }
}
