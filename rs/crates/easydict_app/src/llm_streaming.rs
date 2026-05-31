use serde_json::Value;
use std::str::Lines;

const DATA_PREFIX: &str = "data: ";
const EVENT_PREFIX: &str = "event: ";
const DONE_MARKER: &str = "[DONE]";
const RESPONSES_DELTA_EVENT: &str = "response.output_text.delta";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ChatRole {
    System,
    User,
    Assistant,
}

impl ChatRole {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::System => "system",
            Self::User => "user",
            Self::Assistant => "assistant",
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ChatMessage {
    pub role: ChatRole,
    pub content: String,
}

impl ChatMessage {
    pub fn new(role: ChatRole, content: impl Into<String>) -> Self {
        Self {
            role,
            content: content.into(),
        }
    }

    pub fn role_str(&self) -> &'static str {
        self.role.as_str()
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OpenAiStreamingFormat {
    ChatCompletions,
    Responses,
}

pub fn parse_openai_sse_chunks(format: OpenAiStreamingFormat, sse: &str) -> Vec<String> {
    match format {
        OpenAiStreamingFormat::ChatCompletions => parse_chat_completions_sse_chunks(sse),
        OpenAiStreamingFormat::Responses => parse_responses_sse_chunks(sse),
    }
}

pub fn chat_completions_sse_chunks(sse: &str) -> ChatCompletionsSseChunks<'_> {
    ChatCompletionsSseChunks {
        lines: sse.lines(),
        done: false,
    }
}

pub fn parse_chat_completions_sse_chunks(sse: &str) -> Vec<String> {
    chat_completions_sse_chunks(sse).collect()
}

pub struct ChatCompletionsSseChunks<'a> {
    lines: Lines<'a>,
    done: bool,
}

impl Iterator for ChatCompletionsSseChunks<'_> {
    type Item = String;

    fn next(&mut self) -> Option<Self::Item> {
        if self.done {
            return None;
        }

        for line in self.lines.by_ref() {
            if line.is_empty() {
                continue;
            }

            let Some(data) = line.strip_prefix(DATA_PREFIX) else {
                continue;
            };

            if data == DONE_MARKER {
                self.done = true;
                return None;
            }

            if let Some(content) = extract_chat_completions_delta(data) {
                return Some(content);
            }
        }

        self.done = true;
        None
    }
}

pub fn responses_sse_chunks(sse: &str) -> ResponsesSseChunks<'_> {
    ResponsesSseChunks {
        lines: sse.lines(),
        current_event: None,
        done: false,
    }
}

pub fn parse_responses_sse_chunks(sse: &str) -> Vec<String> {
    responses_sse_chunks(sse).collect()
}

pub struct ResponsesSseChunks<'a> {
    lines: Lines<'a>,
    current_event: Option<&'a str>,
    done: bool,
}

impl Iterator for ResponsesSseChunks<'_> {
    type Item = String;

    fn next(&mut self) -> Option<Self::Item> {
        if self.done {
            return None;
        }

        for line in self.lines.by_ref() {
            if line.is_empty() {
                self.current_event = None;
                continue;
            }

            if let Some(event) = line.strip_prefix(EVENT_PREFIX) {
                self.current_event = Some(event.trim());
                continue;
            }

            let Some(data) = line.strip_prefix(DATA_PREFIX) else {
                continue;
            };
            let data = data.trim();

            if data == DONE_MARKER {
                self.done = true;
                return None;
            }

            if let Some(delta) = extract_responses_delta(data, self.current_event) {
                if !delta.is_empty() {
                    return Some(delta);
                }
            }
        }

        self.done = true;
        None
    }
}

pub fn extract_chat_completions_delta(json: &str) -> Option<String> {
    let value = serde_json::from_str::<Value>(json).ok()?;
    value
        .get("choices")
        .and_then(Value::as_array)
        .and_then(|choices| choices.first())
        .and_then(|choice| choice.get("delta"))
        .and_then(|delta| delta.get("content"))
        .and_then(Value::as_str)
        .map(ToOwned::to_owned)
}

pub fn extract_responses_delta(json: &str, current_event: Option<&str>) -> Option<String> {
    let value = serde_json::from_str::<Value>(json).ok()?;
    if !is_responses_delta_event(&value, current_event) {
        return None;
    }

    value
        .get("delta")
        .and_then(Value::as_str)
        .map(ToOwned::to_owned)
}

fn is_responses_delta_event(value: &Value, current_event: Option<&str>) -> bool {
    if current_event == Some(RESPONSES_DELTA_EVENT) {
        return true;
    }

    value
        .get("type")
        .and_then(Value::as_str)
        .is_some_and(|value| value == RESPONSES_DELTA_EVENT)
}
