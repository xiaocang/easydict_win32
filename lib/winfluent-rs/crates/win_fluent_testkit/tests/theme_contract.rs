use win_fluent::theme::{ThemeMode, ThemeTokens};

fn assert_multiple_of_four(value: f32, name: &str) {
    assert!(
        (value % 4.0).abs() < f32::EPSILON,
        "{name}={value} must align to the 4px grid"
    );
}

#[test]
fn fluent_theme_tokens_follow_spacing_typography_touch_and_focus_contracts() {
    for mode in [
        ThemeMode::Light,
        ThemeMode::Dark,
        ThemeMode::Minimal,
        ThemeMode::HighContrast,
    ] {
        let theme = ThemeTokens::resolve(mode);

        assert_eq!(theme.spacing.xxs, 2.0, "{mode:?} allows the 2px half-step");
        assert_multiple_of_four(theme.spacing.xs, "spacing.xs");
        assert_multiple_of_four(theme.spacing.sm, "spacing.sm");
        assert_multiple_of_four(theme.spacing.md, "spacing.md");
        assert_multiple_of_four(theme.spacing.lg, "spacing.lg");
        assert_multiple_of_four(theme.spacing.xl, "spacing.xl");

        assert_eq!(theme.typography.caption, 12.0);
        assert_eq!(theme.typography.body, 14.0);
        assert_eq!(theme.typography.body_strong, 14.0);
        assert_eq!(theme.typography.body_large, 18.0);
        assert!(theme.typography.subtitle > theme.typography.body_large);
        assert!(theme.typography.title > theme.typography.subtitle);
        assert!(theme.typography.title_large > theme.typography.title);

        assert!(theme.control.min_touch_target >= 40.0);
        assert!(theme.control.height >= 32.0);
        assert!(theme.control.icon_button >= theme.control.height);
        assert!(theme.control.primary_round_button >= theme.control.min_touch_target);

        assert_eq!(theme.stroke.control, 1.0);
        assert!(theme.stroke.focus >= 2.0);
        assert_ne!(theme.focus, theme.border);
    }
}
