using System.Windows;
using System.Windows.Input;
using ClipNest.Infrastructure;

namespace ClipNest;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionTextBlock.Text = $"版本 {AppVersion.Display}";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            Close();
            eventArgs.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        Close();
    }
}
