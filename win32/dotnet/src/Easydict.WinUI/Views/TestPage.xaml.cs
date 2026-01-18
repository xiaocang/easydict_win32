namespace Easydict.WinUI.Views;

public sealed partial class TestPage : Microsoft.UI.Xaml.Controls.Page
{
    public TestPage()
    {
        this.InitializeComponent();
        System.Diagnostics.Debug.WriteLine("[TestPage] Constructor called and InitializeComponent completed");
    }
}
