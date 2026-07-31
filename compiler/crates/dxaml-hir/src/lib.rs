//! Typed, schema-checked representation of a Direct XAML document.

pub mod build;
pub mod value;

pub use build::{build, HirDocument, HirEvent, HirNode, HirProperty, NodeId};
pub use value::{
    Color, CornerRadius, GridLength, HirValue, Length, LiteralValue, ResourceKind, ResourceRef,
    Thickness,
};

use dxaml_syntax::DiagnosticBag;

/// Runs the full front-end: XML → CST → XAML AST → HIR.
///
/// Returns `None` for the document when an error prevented a complete tree from being built.
/// Diagnostics are always returned, including for a partially-analysed document.
pub fn analyze(source: &str) -> (Option<HirDocument>, DiagnosticBag) {
    let (tree, mut diagnostics) = dxaml_syntax::parse(source);
    let document = dxaml_ast::build(&tree, &mut diagnostics);
    let hir = build(&document, &mut diagnostics);
    (hir, diagnostics)
}

#[cfg(test)]
mod tests {
    use super::*;
    use dxaml_schema::ControlKind;
    use dxaml_syntax::codes;

    const HEADER: &str = concat!(
        r#"xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" "#,
        r#"xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" "#,
        r#"xmlns:d="http://schemas.microsoft.com/expression/blend/2008" "#,
        r#"xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" "#,
        r#"mc:Ignorable="d" x:Class="Easydict.Sample""#
    );

    fn wrap(body: &str) -> String {
        format!("<UserControl {HEADER}>{body}</UserControl>")
    }

    fn analyze_body(body: &str) -> (Option<HirDocument>, DiagnosticBag) {
        analyze(&wrap(body))
    }

    fn codes_of(diagnostics: &DiagnosticBag) -> Vec<&'static str> {
        diagnostics.sorted().into_iter().map(|d| d.code).collect()
    }

    #[test]
    fn builds_a_minimal_card() {
        let (hir, diagnostics) = analyze_body(
            r#"<Border x:Name="RootBorder" Background="{ThemeResource CardBrush}" CornerRadius="4" Margin="0,0,0,2">
                   <StackPanel Spacing="8">
                       <TextBlock x:Name="ServiceNameText" FontSize="12" FontWeight="SemiBold"/>
                       <TextBlock x:Name="ResultText" TextWrapping="Wrap" Visibility="Collapsed"/>
                   </StackPanel>
               </Border>"#,
        );
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());

        let hir = hir.expect("hir");
        assert_eq!(hir.class_name, "Easydict.Sample");
        assert_eq!(hir.nodes[hir.root].kind, ControlKind::UserControl);

        let names: Vec<&str> = hir
            .nodes
            .iter()
            .filter_map(|node| node.name.as_deref())
            .collect();
        assert_eq!(names, vec!["RootBorder", "ServiceNameText", "ResultText"]);

        let border = &hir.nodes[hir.nodes[hir.root].children[0]];
        let background = border
            .properties
            .iter()
            .find(|p| p.name == "Background")
            .expect("Background");
        assert_eq!(
            background.value,
            HirValue::Resource(ResourceRef {
                kind: ResourceKind::Theme,
                key: "CardBrush".to_string()
            })
        );
    }

    #[test]
    fn theme_resources_are_accepted_for_non_brush_properties() {
        // The real card writes BorderThickness and CornerRadius as theme resources.
        let (_, diagnostics) = analyze_body(
            r#"<Border BorderThickness="{ThemeResource EasydictCardBorderThickness}"
                       CornerRadius="{ThemeResource EasydictCardCornerRadius}">
                   <TextBlock/>
               </Border>"#,
        );
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());
    }

    #[test]
    fn grid_definitions_become_children() {
        let (hir, diagnostics) = analyze_body(
            r#"<Grid>
                   <Grid.RowDefinitions>
                       <RowDefinition Height="Auto"/>
                       <RowDefinition Height="*"/>
                   </Grid.RowDefinitions>
                   <TextBlock Grid.Row="0"/>
               </Grid>"#,
        );
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());

        let hir = hir.expect("hir");
        let grid = &hir.nodes[hir.nodes[hir.root].children[0]];
        let kinds: Vec<ControlKind> = grid
            .children
            .iter()
            .map(|&child| hir.nodes[child].kind)
            .collect();
        assert_eq!(
            kinds,
            vec![
                ControlKind::RowDefinition,
                ControlKind::RowDefinition,
                ControlKind::TextBlock
            ]
        );

        let first = &hir.nodes[grid.children[0]];
        assert_eq!(
            first.properties[0].value,
            HirValue::Literal(LiteralValue::GridLength(GridLength::Auto))
        );
    }

    #[test]
    fn records_events_as_actions() {
        let (hir, diagnostics) =
            analyze_body(r#"<Border PointerPressed="OnHeaderPointerPressed"><TextBlock/></Border>"#);
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());

        let hir = hir.expect("hir");
        let border = &hir.nodes[hir.nodes[hir.root].children[0]];
        assert_eq!(border.events.len(), 1);
        assert_eq!(border.events[0].ir_event, "pointerPressed");
        assert_eq!(border.events[0].handler, "OnHeaderPointerPressed");
    }

    #[test]
    fn rejects_controls_outside_the_subset() {
        let (_, diagnostics) = analyze_body(r#"<ProgressRing/>"#);
        assert!(codes_of(&diagnostics).contains(&codes::UNSUPPORTED_CONTROL));
    }

    #[test]
    fn rejects_bindings() {
        let (_, diagnostics) =
            analyze_body(r#"<TextBlock Text="{Binding ResultText, Mode=OneWay}"/>"#);
        assert!(codes_of(&diagnostics).contains(&codes::UNSUPPORTED_MARKUP_EXTENSION));
    }

    #[test]
    fn rejects_properties_on_the_wrong_element() {
        let (_, diagnostics) = analyze_body(r#"<Border TextWrapping="Wrap"><TextBlock/></Border>"#);
        assert!(codes_of(&diagnostics).contains(&codes::PROPERTY_NOT_VALID_HERE));
    }

    #[test]
    fn rejects_unsupported_attached_properties() {
        let (_, diagnostics) =
            analyze_body(r#"<Grid><TextBlock ToolTipService.ToolTip="hi"/></Grid>"#);
        assert!(codes_of(&diagnostics).contains(&codes::UNSUPPORTED_ATTACHED_PROPERTY));
    }

    #[test]
    fn rejects_grid_attached_properties_outside_a_grid() {
        let (_, diagnostics) = analyze_body(r#"<StackPanel><TextBlock Grid.Row="1"/></StackPanel>"#);
        assert!(codes_of(&diagnostics).contains(&codes::PROPERTY_NOT_VALID_HERE));
    }

    #[test]
    fn rejects_bad_enum_values() {
        let (_, diagnostics) = analyze_body(r#"<TextBlock Visibility="Hidden"/>"#);
        assert!(codes_of(&diagnostics).contains(&codes::BAD_VALUE));
    }

    #[test]
    fn rejects_duplicate_names() {
        let (_, diagnostics) = analyze_body(
            r#"<StackPanel><TextBlock x:Name="Same"/><TextBlock x:Name="Same"/></StackPanel>"#,
        );
        assert!(codes_of(&diagnostics).contains(&codes::DUPLICATE_NAME));
    }

    #[test]
    fn rejects_unsupported_directives() {
        let (_, diagnostics) = analyze_body(r#"<TextBlock x:Uid="Greeting"/>"#);
        assert!(codes_of(&diagnostics).contains(&codes::UNSUPPORTED_DIRECTIVE));
    }

    #[test]
    fn rejects_a_missing_class() {
        let source = format!(
            r#"<UserControl xmlns="{}" xmlns:x="{}"><TextBlock/></UserControl>"#,
            dxaml_schema::NS_PRESENTATION,
            dxaml_schema::NS_DIRECTIVES
        );
        let (hir, diagnostics) = analyze(&source);
        assert!(hir.is_none());
        assert!(codes_of(&diagnostics).contains(&codes::MISSING_X_CLASS));
    }

    #[test]
    fn rejects_a_non_usercontrol_root() {
        let source = format!(
            r#"<Border xmlns="{}" xmlns:x="{}" x:Class="A.B"/>"#,
            dxaml_schema::NS_PRESENTATION,
            dxaml_schema::NS_DIRECTIVES
        );
        let (_, diagnostics) = analyze(&source);
        assert!(codes_of(&diagnostics).contains(&codes::ROOT_MUST_BE_USERCONTROL));
    }

    #[test]
    fn enforces_single_child_containers() {
        let (_, diagnostics) = analyze_body(r#"<Border><TextBlock/><TextBlock/></Border>"#);
        assert!(codes_of(&diagnostics).contains(&codes::WRONG_CHILD_COUNT));
    }

    #[test]
    fn rejects_text_outside_a_textblock() {
        let (_, diagnostics) = analyze_body(r#"<Border>stray text<TextBlock/></Border>"#);
        assert!(codes_of(&diagnostics).contains(&codes::ELEMENT_NOT_VALID_HERE));
    }

    #[test]
    fn rejects_text_alongside_a_text_attribute() {
        let (_, diagnostics) = analyze_body(r#"<TextBlock Text="a">b</TextBlock>"#);
        assert!(codes_of(&diagnostics).contains(&codes::TEXT_AND_TEXT_ATTRIBUTE));
    }

    #[test]
    fn keeps_text_content() {
        let (hir, diagnostics) = analyze_body(r#"<TextBlock>hello</TextBlock>"#);
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());
        let hir = hir.expect("hir");
        let text = &hir.nodes[hir.nodes[hir.root].children[0]];
        assert_eq!(text.text.as_deref(), Some("hello"));
    }
}
