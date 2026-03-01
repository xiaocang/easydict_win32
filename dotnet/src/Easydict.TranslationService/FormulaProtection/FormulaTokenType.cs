namespace Easydict.TranslationService.FormulaProtection;

public enum FormulaTokenType
{
    InlineMath,      // $...$ or \(...\)
    DisplayMath,     // $$...$$ or \[...\]
    LaTeXEnv,        // \begin{..}..\end{..}
    MathSubscript,   // h_t, W_Q, x_i, 1_c_i (short base ≤5 chars)
    MathSuperscript, // x^2, e^{i\pi}
    GreekLetter,     // \alpha, \beta, \Delta (bare command)
    MathOperator,    // \infty, \pm, \leq, \times, \cdot
    Fraction,        // \frac{a}{b}
    SquareRoot,      // \sqrt{x}, \sqrt[n]{x}
    SumProduct,      // \sum_{i}^{n}, \prod
    Integral,        // \int_0^\infty
    MathFormatting,  // \mathbf{}, \mathrm{}, \text{}
    Matrix,          // \begin{bmatrix}...
    InlineEquation,  // x = y_1 + z^2
    SequenceToken,   // hidden_state, sequence_1 (long base >5 chars — NOT subscript rendered)
    UnitFragment,    // catch-all
}
