use easydict_app::{
    chat_completions_sse_chunks, parse_chat_completions_sse_chunks, parse_openai_sse_chunks,
    parse_responses_sse_chunks, ChatMessage, ChatRole, OpenAiStreamingFormat,
};

#[test]
fn chat_completions_sse_yields_content_from_data_lines() {
    let sse = r#"data: {"choices":[{"delta":{"content":"Hello"}}]}

"#;

    assert_eq!(parse_chat_completions_sse_chunks(sse), ["Hello"]);
}

#[test]
fn chat_completions_sse_yields_multiple_chunks_in_order() {
    let sse = r#"data: {"choices":[{"delta":{"content":"Hello"}}]}

data: {"choices":[{"delta":{"content":" "}}]}

data: {"choices":[{"delta":{"content":"World"}}]}

"#;

    assert_eq!(
        parse_chat_completions_sse_chunks(sse),
        ["Hello", " ", "World"]
    );
}

#[test]
fn chat_completions_sse_ignores_blank_and_non_data_lines() {
    let sse = r#"
event: message
id: 123
retry: 1000
data: {"choices":[{"delta":{"content":"Hello"}}]}
: this is a comment

data: {"choices":[{"delta":{"content":"World"}}]}
"#;

    assert_eq!(parse_chat_completions_sse_chunks(sse), ["Hello", "World"]);
}

#[test]
fn chat_completions_sse_stops_at_done_marker() {
    let sse = r#"data: {"choices":[{"delta":{"content":"Hello"}}]}

data: [DONE]

data: {"choices":[{"delta":{"content":"World"}}]}
"#;

    assert_eq!(parse_chat_completions_sse_chunks(sse), ["Hello"]);
}

#[test]
fn chat_completions_sse_skips_malformed_or_empty_events() {
    let sse = r#"data: {"choices":[{"delta":{"content":"Hello"}}]}

data: {invalid json}

data: {"choices":[]}

data: {"choices":[{"delta":{"role":"assistant"}}]}

data: {"choices":[{"finish_reason":"stop"}]}

data: {"choices":[{"delta":{"content":"World"}}]}
"#;

    assert_eq!(parse_chat_completions_sse_chunks(sse), ["Hello", "World"]);
}

#[test]
fn chat_completions_sse_handles_empty_stream() {
    assert!(parse_chat_completions_sse_chunks("").is_empty());
}

#[test]
fn chat_completions_sse_iterator_allows_caller_cancellation() {
    let sse = r#"data: {"choices":[{"delta":{"content":"Hello"}}]}

data: {"choices":[{"delta":{"content":"World"}}]}
"#;

    let mut chunks = chat_completions_sse_chunks(sse);
    assert_eq!(chunks.next().as_deref(), Some("Hello"));
    drop(chunks);
}

#[test]
fn chat_completions_sse_handles_unicode_content() {
    let sse = r#"data: {"choices":[{"delta":{"content":"你好"}}]}

data: {"choices":[{"delta":{"content":"世界"}}]}

data: {"choices":[{"delta":{"content":"🌍"}}]}
"#;

    assert_eq!(
        parse_chat_completions_sse_chunks(sse),
        ["你好", "世界", "🌍"]
    );
}

#[test]
fn chat_message_roles_match_wire_names() {
    assert_eq!(ChatMessage::new(ChatRole::System, "s").role_str(), "system");
    assert_eq!(ChatMessage::new(ChatRole::User, "u").role_str(), "user");
    assert_eq!(
        ChatMessage::new(ChatRole::Assistant, "a").role_str(),
        "assistant"
    );
}

#[test]
fn responses_sse_yields_deltas_from_output_text_delta_events() {
    let sse = "event: response.output_text.delta\n\
data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\
\n\
event: response.output_text.delta\n\
data: {\"type\":\"response.output_text.delta\",\"delta\":\" world\"}\n\
\n\
data: [DONE]\n\n";

    assert_eq!(parse_responses_sse_chunks(sse), ["Hello", " world"]);
}

#[test]
fn responses_sse_uses_type_when_event_line_is_absent() {
    let sse = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\
\n\
data: {\"type\":\"response.completed\"}\n\n";

    assert_eq!(parse_responses_sse_chunks(sse), ["Hello"]);
}

#[test]
fn responses_sse_ignores_other_event_types() {
    let sse = "event: response.created\n\
data: {\"type\":\"response.created\",\"response\":{}}\n\
\n\
event: response.output_text.delta\n\
data: {\"type\":\"response.output_text.delta\",\"delta\":\"X\"}\n\
\n\
event: response.completed\n\
data: {\"type\":\"response.completed\"}\n\n";

    assert_eq!(parse_responses_sse_chunks(sse), ["X"]);
}

#[test]
fn responses_sse_stops_at_done_marker() {
    let sse = "event: response.output_text.delta\n\
data: {\"type\":\"response.output_text.delta\",\"delta\":\"a\"}\n\
\n\
data: [DONE]\n\
\n\
event: response.output_text.delta\n\
data: {\"type\":\"response.output_text.delta\",\"delta\":\"should-not-arrive\"}\n";

    assert_eq!(parse_responses_sse_chunks(sse), ["a"]);
}

#[test]
fn responses_sse_is_tolerant_to_malformed_json() {
    let sse = "event: response.output_text.delta\n\
data: {not-json}\n\
\n\
event: response.output_text.delta\n\
data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\n";

    assert_eq!(parse_responses_sse_chunks(sse), ["ok"]);
}

#[test]
fn openai_sse_dispatches_by_format() {
    let chat_sse = r#"data: {"choices":[{"delta":{"content":"chat"}}]}
"#;
    let responses_sse = "event: response.output_text.delta\n\
data: {\"type\":\"response.output_text.delta\",\"delta\":\"responses\"}\n";

    assert_eq!(
        parse_openai_sse_chunks(OpenAiStreamingFormat::ChatCompletions, chat_sse),
        ["chat"]
    );
    assert_eq!(
        parse_openai_sse_chunks(OpenAiStreamingFormat::Responses, responses_sse),
        ["responses"]
    );
}
