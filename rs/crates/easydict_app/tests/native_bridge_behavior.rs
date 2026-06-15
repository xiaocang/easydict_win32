use std::io::Cursor;
use std::io::Write;
use std::path::PathBuf;
use std::process::{Command, Stdio};
#[cfg(windows)]
use std::time::Duration;

use easydict_app::native_bridge::{
    encode_native_message, parse_native_action, run_native_bridge, BridgeResponse,
    MAX_NATIVE_MESSAGE_BYTES, NATIVE_HOST_NAME, OCR_TRANSLATE_ACTION,
};
use easydict_app::OCR_TRANSLATE_EVENT_NAME;

#[test]
fn native_bridge_binary_signals_rs_ocr_event_not_legacy_dotnet_event() {
    let bridge_bin = include_str!("../src/bin/easydict_native_bridge.rs");

    assert_eq!(OCR_TRANSLATE_EVENT_NAME, r"Local\EasydictRs-OcrTranslate");
    assert_ne!(OCR_TRANSLATE_EVENT_NAME, r"Local\Easydict-OcrTranslate");
    assert!(
        bridge_bin.contains("OCR_TRANSLATE_EVENT_NAME"),
        "NativeBridge binary should signal the rs-specific OCR named event constant"
    );
    assert!(
        bridge_bin.contains("[easydict-native-bridge]"),
        "native bridge binary error prefix should use the Rust helper name"
    );
    assert!(
        !bridge_bin.contains(r"Local\Easydict-OcrTranslate"),
        "NativeBridge binary must not hard-code the legacy dotnet OCR event"
    );
    assert!(
        !bridge_bin.contains("Easydict NativeBridge"),
        "native bridge binary should not expose the legacy .NET helper name in errors"
    );
    assert!(
        !bridge_bin.contains("win_fluent_platform_win"),
        "native bridge binary should signal named events through the Rust-owned IPC helper"
    );
    assert!(
        bridge_bin.contains("easydict_windows_ipc::signal_named_event"),
        "native bridge binary should keep the signal boundary in lib/easydict-windows-ipc"
    );
}

#[test]
fn native_bridge_defaults_invalid_or_missing_action_to_ocr_translate() {
    assert_eq!(NATIVE_HOST_NAME, "com.easydict.bridge");
    assert_eq!(
        parse_native_action(br#"{"action":"ocr-translate"}"#),
        OCR_TRANSLATE_ACTION
    );
    assert_eq!(
        parse_native_action(br#"{"other":true}"#),
        OCR_TRANSLATE_ACTION
    );
    assert_eq!(parse_native_action(b"not-json"), OCR_TRANSLATE_ACTION);
}

#[test]
fn native_bridge_signals_ocr_and_writes_success_response() {
    let input = encode_native_message(&serde_json::json!({ "action": OCR_TRANSLATE_ACTION }))
        .expect("input frame");
    let mut output = Vec::new();
    let mut signal_count = 0usize;

    let responses = run_native_bridge(Cursor::new(input), &mut output, || {
        signal_count += 1;
        Ok(true)
    })
    .expect("bridge loop");

    assert_eq!(responses, 1);
    assert_eq!(signal_count, 1);
    assert_eq!(
        decode_single_response(&output),
        BridgeResponse::new(true, OCR_TRANSLATE_ACTION)
    );
}

#[test]
fn native_bridge_reports_false_when_ocr_event_is_not_available() {
    let input = encode_native_message(&serde_json::json!({ "action": OCR_TRANSLATE_ACTION }))
        .expect("input frame");
    let mut output = Vec::new();
    let mut signal_count = 0usize;

    let responses = run_native_bridge(Cursor::new(input), &mut output, || {
        signal_count += 1;
        Ok(false)
    })
    .expect("bridge loop");

    assert_eq!(responses, 1);
    assert_eq!(signal_count, 1);
    assert_eq!(
        decode_single_response(&output),
        BridgeResponse::new(false, OCR_TRANSLATE_ACTION)
    );
}

#[test]
fn native_bridge_reports_false_for_unknown_actions_without_signal() {
    let input =
        encode_native_message(&serde_json::json!({ "action": "status" })).expect("input frame");
    let mut output = Vec::new();
    let mut signal_count = 0usize;

    let responses = run_native_bridge(Cursor::new(input), &mut output, || {
        signal_count += 1;
        Ok(true)
    })
    .expect("bridge loop");

    assert_eq!(responses, 1);
    assert_eq!(signal_count, 0);
    assert_eq!(
        decode_single_response(&output),
        BridgeResponse::new(false, "status")
    );
}

#[test]
fn native_bridge_binary_handles_unknown_action_without_dotnet_host_or_event_signal() {
    let input =
        encode_native_message(&serde_json::json!({ "action": "status" })).expect("input frame");
    let bridge_bin = native_bridge_binary_path();
    let mut child = Command::new(bridge_bin)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("native bridge binary should spawn");

    child
        .stdin
        .take()
        .expect("stdin should be piped")
        .write_all(&input)
        .expect("native bridge input should be written");

    let output = child
        .wait_with_output()
        .expect("native bridge binary should exit");

    assert!(
        output.status.success(),
        "native bridge binary should handle unknown actions locally\nstderr:\n{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        decode_single_response(&output.stdout),
        BridgeResponse::new(false, "status")
    );

    let combined = format!(
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    for forbidden in [
        "Easydict.NativeBridge",
        "Easydict NativeBridge",
        "CompatHost",
        ".NET",
        "dotnet",
        "worker executable",
    ] {
        assert!(
            !combined.contains(forbidden),
            "native bridge binary should not expose legacy host marker {forbidden}:\n{combined}"
        );
    }
}

#[cfg(windows)]
#[test]
fn native_bridge_binary_signals_ocr_translate_event_through_real_binary() {
    let event =
        easydict_windows_ipc::test_support::TestNamedEvent::create(OCR_TRANSLATE_EVENT_NAME)
            .expect("OCR translate named event should be created");
    event.drain().expect("OCR translate event should drain");

    let input = encode_native_message(&serde_json::json!({ "action": OCR_TRANSLATE_ACTION }))
        .expect("input frame");
    let bridge_bin = native_bridge_binary_path();
    let mut child = Command::new(bridge_bin)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("native bridge binary should spawn");

    child
        .stdin
        .take()
        .expect("stdin should be piped")
        .write_all(&input)
        .expect("native bridge input should be written");

    let signaled = event
        .wait_signaled(Duration::from_secs(5))
        .expect("OCR translate event wait should succeed");
    let output = child
        .wait_with_output()
        .expect("native bridge binary should exit");

    assert!(
        output.status.success(),
        "native bridge binary should handle OCR action locally\nstderr:\n{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        decode_single_response(&output.stdout),
        BridgeResponse::new(true, OCR_TRANSLATE_ACTION)
    );
    assert!(
        signaled,
        "{} should be signaled by the real native bridge binary",
        event.name()
    );
}

fn native_bridge_binary_path() -> PathBuf {
    if let Some(path) = std::env::var_os("CARGO_BIN_EXE_easydict-native-bridge") {
        return PathBuf::from(path);
    }
    if let Some(path) = std::env::var_os("CARGO_BIN_EXE_easydict_native_bridge") {
        return PathBuf::from(path);
    }

    let test_exe = std::env::current_exe().expect("current test exe path should resolve");
    let deps_dir = test_exe
        .parent()
        .expect("test exe should have a deps parent");
    let target_dir = deps_dir
        .parent()
        .expect("deps directory should have a target profile parent");
    target_dir.join("easydict-native-bridge.exe")
}

#[test]
fn native_bridge_stops_without_response_for_invalid_lengths_or_incomplete_frames() {
    let mut signal_count = 0usize;
    let mut zero_output = Vec::new();
    let responses = run_native_bridge(Cursor::new(0u32.to_le_bytes()), &mut zero_output, || {
        signal_count += 1;
        Ok(true)
    })
    .expect("zero length");
    assert_eq!(responses, 0);
    assert_eq!(signal_count, 0);
    assert!(zero_output.is_empty());

    let mut too_large = Vec::new();
    too_large.extend_from_slice(&(MAX_NATIVE_MESSAGE_BYTES + 1).to_le_bytes());
    let mut large_output = Vec::new();
    let responses = run_native_bridge(Cursor::new(too_large), &mut large_output, || {
        signal_count += 1;
        Ok(true)
    })
    .expect("too large");
    assert_eq!(responses, 0);
    assert_eq!(signal_count, 0);
    assert!(large_output.is_empty());

    let mut incomplete = Vec::new();
    incomplete.extend_from_slice(&16u32.to_le_bytes());
    incomplete.extend_from_slice(b"{}");
    let mut incomplete_output = Vec::new();
    let responses = run_native_bridge(Cursor::new(incomplete), &mut incomplete_output, || {
        signal_count += 1;
        Ok(true)
    })
    .expect("incomplete");
    assert_eq!(responses, 0);
    assert_eq!(signal_count, 0);
    assert!(incomplete_output.is_empty());
}

fn decode_single_response(frame: &[u8]) -> BridgeResponse {
    assert!(frame.len() >= 4);
    let length = u32::from_le_bytes(frame[0..4].try_into().expect("length prefix")) as usize;
    assert_eq!(frame.len(), 4 + length);
    serde_json::from_slice(&frame[4..]).expect("response json")
}
