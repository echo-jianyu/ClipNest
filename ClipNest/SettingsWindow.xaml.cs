using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using ClipNest.Infrastructure;
using ClipNest.Models;

namespace ClipNest;

public partial class SettingsWindow : Window
{
    private HotKeyDefinition _hotKey;
    private bool _isCapturingHotKey;

    public SettingsWindow(
        int cardTextLimit,
        double cardFontSize,
        HotKeyDefinition hotKey,
        bool hideAfterCopy,
        bool autoStartOnLogin)
    {
        InitializeComponent();
        TextLimitTextBox.Text = cardTextLimit.ToString(CultureInfo.CurrentCulture);
        FontSizeTextBox.Text = cardFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        _hotKey = hotKey ?? throw new ArgumentNullException(nameof(hotKey));
        HideAfterCopyCheckBox.IsChecked = hideAfterCopy;
        AutoStartCheckBox.IsChecked = autoStartOnLogin;
        VersionTextBlock.Text = $"版本 {AppVersion.Display}";
        UpdateHotKeyButton();
    }

    public int CardTextLimit { get; private set; } =
        ClipItem.DefaultDisplayTextLimit;

    public double CardFontSize { get; private set; } =
        ClipItem.DefaultDisplayFontSize;

    public HotKeyDefinition HotKey => _hotKey;

    public bool HideAfterCopy { get; private set; }

    public bool AutoStartOnLogin { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!int.TryParse(
                TextLimitTextBox.Text,
                NumberStyles.Integer,
                CultureInfo.CurrentCulture,
                out var textLimit))
        {
            ShowValidationError("卡片字符数必须是整数。");
            TextLimitTextBox.Focus();
            return;
        }

        if (!double.TryParse(
                FontSizeTextBox.Text,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out var fontSize))
        {
            ShowValidationError("卡片字号必须是数字。");
            FontSizeTextBox.Focus();
            return;
        }

        if (textLimit < AppSettings.MinimumCardTextLimit
            || textLimit > AppSettings.MaximumCardTextLimit)
        {
            ShowValidationError(
                $"卡片字符数需在 {AppSettings.MinimumCardTextLimit}–" +
                $"{AppSettings.MaximumCardTextLimit} 之间。");
            TextLimitTextBox.Focus();
            return;
        }

        if (fontSize < AppSettings.MinimumCardFontSize
            || fontSize > AppSettings.MaximumCardFontSize)
        {
            ShowValidationError(
                $"卡片字号需在 {AppSettings.MinimumCardFontSize:0.#}–" +
                $"{AppSettings.MaximumCardFontSize:0.#} px 之间。");
            FontSizeTextBox.Focus();
            return;
        }

        HideValidationError();
        CardTextLimit = textLimit;
        CardFontSize = fontSize;
        HideAfterCopy = HideAfterCopyCheckBox.IsChecked == true;
        AutoStartOnLogin = AutoStartCheckBox.IsChecked == true;
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

    private void HotKeyButton_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs eventArgs)
    {
        if (!_isCapturingHotKey)
        {
            return;
        }

        _isCapturingHotKey = false;
        UpdateHotKeyButton();
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

    private void IntegerTextBox_PreviewTextInput(
        object sender,
        TextCompositionEventArgs eventArgs)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        var candidate = BuildCandidateText(textBox, eventArgs.Text);
        eventArgs.Handled = !IsValidNumericText(candidate, allowDecimal: false);
        if (!eventArgs.Handled)
        {
            HideValidationError();
        }
    }

    private void FloatTextBox_PreviewTextInput(
        object sender,
        TextCompositionEventArgs eventArgs)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        var candidate = BuildCandidateText(textBox, eventArgs.Text);
        eventArgs.Handled = !IsValidNumericText(candidate, allowDecimal: true);
        if (!eventArgs.Handled)
        {
            HideValidationError();
        }
    }

    private void IntegerTextBox_Pasting(
        object sender,
        DataObjectPastingEventArgs eventArgs)
    {
        if (sender is not TextBox textBox
            || !TryGetPastedText(eventArgs, out var pastedText)
            || !IsValidNumericText(
                BuildCandidateText(textBox, pastedText),
                allowDecimal: false))
        {
            eventArgs.CancelCommand();
        }
    }

    private void FloatTextBox_Pasting(
        object sender,
        DataObjectPastingEventArgs eventArgs)
    {
        if (sender is not TextBox textBox
            || !TryGetPastedText(eventArgs, out var pastedText)
            || !IsValidNumericText(
                BuildCandidateText(textBox, pastedText),
                allowDecimal: true))
        {
            eventArgs.CancelCommand();
        }
    }

    private static bool TryGetPastedText(
        DataObjectPastingEventArgs eventArgs,
        out string text)
    {
        text = string.Empty;
        var dataObject = eventArgs.SourceDataObject;
        var value = dataObject.GetDataPresent(DataFormats.UnicodeText, true)
            ? dataObject.GetData(DataFormats.UnicodeText, true)
            : dataObject.GetData(DataFormats.Text, true);
        if (value is not string stringValue)
        {
            return false;
        }

        text = stringValue;
        return true;
    }

    private static bool IsValidNumericText(string text, bool allowDecimal)
    {
        if (text.Length == 0)
        {
            return true;
        }

        var decimalSeparator =
            CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var decimalCount = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsDigit(character))
            {
                continue;
            }

            if (allowDecimal
                && decimalSeparator.Length > 0
                && string.CompareOrdinal(
                    text,
                    index,
                    decimalSeparator,
                    0,
                    decimalSeparator.Length) == 0)
            {
                decimalCount++;
                if (decimalCount > 1)
                {
                    return false;
                }

                index += decimalSeparator.Length - 1;
                continue;
            }

            return false;
        }

        return true;
    }

    private static string BuildCandidateText(TextBox textBox, string input)
    {
        var selectionStart = Math.Min(
            textBox.SelectionStart,
            textBox.Text.Length);
        var selectionLength = Math.Min(
            textBox.SelectionLength,
            textBox.Text.Length - selectionStart);
        return textBox.Text
            .Remove(selectionStart, selectionLength)
            .Insert(selectionStart, input);
    }

    private void ShowValidationError(string message)
    {
        ValidationMessage.Text = message;
        ValidationMessage.Visibility = Visibility.Visible;
    }

    private void HideValidationError()
    {
        ValidationMessage.Visibility = Visibility.Collapsed;
    }

    private void ProjectLink_RequestNavigate(
        object sender,
        RequestNavigateEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (eventArgs.Uri is not { IsAbsoluteUri: true } uri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or Win32Exception)
        {
            MessageBox.Show(
                this,
                $"无法打开项目链接。\n\n{exception.Message}",
                "ClipNest",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void UpdateHotKeyButton()
    {
        HotKeyButton.Content = _hotKey.DisplayText;
    }
}
