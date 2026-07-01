use win_fluent::prelude::*;

#[test]
fn public_i18n_contract_resolves_locale_fallback_and_text_direction() {
    let i18n = I18n::new("fr-FR")
        .fallback_locale("en-US")
        .with_bundle(I18nBundle::new("en-US").with("status.ready", "Ready {count}"))
        .with_bundle(I18nBundle::new("ar-SA").with("status.ready", "جاهز {count}"));
    let text = t("status.ready", "Fallback {count}").arg("count", 2);

    assert_eq!(i18n.resolve(&text), "Ready 2");
    assert_eq!(
        LocaleId::new("fr-FR").text_direction(),
        TextDirection::LeftToRight
    );
    assert_eq!(
        LocaleId::new("ar-SA").text_direction(),
        TextDirection::RightToLeft
    );
    assert_eq!(
        I18n::new("missing")
            .fallback_locale("missing-fallback")
            .resolve(&text),
        "Fallback 2"
    );
}
