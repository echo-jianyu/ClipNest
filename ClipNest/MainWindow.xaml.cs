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
    private const double WheelScaleFactor = 1.08;
    private const double ImagePreviewWheelScaleFactor = 1.15;
    private const double ImagePreviewInitialScale = 1;
    private const double ImagePreviewMinimumScale = 0.2;
    private const double ImagePreviewMaximumScale = 10;

    private readonly MainViewModel _viewModel;
    private readonly IClipboardService _clipboardService;
    private readonly HotKeyService _hotKeyService;
    private readonly HistoryStore _historyStore;
    private readonly SettingsStore _settingsStore;
    private readonly AutoStartService _autoStartService;
    private readonly ThemeService _themeService;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _historySaveTimer;
    private readonly DispatcherTimer _placementSaveTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly HotKeyDefinition _hotKey;

    private bool _disposed;
    private bool _isResizing;
    private Point _resizeStartCursor;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private bool _historySaveWarningShown;
    private bool _hotKeyAvailable = true;
    private ClipItem? _pressedClipItem;
    private Point _cardDragStartPoint;
    private bool _cardDragStartPointValid;
    private bool _isCardDragging;
    private ClipItem? _selectedItem;
    private bool _suppressCardClick;
    private bool _selectionRefreshQueued;
    private HwndSource? _windowSource;
    private SettingsWindow? _settingsWindow;
    private bool _toastCanUndo;
    private Matrix _imagePreviewMatrix = Matrix.Identity;
    private Matrix _imagePreviewPanStartMatrix = Matrix.Identity;
    private Point _imagePreviewPanStartPoint;
    private Size _imagePreviewFittedContentSize;
    private double _imagePreviewScale = ImagePreviewInitialScale;
    private string _imagePreviewDimensionsText = string.Empty;
    private bool _imagePreviewPointerDown;
    private bool _imagePreviewPanning;

    public MainWindow(
        MainViewModel viewModel,
        IClipboardService clipboardService,
        HistoryStore historyStore,
        SettingsStore settingsStore,
        AutoStartService autoStartService,
        ThemeService themeService,
        AppSettings settings)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _autoStartService = autoStartService ?? throw new ArgumentNullException(nameof(autoStartService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hotKey = HotKeyDefinition.FromSettings(_settings);
        _hotKeyService = new HotKeyService();

        InitializeComponent();
        DpiTextRendering.Apply(this);
        DataContext = _viewModel;
        _viewModel.ApplyDisplaySettings(_settings.CardFontSize);
        ApplySettings();
        ApplyTitleBarVisibility();

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
        DpiChanged += MainWindow_DpiChanged;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    public string? RegistrationWarning { get; private set; }

    public string HotKeyDisplayText => _hotKeyService.CurrentHotKey.DisplayText;

    public event EventHandler? HotKeyChanged;

    public event Action<string>? BackgroundNotificationRequested;

    public void HideToTray()
    {
        if (!IsVisible)
        {
            return;
        }

        CloseImagePreview();
        if (_viewModel.IsSearchOpen)
        {
            _viewModel.CloseSearchCommand.Execute(null);
        }

        if (_viewModel.IsEditing)
        {
            _viewModel.CancelEditCommand.Execute(null);
        }

        if (_viewModel.IsEditingImage)
        {
            _viewModel.CancelImageEditCommand.Execute(null);
        }

        HideToastImmediately();
        Hide();
        _placementSaveTimer.Stop();
        _ = SavePlacementAsync();
    }

    public void ShowFromTray(bool activate = true)
    {
        _themeService.RefreshSystemTheme();
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
        WindowPlacement.ExcludeFromAltTab(this);
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

        var result = await SaveHistoryAsync();
        _viewModel.ApplyExternalizedImages(result);
        await SavePlacementAsync();
    }

    public void FlushForShutdown()
    {
        _historySaveTimer.Stop();
        _placementSaveTimer.Stop();
        _settings.Width = Width;
        _settings.Height = Height;
        _settings.Left = Left;
        _settings.Top = Top;

        var result = SaveHistoryAsync().GetAwaiter().GetResult();
        _viewModel.ApplyExternalizedImages(result);
        _settingsStore.SaveAsync(_settings).GetAwaiter().GetResult();
    }

    private void ApplySettings()
    {
        _settings.Normalize();
        MinWidth = AppSettings.MinimumWidth;
        MinHeight = AppSettings.MinimumHeight;
        MaxWidth = AppSettings.MaximumWidth;
        MaxHeight = AppSettings.MaximumHeight;

        var workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(
            AppSettings.MinimumWidth,
            Math.Min(MaxWidth, workArea.Width - 12));
        MaxHeight = Math.Max(
            AppSettings.MinimumHeight,
            Math.Min(MaxHeight, workArea.Height - 12));
        Width = Math.Min(_settings.Width, MaxWidth);
        Height = Math.Min(_settings.Height, MaxHeight);

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

        Left = Math.Max(workArea.Left + 18, workArea.Right - Width - 22);
        Top = workArea.Top + 22;
    }

    private void ApplyTitleBarVisibility()
    {
        var showTitleBar = !_settings.HideTitleBar;
        TitleBarRow.Height = showTitleBar
            ? new GridLength(35)
            : new GridLength(0);
        DragSurface.Visibility = showTitleBar
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        _themeService.AttachToWindow(this);
        WindowPlacement.ExcludeFromAltTab(this);

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
            || ImagePreviewOverlay.Visibility == Visibility.Visible
            || Mouse.RightButton == MouseButtonState.Pressed)
        {
            return IntPtr.Zero;
        }

        var value = lParam.ToInt64();
        var mouseMessage = (int)((value >> 16) & 0xFFFF);
        if (mouseMessage != 0x0201
            || !NativeMethods.GetCursorPos(out var cursorPosition))
        {
            return IntPtr.Zero;
        }

        var screenPoint = new Point(cursorPosition.X, cursorPosition.Y);
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
        Dispatcher.BeginInvoke(new Action(() => NotifyUser(message, canUndo)));
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
            var result = await SaveHistoryAsync();
            _viewModel.ApplyExternalizedImages(result);
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
                NotifyUser("历史记录自动保存失败", false);
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
        _settings.Height = Height;
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
                NotifyUser("窗口位置保存失败", false);
            }
        }
    }

    private Task<HistorySaveResult> SaveHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        return _historyStore.SaveAsync(
            _viewModel.Items.ToList(),
            _viewModel.GetProtectedImageFileNames(),
            cancellationToken);
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
        if (_isResizing)
        {
            return;
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
            ShowFromTray();
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
        if (ImagePreviewOverlay.Visibility == Visibility.Visible)
        {
            ZoomImagePreview(eventArgs);
            return;
        }

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

        var workArea = GetCurrentMonitorWorkArea();
        var maximumWidth = Math.Max(
            AppSettings.MinimumWidth,
            Math.Min(AppSettings.MaximumWidth, workArea.Width - 12));
        var maximumHeight = Math.Max(
            AppSettings.MinimumHeight,
            Math.Min(AppSettings.MaximumHeight, workArea.Height - 12));
        var scale = eventArgs.Delta > 0
            ? WheelScaleFactor
            : 1 / WheelScaleFactor;
        var nextSize = CalculateScaledSize(
            new Size(Width, Height),
            scale,
            AppSettings.MinimumWidth,
            maximumWidth,
            AppSettings.MinimumHeight,
            maximumHeight);

        // Keep the window origin fixed; only the right and bottom edges move.
        _isResizing = true;
        Width = nextSize.Width;
        Height = nextSize.Height;
        _isResizing = false;
        ClampWindowToCurrentMonitor();

        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
        eventArgs.Handled = true;
    }

    private void ResizeGrip_DragStarted(object sender, DragStartedEventArgs eventArgs)
    {
        _resizeStartWidth = Width;
        _resizeStartHeight = Height;
        _resizeStartCursor = GetCursorScreenPositionInDips();
        _isResizing = true;
        _placementSaveTimer.Stop();
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs eventArgs)
    {
        if (!_isResizing)
        {
            _resizeStartWidth = Width;
            _resizeStartHeight = Height;
            _resizeStartCursor = GetCursorScreenPositionInDips();
            _isResizing = true;
        }

        var workArea = GetCurrentMonitorWorkArea();
        var maximumWidth = Math.Max(
            AppSettings.MinimumWidth,
            Math.Min(AppSettings.MaximumWidth, workArea.Width - 12));
        var maximumHeight = Math.Max(
            AppSettings.MinimumHeight,
            Math.Min(AppSettings.MaximumHeight, workArea.Height - 12));
        var nextSize = CalculateResizeSize(
            _resizeStartWidth,
            _resizeStartHeight,
            _resizeStartCursor,
            GetCursorScreenPositionInDips(),
            AppSettings.MinimumWidth,
            maximumWidth,
            AppSettings.MinimumHeight,
            maximumHeight);

        if (Math.Abs(nextSize.Width - Width) < 0.01
            && Math.Abs(nextSize.Height - Height) < 0.01)
        {
            return;
        }

        Width = nextSize.Width;
        Height = nextSize.Height;
        Left = Math.Min(Math.Max(Left, workArea.Left), Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Min(Math.Max(Top, workArea.Top), Math.Max(workArea.Top, workArea.Bottom - Height));

        eventArgs.Handled = true;
    }

    private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs eventArgs)
    {
        _isResizing = false;
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    internal static Size CalculateResizeSize(
        double startWidth,
        double startHeight,
        Point startCursor,
        Point currentCursor,
        double minimumWidth,
        double maximumWidth,
        double minimumHeight,
        double maximumHeight)
    {
        return new Size(
            Math.Min(
                Math.Max(
                    startWidth + (currentCursor.X - startCursor.X),
                    minimumWidth),
                maximumWidth),
            Math.Min(
                Math.Max(
                    startHeight + (currentCursor.Y - startCursor.Y),
                    minimumHeight),
                maximumHeight));
    }

    internal static Size CalculateScaledSize(
        Size currentSize,
        double requestedScale,
        double minimumWidth,
        double maximumWidth,
        double minimumHeight,
        double maximumHeight)
    {
        if (currentSize.Width <= 0
            || currentSize.Height <= 0
            || requestedScale <= 0)
        {
            return currentSize;
        }

        var minimumScale = Math.Max(
            minimumWidth / currentSize.Width,
            minimumHeight / currentSize.Height);
        var maximumScale = Math.Min(
            maximumWidth / currentSize.Width,
            maximumHeight / currentSize.Height);
        var scale = Math.Min(
            Math.Max(requestedScale, minimumScale),
            maximumScale);
        return new Size(
            currentSize.Width * scale,
            currentSize.Height * scale);
    }

    private Point GetCursorScreenPositionInDips()
    {
        if (!NativeMethods.GetCursorPos(out var cursorPosition))
        {
            var screenPoint = PointToScreen(Mouse.GetPosition(this));
            return GetDeviceToDipTransform().Transform(screenPoint);
        }

        var dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerInchX / 96d;
        if (dpiScale <= 0)
        {
            dpiScale = 1;
        }

        return new Point(cursorPosition.X / dpiScale, cursorPosition.Y / dpiScale);
    }

    private void MainWindow_DpiChanged(object sender, DpiChangedEventArgs eventArgs)
    {
        if (!_isResizing)
        {
            return;
        }

        _resizeStartWidth = Width;
        _resizeStartHeight = Height;
        _resizeStartCursor = GetCursorScreenPositionInDips();
    }

    private void ClampWindowToCurrentMonitor()
    {
        if (!IsLoaded)
        {
            return;
        }

        var workArea = GetCurrentMonitorWorkArea();
        MaxWidth = Math.Max(
            AppSettings.MinimumWidth,
            Math.Min(AppSettings.MaximumWidth, workArea.Width - 12));
        MaxHeight = Math.Max(
            AppSettings.MinimumHeight,
            Math.Min(AppSettings.MaximumHeight, workArea.Height - 12));
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
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
                var transform = GetDeviceToDipTransform();
                var topLeft = transform.Transform(new Point(
                    info.Work.Left,
                    info.Work.Top));
                var bottomRight = transform.Transform(new Point(
                    info.Work.Right,
                    info.Work.Bottom));
                return new Rect(topLeft, bottomRight);
            }
        }

        return SystemParameters.WorkArea;
    }

    private Matrix GetDeviceToDipTransform()
    {
        return PresentationSource
            .FromVisual(this)
            ?.CompositionTarget
            ?.TransformFromDevice
            ?? Matrix.Identity;
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
        _cardDragStartPoint = eventArgs.GetPosition((FrameworkElement)sender);
        _cardDragStartPointValid = true;
        _suppressCardClick = false;

        if (eventArgs.ClickCount == 2 && item.IsImage)
        {
            ShowImagePreview(item);
            _cardDragStartPointValid = false;
            _suppressCardClick = true;
            eventArgs.Handled = true;
        }
    }

    private void ClipItem_MouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (_isCardDragging
            || _pressedClipItem is null
            || !_cardDragStartPointValid
            || eventArgs.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement { DataContext: ClipItem item } target
            || !ReferenceEquals(item, _pressedClipItem))
        {
            return;
        }

        var point = eventArgs.GetPosition(target);
        if (Math.Abs(point.X - _cardDragStartPoint.X)
                < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _cardDragStartPoint.Y)
                < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        BeginCardDrag(item, target);
    }

    private void ClipItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        var pressedItem = _pressedClipItem;
        ResetCardPointer();
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
            CopyCardImmediately(item);
            eventArgs.Handled = true;
        }
    }

    private void BeginCardDrag(ClipItem item, FrameworkElement target)
    {
        _isCardDragging = true;
        _suppressCardClick = true;
        ResetCardPointer();

        try
        {
            var dataObject = ClipboardService.BuildDragDataObject(item);
            var dragResult = DragDrop.DoDragDrop(
                target,
                dataObject,
                DragDropEffects.Copy);
            if (dragResult != DragDropEffects.None)
            {
                ActivateDropTargetUnderCursor();
            }
        }
        catch (Exception exception) when (
            exception is FormatException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException
            or System.Runtime.InteropServices.ExternalException)
        {
            ShowToast("无法拖动这条记录", false);
        }
        finally
        {
            _isCardDragging = false;
        }
    }

    private void ActivateDropTargetUnderCursor()
    {
        if (!NativeMethods.GetCursorPos(out var cursorPosition))
        {
            return;
        }

        var targetWindow = NativeMethods.GetAncestor(
            NativeMethods.WindowFromPoint(cursorPosition),
            NativeMethods.GaRoot);
        var sourceWindow = new WindowInteropHelper(this).Handle;
        if (targetWindow == IntPtr.Zero || targetWindow == sourceWindow)
        {
            return;
        }

        NativeMethods.SetForegroundWindow(targetWindow);
    }

    private void ResetCardPointer()
    {
        _pressedClipItem = null;
        _cardDragStartPointValid = false;
    }

    private void CopyCardImmediately(ClipItem item)
    {
        if (!_disposed && _viewModel.VisibleItems.Contains(item))
        {
            _viewModel.CopyFormattedCommand.Execute(item);
        }
    }

    private void ClipItem_MouseRightButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: ClipItem item } target)
        {
            return;
        }

        ResetCardPointer();
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

    private void MoreButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (Application.Current is App application)
        {
            application.ShowApplicationMenu(MoreButton);
        }

        eventArgs.Handled = true;
    }

    public void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.IsVisible)
            {
                _settingsWindow.Activate();
            }

            return;
        }

        var previousThemeMode = _settings.ThemeMode;
        var dialog = new SettingsWindow(
            _settings.CardFontSize,
            _hotKeyService.CurrentHotKey,
            _settings.HideAfterCopy,
            _settings.AutoStartOnLogin,
            _settings.ThemeMode,
            _settings.HideTitleBar);
        dialog.ThemePreviewRequested += _themeService.ApplyTheme;
        _settingsWindow = dialog;
        if (IsVisible)
        {
            dialog.Owner = this;
            WindowPlacement.ConstrainToWindowScreen(
                dialog,
                this,
                maximumHeightRatio: 0.85);
        }
        else
        {
            WindowPlacement.CenterOnCursor(dialog, maximumHeightRatio: 0.85);
        }

        _hotKeyService.Suspend();
        var accepted = false;
        try
        {
            accepted = dialog.ShowDialog() == true;
        }
        finally
        {
            dialog.ThemePreviewRequested -= _themeService.ApplyTheme;
            _settingsWindow = null;
            if (!accepted)
            {
                _themeService.ApplyTheme(previousThemeMode);
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

        var autoStartUpdated = _autoStartService.TrySetEnabled(
            dialog.AutoStartOnLogin,
            out _);
        if (autoStartUpdated)
        {
            _settings.AutoStartOnLogin = dialog.AutoStartOnLogin;
        }

        _settings.CardFontSize = dialog.CardFontSize;
        _settings.HideAfterCopy = dialog.HideAfterCopy;
        _settings.HideTitleBar = dialog.HideTitleBar;
        _settings.ThemeMode = dialog.ThemeMode;
        _themeService.ApplyTheme(_settings.ThemeMode);
        ApplyTitleBarVisibility();
        _viewModel.ApplyDisplaySettings(_settings.CardFontSize);
        _ = SaveSettingsAsync();
        UpdateHotKeyUi();
        var notices = new List<string>();
        if (!autoStartUpdated)
        {
            notices.Add("开机自启动设置失败，其他设置已保存");
        }

        if (hotKeyUpdated)
        {
            HotKeyChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            notices.Add(
                $"{requestedHotKey.DisplayText} 已被占用，仍使用 " +
                $"{_hotKeyService.CurrentHotKey.DisplayText}，其他设置已保存");
        }

        if (notices.Count > 0)
        {
            NotifyUser(string.Join(" ", notices), false);
        }
    }

    private void UpdateHotKeyUi()
    {
        HotKeyConflictDot.Visibility = _hotKeyAvailable
            ? Visibility.Collapsed
            : Visibility.Visible;
        MoreButton.ToolTip = _hotKeyAvailable
            ? "菜单"
            : "菜单 · 快捷键冲突";
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
            NotifyUser("设置保存失败", false);
        }
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs eventArgs)
    {
        if (ImagePreviewOverlay.Visibility == Visibility.Visible
            || _viewModel.IsEditing
            || _viewModel.IsEditingImage
            || Keyboard.FocusedElement is TextBoxBase
            || string.IsNullOrEmpty(eventArgs.Text))
        {
            return;
        }

        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return;
        }

        if (_viewModel.IsSearchOpen)
        {
            _viewModel.SearchText += eventArgs.Text;
            eventArgs.Handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                SearchTextBox.Focus();
                SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
            }));
            return;
        }

        _viewModel.OpenSearchCommand.Execute(null);
        _viewModel.SearchText = eventArgs.Text;
        eventArgs.Handled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            SearchTextBox.Focus();
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
        }));
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
        var bytes = item.GetImageBytes();
        if (bytes is null)
        {
            return;
        }

        try
        {
            var image = ImageCodec.Decode(bytes);
            ImagePreviewLarge.Source = image;
            _imagePreviewDimensionsText = item.ImageDimensionsLabel;
            ImagePreviewOverlay.Visibility = Visibility.Visible;
            ImagePreviewOverlay.UpdateLayout();
            UpdateImagePreviewContentLayout(image);
            ResetImagePreviewZoom();
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
        if (ImagePreviewOverlay.IsMouseCaptured)
        {
            ImagePreviewOverlay.ReleaseMouseCapture();
        }

        if (ImagePreviewViewport.IsMouseCaptured)
        {
            ImagePreviewViewport.ReleaseMouseCapture();
        }

        ImagePreviewOverlay.Visibility = Visibility.Collapsed;
        ImagePreviewLarge.Source = null;
        ResetImagePreviewZoom();
    }

    private void ZoomImagePreview(MouseWheelEventArgs eventArgs)
    {
        if (eventArgs.Delta == 0)
        {
            return;
        }

        var scaleStep = Math.Pow(
            ImagePreviewWheelScaleFactor,
            eventArgs.Delta / 120d);
        var nextScale = Math.Max(
            ImagePreviewMinimumScale,
            Math.Min(
                ImagePreviewMaximumScale,
                _imagePreviewScale * scaleStep));

        if (Math.Abs(nextScale - _imagePreviewScale) < 0.0001)
        {
            eventArgs.Handled = true;
            return;
        }

        var anchor = eventArgs.GetPosition(ImagePreviewViewport);
        anchor.X = Math.Max(0, Math.Min(ImagePreviewViewport.ActualWidth, anchor.X));
        anchor.Y = Math.Max(0, Math.Min(ImagePreviewViewport.ActualHeight, anchor.Y));

        _imagePreviewMatrix = CalculateImagePreviewZoomMatrix(
            _imagePreviewMatrix,
            anchor,
            nextScale);
        _imagePreviewMatrix = ConstrainImagePreviewPanMatrix(
            _imagePreviewMatrix,
            new Size(
                ImagePreviewViewport.ActualWidth,
                ImagePreviewViewport.ActualHeight),
            GetImagePreviewContentSize());
        _imagePreviewScale = nextScale;
        ImagePreviewTransform.Matrix = _imagePreviewMatrix;
        UpdateImagePreviewCursor();
        UpdateImagePreviewDimensions();
        eventArgs.Handled = true;
    }

    internal static Matrix CalculateImagePreviewZoomMatrix(
        Matrix current,
        Point anchor,
        double nextScale)
    {
        if (current.M11 <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(current));
        }

        if (nextScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextScale));
        }

        var anchorXInImage = (anchor.X - current.OffsetX) / current.M11;
        var anchorYInImage = (anchor.Y - current.OffsetY) / current.M22;
        return new Matrix(
            nextScale,
            0,
            0,
            nextScale,
            anchor.X - (anchorXInImage * nextScale),
            anchor.Y - (anchorYInImage * nextScale));
    }

    internal static Matrix ConstrainImagePreviewPanMatrix(
        Matrix current,
        Size viewport,
        Size content)
    {
        if (current.M11 <= 0
            || viewport.Width <= 0
            || viewport.Height <= 0
            || content.Width <= 0
            || content.Height <= 0)
        {
            return current;
        }

        var scaleX = current.M11;
        var scaleY = current.M22;
        var scaledWidth = content.Width * scaleX;
        var scaledHeight = content.Height * scaleY;

        var minimumOffsetX = Math.Min(0, viewport.Width - scaledWidth);
        var maximumOffsetX = Math.Max(0, viewport.Width - scaledWidth);
        var minimumOffsetY = Math.Min(0, viewport.Height - scaledHeight);
        var maximumOffsetY = Math.Max(0, viewport.Height - scaledHeight);
        var offsetX = Math.Max(
            minimumOffsetX,
            Math.Min(maximumOffsetX, current.OffsetX));
        var offsetY = Math.Max(
            minimumOffsetY,
            Math.Min(maximumOffsetY, current.OffsetY));

        return new Matrix(
            scaleX,
            current.M12,
            current.M21,
            scaleY,
            offsetX,
            offsetY);
    }

    private void ResetImagePreviewZoom()
    {
        _imagePreviewScale = ImagePreviewInitialScale;
        _imagePreviewPointerDown = false;
        _imagePreviewPanning = false;
        var viewportWidth = ImagePreviewViewport.ActualWidth;
        var viewportHeight = ImagePreviewViewport.ActualHeight;
        var offsetX = Math.Max(
            0,
            (viewportWidth - _imagePreviewFittedContentSize.Width) / 2);
        var offsetY = Math.Max(
            0,
            (viewportHeight - _imagePreviewFittedContentSize.Height) / 2);
        _imagePreviewMatrix = new Matrix(1, 0, 0, 1, offsetX, offsetY);
        if (ImagePreviewTransform is not null)
        {
            ImagePreviewTransform.Matrix = _imagePreviewMatrix;
        }

        UpdateImagePreviewCursor();
        UpdateImagePreviewDimensions();
    }

    private Size GetImagePreviewContentSize()
    {
        return _imagePreviewFittedContentSize;
    }

    private void UpdateImagePreviewContentLayout(BitmapSource bitmap)
    {
        var viewportWidth = ImagePreviewViewport.ActualWidth;
        var viewportHeight = ImagePreviewViewport.ActualHeight;
        if (bitmap.PixelWidth <= 0
            || bitmap.PixelHeight <= 0
            || viewportWidth <= 0
            || viewportHeight <= 0)
        {
            _imagePreviewFittedContentSize = Size.Empty;
            ImagePreviewLarge.Width = 0;
            ImagePreviewLarge.Height = 0;
            return;
        }

        var fitScale = Math.Min(
            viewportWidth / bitmap.PixelWidth,
            viewportHeight / bitmap.PixelHeight);
        _imagePreviewFittedContentSize = new Size(
            bitmap.PixelWidth * fitScale,
            bitmap.PixelHeight * fitScale);
        ImagePreviewLarge.Width = _imagePreviewFittedContentSize.Width;
        ImagePreviewLarge.Height = _imagePreviewFittedContentSize.Height;
    }

    private void UpdateImagePreviewCursor()
    {
        ImagePreviewViewport.Cursor = Cursors.SizeAll;
    }

    private void UpdateImagePreviewDimensions()
    {
        ImagePreviewDimensions.Text = string.IsNullOrEmpty(_imagePreviewDimensionsText)
            ? string.Empty
            : $"{_imagePreviewDimensionsText} · {_imagePreviewScale * 100:0}%";
    }

    private void ImagePreviewOverlay_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (IsButtonSource(eventArgs.OriginalSource as DependencyObject)
            || !ReferenceEquals(eventArgs.OriginalSource, ImagePreviewOverlay))
        {
            return;
        }

        CloseImagePreview();
        eventArgs.Handled = true;
    }

    private void ImagePreviewViewport_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ClickCount >= 2)
        {
            CloseImagePreview();
            eventArgs.Handled = true;
            return;
        }

        _imagePreviewPointerDown = true;
        _imagePreviewPanning = false;
        _imagePreviewPanStartPoint = eventArgs.GetPosition(ImagePreviewViewport);
        _imagePreviewPanStartMatrix = _imagePreviewMatrix;
        ImagePreviewViewport.CaptureMouse();
        eventArgs.Handled = true;
    }

    private void ImagePreviewViewport_PreviewMouseMove(
        object sender,
        MouseEventArgs eventArgs)
    {
        if (!_imagePreviewPointerDown
            || eventArgs.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = eventArgs.GetPosition(ImagePreviewViewport);
        if (!_imagePreviewPanning)
        {
            if (Math.Abs(point.X - _imagePreviewPanStartPoint.X)
                    < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(point.Y - _imagePreviewPanStartPoint.Y)
                    < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _imagePreviewPanning = true;
            ImagePreviewViewport.Cursor = Cursors.ScrollAll;
        }

        var nextMatrix = new Matrix(
            _imagePreviewPanStartMatrix.M11,
            _imagePreviewPanStartMatrix.M12,
            _imagePreviewPanStartMatrix.M21,
            _imagePreviewPanStartMatrix.M22,
            _imagePreviewPanStartMatrix.OffsetX
                + point.X
                - _imagePreviewPanStartPoint.X,
            _imagePreviewPanStartMatrix.OffsetY
                + point.Y
                - _imagePreviewPanStartPoint.Y);
        _imagePreviewMatrix = ConstrainImagePreviewPanMatrix(
            nextMatrix,
            new Size(
                ImagePreviewViewport.ActualWidth,
                ImagePreviewViewport.ActualHeight),
            GetImagePreviewContentSize());
        ImagePreviewTransform.Matrix = _imagePreviewMatrix;
        eventArgs.Handled = true;
    }

    private void ImagePreviewViewport_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (!_imagePreviewPointerDown)
        {
            return;
        }

        _imagePreviewPointerDown = false;
        _imagePreviewPanning = false;
        if (ImagePreviewViewport.IsMouseCaptured)
        {
            ImagePreviewViewport.ReleaseMouseCapture();
        }

        UpdateImagePreviewCursor();
        eventArgs.Handled = true;
    }

    private void ImagePreviewViewport_LostMouseCapture(
        object sender,
        MouseEventArgs eventArgs)
    {
        _imagePreviewPointerDown = false;
        _imagePreviewPanning = false;
        UpdateImagePreviewCursor();
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
        _toastCanUndo = false;
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

        if (ImagePreviewOverlay.Visibility == Visibility.Visible)
        {
            if (eventArgs.Key == Key.Space)
            {
                CloseImagePreview();
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
            && TryGetHistoryShortcutIndex(eventArgs.Key, out var shortcutIndex)
            && shortcutIndex < _viewModel.VisibleItems.Count)
        {
            var item = _viewModel.VisibleItems[shortcutIndex];
            SelectItem(item);
            _viewModel.CopyFormattedCommand.Execute(item);
            eventArgs.Handled = true;
        }
    }

    internal static bool TryGetHistoryShortcutIndex(Key key, out int index)
    {
        switch (key)
        {
            case Key.D1:
            case Key.NumPad1:
                index = 0;
                return true;
            case Key.D2:
            case Key.NumPad2:
                index = 1;
                return true;
            case Key.D3:
            case Key.NumPad3:
                index = 2;
                return true;
            case Key.D4:
            case Key.NumPad4:
                index = 3;
                return true;
            case Key.D5:
            case Key.NumPad5:
                index = 4;
                return true;
            default:
                index = -1;
                return false;
        }
    }

    private void ShowToast(string message, bool canUndo)
    {
        if (_toastCanUndo && !canUndo)
        {
            _toastCanUndo = false;
            _viewModel.DiscardUndo();
        }

        _toastTimer.Stop();
        ToastMessage.Text = message;
        UndoToastButton.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        _toastCanUndo = canUndo;
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

    private void HideToastImmediately()
    {
        _toastTimer.Stop();
        if (_toastCanUndo)
        {
            _toastCanUndo = false;
            _viewModel.DiscardUndo();
        }

        ToastHost.BeginAnimation(OpacityProperty, null);
        ToastHost.Opacity = 0;
        ToastHost.Visibility = Visibility.Collapsed;
    }

    private void ToastTimer_Tick(object? sender, EventArgs eventArgs)
    {
        if (_toastCanUndo)
        {
            _toastCanUndo = false;
            _viewModel.DiscardUndo();
        }

        HideToast();
    }

    private void NotifyUser(string message, bool canUndo)
    {
        if (IsVisible)
        {
            ShowToast(message, canUndo);
            return;
        }

        if (canUndo)
        {
            _viewModel.DiscardUndo();
        }

        BackgroundNotificationRequested?.Invoke(message);
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
    }

    public void Dispose()
    {
        DisposeResources();
        GC.SuppressFinalize(this);
    }
}
