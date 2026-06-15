use std::io::{self, Read, Write};

use serde::{Deserialize, Serialize};

pub const NATIVE_HOST_NAME: &str = "com.easydict.rs.bridge";
pub const OCR_TRANSLATE_ACTION: &str = "ocr-translate";
pub const INVALID_NATIVE_ACTION: &str = "invalid";
pub const MAX_NATIVE_MESSAGE_BYTES: u32 = 1024 * 1024;

#[derive(Clone, Debug, Deserialize, Eq, PartialEq)]
struct BridgeRequest {
    action: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct BridgeResponse {
    pub success: bool,
    pub action: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

impl BridgeResponse {
    pub fn new(success: bool, action: impl Into<String>) -> Self {
        Self {
            success,
            action: action.into(),
            error: None,
        }
    }

    pub fn error(action: impl Into<String>, error: impl Into<String>) -> Self {
        Self {
            success: false,
            action: action.into(),
            error: Some(error.into()),
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
        let action = match parse_native_action(&payload) {
            Ok(action) => action,
            Err(error) => {
                write_native_message(
                    &mut writer,
                    &BridgeResponse::error(INVALID_NATIVE_ACTION, error.to_string()),
                )?;
                responses += 1;
                continue;
            }
        };
        let response = if action == OCR_TRANSLATE_ACTION {
            match signal_ocr_translate() {
                Ok(true) => BridgeResponse::new(true, action),
                Ok(false) => BridgeResponse::error(action, "OCR translate event is not available"),
                Err(error) => BridgeResponse::error(
                    action,
                    format!("failed to signal OCR translate event: {error}"),
                ),
            }
        } else {
            BridgeResponse::error(
                action.clone(),
                format!("unsupported native message action: {action}"),
            )
        };

        write_native_message(&mut writer, &response)?;
        responses += 1;
    }

    Ok(responses)
}

pub fn parse_native_action(payload: &[u8]) -> io::Result<String> {
    let request = serde_json::from_slice::<BridgeRequest>(payload).map_err(|error| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            format!("invalid native message JSON: {error}"),
        )
    })?;
    let action = request.action.ok_or_else(|| {
        io::Error::new(io::ErrorKind::InvalidData, "missing native message action")
    })?;
    if action.trim().is_empty() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "native message action must be non-empty",
        ));
    }

    Ok(action)
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
    let Some(length_bytes) = read_length_prefix_or_eof(reader)? else {
        return Ok(None);
    };

    let length = u32::from_le_bytes(length_bytes);
    if length == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "native message length must be non-zero",
        ));
    }
    if length > MAX_NATIVE_MESSAGE_BYTES {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "native message exceeds maximum size",
        ));
    }

    let mut payload = vec![0u8; length as usize];
    read_exact_payload(reader, &mut payload)?;

    Ok(Some(payload))
}

fn write_native_message<T: Serialize>(writer: &mut impl Write, message: &T) -> io::Result<()> {
    let frame = encode_native_message(message)?;
    writer.write_all(&frame)?;
    writer.flush()
}

fn read_length_prefix_or_eof(reader: &mut impl Read) -> io::Result<Option<[u8; 4]>> {
    let mut length_bytes = [0u8; 4];
    let mut total_read = 0usize;
    while total_read < length_bytes.len() {
        let read = reader.read(&mut length_bytes[total_read..])?;
        if read == 0 {
            if total_read == 0 {
                return Ok(None);
            }

            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "incomplete native message length prefix",
            ));
        }

        total_read += read;
    }

    Ok(Some(length_bytes))
}

fn read_exact_payload(reader: &mut impl Read, buffer: &mut [u8]) -> io::Result<()> {
    reader.read_exact(buffer).map_err(|error| {
        if error.kind() == io::ErrorKind::UnexpectedEof {
            io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "incomplete native message payload",
            )
        } else {
            error
        }
    })
}

fn json_io_error(error: serde_json::Error) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, error)
}
