const MATRIX_ENVIRONMENTS: &[&str] = &[
    "bmatrix",
    "pmatrix",
    "matrix",
    "vmatrix",
    "Vmatrix",
    "smallmatrix",
];

/// Checks whether a character is a super/subscript signal consumed by the PDF renderer.
pub fn is_script_signal(ch: char) -> bool {
    matches!(ch, '^' | '_')
}

/// Simplifies text that may contain LaTeX markup into a renderable Unicode approximation.
pub fn simplify(text: &str) -> String {
    simplify_with_options(text, true)
}

/// Simplifies text that may contain LaTeX markup into a renderable Unicode approximation.
///
/// `preserve_script_signals` is retained for C# API parity. The current renderer path keeps
/// `^` and `_` as positioning signals in both modes, matching the C# implementation.
pub fn simplify_with_options(text: &str, preserve_script_signals: bool) -> String {
    if text.is_empty() {
        return String::new();
    }

    let mut simplified = replace_math_delimiters(text, "$$", "$$", true, preserve_script_signals);
    simplified = replace_math_delimiters(&simplified, r"\[", r"\]", true, preserve_script_signals);
    simplified = replace_inline_dollar_math(&simplified, preserve_script_signals);
    simplified = replace_math_delimiters(&simplified, r"\(", r"\)", false, preserve_script_signals);
    simplified = strip_residual_commands(&simplified);
    simplified = expand_script_groups(&simplified, '_');
    simplified = expand_script_groups(&simplified, '^');
    simplified = simplified
        .chars()
        .filter(|ch| !matches!(ch, '$' | '\\' | '{' | '}'))
        .collect::<String>();

    collapse_ascii_space_and_trim(&simplified)
}

/// Simplifies the inner content of a LaTeX math expression.
pub fn simplify_math_content(latex: &str) -> String {
    simplify_math_content_with_options(latex, true)
}

/// Simplifies the inner content of a LaTeX math expression.
///
/// `preserve_script_signals` is retained for C# API parity. The current renderer path keeps
/// `^` and `_` as positioning signals in both modes, matching the C# implementation.
pub fn simplify_math_content_with_options(latex: &str, preserve_script_signals: bool) -> String {
    if latex.is_empty() {
        return String::new();
    }

    let mut simplified = replace_matrix_environments(latex);
    simplified = replace_fraction_and_sqrt(&simplified, preserve_script_signals);
    simplified = strip_math_commands(&simplified, preserve_script_signals);
    simplified = normalize_implicit_subscripts(&simplified);
    simplified = expand_script_groups(&simplified, '_');
    simplified = expand_script_groups(&simplified, '^');
    simplified = simplified
        .chars()
        .filter(|ch| !matches!(ch, '{' | '}'))
        .collect::<String>();

    collapse_whitespace_and_trim(&simplified)
}

/// Mirrors the C# PDF render path: blank input renders as empty text.
pub fn prepare_renderable_text_for_pdf(text: Option<&str>) -> String {
    match text {
        Some(text) if !text.trim().is_empty() => simplify(text),
        _ => String::new(),
    }
}

fn replace_math_delimiters(
    text: &str,
    open: &str,
    close: &str,
    pad_display_math: bool,
    preserve_script_signals: bool,
) -> String {
    let mut output = String::with_capacity(text.len());
    let mut position = 0;

    while let Some(relative_start) = text[position..].find(open) {
        let start = position + relative_start;
        let inner_start = start + open.len();
        let Some(relative_end) = text[inner_start..].find(close) else {
            break;
        };

        let end = inner_start + relative_end;
        output.push_str(&text[position..start]);
        let content =
            simplify_math_content_with_options(&text[inner_start..end], preserve_script_signals);
        if pad_display_math {
            output.push(' ');
            output.push_str(&content);
            output.push(' ');
        } else {
            output.push_str(&content);
        }
        position = end + close.len();
    }

    output.push_str(&text[position..]);
    output
}

fn replace_inline_dollar_math(text: &str, preserve_script_signals: bool) -> String {
    let mut output = String::with_capacity(text.len());
    let mut position = 0;

    while let Some(relative_start) = text[position..].find('$') {
        let start = position + relative_start;
        let inner_start = start + 1;
        let Some(relative_end) = text[inner_start..].find('$') else {
            break;
        };

        let end = inner_start + relative_end;
        if text[inner_start..end].contains('\n') {
            output.push_str(&text[position..inner_start]);
            position = inner_start;
            continue;
        }

        output.push_str(&text[position..start]);
        output.push_str(&simplify_math_content_with_options(
            &text[inner_start..end],
            preserve_script_signals,
        ));
        position = end + 1;
    }

    output.push_str(&text[position..]);
    output
}

fn strip_residual_commands(text: &str) -> String {
    let mut output = String::with_capacity(text.len());
    let mut position = 0;

    while position < text.len() {
        let Some(ch) = char_at(text, position) else {
            break;
        };

        if ch != '\\' {
            output.push(ch);
            position += ch.len_utf8();
            continue;
        }

        let command_start = position + ch.len_utf8();
        let Some((_, command_end)) = parse_ascii_command(text, command_start) else {
            position += ch.len_utf8();
            continue;
        };

        if text[command_end..].starts_with('{') {
            if let Some((content, next_position)) = parse_brace_group(text, command_end) {
                output.push_str(&content);
                position = next_position;
                continue;
            }
        }

        position = command_end;
    }

    output
}

fn replace_matrix_environments(latex: &str) -> String {
    let mut output = String::with_capacity(latex.len());
    let mut position = 0;

    while let Some(relative_start) = latex[position..].find(r"\begin{") {
        let start = position + relative_start;
        let env_start = start + r"\begin{".len();
        let Some(relative_env_end) = latex[env_start..].find('}') else {
            break;
        };
        let env_end = env_start + relative_env_end;
        let environment = &latex[env_start..env_end];

        if !is_matrix_environment(environment) {
            output.push_str(&latex[position..env_end + 1]);
            position = env_end + 1;
            continue;
        }

        let search_start = env_end + 1;
        let Some(matrix_end) = find_matrix_end(latex, search_start) else {
            break;
        };

        output.push_str(&latex[position..start]);
        output.push_str("[matrix]");
        position = matrix_end;
    }

    output.push_str(&latex[position..]);
    output
}

fn find_matrix_end(latex: &str, start: usize) -> Option<usize> {
    let mut position = start;

    while let Some(relative_end) = latex[position..].find(r"\end{") {
        let end_start = position + relative_end;
        let env_start = end_start + r"\end{".len();
        let Some(relative_env_end) = latex[env_start..].find('}') else {
            return None;
        };
        let env_end = env_start + relative_env_end;
        if is_matrix_environment(&latex[env_start..env_end]) {
            return Some(env_end + 1);
        }

        position = env_end + 1;
    }

    None
}

fn replace_fraction_and_sqrt(latex: &str, preserve_script_signals: bool) -> String {
    let mut output = String::with_capacity(latex.len());
    let mut position = 0;

    while position < latex.len() {
        if latex[position..].starts_with(r"\frac") {
            let first_group_start = position + r"\frac".len();
            if let Some((numerator, after_numerator)) = parse_brace_group(latex, first_group_start)
            {
                if let Some((denominator, after_denominator)) =
                    parse_brace_group(latex, after_numerator)
                {
                    output.push_str(&simplify_math_content_with_options(
                        &numerator,
                        preserve_script_signals,
                    ));
                    output.push('/');
                    output.push_str(&simplify_math_content_with_options(
                        &denominator,
                        preserve_script_signals,
                    ));
                    position = after_denominator;
                    continue;
                }
            }
        }

        if latex[position..].starts_with(r"\sqrt") {
            let mut group_start = position + r"\sqrt".len();
            let mut indexed_root = false;
            if latex[group_start..].starts_with('[') {
                if let Some(after_index) = find_closing_byte(latex, group_start, b']') {
                    group_start = after_index;
                    indexed_root = true;
                }
            }

            if let Some((radicand, after_radicand)) = parse_brace_group(latex, group_start) {
                if indexed_root {
                    output.push('ⁿ');
                }
                output.push('√');
                output.push_str(&simplify_math_content_with_options(
                    &radicand,
                    preserve_script_signals,
                ));
                position = after_radicand;
                continue;
            }
        }

        let Some(ch) = char_at(latex, position) else {
            break;
        };
        output.push(ch);
        position += ch.len_utf8();
    }

    output
}

fn strip_math_commands(latex: &str, preserve_script_signals: bool) -> String {
    let mut output = String::with_capacity(latex.len());
    let mut position = 0;

    while position < latex.len() {
        let Some(ch) = char_at(latex, position) else {
            break;
        };

        if ch != '\\' {
            output.push(ch);
            position += ch.len_utf8();
            continue;
        }

        let command_start = position + ch.len_utf8();
        let Some((command, command_end)) = parse_ascii_command(latex, command_start) else {
            output.push(ch);
            position += ch.len_utf8();
            continue;
        };

        if latex[command_end..].starts_with('{') {
            if let Some((content, next_position)) = parse_brace_group(latex, command_end) {
                output.push_str(&simplify_math_content_with_options(
                    &content,
                    preserve_script_signals,
                ));
                position = next_position;
                continue;
            }
        }

        if let Some(mapped) = greek_or_operator(command) {
            output.push_str(mapped);
        }
        position = command_end;
    }

    output
}

fn normalize_implicit_subscripts(text: &str) -> String {
    let mut output = String::with_capacity(text.len());
    let mut position = 0;

    while position < text.len() {
        let Some(ch) = char_at(text, position) else {
            break;
        };

        if is_word_char(ch) {
            let token_start = position;
            position += ch.len_utf8();
            while position < text.len() {
                let Some(next) = char_at(text, position) else {
                    break;
                };
                if !is_word_char(next) {
                    break;
                }
                position += next.len_utf8();
            }

            let token = &text[token_start..position];
            if let Some((base, digits)) = single_letter_digit_token(token) {
                output.push(base);
                output.push_str("_{");
                output.push_str(digits);
                output.push('}');
            } else {
                output.push_str(token);
            }
            continue;
        }

        output.push(ch);
        position += ch.len_utf8();
    }

    output
}

fn expand_script_groups(text: &str, marker: char) -> String {
    let mut output = String::with_capacity(text.len());
    let mut position = 0;
    let marker_len = marker.len_utf8();

    while position < text.len() {
        let Some(ch) = char_at(text, position) else {
            break;
        };

        if ch == marker {
            let group_start = position + marker_len;
            if text[group_start..].starts_with('{') {
                if let Some((content, next_position)) = parse_brace_group(text, group_start) {
                    for content_char in content.chars() {
                        output.push(marker);
                        output.push(content_char);
                    }
                    position = next_position;
                    continue;
                }
            }
        }

        output.push(ch);
        position += ch.len_utf8();
    }

    output
}

fn parse_ascii_command(text: &str, start: usize) -> Option<(&str, usize)> {
    let bytes = text.as_bytes();
    let mut end = start;

    while end < bytes.len() && bytes[end].is_ascii_alphabetic() {
        end += 1;
    }

    (end > start).then(|| (&text[start..end], end))
}

fn parse_brace_group(text: &str, open_position: usize) -> Option<(String, usize)> {
    if !text[open_position..].starts_with('{') {
        return None;
    }

    let mut depth = 0usize;
    let content_start = open_position + 1;

    for (relative_index, ch) in text[open_position..].char_indices() {
        let index = open_position + relative_index;
        match ch {
            '{' => depth += 1,
            '}' => {
                depth = depth.checked_sub(1)?;
                if depth == 0 {
                    return Some((text[content_start..index].to_string(), index + 1));
                }
            }
            _ => {}
        }
    }

    None
}

fn find_closing_byte(text: &str, open_position: usize, close: u8) -> Option<usize> {
    text.as_bytes()[open_position + 1..]
        .iter()
        .position(|byte| *byte == close)
        .map(|relative| open_position + 1 + relative + 1)
}

fn single_letter_digit_token(token: &str) -> Option<(char, &str)> {
    let mut chars = token.char_indices();
    let (_, base) = chars.next()?;
    if !base.is_ascii_alphabetic() {
        return None;
    }

    let Some((digit_start, first_digit)) = chars.next() else {
        return None;
    };
    if !first_digit.is_ascii_digit() {
        return None;
    }

    if token[digit_start..].chars().all(|ch| ch.is_ascii_digit()) {
        Some((base, &token[digit_start..]))
    } else {
        None
    }
}

fn char_at(text: &str, position: usize) -> Option<char> {
    text[position..].chars().next()
}

fn is_word_char(ch: char) -> bool {
    ch.is_ascii_alphanumeric() || ch == '_'
}

fn is_matrix_environment(environment: &str) -> bool {
    MATRIX_ENVIRONMENTS.contains(&environment)
}

fn collapse_ascii_space_and_trim(text: &str) -> String {
    let mut output = String::with_capacity(text.len());
    let mut previous_space = false;

    for ch in text.chars() {
        if matches!(ch, ' ' | '\t') {
            if !previous_space {
                output.push(' ');
                previous_space = true;
            }
        } else {
            output.push(ch);
            previous_space = false;
        }
    }

    output.trim().to_string()
}

fn collapse_whitespace_and_trim(text: &str) -> String {
    let mut output = String::with_capacity(text.len());
    let mut previous_space = false;

    for ch in text.chars() {
        if ch.is_whitespace() {
            if !previous_space {
                output.push(' ');
                previous_space = true;
            }
        } else {
            output.push(ch);
            previous_space = false;
        }
    }

    output.trim().to_string()
}

fn greek_or_operator(command: &str) -> Option<&'static str> {
    match command {
        "alpha" => Some("α"),
        "beta" => Some("β"),
        "gamma" => Some("γ"),
        "delta" => Some("δ"),
        "epsilon" => Some("ε"),
        "zeta" => Some("ζ"),
        "eta" => Some("η"),
        "theta" => Some("θ"),
        "iota" => Some("ι"),
        "kappa" => Some("κ"),
        "lambda" => Some("λ"),
        "mu" => Some("μ"),
        "nu" => Some("ν"),
        "xi" => Some("ξ"),
        "pi" => Some("π"),
        "rho" => Some("ρ"),
        "sigma" => Some("σ"),
        "tau" => Some("τ"),
        "upsilon" => Some("υ"),
        "phi" => Some("φ"),
        "chi" => Some("χ"),
        "psi" => Some("ψ"),
        "omega" => Some("ω"),
        "Gamma" => Some("Γ"),
        "Delta" => Some("Δ"),
        "Theta" => Some("Θ"),
        "Lambda" => Some("Λ"),
        "Xi" => Some("Ξ"),
        "Pi" => Some("Π"),
        "Sigma" => Some("Σ"),
        "Upsilon" => Some("Υ"),
        "Phi" => Some("Φ"),
        "Psi" => Some("Ψ"),
        "Omega" => Some("Ω"),
        "infty" => Some("∞"),
        "pm" => Some("±"),
        "mp" => Some("∓"),
        "times" => Some("×"),
        "div" => Some("÷"),
        "cdot" => Some("·"),
        "leq" => Some("≤"),
        "geq" => Some("≥"),
        "neq" => Some("≠"),
        "approx" => Some("≈"),
        "equiv" => Some("≡"),
        "sim" => Some("∼"),
        "subset" => Some("⊂"),
        "supset" => Some("⊃"),
        "cup" => Some("∪"),
        "cap" => Some("∩"),
        "in" => Some("∈"),
        "notin" => Some("∉"),
        "forall" => Some("∀"),
        "exists" => Some("∃"),
        "nabla" => Some("∇"),
        "partial" => Some("∂"),
        "sum" => Some("Σ"),
        "prod" => Some("Π"),
        "int" => Some("∫"),
        "oint" => Some("∮"),
        "sqrt" => Some("√"),
        "ldots" => Some("…"),
        "cdots" => Some("⋯"),
        "vdots" => Some("⋮"),
        "ddots" => Some("⋱"),
        "to" | "rightarrow" => Some("→"),
        "leftarrow" => Some("←"),
        "Leftarrow" => Some("⇐"),
        "Rightarrow" => Some("⇒"),
        "leftrightarrow" => Some("↔"),
        "Leftrightarrow" => Some("⇔"),
        "oplus" => Some("⊕"),
        "otimes" => Some("⊗"),
        "circ" => Some("∘"),
        "bullet" => Some("•"),
        _ => None,
    }
}
