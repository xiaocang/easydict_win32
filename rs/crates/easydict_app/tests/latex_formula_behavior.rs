use easydict_app::{
    is_latex_script_signal, prepare_renderable_text_for_pdf, simplify_latex_formula,
    simplify_math_content,
};

#[test]
fn native_latex_formula_maps_greek_letters_to_unicode() {
    for (input, expected) in [
        (r"\alpha", "α"),
        (r"\beta", "β"),
        (r"\gamma", "γ"),
        (r"\delta", "δ"),
        (r"\epsilon", "ε"),
        (r"\zeta", "ζ"),
        (r"\eta", "η"),
        (r"\theta", "θ"),
        (r"\iota", "ι"),
        (r"\kappa", "κ"),
        (r"\lambda", "λ"),
        (r"\mu", "μ"),
        (r"\nu", "ν"),
        (r"\xi", "ξ"),
        (r"\pi", "π"),
        (r"\rho", "ρ"),
        (r"\sigma", "σ"),
        (r"\tau", "τ"),
        (r"\upsilon", "υ"),
        (r"\phi", "φ"),
        (r"\chi", "χ"),
        (r"\psi", "ψ"),
        (r"\omega", "ω"),
        (r"\Gamma", "Γ"),
        (r"\Delta", "Δ"),
        (r"\Theta", "Θ"),
        (r"\Lambda", "Λ"),
        (r"\Xi", "Ξ"),
        (r"\Pi", "Π"),
        (r"\Sigma", "Σ"),
        (r"\Upsilon", "Υ"),
        (r"\Phi", "Φ"),
        (r"\Psi", "Ψ"),
        (r"\Omega", "Ω"),
    ] {
        assert_eq!(simplify_math_content(input), expected);
    }
}

#[test]
fn native_latex_formula_maps_common_operators_to_unicode() {
    for (input, expected) in [
        (r"\infty", "∞"),
        (r"\pm", "±"),
        (r"\mp", "∓"),
        (r"\times", "×"),
        (r"\div", "÷"),
        (r"\cdot", "·"),
        (r"\leq", "≤"),
        (r"\geq", "≥"),
        (r"\neq", "≠"),
        (r"\approx", "≈"),
        (r"\equiv", "≡"),
        (r"\sim", "∼"),
        (r"\subset", "⊂"),
        (r"\supset", "⊃"),
        (r"\cup", "∪"),
        (r"\cap", "∩"),
        (r"\in", "∈"),
        (r"\notin", "∉"),
        (r"\forall", "∀"),
        (r"\exists", "∃"),
        (r"\nabla", "∇"),
        (r"\partial", "∂"),
        (r"\sum", "Σ"),
        (r"\prod", "Π"),
        (r"\int", "∫"),
        (r"\oint", "∮"),
        (r"\sqrt", "√"),
        (r"\ldots", "…"),
        (r"\cdots", "⋯"),
        (r"\vdots", "⋮"),
        (r"\ddots", "⋱"),
        (r"\to", "→"),
        (r"\leftarrow", "←"),
        (r"\rightarrow", "→"),
        (r"\Leftarrow", "⇐"),
        (r"\Rightarrow", "⇒"),
        (r"\leftrightarrow", "↔"),
        (r"\Leftrightarrow", "⇔"),
        (r"\oplus", "⊕"),
        (r"\otimes", "⊗"),
        (r"\circ", "∘"),
        (r"\bullet", "•"),
    ] {
        assert_eq!(simplify_math_content(input), expected);
    }
}

#[test]
fn native_latex_formula_simplifies_fraction_and_square_root_forms() {
    assert_eq!(simplify_math_content(r"\frac{a}{b}"), "a/b");
    assert_eq!(simplify_math_content(r"\frac{\alpha}{\beta}"), "α/β");
    assert_eq!(simplify_math_content(r"\sqrt{x}"), "√x");
    assert_eq!(simplify_math_content(r"\sqrt[3]{x}"), "ⁿ√x");
}

#[test]
fn native_latex_formula_strips_formatting_and_generic_content_commands() {
    assert_eq!(simplify_math_content(r"\mathbf{x}"), "x");
    assert_eq!(simplify_math_content(r"\mathrm{R}"), "R");
    assert_eq!(simplify_math_content(r"\text{loss}"), "loss");
    assert_eq!(simplify_math_content(r"\operatorname{argmax}"), "argmax");
    assert_eq!(simplify_math_content(r"\unknown{content}"), "content");
    assert_eq!(simplify_math_content(r"\unknown"), "");
}

#[test]
fn native_latex_formula_replaces_matrix_environments_with_placeholder() {
    assert_eq!(
        simplify_math_content(r"\begin{bmatrix} a & b \\ c & d \end{bmatrix}"),
        "[matrix]"
    );
    assert_eq!(
        simplify_math_content(r"A + \begin{pmatrix} x \\ y \end{pmatrix}"),
        "A + [matrix]"
    );
}

#[test]
fn native_latex_formula_expands_grouped_subscript_and_superscript_signals() {
    assert_eq!(simplify_math_content(r"h_{t-1}"), "h_t_-_1");
    assert_eq!(simplify_math_content(r"x^{2n}"), "x^2^n");
    assert_eq!(
        simplify_latex_formula(r"$$\sum_{i=1}^{n} x_i$$"),
        "Σ_i_=_1^n x_i"
    );
}

#[test]
fn native_latex_formula_normalizes_single_letter_digit_implicit_subscripts() {
    for (input, expected) in [
        ("x1", "x_1"),
        ("z2", "z_2"),
        ("v0", "v_0"),
        ("x12", "x_1_2"),
        ("(x1, ..., xn)", "(x_1, ..., xn)"),
    ] {
        assert_eq!(simplify_math_content(input), expected);
    }

    for input in ["mp4", "version1", "w3c"] {
        assert!(!simplify_math_content(input).contains('_'));
    }
}

#[test]
fn native_latex_formula_handles_empty_plain_and_delimited_text_boundaries() {
    assert_eq!(simplify_latex_formula(""), "");
    assert_eq!(simplify_latex_formula("Hello world"), "Hello world");
    assert_eq!(simplify_latex_formula(r"$\alpha + \beta$"), "α + β");
    assert_eq!(
        simplify_latex_formula(r"before \(\sqrt{x}\) after"),
        "before √x after"
    );
    assert_eq!(prepare_renderable_text_for_pdf(None), "");
    assert_eq!(prepare_renderable_text_for_pdf(Some("   \t")), "");
    assert_eq!(
        prepare_renderable_text_for_pdf(Some(r"loss $\leq \infty$")),
        "loss ≤ ∞"
    );
}

#[test]
fn native_latex_formula_identifies_pdf_script_signals() {
    assert!(is_latex_script_signal('^'));
    assert!(is_latex_script_signal('_'));
    assert!(!is_latex_script_signal('a'));
    assert!(!is_latex_script_signal('0'));
    assert!(!is_latex_script_signal(' '));
}
