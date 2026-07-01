use win_fluent::a11y::A11yRole;
use win_fluent::diff::{diff_views, ViewChangeKind};
use win_fluent::prelude::*;
use win_fluent::resolve_accessibility_tree;

#[derive(Clone, Debug, PartialEq)]
enum Msg {
    Pointer(PointerPosition),
    Wheel(PointerWheel),
    Escape,
    Context,
}

#[test]
fn overlay_adaptive_lazy_pointer_and_capture_contracts_are_serialized() {
    let overlay_view = overlay(text("Base content"))
        .id("modal-layer")
        .layer(
            OverlayLayer::new(button("Floating action"))
                .align(Alignment::End, Alignment::End)
                .scrim(2.0)
                .blocks_input(true),
        )
        .into_view();

    let adaptive_view = adaptive_switch(720, text("Wide layout"), text("Narrow layout"))
        .id("adaptive-shell")
        .resolved_width(640.0)
        .into_view();

    let pointer_view = pointer_region(text("Canvas"))
        .id("capture-pointer")
        .width(Length::Fixed(320))
        .height(Length::Fixed(240))
        .on_move(Msg::Pointer)
        .on_left_down(Msg::Pointer)
        .on_left_up(Msg::Pointer)
        .on_double_click(Msg::Pointer)
        .on_right_down(Msg::Context)
        .on_wheel(Msg::Wheel)
        .on_escape(Msg::Escape)
        .into_view();

    let capture_view: View<Msg> = capture_overlay(CaptureOverlayPhase::Selecting)
        .id("capture")
        .detection_depth(2)
        .dragging(true)
        .detected_rect(CaptureOverlayRect::new(10, 20, 300, 180))
        .selection_rect(CaptureOverlayRect::new(16, 28, 120, 80))
        .handles_visible(true)
        .magnifier_visible(true)
        .background(CaptureOverlayBackground::new("desktop.bgra", 1920, 1080))
        .cursor(CaptureOverlayPoint::new(50, 60))
        .into_view();

    let view = column(vec![
        overlay_view,
        adaptive_view,
        lazy("settings-row:advanced", text("Lazy content"))
            .id("lazy-row")
            .into_view(),
        pointer_view,
        capture_view,
    ])
    .into_view();

    let schema = view_schema(&view).snapshot();
    assert!(schema.contains("Overlay"));
    assert!(schema.contains("blocking_layers=1"));
    assert!(schema.contains("scrim_layers=1"));
    assert!(schema.contains("layout=\"End/End/scrim=1.00/block=true\""));
    assert!(schema.contains("AdaptiveSwitch"));
    assert!(schema.contains("resolved_width=640.00"));
    assert!(schema.contains("resolved_branch=narrow"));
    assert!(schema.contains("Narrow layout"));
    assert!(!schema.contains("Wide layout"));
    assert!(schema.contains("Lazy"));
    assert!(schema.contains("key=\"settings-row:advanced\""));
    assert!(schema.contains("PointerRegion"));
    assert!(schema.contains("move=position"));
    assert!(schema.contains("wheel=wheel"));
    assert!(schema.contains("escape=message"));
    assert!(schema.contains("CaptureOverlay"));
    assert!(schema.contains("background_pixels=1920x1080"));
    assert!(schema.contains("cursor=(50,60)"));
    assert!(schema.contains("handles_visible=true"));
    assert!(schema.contains("magnifier_visible=true"));

    let a11y = resolve_accessibility_tree(&view);
    let nodes = collect_a11y_nodes(&a11y);
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::Group
            && node
                .help_text
                .as_deref()
                .is_some_and(|text| text.contains("blocking_layers=1"))
    }));
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::Group
            && node
                .help_text
                .as_deref()
                .is_some_and(|text| text.contains("resolved_branch=narrow"))
    }));
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::Pane
            && node.name.as_deref() == Some("OCR capture overlay")
            && node
                .description
                .as_deref()
                .is_some_and(|text| text.contains("background=Some((1920, 1080))"))
    }));
}

#[test]
fn lazy_key_changes_are_reported_by_diff() {
    let before: View<Msg> = lazy("row:1", text("Same content")).into_view();
    let after: View<Msg> = lazy("row:2", text("Same content")).into_view();

    let changes = diff_views(&before, &after);

    assert!(changes.iter().any(|change| {
        change.path.to_string() == "root" && change.kind == ViewChangeKind::Updated { kind: "Lazy" }
    }));
}

fn collect_a11y_nodes(root: &win_fluent::A11yNode) -> Vec<&win_fluent::A11yNode> {
    fn visit<'a>(node: &'a win_fluent::A11yNode, nodes: &mut Vec<&'a win_fluent::A11yNode>) {
        nodes.push(node);
        for child in &node.children {
            visit(child, nodes);
        }
    }

    let mut nodes = Vec::new();
    visit(root, &mut nodes);
    nodes
}
