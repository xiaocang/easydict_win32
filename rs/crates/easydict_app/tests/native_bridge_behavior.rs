use std::io::Cursor;
use std::io::Write;
use std::path::PathBuf;
use std::process::{Command, Stdio};
#[cfg(windows)]
use std::time::Duration;

use easydict_app::native_bridge::{
    encode_native_message, parse_native_action, run_native_bridge, BridgeResponse,
    INVALID_NATIVE_ACTION, MAX_NATIVE_MESSAGE_BYTES, NATIVE_HOST_NAME, OCR_TRANSLATE_ACTION,
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
fn native_bridge_rejects_invalid_or_missing_action_before_signal() {
    assert_eq!(NATIVE_HOST_NAME, "com.easydict.rs.bridge");
    assert_eq!(
        parse_native_action(br#"{"action":"ocr-translate"}"#).expect("valid action"),
        OCR_TRANSLATE_ACTION
    );

    for (payload, expected_error) in [
        (b"not-json".as_slice(), "invalid native message JSON"),
        (
            br#"{"other":true}"#.as_slice(),
            "missing native message action",
        ),
        (
            br#"{"action":null}"#.as_slice(),
            "missing native message action",
        ),
        (
            br#"{"action":123}"#.as_slice(),
            "invalid native message JSON",
        ),
        (
            br#"{"action":""}"#.as_slice(),
            "native message action must be non-empty",
        ),
        (
            br#"{"action":"   "}"#.as_slice(),
            "native message action must be non-empty",
        ),
    ] {
        let error = parse_native_action(payload).expect_err("invalid action should be rejected");
        assert_eq!(error.kind(), std::io::ErrorKind::InvalidData);
        assert!(
            error.to_string().contains(expected_error),
            "invalid action error should be diagnosable: {error}"
        );
    }
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
fn native_bridge_reports_unavailable_ocr_event_as_json_error() {
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
    let response = decode_single_response(&output);
    assert!(!response.success);
    assert_eq!(response.action, OCR_TRANSLATE_ACTION);
    assert_eq!(
        response.error.as_deref(),
        Some("OCR translate event is not available")
    );
}

#[test]
fn native_bridge_reports_unknown_actions_as_json_error_without_signal() {
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
    let response = decode_single_response(&output);
    assert!(!response.success);
    assert_eq!(response.action, "status");
    assert_eq!(
        response.error.as_deref(),
        Some("unsupported native message action: status")
    );
}

#[test]
fn native_bridge_rejects_malformed_or_missing_action_without_signal() {
    for (payload, expected_error) in [
        (b"not-json".as_slice(), "invalid native message JSON"),
        (
            br#"{"other":true}"#.as_slice(),
            "missing native message action",
        ),
        (
            br#"{"action":null}"#.as_slice(),
            "missing native message action",
        ),
        (
            br#"{"action":123}"#.as_slice(),
            "invalid native message JSON",
        ),
        (
            br#"{"action":""}"#.as_slice(),
            "native message action must be non-empty",
        ),
        (
            br#"{"action":"   "}"#.as_slice(),
            "native message action must be non-empty",
        ),
    ] {
        let input = encode_raw_native_message(payload);
        let mut output = Vec::new();
        let mut signal_count = 0usize;

        let responses = run_native_bridge(Cursor::new(input), &mut output, || {
            signal_count += 1;
            Ok(true)
        })
        .expect("bridge loop should report invalid action locally");

        assert_eq!(responses, 1);
        assert_eq!(signal_count, 0);

        let response = decode_single_response(&output);
        assert!(!response.success);
        assert_eq!(response.action, INVALID_NATIVE_ACTION);
        let error = response
            .error
            .as_deref()
            .expect("invalid action response should carry an error");
        assert!(
            error.contains(expected_error),
            "invalid action response should be diagnosable: {error}"
        );
    }
}

#[test]
fn native_bridge_reports_signal_backend_errors_as_json_without_process_error() {
    let input = encode_native_message(&serde_json::json!({ "action": OCR_TRANSLATE_ACTION }))
        .expect("input frame");
    let mut output = Vec::new();
    let mut signal_count = 0usize;

    let responses = run_native_bridge(Cursor::new(input), &mut output, || {
        signal_count += 1;
        Err(std::io::Error::new(
            std::io::ErrorKind::PermissionDenied,
            "named event access denied",
        ))
    })
    .expect("signal backend errors should be serialized as a response");

    assert_eq!(responses, 1);
    assert_eq!(signal_count, 1);

    let response = decode_single_response(&output);
    assert!(!response.success);
    assert_eq!(response.action, OCR_TRANSLATE_ACTION);
    let error = response.error.as_deref().expect("signal error");
    assert!(error.contains("failed to signal OCR translate event"));
    assert!(error.contains("named event access denied"));
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
    let response = decode_single_response(&output.stdout);
    assert!(!response.success);
    assert_eq!(response.action, "status");
    assert_eq!(
        response.error.as_deref(),
        Some("unsupported native message action: status")
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

#[test]
fn native_bridge_binary_rejects_malformed_json_without_dotnet_host_or_event_signal() {
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
        .write_all(&encode_raw_native_message(b"not-json"))
        .expect("native bridge input should be written");

    let output = child
        .wait_with_output()
        .expect("native bridge binary should exit");

    assert!(
        output.status.success(),
        "native bridge binary should reject malformed JSON locally\nstderr:\n{}",
        String::from_utf8_lossy(&output.stderr)
    );

    let response = decode_single_response(&output.stdout);
    assert!(!response.success);
    assert_eq!(response.action, INVALID_NATIVE_ACTION);
    let error = response
        .error
        .as_deref()
        .expect("invalid JSON response should carry an error");
    assert!(
        error.contains("invalid native message JSON"),
        "invalid JSON response should be diagnosable: {error}"
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

#[test]
fn native_bridge_binary_rejects_invalid_frame_length_without_dotnet_host_wording() {
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
        .write_all(&0u32.to_le_bytes())
        .expect("native bridge input should be written");

    let output = child
        .wait_with_output()
        .expect("native bridge binary should exit");

    assert!(
        !output.status.success(),
        "native bridge binary should reject invalid frame length"
    );
    assert!(
        output.stdout.is_empty(),
        "invalid frame length should not produce a success response"
    );

    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(
        stderr.contains("[easydict-native-bridge]"),
        "invalid frame length should be reported by the Rust helper: {stderr}"
    );
    assert!(
        stderr.contains("native message length must be non-zero"),
        "invalid frame length should be diagnosable: {stderr}"
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
            !stderr.contains(forbidden),
            "native bridge binary should not expose legacy host marker {forbidden}:\n{stderr}"
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
fn native_bridge_stops_cleanly_on_empty_stdin_without_response() {
    let mut signal_count = 0usize;
    let mut output = Vec::new();
    let responses = run_native_bridge(Cursor::new(Vec::new()), &mut output, || {
        signal_count += 1;
        Ok(true)
    })
    .expect("empty stdin");

    assert_eq!(responses, 0);
    assert_eq!(signal_count, 0);
    assert!(output.is_empty());
}

#[test]
fn native_bridge_rejects_invalid_lengths_or_incomplete_frames_without_signal() {
    let cases = [
        (
            0u32.to_le_bytes().to_vec(),
            std::io::ErrorKind::InvalidData,
            "native message length must be non-zero",
        ),
        (
            (MAX_NATIVE_MESSAGE_BYTES + 1).to_le_bytes().to_vec(),
            std::io::ErrorKind::InvalidData,
            "native message exceeds maximum size",
        ),
        (
            vec![16, 0],
            std::io::ErrorKind::UnexpectedEof,
            "incomplete native message length prefix",
        ),
        (
            {
                let mut incomplete = Vec::new();
                incomplete.extend_from_slice(&16u32.to_le_bytes());
                incomplete.extend_from_slice(b"{}");
                incomplete
            },
            std::io::ErrorKind::UnexpectedEof,
            "incomplete native message payload",
        ),
    ];

    for (input, expected_kind, expected_message) in cases {
        let mut signal_count = 0usize;
        let mut output = Vec::new();
        let error = run_native_bridge(Cursor::new(input), &mut output, || {
            signal_count += 1;
            Ok(true)
        })
        .expect_err("invalid native message frame should fail");

        assert_eq!(error.kind(), expected_kind);
        assert!(
            error.to_string().contains(expected_message),
            "invalid frame error should mention {expected_message}: {error}"
        );
        assert_eq!(signal_count, 0);
        assert!(output.is_empty());
    }
}

fn decode_single_response(frame: &[u8]) -> BridgeResponse {
    assert!(frame.len() >= 4);
    let length = u32::from_le_bytes(frame[0..4].try_into().expect("length prefix")) as usize;
    assert_eq!(frame.len(), 4 + length);
    serde_json::from_slice(&frame[4..]).expect("response json")
}

fn encode_raw_native_message(payload: &[u8]) -> Vec<u8> {
    let mut frame = Vec::with_capacity(4 + payload.len());
    frame.extend_from_slice(&(payload.len() as u32).to_le_bytes());
    frame.extend_from_slice(payload);
    frame
}
