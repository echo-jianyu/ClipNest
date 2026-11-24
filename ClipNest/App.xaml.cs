using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ClipNest.Infrastructure;
using ClipNest.Services;
using ClipNest.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace ClipNest;

public partial class App : System.Windows.Application, IDisposable
{
    private const string InstanceMutexName = @"Local\ClipNest.Instance";
    private const string ActivationEventName = @"Local\ClipNest.Activate";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;
    private MainWindow? _mainWindow;
    private Forms.NotifyIcon? _trayIcon;
    private ContextMenu? _trayContextMenu;
    private Drawing.Icon? _trayDrawingIcon;
    private MenuItem? _showHideMenuItem;
    private MenuItem? _settingsMenuItem;
    private MenuItem? _clearHistoryMenuItem;
    private NativeMethods.WinEventDelegate? _trayMenuForegroundChangedHandler;
    private IntPtr _trayMenuForegroundHook;
    private HistoryStore? _historyStore;
    private MainViewModel? _viewModel;
    private bool _isShuttingDown;
    private bool _resourcesDisposed;

    public bool IsShuttingDown => _isShuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += Application_DispatcherUnhandledException;

        try
        {
            if (!AcquireSingleInstance())
            {
                SignalExistingInstance();
                Shutdown();
                return;
            }

            StartActivationListener();

            _historyStore = new HistoryStore(AppPaths.HistoryFile);
            var settingsStore = new SettingsStore(AppPaths.SettingsFile);
            var settings = await settingsStore.LoadAsync();
            var history = await _historyStore.LoadAsync();
            var clipboardService = new ClipboardService();
            _viewModel = new MainViewModel(clipboardService, history);
            _mainWindow = new MainWindow(
                _viewModel,
                clipboardService,
                _historyStore,
                settingsStore,
                settings);
            _mainWindow.HotKeyChanged += MainWindow_HotKeyChanged;

            MainWindow = _mainWindow;
            InitializeTrayIcon();
            _mainWindow.Show();

            var warnings = new[]
            {
                _historyStore.LastWarning,
                _mainWindow.RegistrationWarning
            }.Where(message => !string.IsNullOrWhiteSpace(message));

            var warning = string.Join(" ", warnings);
            if (!string.IsNullOrWhiteSpace(warning))
            {
                ShowTrayBalloon("ClipNest", warning, Forms.ToolTipIcon.Warning);
            }
        }
        catch (Exception exception)
        {
            LogException(exception);
            WpfMessageBox.Show(
                $"ClipNest 启动失败。\n\n{exception.Message}",
                "ClipNest",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _isShuttingDown = true;
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;
        DisposeResources();
        base.OnExit(e);
    }

    public void Dispose()
    {
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    private void DisposeResources()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        _resourcesDisposed = true;
        _activationCancellation?.Cancel();
        _activationCancellation?.Dispose();
        _activationEvent?.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_trayContextMenu is not null)
        {
            _trayContextMenu.IsOpen = false;
            _trayContextMenu = null;
        }

        StopTrayMenuForegroundMonitor();
        _trayDrawingIcon?.Dispose();
        _trayDrawingIcon = null;
        _mainWindow?.Dispose();
        _viewModel?.Dispose();
        _historyStore?.Dispose();

        if (_instanceMutex is not null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
    }

    private bool AcquireSingleInstance()
    {
        _instanceMutex = new Mutex(
            initiallyOwned: true,
            InstanceMutexName,
            out var createdNew);
        _ownsMutex = createdNew;
        return createdNew;
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private void StartActivationListener()
    {
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _activationCancellation = new CancellationTokenSource();

        var cancellation = _activationCancellation;
        var activationEvent = _activationEvent;
        var dispatcher = Dispatcher;
        _ = Task.Run(() =>
        {
            var handles = new WaitHandle[]
            {
                activationEvent,
                cancellation.Token.WaitHandle
            };

            try
            {
                while (true)
                {
                    var index = WaitHandle.WaitAny(handles);
                    if (index != 0)
                    {
                        return;
                    }

                    dispatcher.BeginInvoke(new Action(ShowWindowFromTray));
                }
            }
            catch (Exception exception) when (
                exception is ObjectDisposedException
                or InvalidOperationException)
            {
            }
        });
    }

    private void InitializeTrayIcon()
    {
        var iconResource = GetResourceStream(new Uri("pack://application:,,,/Assets/ClipNest.ico"));
        if (iconResource is null)
        {
            throw new InvalidOperationException("Tray icon resource is missing.");
        }

        using var iconStream = iconResource.Stream;
        _trayDrawingIcon = new Drawing.Icon(iconStream);

        _trayContextMenu = CreateTrayContextMenu();
        _showHideMenuItem = CreateTrayMenuItem("隐藏");
        _showHideMenuItem.Click += (_, _) => ToggleWindowFromTray();
        _settingsMenuItem = CreateTrayMenuItem("设置");
        _settingsMenuItem.Click += (_, _) => OpenSettingsFromTray();
        _clearHistoryMenuItem = CreateTrayMenuItem("清空历史");
        _clearHistoryMenuItem.Click += (_, _) => ClearHistoryFromTray();
        var exitMenuItem = CreateTrayMenuItem("退出");
        exitMenuItem.Click += async (_, _) => await ExitApplicationAsync();

        _trayContextMenu.Items.Add(_showHideMenuItem);
        _trayContextMenu.Items.Add(_settingsMenuItem);
        _trayContextMenu.Items.Add(_clearHistoryMenuItem);
        _trayContextMenu.Items.Add(new Separator());
        _trayContextMenu.Items.Add(exitMenuItem);
        _trayContextMenu.Opened += (_, _) =>
        {
            UpdateTrayMenuState();
            StartTrayMenuForegroundMonitor();
        };
        _trayContextMenu.Closed += (_, _) => StopTrayMenuForegroundMonitor();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayDrawingIcon,
            Text = "ClipNest",
            Visible = true
        };
        _trayIcon.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                ToggleWindowFromTray();
            }
            else if (eventArgs.Button == Forms.MouseButtons.Right)
            {
                Dispatcher.BeginInvoke(new Action(ShowTrayContextMenu));
            }
        };
    }

    private ContextMenu CreateTrayContextMenu()
    {
        return new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            Style = (Style)FindResource("ClipContextMenuStyle")
        };
    }

    private MenuItem CreateTrayMenuItem(string header)
    {
        return new MenuItem
        {
            Header = header,
            Style = (Style)FindResource("ClipMenuItemStyle")
        };
    }

    private void ShowTrayContextMenu()
    {
        if (_trayContextMenu is null)
        {
            return;
        }

        UpdateTrayMenuState();
        _trayContextMenu.PlacementTarget = null;
        _trayContextMenu.Placement = PlacementMode.MousePoint;
        _trayContextMenu.IsOpen = true;
    }

    public void ShowApplicationMenu(UIElement placementTarget)
    {
        if (placementTarget is null)
        {
            throw new ArgumentNullException(nameof(placementTarget));
        }

        if (_trayContextMenu is null)
        {
            return;
        }

        UpdateTrayMenuState();
        _trayContextMenu.PlacementTarget = placementTarget;
        _trayContextMenu.Placement = PlacementMode.Bottom;
        _trayContextMenu.IsOpen = true;
    }

    private void StartTrayMenuForegroundMonitor()
    {
        if (_trayMenuForegroundHook != IntPtr.Zero)
        {
            return;
        }

        _trayMenuForegroundChangedHandler ??= TrayMenuForegroundChanged;
        _trayMenuForegroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground,
            NativeMethods.EventSystemForeground,
            IntPtr.Zero,
            _trayMenuForegroundChangedHandler,
            0,
            0,
            NativeMethods.WineventOutOfContext | NativeMethods.WineventSkipOwnProcess);
    }

    private void StopTrayMenuForegroundMonitor()
    {
        if (_trayMenuForegroundHook == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWinEvent(_trayMenuForegroundHook);
        _trayMenuForegroundHook = IntPtr.Zero;
    }

    private void TrayMenuForegroundChanged(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            if (_trayContextMenu is { IsOpen: true })
            {
                _trayContextMenu.IsOpen = false;
            }
        }));
    }

    private void UpdateTrayMenuState()
    {
        if (_showHideMenuItem is null || _mainWindow is null)
        {
            return;
        }

        _showHideMenuItem.Header = _mainWindow.IsVisible ? "隐藏" : "显示";
        _clearHistoryMenuItem!.IsEnabled = _viewModel?.Items.Count > 0;
        if (_trayIcon is not null)
        {
            _trayIcon.Text = $"ClipNest · {_mainWindow.HotKeyDisplayText} 显示或隐藏";
        }
    }

    private void MainWindow_HotKeyChanged(object? sender, EventArgs eventArgs)
    {
        Dispatcher.BeginInvoke(new Action(UpdateTrayMenuState));
    }

    private void OpenSettingsFromTray()
    {
        Dispatcher.BeginInvoke(new Action(() => _mainWindow?.ShowSettings()));
    }

    private void ToggleWindowFromTray()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_mainWindow is null)
            {
                return;
            }

            if (_mainWindow.IsVisible)
            {
                _mainWindow.HideToTray();
            }
            else
            {
                _mainWindow.ShowFromTray();
            }
        }));
    }

    private void ShowWindowFromTray()
    {
        _mainWindow?.ShowFromTray();
    }

    private void ClearHistoryFromTray()
    {
        if (_viewModel is null || _viewModel.Items.Count == 0)
        {
            return;
        }

        var owner = _mainWindow is { IsVisible: true } ? _mainWindow : null;
        if (ConfirmWindow.ConfirmClearHistory(owner))
        {
            _viewModel.ClearHistory();
        }
    }

    private void ShowTrayBalloon(string title, string message, Forms.ToolTipIcon icon)
    {
        _trayIcon?.ShowBalloonTip(4500, title, message, icon);
    }

    private async Task ExitApplicationAsync()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;

        try
        {
            if (_mainWindow is not null)
            {
                await _mainWindow.FlushAsync();
            }
        }
        catch (Exception exception)
        {
            LogException(exception);
        }
        finally
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
            }

            Shutdown();
        }
    }

    private void Application_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        WpfMessageBox.Show(
            $"ClipNest 遇到意外错误。\n\n{e.Exception.Message}",
            "ClipNest",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void LogException(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var logPath = Path.Combine(AppPaths.DataDirectory, "error.log");
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:O}] {exception}\n\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
