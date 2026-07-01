use win_fluent::a11y::resolve_accessibility_tree;
use win_fluent::prelude::*;
use win_fluent_platform_win::{WindowsPlatformAdapter, WindowsUiaControlType};

#[derive(Clone, Debug, PartialEq)]
enum Msg {
    Bool(bool),
    Selected(String),
}

fn collect_control_types(
    node: &win_fluent_platform_win::WindowsUiaNodePlan,
    output: &mut Vec<WindowsUiaControlType>,
) {
    output.push(node.control_type);
    for child in &node.children {
        collect_control_types(child, output);
    }
}

fn collect_nodes<'a>(
    node: &'a win_fluent_platform_win::WindowsUiaNodePlan,
    output: &mut Vec<&'a win_fluent_platform_win::WindowsUiaNodePlan>,
) {
    output.push(node);
    for child in &node.children {
        collect_nodes(child, output);
    }
}

#[test]
fn first_wave_controls_map_to_windows_uia_control_types() {
    let view = page("UIA Contract")
        .content(column(vec![
            button("Open").on_press(Msg::Selected("open".into())),
            checkbox("Enable", true)
                .indeterminate(true)
                .on_toggle(Msg::Bool),
            radio_group()
                .option("system", "System")
                .option("dark", "Dark")
                .selected("system")
                .on_select(Msg::Selected),
            list_view([
                ListViewItem::new("one", text("One")),
                ListViewItem::new("two", text("Two")),
            ])
            .selected("one")
            .on_select(Msg::Selected),
            image("assets/logo.png").into_view(),
            progress_bar().value(0.5).into_view(),
            text_runs([
                TextRun::plain("Open "),
                TextRun::link("documentation", "https://learn.microsoft.com/windows/apps/"),
            ])
            .on_link(Msg::Selected),
            tab_view([TabItem::new("main", "Main", text("Main tab"))
                .closable(true)
                .close_a11y_name("Close Main tab")])
            .selected("main")
            .on_select(Msg::Selected),
            tree_view([
                TreeNode::branch("root", "Root", [TreeNode::leaf("leaf", "Leaf")]),
                TreeNode::branch(
                    "collapsed",
                    "Collapsed",
                    [TreeNode::leaf("hidden", "Hidden")],
                )
                .expanded(false),
            ])
            .selected("leaf")
            .on_select(Msg::Selected),
        ]))
        .into_view();

    let a11y = resolve_accessibility_tree(&view);
    let plan = WindowsPlatformAdapter::plan_uia_tree(&a11y);

    let mut types = Vec::new();
    collect_control_types(&plan.root, &mut types);

    for expected in [
        WindowsUiaControlType::Window,
        WindowsUiaControlType::Button,
        WindowsUiaControlType::CheckBox,
        WindowsUiaControlType::RadioButton,
        WindowsUiaControlType::List,
        WindowsUiaControlType::ListItem,
        WindowsUiaControlType::Image,
        WindowsUiaControlType::ProgressBar,
        WindowsUiaControlType::Hyperlink,
        WindowsUiaControlType::Tab,
        WindowsUiaControlType::TabItem,
        WindowsUiaControlType::Tree,
        WindowsUiaControlType::TreeItem,
    ] {
        assert!(
            types.contains(&expected),
            "UIA plan missing {expected:?}; got {types:?}"
        );
    }

    let mut nodes = Vec::new();
    collect_nodes(&plan.root, &mut nodes);
    assert!(nodes
        .iter()
        .any(|node| node.name.as_deref() == Some("Close Main tab")));
    assert!(nodes
        .iter()
        .any(|node| node.help_text.as_deref() == Some("expanded")));
    assert!(nodes
        .iter()
        .any(|node| node.help_text.as_deref() == Some("collapsed")));
}
