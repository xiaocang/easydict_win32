using System.ComponentModel;

namespace Easydict.DirectXaml.Tests;

public sealed class TypedBindingContext : INotifyPropertyChanged
{
    private string _resultText = string.Empty;
    private string _status = string.Empty;

    public string ResultText
    {
        get => _resultText;
        set
        {
            if (_resultText == value)
            {
                return;
            }

            _resultText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResultText)));
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
