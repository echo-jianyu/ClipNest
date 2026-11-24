using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    private readonly HotKeyDefinition _hotKey;

    private bool _disposed;
    private bool _isResizing;
    private bool _historySaveWarningShown;
    private bool _hotKeyAvailable = true;
    private ClipItem? _pressedClipItem;
    private ClipItem? _pendingCardCopyItem;
    private bool _copyDispatchQueued;
    private ClipItem? _selectedItem;
    private bool _suppressCardClick;
    private bool _selectionRefreshQueued;
    private HwndSource? _windowSource;

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
        _hotKey = HotKeyDefinition.FromSettings(_settings);
        _hotKeyService = new HotKeyService();

        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.ApplyDisplaySettings(
            _settings.CardTextLimit,
            _settings.CardFontSize);
        ApplySettings();

        _viewModel.HistoryChanged += ViewModel_HistoryChanged;
        _viewModel.NotificationRequested += ViewModel_NotificationRequested;
        _viewModel.ItemCopied += ViewModel_ItemCopied;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.VisibleItems.CollectionChanged += VisibleItems_CollectionChanged;
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

    public string HotKeyDisplayText => _hotKeyService.CurrentHotKey.DisplayText;

    public event EventHandler? HotKeyChanged;

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

    public void ShowFromTray(bool activate = true)
    {
        if (IsVisible)
        {
            if (activate)
            {
                Activate();
            }

            return;
        }

        var previousShowActivated = ShowActivated;
        ShowActivated = activate;
        Show();
        ShowActivated = previousShowActivated;
        WindowState = WindowState.Normal;
        Topmost = true;
        if (activate)
        {
            Activate();
            EnsureKeyboardSelection();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                Topmost = true;
                Focus();
            }));
        }
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
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
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

        _hotKeyAvailable = _hotKeyService.Attach(this, _hotKey);
        if (!_hotKeyAvailable)
        {
            var conflict = $"{_hotKey.DisplayText} 快捷键已被其他程序占用。";
            RegistrationWarning = string.IsNullOrWhiteSpace(RegistrationWarning)
                ? conflict
                : $"{RegistrationWarning} {conflict}";
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            ClampWindowToCurrentMonitor();
            UpdateHotKeyUi();
            EnsureKeyboardSelection();
        }));
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != NativeMethods.WmMouseActivate
            || !IsLoaded
            || _viewModel.IsEditing
            || _viewModel.IsEditingImage
            || _viewModel.IsSearchOpen
            || Mouse.RightButton == MouseButtonState.Pressed)
        {
            return IntPtr.Zero;
        }

        var value = lParam.ToInt64();
        var screenPoint = new Point(
            unchecked((short)(value & 0xFFFF)),
            unchecked((short)((value >> 16) & 0xFFFF)));
        var historyPoint = HistoryScrollViewer.PointFromScreen(screenPoint);
        if (historyPoint.X >= 0
            && historyPoint.Y >= 0
            && historyPoint.X <= HistoryScrollViewer.ActualWidth
            && historyPoint.Y <= HistoryScrollViewer.ActualHeight)
        {
            handled = true;
            return new IntPtr(NativeMethods.MaNoActivate);
        }

        return IntPtr.Zero;
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

    private void ViewModel_ItemCopied(object? sender, EventArgs eventArgs)
    {
        if (_settings.HideAfterCopy)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(HideToTray));
        }
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

        if (eventArgs.PropertyName == nameof(MainViewModel.IsSearchOpen)
            && _viewModel.IsSearchOpen)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                SearchTextBox.Focus();
                SearchTextBox.SelectAll();
            }));
        }

        if (eventArgs.PropertyName == nameof(MainViewModel.IsEditingImage)
            && _viewModel.IsEditingImage)
        {
            Activate();
        }
    }

    private void VisibleItems_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs)
    {
        if (_selectionRefreshQueued)
        {
            return;
        }

        _selectionRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _selectionRefreshQueued = false;
            EnsureKeyboardSelection();
        }));
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
        if (IsVisible)
        {
            HideToTray();
        }
        else
        {
            ShowFromTray(activate: false);
        }
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

        var direction = eventArgs.Delta > 0 ? 1 : -1;
        var currentStep = Math.Round(Width / ResizeStep);
        var nextWidth = Math.Min(
            Math.Max(
                (currentStep + direction) * ResizeStep,
                AppSettings.MinimumWidth),
            AppSettings.MaximumWidth);
        var nextHeight = nextWidth / AppSettings.AspectRatio;

        // Keep the window origin fixed; only the right and bottom edges move.
        _isResizing = true;
        Width = nextWidth;
        Height = nextHeight;
        _isResizing = false;

        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
        eventArgs.Handled = true;
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs eventArgs)
    {
        var horizontal = eventArgs.HorizontalChange;
        var vertical = eventArgs.VerticalChange * AppSettings.AspectRatio;
        var delta = Math.Abs(horizontal) >= Math.Abs(vertical)
            ? horizontal
            : vertical;
        if (Math.Abs(delta) < 0.1)
        {
            return;
        }

        var workArea = GetCurrentMonitorWorkArea();
        var maximumWidth = Math.Max(
            AppSettings.MinimumWidth,
            Math.Min(
                AppSettings.MaximumWidth,
                Math.Min(
                    workArea.Width - 12,
                    (workArea.Height - 12) * AppSettings.AspectRatio)));
        var nextWidth = Math.Min(
            Math.Max(Width + delta, AppSettings.MinimumWidth),
            maximumWidth);
        var nextHeight = nextWidth / AppSettings.AspectRatio;

        _isResizing = true;
        Width = nextWidth;
        Height = nextHeight;
        Left = Math.Min(Math.Max(Left, workArea.Left), Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Min(Math.Max(Top, workArea.Top), Math.Max(workArea.Top, workArea.Bottom - Height));
        _isResizing = false;

        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
        eventArgs.Handled = true;
    }

    private void ClampWindowToCurrentMonitor()
    {
        if (!IsLoaded)
        {
            return;
        }

        var workArea = GetCurrentMonitorWorkArea();
        Left = Math.Min(
            Math.Max(Left, workArea.Left),
            Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Min(
            Math.Max(Top, workArea.Top),
            Math.Max(workArea.Top, workArea.Bottom - Height));
    }

    private Rect GetCurrentMonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            var monitor = NativeMethods.MonitorFromWindow(
                handle,
                NativeMethods.MonitorDefaultToNearest);
            var info = new NativeMethods.MonitorInfo
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
            };
            if (monitor != IntPtr.Zero
                && NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                return new Rect(
                    info.Work.Left / dpi.DpiScaleX,
                    info.Work.Top / dpi.DpiScaleY,
                    info.Work.Width / dpi.DpiScaleX,
                    info.Work.Height / dpi.DpiScaleY);
            }
        }

        return SystemParameters.WorkArea;
    }

    private void HideButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        HideToTray();
    }

    private void ClipItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: ClipItem item }
            || IsButtonSource(eventArgs.OriginalSource as DependencyObject))
        {
            return;
        }

        SelectItem(item);
        _pressedClipItem = item;
        _suppressCardClick = false;

        if (eventArgs.ClickCount == 2 && item.IsImage)
        {
            ShowImagePreview(item);
            _suppressCardClick = true;
            eventArgs.Handled = true;
        }
    }

    private void ClipItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        var pressedItem = _pressedClipItem;
        _pressedClipItem = null;
        if (_suppressCardClick)
        {
            _suppressCardClick = false;
            eventArgs.Handled = true;
            return;
        }

        if (sender is FrameworkElement { DataContext: ClipItem item }
            && ReferenceEquals(item, pressedItem)
            && !IsButtonSource(eventArgs.OriginalSource as DependencyObject))
        {
            SelectItem(item);
            ScheduleCardCopy(item);
            eventArgs.Handled = true;
        }
    }

    private void ScheduleCardCopy(ClipItem item)
    {
        _pendingCardCopyItem = item;
        if (_copyDispatchQueued)
        {
            return;
        }

        _copyDispatchQueued = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(CopyPendingCard));
    }

    private void CopyPendingCard()
    {
        _copyDispatchQueued = false;
        if (_disposed)
        {
            _pendingCardCopyItem = null;
            return;
        }

        var item = _pendingCardCopyItem;
        _pendingCardCopyItem = null;
        if (item is not null && _viewModel.VisibleItems.Contains(item))
        {
            _viewModel.CopyFormattedCommand.Execute(item);
        }
    }

    private void CancelPendingCardCopy()
    {
        _pendingCardCopyItem = null;
    }

    private void ClipItem_MouseRightButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: ClipItem item } target)
        {
            return;
        }

        CancelPendingCardCopy();
        SelectItem(item);
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

    private void CloseSearchButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        _viewModel.CloseSearchCommand.Execute(null);
        HistoryScrollViewer.Focus();
    }

    private void SearchFilter_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement { Tag: string filter })
        {
            _viewModel.SetFilterCommand.Execute(filter);
            eventArgs.Handled = true;
        }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        ShowSettings();
        eventArgs.Handled = true;
    }

    public void ShowSettings()
    {
        var dialog = new SettingsWindow(
            _settings.CardTextLimit,
            _settings.CardFontSize,
            _hotKeyService.CurrentHotKey,
            _settings.HideAfterCopy);
        if (IsVisible)
        {
            dialog.Owner = this;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _hotKeyService.Suspend();
        var accepted = false;
        try
        {
            accepted = dialog.ShowDialog() == true;
        }
        finally
        {
            if (!accepted)
            {
                _hotKeyAvailable = _hotKeyService.Resume();
                UpdateHotKeyUi();
            }
        }

        if (!accepted)
        {
            return;
        }

        var requestedHotKey = dialog.HotKey;
        var hotKeyUpdated = _hotKeyService.UpdateHotKey(requestedHotKey);
        _hotKeyAvailable = _hotKeyService.IsRegistered;
        if (hotKeyUpdated)
        {
            _settings.HotKeyModifiers = requestedHotKey.Modifiers;
            _settings.HotKeyKey = requestedHotKey.VirtualKey;
        }

        _settings.CardTextLimit = dialog.CardTextLimit;
        _settings.CardFontSize = dialog.CardFontSize;
        _settings.HideAfterCopy = dialog.HideAfterCopy;
        _viewModel.ApplyDisplaySettings(
            _settings.CardTextLimit,
            _settings.CardFontSize);
        _ = SaveSettingsAsync();
        UpdateHotKeyUi();
        if (hotKeyUpdated)
        {
            HotKeyChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ShowToast(
                $"{requestedHotKey.DisplayText} 已被占用，仍使用 " +
                $"{_hotKeyService.CurrentHotKey.DisplayText}，其他设置已保存",
                false);
        }
    }

    private void UpdateHotKeyUi()
    {
        HotKeyConflictIndicator.Visibility = _hotKeyAvailable
            ? Visibility.Collapsed
            : Visibility.Visible;
        HideButton.ToolTip = $"隐藏 ({_hotKeyService.CurrentHotKey.DisplayText})";
        MoreButton.ToolTip = _hotKeyAvailable
            ? "设置"
            : "设置 · 快捷键冲突";
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            ShowToast("设置保存失败", false);
        }
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs eventArgs)
    {
        if (_viewModel.IsEditing
            || _viewModel.IsEditingImage
            || _viewModel.IsSearchOpen
            || Keyboard.FocusedElement is TextBoxBase
            || string.IsNullOrEmpty(eventArgs.Text))
        {
            return;
        }

        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return;
        }

        var text = eventArgs.Text;
        _viewModel.OpenSearchCommand.Execute(null);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            SearchTextBox.Focus();
            _viewModel.SearchText = text;
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
        }));
        eventArgs.Handled = true;
    }

    private void SelectItem(ClipItem item)
    {
        if (!_viewModel.VisibleItems.Contains(item))
        {
            return;
        }

        if (_selectedItem is not null && !ReferenceEquals(_selectedItem, item))
        {
            _selectedItem.IsKeyboardSelected = false;
        }

        _selectedItem = item;
        item.IsKeyboardSelected = true;
    }

    private void EnsureKeyboardSelection()
    {
        if (_selectedItem is not null
            && _viewModel.VisibleItems.Contains(_selectedItem))
        {
            return;
        }

        if (_selectedItem is not null)
        {
            _selectedItem.IsKeyboardSelected = false;
            _selectedItem = null;
        }

        if (_viewModel.VisibleItems.Count > 0)
        {
            SelectItem(_viewModel.VisibleItems[0]);
        }
    }

    private void MoveKeyboardSelection(int offset)
    {
        if (_viewModel.VisibleItems.Count == 0)
        {
            return;
        }

        EnsureKeyboardSelection();
        var index = _selectedItem is null
            ? 0
            : _viewModel.VisibleItems.IndexOf(_selectedItem);
        var nextIndex = Math.Min(
            Math.Max(index + offset, 0),
            _viewModel.VisibleItems.Count - 1);
        SelectItem(_viewModel.VisibleItems[nextIndex]);
        BringSelectedItemIntoView();
    }

    private void BringSelectedItemIntoView()
    {
        if (_selectedItem is null)
        {
            return;
        }

        var container = HistoryItemsControl.ItemContainerGenerator
            .ContainerFromItem(_selectedItem) as FrameworkElement;
        container?.BringIntoView();
    }

    private void ShowImagePreview(ClipItem item)
    {
        CancelPendingCardCopy();
        var bytes = item.GetImageBytes();
        if (bytes is null)
        {
            return;
        }

        try
        {
            ImagePreviewLarge.Source = DecodeImage(bytes);
            ImagePreviewDimensions.Text = item.ImageDimensionsLabel;
            ImagePreviewOverlay.Visibility = Visibility.Visible;
            Activate();
        }
        catch (FormatException)
        {
            ShowToast("图片数据损坏，无法预览", false);
        }
        catch (NotSupportedException)
        {
            ShowToast("图片格式不受支持", false);
        }
    }

    private void CloseImagePreview()
    {
        ImagePreviewOverlay.Visibility = Visibility.Collapsed;
        ImagePreviewLarge.Source = null;
    }

    private void ImagePreviewOverlay_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (IsButtonSource(eventArgs.OriginalSource as DependencyObject))
        {
            return;
        }

        CloseImagePreview();
        eventArgs.Handled = true;
    }

    private void ImagePreviewCloseButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        CloseImagePreview();
        eventArgs.Handled = true;
    }

    private static bool IsButtonSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
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
            if (ImagePreviewOverlay.Visibility == Visibility.Visible)
            {
                CloseImagePreview();
            }
            else if (_viewModel.IsSearchOpen)
            {
                _viewModel.CloseSearchCommand.Execute(null);
            }
            else if (_viewModel.IsEditingImage)
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

        if (eventArgs.Key == Key.F
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && !_viewModel.IsEditing
            && !_viewModel.IsEditingImage)
        {
            _viewModel.OpenSearchCommand.Execute(null);
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
            return;
        }

        if (_viewModel.IsEditing || _viewModel.IsEditingImage)
        {
            return;
        }

        var searchHasFocus = Keyboard.FocusedElement is TextBoxBase
            && _viewModel.IsSearchOpen;
        if (eventArgs.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            if (searchHasFocus && Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            var offset = eventArgs.Key is Key.Up or Key.Left ? -1 : 1;
            MoveKeyboardSelection(offset);
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Enter)
        {
            EnsureKeyboardSelection();
            if (_selectedItem is not null)
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                    && _selectedItem.IsText)
                {
                    _viewModel.CopyPlainCommand.Execute(_selectedItem);
                }
                else
                {
                    _viewModel.CopyFormattedCommand.Execute(_selectedItem);
                }

                eventArgs.Handled = true;
            }

            return;
        }

        if (searchHasFocus || Keyboard.FocusedElement is TextBoxBase)
        {
            return;
        }

        if (eventArgs.Key == Key.Delete && _selectedItem is not null)
        {
            _viewModel.DeleteCommand.Execute(_selectedItem);
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.P
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && _selectedItem is not null)
        {
            _viewModel.TogglePinCommand.Execute(_selectedItem);
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Space
            && Keyboard.Modifiers == ModifierKeys.None
            && _selectedItem is { IsImage: true })
        {
            ShowImagePreview(_selectedItem);
            eventArgs.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None
            && eventArgs.Key is >= Key.D1 and <= Key.D5)
        {
            var index = (int)eventArgs.Key - (int)Key.D1;
            if (index < _viewModel.VisibleItems.Count)
            {
                var item = _viewModel.VisibleItems[index];
                SelectItem(item);
                _viewModel.CopyFormattedCommand.Execute(item);
                eventArgs.Handled = true;
            }
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

    private static BitmapImage DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
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
        CancelPendingCardCopy();
        _selectionRefreshQueued = false;
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        _viewModel.HistoryChanged -= ViewModel_HistoryChanged;
        _viewModel.NotificationRequested -= ViewModel_NotificationRequested;
        _viewModel.ItemCopied -= ViewModel_ItemCopied;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.VisibleItems.CollectionChanged -= VisibleItems_CollectionChanged;
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
