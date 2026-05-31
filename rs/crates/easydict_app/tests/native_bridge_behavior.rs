use std::io::Cursor;

use easydict_app::native_bridge::{
    encode_native_message, parse_native_action, run_native_bridge, BridgeResponse,
    MAX_NATIVE_MESSAGE_BYTES, NATIVE_HOST_NAME, OCR_TRANSLATE_ACTION,
};

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
fn native_bridge_stops_without_response_for_invalid_lengths_or_incomplete_frames() {
    let mut zero_output = Vec::new();
    let responses = run_native_bridge(Cursor::new(0u32.to_le_bytes()), &mut zero_output, || {
        Ok(true)
    })
    .expect("zero length");
    assert_eq!(responses, 0);
    assert!(zero_output.is_empty());

    let mut too_large = Vec::new();
    too_large.extend_from_slice(&(MAX_NATIVE_MESSAGE_BYTES + 1).to_le_bytes());
    let mut large_output = Vec::new();
    let responses = run_native_bridge(Cursor::new(too_large), &mut large_output, || Ok(true))
        .expect("too large");
    assert_eq!(responses, 0);
    assert!(large_output.is_empty());

    let mut incomplete = Vec::new();
    incomplete.extend_from_slice(&16u32.to_le_bytes());
    incomplete.extend_from_slice(b"{}");
    let mut incomplete_output = Vec::new();
    let responses = run_native_bridge(Cursor::new(incomplete), &mut incomplete_output, || Ok(true))
        .expect("incomplete");
    assert_eq!(responses, 0);
    assert!(incomplete_output.is_empty());
}

fn decode_single_response(frame: &[u8]) -> BridgeResponse {
    assert!(frame.len() >= 4);
    let length = u32::from_le_bytes(frame[0..4].try_into().expect("length prefix")) as usize;
    assert_eq!(frame.len(), 4 + length);
    serde_json::from_slice(&frame[4..]).expect("response json")
}
