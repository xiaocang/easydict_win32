#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PdfLiteralString {
    pub start: usize,
    pub length: usize,
    pub value: String,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TextOperatorRange {
    pub start: usize,
    pub end: usize,
}

pub fn cid_to_hex(cid: u32, is_cid_font: bool) -> String {
    if is_cid_font {
        format!("{cid:04X}")
    } else {
        format!("{cid:02X}")
    }
}

pub fn generate_text_operator(
    font_resource_name: &str,
    font_size: f64,
    x: f64,
    y: f64,
    hex_cid: &str,
) -> String {
    format!("/{font_resource_name} {font_size:.6} Tf 1 0 0 1 {x:.6} {y:.6} Tm [<{hex_cid}>] TJ ")
}

pub fn build_content_stream(
    graphics_ops_bytes: &[u8],
    text_ops: &str,
    origin_x: f64,
    origin_y: f64,
    erase_ops: &str,
) -> Vec<u8> {
    let mut bytes = Vec::new();
    bytes.extend_from_slice(b"q ");
    bytes.extend_from_slice(graphics_ops_bytes);
    bytes.extend_from_slice(b"Q ");

    if !erase_ops.is_empty() {
        bytes.extend_from_slice(b"q ");
        bytes.extend_from_slice(erase_ops.as_bytes());
        bytes.extend_from_slice(b"Q ");
    }

    bytes.extend_from_slice(format!("1 0 0 1 {origin_x:.6} {origin_y:.6} cm ").as_bytes());
    bytes.extend_from_slice(b"BT ");
    bytes.extend_from_slice(text_ops.as_bytes());
    bytes.extend_from_slice(b"ET");
    bytes
}

pub fn hide_text_operator_in_stream(content: &str, source_text: &str) -> Option<String> {
    if source_text.trim().is_empty() {
        return None;
    }

    let range = find_text_operator_range(content, source_text)?;
    Some(format!(
        "{}3 Tr {} 0 Tr{}",
        &content[..range.start],
        &content[range.start..range.end],
        &content[range.end..]
    ))
}

pub fn find_text_operator_range(content: &str, source_text: &str) -> Option<TextOperatorRange> {
    find_text_operator_range_bytes(content.as_bytes(), source_text)
}

pub fn find_text_operator_range_bytes(
    content: &[u8],
    source_text: &str,
) -> Option<TextOperatorRange> {
    let normalized_source = normalize_pdf_text_for_match(source_text);
    if normalized_source.trim().is_empty() {
        return None;
    }

    let mut index = 0usize;
    while index < content.len() {
        if content[index] == b'[' {
            if let Some((range, value)) = parse_text_array_operator_bytes(content, index) {
                if normalize_pdf_text_for_match(&value) == normalized_source {
                    return Some(range);
                }
                index = range.end;
                continue;
            }
        }

        if let Some(operand) = parse_pdf_text_operand_bytes(content, index) {
            let after_operand = skip_pdf_whitespace_bytes(content, operand.end);
            if starts_pdf_keyword(content, after_operand, b"Tj")
                && normalize_pdf_text_for_match(&operand.value) == normalized_source
            {
                return Some(TextOperatorRange {
                    start: operand.start,
                    end: after_operand + 2,
                });
            }
            index = operand.end;
            continue;
        }

        index += 1;
    }

    None
}

pub fn replace_text_operator_in_stream(
    content: &str,
    source_text: &str,
    translated_text: &str,
) -> Option<String> {
    let patched =
        replace_text_operator_in_stream_bytes(content.as_bytes(), source_text, translated_text)?;
    String::from_utf8(patched).ok()
}

pub fn replace_text_operator_in_stream_bytes(
    content: &[u8],
    source_text: &str,
    translated_text: &str,
) -> Option<Vec<u8>> {
    let range = find_text_operator_range_bytes(content, source_text)?;
    let replacement = format!("({}) Tj", escape_pdf_literal_string(translated_text));
    let mut patched =
        Vec::with_capacity(content.len() - (range.end - range.start) + replacement.len());
    patched.extend_from_slice(&content[..range.start]);
    patched.extend_from_slice(replacement.as_bytes());
    patched.extend_from_slice(&content[range.end..]);
    Some(patched)
}

pub fn try_patch_pdf_literal_token(
    content: &str,
    source_text: &str,
    translated_text: &str,
) -> Option<String> {
    let escaped_source = escape_pdf_literal_string(source_text);
    let source_token = format!("({escaped_source})");
    if let Some(index) = content.find(&source_token) {
        if translated_text.len() > source_text.len() {
            return None;
        }

        let padded = format!("{translated_text:<width$}", width = source_text.len());
        let target_token = format!("({})", escape_pdf_literal_string(&padded));
        let mut patched =
            String::with_capacity(content.len() - source_token.len() + target_token.len());
        patched.push_str(&content[..index]);
        patched.push_str(&target_token);
        patched.push_str(&content[index + source_token.len()..]);
        return Some(patched);
    }

    try_patch_pdf_array_text_token(content, source_text, translated_text)
}

pub fn try_patch_pdf_array_text_token(
    content: &str,
    source_text: &str,
    translated_text: &str,
) -> Option<String> {
    let normalized_source = normalize_pdf_text_for_match(source_text);
    if normalized_source.trim().is_empty() {
        return None;
    }

    for array in text_array_matches(content) {
        let extracted = extract_pdf_literal_strings(array.body);
        if extracted.is_empty() {
            continue;
        }

        let combined = extracted
            .iter()
            .map(|item| item.value.as_str())
            .collect::<String>();
        if normalize_pdf_text_for_match(&combined) != normalized_source {
            continue;
        }

        let replacement = format!("({}) Tj", escape_pdf_literal_string(translated_text));
        let mut patched =
            String::with_capacity(content.len() - (array.end - array.start) + replacement.len());
        patched.push_str(&content[..array.start]);
        patched.push_str(&replacement);
        patched.push_str(&content[array.end..]);
        return Some(patched);
    }

    None
}

pub fn extract_pdf_literal_strings(content: &str) -> Vec<PdfLiteralString> {
    let mut items = Vec::new();
    let mut index = 0;
    while index < content.len() {
        let Some(relative) = content[index..].find('(') else {
            break;
        };
        let start = index + relative;
        if let Some(item) = parse_pdf_literal_string(content, start) {
            index = start + item.length;
            items.push(item);
        } else {
            index = start + 1;
        }
    }

    items
}

pub fn parse_pdf_literal_string(content: &str, start_index: usize) -> Option<PdfLiteralString> {
    if !content[start_index..].starts_with('(') {
        return None;
    }

    let mut value = String::new();
    let mut nesting = 0usize;
    let mut escaped = false;

    for (offset, current) in content[start_index..].char_indices() {
        let index = start_index + offset;
        if index == start_index {
            nesting = 1;
            continue;
        }

        if escaped {
            value.push(current);
            escaped = false;
            continue;
        }

        if current == '\\' {
            escaped = true;
            continue;
        }

        if current == '(' {
            nesting += 1;
            value.push(current);
            continue;
        }

        if current == ')' {
            nesting -= 1;
            if nesting == 0 {
                return Some(PdfLiteralString {
                    start: start_index,
                    length: index + current.len_utf8() - start_index,
                    value,
                });
            }

            value.push(current);
            continue;
        }

        value.push(current);
    }

    None
}

pub fn normalize_pdf_text_for_match(text: &str) -> String {
    text.chars().filter(|ch| !ch.is_whitespace()).collect()
}

pub fn escape_pdf_literal_string(text: &str) -> String {
    text.replace('\\', "\\\\")
        .replace('(', "\\(")
        .replace(')', "\\)")
}

#[derive(Clone, Copy, Debug)]
struct TextArrayMatch<'a> {
    start: usize,
    end: usize,
    body: &'a str,
}

fn text_array_matches(content: &str) -> Vec<TextArrayMatch<'_>> {
    let mut matches = Vec::new();
    let mut search_start = 0;
    while search_start < content.len() {
        let Some(relative_start) = content[search_start..].find('[') else {
            break;
        };
        let array_start = search_start + relative_start;
        let mut close_search_start = array_start + 1;
        let mut matched = None;
        while close_search_start < content.len() {
            let Some(relative_end) = content[close_search_start..].find(']') else {
                break;
            };
            let array_close = close_search_start + relative_end;
            let after_close = skip_whitespace(content, array_close + 1);
            if content[after_close..].starts_with("TJ") {
                matched = Some(TextArrayMatch {
                    start: array_start,
                    end: after_close + 2,
                    body: &content[array_start + 1..array_close],
                });
                break;
            }
            close_search_start = array_close + 1;
        }

        if let Some(value) = matched {
            search_start = value.end;
            matches.push(value);
        } else {
            search_start = array_start + 1;
        }
    }

    matches
}

fn skip_whitespace(content: &str, start: usize) -> usize {
    let mut pos = start.min(content.len());
    while pos < content.len() {
        let Some(ch) = next_char(content, pos) else {
            break;
        };
        if !ch.is_whitespace() {
            break;
        }
        pos += ch.len_utf8();
    }
    pos
}

fn next_char(content: &str, byte_index: usize) -> Option<char> {
    content.get(byte_index..)?.chars().next()
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct PdfTextOperand {
    start: usize,
    end: usize,
    value: String,
}

fn parse_pdf_text_operand_bytes(content: &[u8], start: usize) -> Option<PdfTextOperand> {
    match content.get(start).copied()? {
        b'(' => parse_pdf_literal_string_bytes(content, start),
        b'<' if content.get(start + 1).copied() != Some(b'<') => {
            parse_pdf_hex_string_bytes(content, start)
        }
        _ => None,
    }
}

fn parse_pdf_literal_string_bytes(content: &[u8], start: usize) -> Option<PdfTextOperand> {
    if content.get(start).copied() != Some(b'(') {
        return None;
    }

    let mut value = Vec::new();
    let mut nesting = 1usize;
    let mut escaped = false;
    let mut index = start + 1;

    while index < content.len() {
        let current = content[index];
        if escaped {
            value.push(current);
            escaped = false;
            index += 1;
            continue;
        }

        match current {
            b'\\' => {
                escaped = true;
                index += 1;
            }
            b'(' => {
                nesting += 1;
                value.push(current);
                index += 1;
            }
            b')' => {
                nesting -= 1;
                index += 1;
                if nesting == 0 {
                    return Some(PdfTextOperand {
                        start,
                        end: index,
                        value: String::from_utf8(value).ok()?,
                    });
                }
                value.push(current);
            }
            _ => {
                value.push(current);
                index += 1;
            }
        }
    }

    None
}

fn parse_pdf_hex_string_bytes(content: &[u8], start: usize) -> Option<PdfTextOperand> {
    if content.get(start).copied() != Some(b'<') || content.get(start + 1).copied() == Some(b'<') {
        return None;
    }

    let mut hex_digits = Vec::new();
    let mut index = start + 1;
    while index < content.len() {
        let current = content[index];
        if current == b'>' {
            let value = decode_pdf_hex_text(&hex_digits)?;
            return Some(PdfTextOperand {
                start,
                end: index + 1,
                value,
            });
        }

        if is_pdf_whitespace_byte(current) {
            index += 1;
            continue;
        }

        if !current.is_ascii_hexdigit() {
            return None;
        }

        hex_digits.push(current);
        index += 1;
    }

    None
}

fn decode_pdf_hex_text(hex_digits: &[u8]) -> Option<String> {
    let mut bytes = Vec::with_capacity(hex_digits.len().div_ceil(2));
    for pair in hex_digits.chunks(2) {
        let high = hex_value(pair[0])?;
        let low = pair.get(1).copied().and_then(hex_value).unwrap_or(0);
        bytes.push((high << 4) | low);
    }
    String::from_utf8(bytes).ok()
}

fn hex_value(value: u8) -> Option<u8> {
    match value {
        b'0'..=b'9' => Some(value - b'0'),
        b'a'..=b'f' => Some(value - b'a' + 10),
        b'A'..=b'F' => Some(value - b'A' + 10),
        _ => None,
    }
}

fn parse_text_array_operator_bytes(
    content: &[u8],
    start: usize,
) -> Option<(TextOperatorRange, String)> {
    if content.get(start).copied() != Some(b'[') {
        return None;
    }

    let mut values = Vec::new();
    let mut index = start + 1;
    while index < content.len() {
        index = skip_pdf_whitespace_bytes(content, index);
        if content.get(index).copied() == Some(b']') {
            let after_close = skip_pdf_whitespace_bytes(content, index + 1);
            if starts_pdf_keyword(content, after_close, b"TJ") {
                return Some((
                    TextOperatorRange {
                        start,
                        end: after_close + 2,
                    },
                    values.concat(),
                ));
            }
            return None;
        }

        if let Some(operand) = parse_pdf_text_operand_bytes(content, index) {
            values.push(operand.value);
            index = operand.end;
            continue;
        }

        index += 1;
    }

    None
}

fn skip_pdf_whitespace_bytes(content: &[u8], start: usize) -> usize {
    let mut pos = start.min(content.len());
    while pos < content.len() && is_pdf_whitespace_byte(content[pos]) {
        pos += 1;
    }
    pos
}

fn is_pdf_whitespace_byte(value: u8) -> bool {
    matches!(value, 0 | b'\t' | b'\n' | 0x0C | b'\r' | b' ')
}

fn starts_pdf_keyword(content: &[u8], start: usize, keyword: &[u8]) -> bool {
    content
        .get(start..)
        .is_some_and(|remaining| remaining.starts_with(keyword))
        && content
            .get(start + keyword.len())
            .is_none_or(|next| !next.is_ascii_alphanumeric())
}
