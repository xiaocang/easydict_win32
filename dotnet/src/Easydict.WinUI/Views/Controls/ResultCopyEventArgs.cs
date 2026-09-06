namespace Easydict.WinUI.Views.Controls;

public sealed class ResultCopyEventArgs(Exception? error = null) : EventArgs
{
    public Exception? Error { get; } = error;
}
