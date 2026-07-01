use win_fluent::a11y::{resolve_accessibility_tree, A11yNode};
use win_fluent::diff::diff_views;
use win_fluent::prelude::*;

#[derive(Clone, Debug, PartialEq)]
enum Msg {
    Pressed(&'static str),
    Bool(bool),
    Text(String),
    Number(f32),
    Selected(String),
}

fn first_wave_contract_view(title: &str) -> View<Msg> {
    let form = grid()
        .id("settings-grid")
        .columns([Length::Shrink, Length::Fill])
        .rows([Length::Shrink, Length::Shrink])
        .spacing(8)
        .cell(0, 0, text("Name"))
        .cell(0, 1, text_editor("").placeholder("Display name"))
        .cell_span(
            1,
            0,
            1,
            2,
            number_box(1.0)
                .id("zoom")
                .range(0.5, 3.0)
                .step(0.25)
                .spin_buttons(true)
                .on_change(Msg::Number),
        )
        .into_view();

    let list = list_view([
        ListViewItem::new("en", text("English")),
        ListViewItem::new("zh", text("Chinese")),
    ])
    .id("languages")
    .selected("en")
    .virtualized(true)
    .max_height(240)
    .on_select(Msg::Selected);

    let navigation = navigation_view([
        NavigationItem::new("home", "Home").icon(icon::app()),
        NavigationItem::new("settings", "Settings").icon(icon::settings()),
    ])
    .id("nav")
    .selected("home")
    .pane_display_mode(PaneDisplayMode::Top)
    .header("Navigation")
    .footer_item(NavigationItem::new("about", "About"))
    .settings_visible(true)
    .back_button(Msg::Pressed("back"))
    .content(text("Content"))
    .on_select(Msg::Selected);

    let tabbed = tab_view([
        TabItem::new("query", "Query", text("Query tab"))
            .closable(true)
            .close_a11y_name("Close Query tab"),
        TabItem::new("history", "History", text("History tab")).closable(false),
    ])
    .id("tabs")
    .selected("query")
    .on_close(Msg::Selected)
    .on_select(Msg::Selected);

    let tree = tree_view([
        TreeNode::branch("root", "Root", [TreeNode::leaf("child", "Child")]),
        TreeNode::branch("archive", "Archive", [TreeNode::leaf("old", "Old")]).expanded(false),
    ])
    .id("outline")
    .selected("child")
    .on_toggle(Msg::Selected)
    .on_select(Msg::Selected);

    page(title)
        .content(column(vec![
            info_bar("Status", ValidationSeverity::Warning)
                .id("status")
                .message("Review required")
                .into_view(),
            text("Hoverable text").tooltip_at("Generic tooltip", TooltipPlacement::Bottom),
            form,
            list,
            radio_group()
                .id("theme-radio")
                .header("Theme")
                .option("system", "System")
                .option_item(RadioOption::new("dark", "Dark").enabled(false))
                .selected("system")
                .horizontal()
                .on_select(Msg::Selected),
            image("assets/logo.png")
                .id("logo")
                .stretch(ImageStretch::Uniform)
                .width(Length::Fixed(32))
                .height(Length::Fixed(32))
                .into_view(),
            auto_suggest_box("en")
                .id("language-search")
                .placeholder("Search languages")
                .suggestions(["English", "Chinese"])
                .highlighted_index(Some(1))
                .on_change(Msg::Text)
                .on_submit(Msg::Selected),
            navigation,
            flyout(button("More"), text("Flyout content"))
                .id("generic-flyout")
                .open(true)
                .placement(FlyoutPlacement::Bottom)
                .light_dismiss(true)
                .focus_behavior(FlyoutFocusBehavior::MoveFocusToContent)
                .into_view(),
            text_runs([
                TextRun::plain("Read "),
                TextRun::bold("Fluent"),
                TextRun::plain(" docs "),
                TextRun::link("online", "https://developer.microsoft.com/en-us/fluentui"),
            ])
            .id("rich-text")
            .on_link(Msg::Selected),
            web_view_url("https://example.com").id("web").into_view(),
            toggle_button("Pin", true).id("pin").on_toggle(Msg::Bool),
            split_button("Save")
                .id("save")
                .items([
                    FlyoutMenuItem::command("legacy-save", "Legacy Save").enabled(false),
                    FlyoutMenuItem::command("save-as", "Save as"),
                ])
                .on_press(Msg::Pressed("save"))
                .on_select(Msg::Selected),
            tabbed,
            tree,
            border(viewbox(text("Scaled")))
                .id("scaled-border")
                .padding(Edges {
                    top: 8,
                    right: 8,
                    bottom: 8,
                    left: 8,
                })
                .filled(true)
                .into_view(),
        ]))
        .into_view()
}

#[test]
fn public_prelude_builds_first_wave_winui_controls() {
    let view = first_wave_contract_view("Fluent Contract");
    let snapshot = view_schema(&view).snapshot();

    for expected in [
        "InfoBar",
        "Grid",
        "ListView",
        "RadioGroup",
        "Image",
        "NumberBox",
        "AutoSuggestBox",
        "NavigationView",
        "Flyout",
        "RichText",
        "WebView",
        "ToggleButton",
        "SplitButton",
        "TabView",
        "TreeView",
        "Border",
        "Viewbox",
    ] {
        assert!(
            snapshot.contains(expected),
            "schema missing {expected}\n{snapshot}"
        );
    }

    assert!(snapshot.contains("tooltip=\"Generic tooltip\""));
    assert!(snapshot.contains("tooltip_placement=Bottom"));
    assert!(snapshot.contains("pane_display_mode=Top"));
    assert!(snapshot.contains("settings_visible=true"));
    assert!(snapshot.contains("back_button=true"));
    assert!(snapshot.contains("action=selection_input"));
    assert!(snapshot.contains("back_action=message"));
    assert!(snapshot.contains("selected=\"system\""));
    assert!(snapshot.contains("source_kind=Raster"));
    assert!(snapshot.contains("fallback=backend-decodes-or-reserves-layout"));
    assert!(snapshot.contains("highlighted_index=1"));
    assert!(snapshot.contains("light_dismiss=Enabled"));
    assert!(snapshot.contains("focus_behavior=MoveFocusToContent"));
    assert!(snapshot.contains("legacy-save:\"Legacy Save\":Command:checked=false:enabled=false"));
    assert!(snapshot.contains("close_a11y_name=\"Close Query tab\""));
    assert!(snapshot.contains("id=\"archive\""));
    assert!(snapshot.contains("expanded=false"));
}

#[test]
fn image_source_kind_and_fallback_contract_are_public() {
    let raster: View<Msg> = image("https://example.com/icon.png").into_view();
    let bgra: View<Msg> = image_bgra_file("missing.raw", 8, 8).into_view();
    let empty: View<Msg> = image("").into_view();

    let source_kinds = [raster, bgra, empty]
        .into_iter()
        .map(|view| match view.token() {
            ViewToken::Image(token) => (token.source_kind(), token.fallback_behavior()),
            other => panic!("expected image token, got {other:?}"),
        })
        .collect::<Vec<_>>();

    assert_eq!(
        source_kinds,
        vec![
            (
                ImageSourceKind::Raster,
                "backend-decodes-or-reserves-layout"
            ),
            (ImageSourceKind::Bgra, "empty-layout-slot-on-read-error"),
            (ImageSourceKind::Empty, "empty-layout-slot"),
        ]
    );
}

#[test]
fn number_box_clamps_initial_value_and_normalizes_step() {
    let view = number_box(f32::NAN)
        .range(0.0, 10.0)
        .step(-2.0)
        .on_change(Msg::Number);

    let ViewToken::NumberBox(token) = view.token() else {
        panic!("expected NumberBox");
    };

    assert_eq!(token.value, 0.0);
    assert_eq!(token.step, 1.0);
    assert_eq!(token.clamp(-5.0), 0.0);
    assert_eq!(token.clamp(15.0), 10.0);
}

#[test]
fn contract_tree_flows_through_schema_a11y_and_diff() {
    let before = first_wave_contract_view("Fluent Contract");
    let after = first_wave_contract_view("Fluent Contract Updated");

    let schema = view_schema(&before).snapshot();
    assert!(schema.contains("ViewSchema version="));

    let a11y = resolve_accessibility_tree(&before);
    assert!(!a11y.children.is_empty());
    let mut a11y_nodes = Vec::new();
    collect_a11y_nodes(&a11y, &mut a11y_nodes);
    assert!(a11y_nodes
        .iter()
        .any(|node| node.name.as_deref() == Some("Close Query tab")));
    assert!(a11y_nodes
        .iter()
        .any(|node| node.help_text.as_deref() == Some("expanded")));
    assert!(a11y_nodes
        .iter()
        .any(|node| node.help_text.as_deref() == Some("collapsed")));

    let changes = diff_views(&before, &after);
    assert!(
        !changes.is_empty(),
        "diff should notice the page title change"
    );
}

fn collect_a11y_nodes<'a>(node: &'a A11yNode, nodes: &mut Vec<&'a A11yNode>) {
    nodes.push(node);
    for child in &node.children {
        collect_a11y_nodes(child, nodes);
    }
}

#[test]
fn tray_menu_style_is_public_and_tokenized() {
    let style = TrayMenuPresenterStyle::winui()
        .presenter_corner_radius(8)
        .presenter_shadow_margin(12)
        .presenter_max_height(Some(320))
        .popup_animation(TrayMenuPopupAnimation::Vertical)
        .item_corner_radius(6)
        .item_font_size(14)
        .item_min_height(32)
        .item_vertical_padding(10)
        .item_horizontal_padding(14)
        .submenu_arrow_column_width(28)
        .hover_inset(5, 4)
        .separator_height(7)
        .separator_line_thickness(1)
        .separator_horizontal_inset(4)
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
        .hover_foreground_mix_percent(12);

    let menu = TrayMenu::new("WinFluent")
        .presenter_kind(TrayMenuPresenterKind::Fluent)
        .presenter_min_width(260)
        .presenter_style(style)
        .item(TrayMenuItem::new("open", "Open").tooltip("Open app"))
        .separator()
        .item(
            TrayMenuItem::submenu("tools", "Tools")
                .tooltip("Tool actions")
                .item(
                    TrayMenuItem::submenu("inspect", "Inspect")
                        .item(TrayMenuItem::new("logs", "Logs")),
                ),
        );

    let view: View<Msg> = tray_menu_presenter(menu);
    let snapshot = view_schema(&view).snapshot();

    assert!(snapshot.contains("TrayMenu"));
    assert!(snapshot.contains("min_width=260"));
    assert!(snapshot.contains("items=3"));
    assert!(snapshot.contains("submenu_arrow_column_width=28"));
    assert!(snapshot.contains("hover_inset_x=5"));
    assert!(snapshot.contains("separator_line_thickness=1"));
    assert!(snapshot.contains("light_surface=#FAFAFA"));
    assert!(snapshot.contains("dark_surface=SystemMenu"));
    assert!(snapshot.contains("hover_foreground_mix_percent=12"));
    assert!(snapshot.contains("tooltip=\"Tool actions\""));
    assert!(snapshot.contains("id=\"logs\""));
}
