use std::io::{self, Read, Write};

use serde::{Deserialize, Serialize};

pub const NATIVE_HOST_NAME: &str = "com.easydict.bridge";
pub const OCR_TRANSLATE_ACTION: &str = "ocr-translate";
pub const MAX_NATIVE_MESSAGE_BYTES: u32 = 1024 * 1024;

#[derive(Clone, Debug, Deserialize, Eq, PartialEq)]
struct BridgeRequest {
    action: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct BridgeResponse {
    pub success: bool,
    pub action: String,
}

impl BridgeResponse {
    pub fn new(success: bool, action: impl Into<String>) -> Self {
        Self {
            success,
            action: action.into(),
        }
    }
}

pub fn run_native_bridge<R, W, F>(
    mut reader: R,
    mut writer: W,
    mut signal_ocr_translate: F,
) -> io::Result<usize>
where
    R: Read,
    W: Write,
    F: FnMut() -> io::Result<bool>,
{
    let mut responses = 0usize;

    while let Some(payload) = read_native_message(&mut reader)? {
        let action = parse_native_action(&payload);
        let success = if action == OCR_TRANSLATE_ACTION {
            signal_ocr_translate()?
        } else {
            false
        };

        write_native_message(&mut writer, &BridgeResponse::new(success, action))?;
        responses += 1;
    }

    Ok(responses)
}

pub fn parse_native_action(payload: &[u8]) -> String {
    serde_json::from_slice::<BridgeRequest>(payload)
        .ok()
        .and_then(|request| request.action)
        .unwrap_or_else(|| OCR_TRANSLATE_ACTION.to_string())
}

pub fn encode_native_message<T: Serialize>(message: &T) -> io::Result<Vec<u8>> {
    let payload = serde_json::to_vec(message).map_err(json_io_error)?;
    if payload.len() > MAX_NATIVE_MESSAGE_BYTES as usize {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "native message exceeds maximum size",
        ));
    }

    let mut frame = Vec::with_capacity(4 + payload.len());
    frame.extend_from_slice(&(payload.len() as u32).to_le_bytes());
    frame.extend_from_slice(&payload);
    Ok(frame)
}

fn read_native_message(reader: &mut impl Read) -> io::Result<Option<Vec<u8>>> {
    let mut length_bytes = [0u8; 4];
    if !read_exact_or_eof(reader, &mut length_bytes)? {
        return Ok(None);
    }

    let length = u32::from_le_bytes(length_bytes);
    if length == 0 || length > MAX_NATIVE_MESSAGE_BYTES {
        return Ok(None);
    }

    let mut payload = vec![0u8; length as usize];
    if !read_exact_or_eof(reader, &mut payload)? {
        return Ok(None);
    }

    Ok(Some(payload))
}

fn write_native_message<T: Serialize>(writer: &mut impl Write, message: &T) -> io::Result<()> {
    let frame = encode_native_message(message)?;
    writer.write_all(&frame)?;
    writer.flush()
}

fn read_exact_or_eof(reader: &mut impl Read, buffer: &mut [u8]) -> io::Result<bool> {
    let mut total_read = 0usize;
    while total_read < buffer.len() {
        let read = reader.read(&mut buffer[total_read..])?;
        if read == 0 {
            return Ok(false);
        }

        total_read += read;
    }

    Ok(true)
}

fn json_io_error(error: serde_json::Error) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, error)
}
