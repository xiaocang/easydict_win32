use crate::{Message, HOTKEY_OCR_TRANSLATE, LEGACY_PROTOCOL_EASYDICT, PROTOCOL_EASYDICT};

pub const OCR_TRANSLATE_ARGUMENT: &str = "--ocr-translate";
pub const OCR_TRANSLATE_PROTOCOL_PAYLOAD: &str = "ocr-translate";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StartupActivation {
    OcrTranslate,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StartupActivationDisposition {
    NormalLaunch,
    SignalRunningInstanceAndExit(StartupActivation),
    ColdLaunchWithPendingActivation(StartupActivation),
}

pub fn parse_startup_activation<I, S>(args: I) -> Option<StartupActivation>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    args.into_iter()
        .find_map(|arg| parse_startup_activation_arg(arg.as_ref()))
}

pub fn startup_activation_message_for_args<I, S>(args: I) -> Option<Message>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    match parse_startup_activation(args) {
        Some(StartupActivation::OcrTranslate) => {
            Some(Message::HotkeyTriggered(HOTKEY_OCR_TRANSLATE.to_string()))
        }
        None => None,
    }
}

pub fn resolve_startup_activation_disposition<I, S, F, E>(
    args: I,
    mut signal_running_instance: F,
) -> Result<StartupActivationDisposition, E>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
    F: FnMut(StartupActivation) -> Result<bool, E>,
{
    let Some(activation) = parse_startup_activation(args) else {
        return Ok(StartupActivationDisposition::NormalLaunch);
    };

    if signal_running_instance(activation)? {
        Ok(StartupActivationDisposition::SignalRunningInstanceAndExit(
            activation,
        ))
    } else {
        Ok(StartupActivationDisposition::ColdLaunchWithPendingActivation(activation))
    }
}

fn parse_startup_activation_arg(arg: &str) -> Option<StartupActivation> {
    let arg = arg.trim().trim_matches('"');

    if arg.eq_ignore_ascii_case(OCR_TRANSLATE_ARGUMENT) {
        return Some(StartupActivation::OcrTranslate);
    }

    parse_protocol_activation(arg)
}

fn parse_protocol_activation(arg: &str) -> Option<StartupActivation> {
    let (scheme, payload) = arg.split_once(':')?;
    if !protocol_scheme_is_supported(scheme) {
        return None;
    }

    let payload = payload
        .trim_start_matches('/')
        .split(['?', '#'])
        .next()
        .unwrap_or_default()
        .trim_matches('/');

    if payload.eq_ignore_ascii_case(OCR_TRANSLATE_PROTOCOL_PAYLOAD) {
        Some(StartupActivation::OcrTranslate)
    } else {
        None
    }
}

fn protocol_scheme_is_supported(scheme: &str) -> bool {
    scheme.eq_ignore_ascii_case(PROTOCOL_EASYDICT)
        || scheme.eq_ignore_ascii_case(LEGACY_PROTOCOL_EASYDICT)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_shell_and_protocol_ocr_activation() {
        for arg in [
            "--ocr-translate",
            "\"--ocr-translate\"",
            "easydict-rs://ocr-translate",
            "EASYDICT-RS://OCR-TRANSLATE?source=browser",
            "easydict://ocr-translate#native-message",
            "easydict-rs:ocr-translate",
        ] {
            assert_eq!(
                parse_startup_activation([arg]),
                Some(StartupActivation::OcrTranslate),
                "{arg}"
            );
        }
    }

    #[test]
    fn ignores_non_ocr_or_unsupported_protocol_activation() {
        for arg in [
            "--unknown",
            "easydict-rs://settings",
            "easydict-rs://ocr-translate-extra",
            "https://ocr-translate",
            "easydict-worker://ocr-translate",
        ] {
            assert_eq!(parse_startup_activation([arg]), None, "{arg}");
        }
    }

    #[test]
    fn startup_activation_disposition_separates_signal_and_cold_launch() {
        let mut signaled = Vec::new();
        let disposition =
            resolve_startup_activation_disposition(["easydict-rs://ocr-translate"], |activation| {
                signaled.push(activation);
                Ok::<_, ()>(true)
            })
            .expect("disposition should resolve");

        assert_eq!(
            disposition,
            StartupActivationDisposition::SignalRunningInstanceAndExit(
                StartupActivation::OcrTranslate
            )
        );
        assert_eq!(signaled, [StartupActivation::OcrTranslate]);

        let cold =
            resolve_startup_activation_disposition(["--ocr-translate"], |_| Ok::<_, ()>(false))
                .expect("cold launch should resolve");
        assert_eq!(
            cold,
            StartupActivationDisposition::ColdLaunchWithPendingActivation(
                StartupActivation::OcrTranslate
            )
        );
    }
}
