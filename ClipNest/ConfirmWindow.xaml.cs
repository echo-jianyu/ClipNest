using System.Windows;
using System.Windows.Input;

namespace ClipNest;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(
        string title,
        string detail,
        string confirmText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        DetailText.Text = detail;
        ConfirmButton.Content = confirmText;
    }

    public static bool ConfirmClearHistory(Window? owner)
    {
        var dialog = new ConfirmWindow(
            "清空历史",
            "此操作会清空所有文字和图片记录，无法撤销，确定清空吗？",
            "清空历史");
        if (owner is not null)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            DialogResult = false;
            eventArgs.Handled = true;
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }
}
