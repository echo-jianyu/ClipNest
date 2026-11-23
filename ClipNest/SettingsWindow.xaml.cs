using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipNest.Models;

namespace ClipNest;

public partial class SettingsWindow : Window
{
    private HotKeyDefinition _hotKey;
    private bool _isCapturingHotKey;

    public SettingsWindow(
        int cardTextLimit,
        double cardFontSize,
        HotKeyDefinition hotKey)
    {
        InitializeComponent();
        TextLimitTextBox.Text = cardTextLimit.ToString(CultureInfo.CurrentCulture);
        FontSizeTextBox.Text = cardFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        _hotKey = hotKey ?? throw new ArgumentNullException(nameof(hotKey));
        UpdateHotKeyButton();
    }

    public int CardTextLimit { get; private set; } =
        ClipItem.DefaultDisplayTextLimit;

    public double CardFontSize { get; private set; } =
        ClipItem.DefaultDisplayFontSize;

    public HotKeyDefinition HotKey => _hotKey;

    private void SaveButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!int.TryParse(
                TextLimitTextBox.Text,
                NumberStyles.Integer,
                CultureInfo.CurrentCulture,
                out var textLimit)
            || !double.TryParse(
                FontSizeTextBox.Text,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out var fontSize))
        {
            return;
        }

        CardTextLimit = Math.Min(
            Math.Max(textLimit, AppSettings.MinimumCardTextLimit),
            AppSettings.MaximumCardTextLimit);
        CardFontSize = Math.Min(
            Math.Max(fontSize, AppSettings.MinimumCardFontSize),
            AppSettings.MaximumCardFontSize);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }

    private void HotKeyButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        _isCapturingHotKey = true;
        HotKeyButton.Content = "请按组合键…";
        HotKeyButton.Focus();
        Keyboard.Focus(HotKeyButton);
        eventArgs.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (_isCapturingHotKey)
        {
            if (eventArgs.Key == Key.Escape)
            {
                _isCapturingHotKey = false;
                UpdateHotKeyButton();
                eventArgs.Handled = true;
                return;
            }

            var key = eventArgs.Key == Key.System
                ? eventArgs.SystemKey
                : eventArgs.Key;
            var hotKey = HotKeyDefinition.FromKeyboard(key, Keyboard.Modifiers);
            if (hotKey is not null)
            {
                _hotKey = hotKey;
                _isCapturingHotKey = false;
                UpdateHotKeyButton();
            }

            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Escape)
        {
            DialogResult = false;
            eventArgs.Handled = true;
        }
    }

    private void NumericTextBox_PreviewTextInput(
        object sender,
        TextCompositionEventArgs eventArgs)
    {
        eventArgs.Handled = eventArgs.Text.Any(
            character => !char.IsDigit(character) && character != '.');
    }

    private void UpdateHotKeyButton()
    {
        HotKeyButton.Content = _hotKey.DisplayText;
    }
}
