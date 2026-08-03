namespace Easydict.DirectXaml.Win2D;

/// <summary>Raised when a pointer gesture reaches a node carrying a compiled action.</summary>
public sealed class DirectXamlActionEventArgs(string handler, int node) : EventArgs
{
    /// <summary>The handler name the XAML declared, e.g. <c>OnHeaderPointerPressed</c>.</summary>
    public string Handler { get; } = handler;

    /// <summary>Stable node identifier that owns the action.</summary>
    public int Node { get; } = node;
}
