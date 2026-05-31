use win_fluent::prelude::Task;

use crate::{Message, HOTKEY_OCR_TRANSLATE, PROTOCOL_EASYDICT};

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

pub fn startup_activation_task_for_args<I, S>(args: I) -> Task<Message>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    match parse_startup_activation(args) {
        Some(StartupActivation::OcrTranslate) => {
            Task::message(Message::HotkeyTriggered(HOTKEY_OCR_TRANSLATE.to_string()))
        }
        None => Task::none(),
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
    if !scheme.eq_ignore_ascii_case(PROTOCOL_EASYDICT) {
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
