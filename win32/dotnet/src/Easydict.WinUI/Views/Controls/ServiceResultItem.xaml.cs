using Easydict.TranslationService.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace Easydict.WinUI.Views.Controls;

/// <summary>
/// A collapsible result item for a single translation service.
/// Mirrors macOS EZResultView behavior with expand/collapse functionality.
/// </summary>
public sealed partial class ServiceResultItem : UserControl
{
    private ServiceQueryResult? _serviceResult;
    private bool _isHovering;

    /// <summary>
    /// Event raised when the expand/collapse state is toggled.
    /// </summary>
    public event EventHandler<ServiceQueryResult>? CollapseToggled;

    public ServiceResultItem()
    {
        this.InitializeComponent();
    }

    /// <summary>
    /// The service query result to display.
    /// </summary>
    public ServiceQueryResult? ServiceResult
    {
        get => _serviceResult;
        set
        {
            if (_serviceResult != null)
            {
                _serviceResult.PropertyChanged -= OnServiceResultPropertyChanged;
            }

            _serviceResult = value;

            if (_serviceResult != null)
            {
                _serviceResult.PropertyChanged += OnServiceResultPropertyChanged;
            }

            UpdateUI();
        }
    }

    private void OnServiceResultPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => UpdateUI());
    }

    private void UpdateUI()
    {
        if (_serviceResult == null)
        {
            return;
        }

        // Service info
        ServiceNameText.Text = _serviceResult.ServiceDisplayName;

        // Try to load service icon
        try
        {
            ServiceIcon.Source = new BitmapImage(new Uri(_serviceResult.ServiceIconPath));
        }
        catch
        {
            // Icon not found, hide it
            ServiceIcon.Visibility = Visibility.Collapsed;
        }

        // Loading state
        LoadingIndicator.IsActive = _serviceResult.IsLoading;
        LoadingIndicator.Visibility = _serviceResult.IsLoading ? Visibility.Visible : Visibility.Collapsed;

        // Error state
        var hasError = _serviceResult.HasError && !_serviceResult.IsLoading;
        ErrorIcon.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;

        // Status text
        StatusText.Text = _serviceResult.StatusText;

        // Arrow direction
        ArrowIcon.Glyph = _serviceResult.ArrowGlyph;

        // Content visibility
        var showContent = _serviceResult.IsExpanded && _serviceResult.HasResult;
        ContentArea.Visibility = showContent ? Visibility.Visible : Visibility.Collapsed;

        // Update header corner radius based on expand state
        HeaderBar.CornerRadius = showContent ? new CornerRadius(6, 6, 0, 0) : new CornerRadius(6);

        // Result text
        if (_serviceResult.Result != null)
        {
            ResultText.Text = _serviceResult.Result.TranslatedText;
            ResultText.Visibility = Visibility.Visible;
            ErrorText.Visibility = Visibility.Collapsed;
            CopyButton.Visibility = _isHovering ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (_serviceResult.Error != null)
        {
            ErrorText.Text = _serviceResult.Error.Message;
            ErrorText.Visibility = Visibility.Visible;
            ResultText.Visibility = Visibility.Collapsed;
            CopyButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResultText.Text = "";
            ResultText.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            CopyButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_serviceResult == null || _serviceResult.IsLoading)
        {
            return;
        }

        // Only handle left click
        var point = e.GetCurrentPoint(HeaderBar);
        if (point.Properties.IsLeftButtonPressed)
        {
            _serviceResult.ToggleExpanded();
            UpdateUI();
            CollapseToggled?.Invoke(this, _serviceResult);
            e.Handled = true;
        }
    }

    private void OnHeaderPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = true;
        HeaderBar.Background = (Brush)Application.Current.Resources["ButtonHoverBrush"];
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

        if (_serviceResult?.Result != null && _serviceResult.IsExpanded)
        {
            CopyButton.Visibility = Visibility.Visible;
        }
    }

    private void OnHeaderPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = false;
        HeaderBar.Background = (Brush)Application.Current.Resources["TitleBarBackgroundBrush"];
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        CopyButton.Visibility = Visibility.Collapsed;
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        var text = _serviceResult?.Result?.TranslatedText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);

        // Visual feedback
        CopyIcon.Glyph = "\uE8FB"; // Checkmark
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1500);
            CopyIcon.Glyph = "\uE8C8"; // Copy icon
        });
    }
}
