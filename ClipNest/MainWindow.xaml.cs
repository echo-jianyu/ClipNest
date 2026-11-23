using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClipNest.Infrastructure;
using ClipNest.Models;
using ClipNest.Services;
using ClipNest.ViewModels;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ClipNest;

public partial class MainWindow : Window, IDisposable
{
    private const double ResizeStep = 30;

    private readonly MainViewModel _viewModel;
    private readonly IClipboardService _clipboardService;
    private readonly HotKeyService _hotKeyService;
    private readonly HistoryStore _historyStore;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _historySaveTimer;
    private readonly DispatcherTimer _placementSaveTimer;
    private readonly DispatcherTimer _toastTimer;

    private bool _disposed;
    private bool _isResizing;
    private bool _historySaveWarningShown;

    public MainWindow(
        MainViewModel viewModel,
        IClipboardService clipboardService,
        HistoryStore historyStore,
        SettingsStore settingsStore,
        AppSettings settings)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hotKeyService = new HotKeyService();

        InitializeComponent();
        DataContext = _viewModel;
        ApplySettings();

        _viewModel.HistoryChanged += ViewModel_HistoryChanged;
        _viewModel.NotificationRequested += ViewModel_NotificationRequested;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _hotKeyService.Pressed += HotKeyService_Pressed;

        _historySaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _historySaveTimer.Tick += HistorySaveTimer_Tick;

        _placementSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _placementSaveTimer.Tick += PlacementSaveTimer_Tick;

        _toastTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _toastTimer.Tick += ToastTimer_Tick;

        SourceInitialized += MainWindow_SourceInitialized;
        LocationChanged += MainWindow_PlacementChanged;
        SizeChanged += MainWindow_SizeChanged;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    public string? RegistrationWarning { get; private set; }

    public void HideToTray()
    {
        if (!IsVisible)
        {
            return;
        }

        Hide();
        _placementSaveTimer.Stop();
        _ = SavePlacementAsync();
    }

    public void ShowFromTray()
    {
        if (IsVisible)
        {
            Activate();
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
        Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Topmost = true;
            Focus();
        }));
    }

    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            HideToTray();
        }
        else
        {
            ShowFromTray();
        }
    }

    public async Task FlushAsync()
    {
        _historySaveTimer.Stop();
        _placementSaveTimer.Stop();

        await _historyStore.SaveAsync(_viewModel.Items.ToList());
        await SavePlacementAsync();
    }

    private void ApplySettings()
    {
        _settings.Normalize();
        Width = _settings.Width;
        Height = _settings.Height;
        MinWidth = AppSettings.MinimumWidth;
        MinHeight = AppSettings.MinimumWidth / AppSettings.AspectRatio;
        MaxWidth = AppSettings.MaximumWidth;
        MaxHeight = AppSettings.MaximumWidth / AppSettings.AspectRatio;

        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        if (_settings.Left is not null && _settings.Top is not null)
        {
            var restoredBounds = new Rect(
                _settings.Left.Value,
                _settings.Top.Value,
                Math.Max(90, Width / 3),
                Math.Max(60, Height / 3));

            if (virtualScreen.IntersectsWith(restoredBounds))
            {
                var maximumLeft = Math.Max(
                    virtualScreen.Left,
                    virtualScreen.Right - Width);
                var maximumTop = Math.Max(
                    virtualScreen.Top,
                    virtualScreen.Bottom - Height);
                Left = Math.Min(
                    Math.Max(_settings.Left.Value, virtualScreen.Left),
                    maximumLeft);
                Top = Math.Min(
                    Math.Max(_settings.Top.Value, virtualScreen.Top),
                    maximumTop);
                return;
            }
        }

        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 18, workArea.Right - Width - 22);
        Top = workArea.Top + 22;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        extendedStyle |= NativeMethods.WsExToolWindow;
        extendedStyle &= ~NativeMethods.WsExAppWindow;
        NativeMethods.SetWindowLongPtr(
            handle,
            NativeMethods.GwlExStyle,
            new IntPtr(extendedStyle));

        try
        {
            _clipboardService.Attach(this);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            RegistrationWarning = "无法监听系统剪贴板。";
        }

        if (!_hotKeyService.Attach(this))
        {
            RegistrationWarning = string.IsNullOrWhiteSpace(RegistrationWarning)
                ? "F4 快捷键已被其他程序占用。"
                : $"{RegistrationWarning} F4 快捷键已被其他程序占用。";
        }
    }

    private void ViewModel_HistoryChanged(object? sender, EventArgs eventArgs)
    {
        _historySaveTimer.Stop();
        _historySaveTimer.Start();
    }

    private void ViewModel_NotificationRequested(string message, bool canUndo)
    {
        Dispatcher.BeginInvoke(new Action(() => ShowToast(message, canUndo)));
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainViewModel.IsEditing) && _viewModel.IsEditing)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                EditTextBox.Focus();
                EditTextBox.SelectAll();
            }));
        }
    }

    private async void HistorySaveTimer_Tick(object? sender, EventArgs eventArgs)
    {
        _historySaveTimer.Stop();
        try
        {
            await _historyStore.SaveAsync(_viewModel.Items.ToList());
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or System.Runtime.Serialization.SerializationException
            or NotSupportedException)
        {
            if (!_historySaveWarningShown)
            {
                _historySaveWarningShown = true;
                ShowToast("历史记录自动保存失败", false);
            }
        }
    }

    private async void PlacementSaveTimer_Tick(object? sender, EventArgs eventArgs)
    {
        _placementSaveTimer.Stop();
        await SavePlacementAsync();
    }

    private async Task SavePlacementAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        _settings.Width = Width;
        _settings.Left = Left;
        _settings.Top = Top;

        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            if (!_historySaveWarningShown)
            {
                _historySaveWarningShown = true;
                ShowToast("窗口位置保存失败", false);
            }
        }
    }

    private void MainWindow_PlacementChanged(object? sender, EventArgs eventArgs)
    {
        if (IsLoaded && !_isResizing)
        {
            _placementSaveTimer.Stop();
            _placementSaveTimer.Start();
        }
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs eventArgs)
    {
        if (!_isResizing && Math.Abs(Width / Height - AppSettings.AspectRatio) > 0.01)
        {
            _isResizing = true;
            Height = Width / AppSettings.AspectRatio;
            _isResizing = false;
        }

        if (IsLoaded)
        {
            _placementSaveTimer.Stop();
            _placementSaveTimer.Start();
        }
    }

    private void HotKeyService_Pressed(object? sender, EventArgs eventArgs)
    {
        ToggleVisibility();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (IsInteractiveElement(eventArgs.OriginalSource as DependencyObject))
        {
            return;
        }

        if (eventArgs.ClickCount == 2)
        {
            HideToTray();
            eventArgs.Handled = true;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void DragSurface_PreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        ResizeWithWheel(eventArgs);
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0)
        {
            ResizeWithWheel(eventArgs);
        }
    }

    private void ResizeWithWheel(MouseWheelEventArgs eventArgs)
    {
        if (eventArgs.Delta == 0)
        {
            return;
        }

        var centerX = Left + (ActualWidth / 2);
        var centerY = Top + (ActualHeight / 2);
        var direction = eventArgs.Delta > 0 ? 1 : -1;
        var currentStep = Math.Round(Width / ResizeStep);
        var nextWidth = Math.Min(
            Math.Max(
                (currentStep + direction) * ResizeStep,
                AppSettings.MinimumWidth),
            AppSettings.MaximumWidth);
        var nextHeight = nextWidth / AppSettings.AspectRatio;
        var virtualRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        var maximumLeft = Math.Max(SystemParameters.VirtualScreenLeft, virtualRight - nextWidth);
        var maximumTop = Math.Max(SystemParameters.VirtualScreenTop, virtualBottom - nextHeight);

        _isResizing = true;
        Width = nextWidth;
        Height = nextHeight;
        Left = Math.Min(
            Math.Max(centerX - (nextWidth / 2), SystemParameters.VirtualScreenLeft),
            maximumLeft);
        Top = Math.Min(
            Math.Max(centerY - (nextHeight / 2), SystemParameters.VirtualScreenTop),
            maximumTop);
        _isResizing = false;

        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
        eventArgs.Handled = true;
    }

    private void HideButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        HideToTray();
    }

    private void ClipItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is FrameworkElement { DataContext: ClipItem item })
        {
            _viewModel.CopyFormattedCommand.Execute(item);
            eventArgs.Handled = true;
        }
    }

    private void ClipItem_MouseRightButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: ClipItem item } target)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = PlacementMode.MousePoint,
            Style = (Style)FindResource("ClipContextMenuStyle")
        };

        if (item.IsText)
        {
            menu.Items.Add(CreateMenuItem(
                "复制纯文本",
                () => _viewModel.CopyPlainCommand.Execute(item)));
        }

        menu.Items.Add(CreateMenuItem(
            item.IsImage ? "编辑图片" : "编辑",
            () => _viewModel.EditCommand.Execute(item)));
        menu.Items.Add(CreateMenuItem(
            item.PinLabel,
            () => _viewModel.TogglePinCommand.Execute(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(
            "删除",
            () => _viewModel.DeleteCommand.Execute(item),
            isDanger: true));

        menu.IsOpen = true;
        eventArgs.Handled = true;
    }

    private MenuItem CreateMenuItem(
        string header,
        Action execute,
        bool isDanger = false)
    {
        var menuItem = new MenuItem
        {
            Header = header,
            IsEnabled = true,
            Style = (Style)FindResource(
                isDanger ? "DangerClipMenuItemStyle" : "ClipMenuItemStyle")
        };
        menuItem.Click += (_, _) => execute();
        return menuItem;
    }

    private void UndoToastButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        _viewModel.UndoDeleteCommand.Execute(null);
        HideToast();
    }

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            if (_viewModel.IsEditingImage)
            {
                _viewModel.CancelImageEditCommand.Execute(null);
            }
            else if (_viewModel.IsEditing)
            {
                _viewModel.CancelEditCommand.Execute(null);
            }
            else
            {
                HideToTray();
            }

            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Enter
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && (_viewModel.IsEditing || _viewModel.IsEditingImage))
        {
            if (_viewModel.IsEditingImage)
            {
                _viewModel.SaveImageEditCommand.Execute(null);
            }
            else
            {
                _viewModel.SaveEditCommand.Execute(null);
            }

            eventArgs.Handled = true;
        }
    }

    private void ShowToast(string message, bool canUndo)
    {
        _toastTimer.Stop();
        ToastMessage.Text = message;
        UndoToastButton.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        ToastHost.Visibility = Visibility.Visible;
        ToastHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        _toastTimer.Interval = canUndo ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(3);
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
        animation.Completed += (_, _) => ToastHost.Visibility = Visibility.Collapsed;
        ToastHost.BeginAnimation(OpacityProperty, animation);
    }

    private void ToastTimer_Tick(object? sender, EventArgs eventArgs)
    {
        HideToast();
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { Tag: "ClipItem" })
            {
                return true;
            }

            if (source is ButtonBase or TextBoxBase or ScrollBar)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs eventArgs)
    {
        if (Application.Current is App { IsShuttingDown: true })
        {
            return;
        }

        eventArgs.Cancel = true;
        HideToTray();
    }

    private void MainWindow_Closed(object? sender, EventArgs eventArgs)
    {
        DisposeResources();
    }

    private void DisposeResources()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _historySaveTimer.Stop();
        _placementSaveTimer.Stop();
        _toastTimer.Stop();
        _viewModel.HistoryChanged -= ViewModel_HistoryChanged;
        _viewModel.NotificationRequested -= ViewModel_NotificationRequested;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _hotKeyService.Pressed -= HotKeyService_Pressed;
        _hotKeyService.Dispose();
        _clipboardService.Detach();
        _clipboardService.Dispose();
    }

    public void Dispose()
    {
        DisposeResources();
        GC.SuppressFinalize(this);
    }
}
