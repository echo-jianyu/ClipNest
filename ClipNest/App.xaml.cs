using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
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
    private Forms.ContextMenuStrip? _trayMenu;
    private Drawing.Icon? _trayDrawingIcon;
    private Forms.ToolStripMenuItem? _showHideMenuItem;
    private Forms.ToolStripMenuItem? _clearHistoryMenuItem;
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

        _trayMenu?.Dispose();
        _trayMenu = null;
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

        _trayMenu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Font = new Drawing.Font("Microsoft YaHei UI", 10.5f),
            BackColor = Drawing.Color.White,
            ForeColor = Drawing.Color.FromArgb(29, 40, 42),
            Padding = Forms.Padding.Empty,
            Renderer = new BorderlessTrayMenuRenderer()
        };

        _showHideMenuItem = CreateTrayMenuItem("隐藏 ClipNest");
        _showHideMenuItem.Click += (_, _) => ToggleWindowFromTray();
        _clearHistoryMenuItem = CreateTrayMenuItem("清空历史");
        _clearHistoryMenuItem.Click += (_, _) => ClearHistoryFromTray();
        var exitMenuItem = CreateTrayMenuItem("退出");
        exitMenuItem.Click += async (_, _) => await ExitApplicationAsync();

        _trayMenu.Items.Add(_showHideMenuItem);
        _trayMenu.Items.Add(_clearHistoryMenuItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(exitMenuItem);
        _trayMenu.Opening += (_, _) => UpdateTrayMenuState();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayDrawingIcon,
            Text = "ClipNest · F4 显示或隐藏",
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                ToggleWindowFromTray();
            }
        };
    }

    private static Forms.ToolStripMenuItem CreateTrayMenuItem(string text)
    {
        return new Forms.ToolStripMenuItem(text)
        {
            AutoSize = true,
            Padding = new Forms.Padding(11, 7, 11, 7),
            Margin = Forms.Padding.Empty
        };
    }

    private sealed class BorderlessTrayMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Drawing.Color Highlight =
            Drawing.Color.FromArgb(229, 242, 237);

        public BorderlessTrayMenuRenderer()
            : base(new TrayMenuColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            var bounds = new Drawing.Rectangle(Drawing.Point.Empty, e.Item.Size);
            using var brush = new Drawing.SolidBrush(
                e.Item.Selected ? Highlight : Drawing.Color.White);
            e.Graphics.FillRectangle(brush, bounds);
        }
    }

    private sealed class TrayMenuColorTable : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => Drawing.Color.White;

        public override Drawing.Color MenuItemSelected => Drawing.Color.FromArgb(229, 242, 237);

        public override Drawing.Color MenuItemSelectedGradientBegin =>
            Drawing.Color.FromArgb(229, 242, 237);

        public override Drawing.Color MenuItemSelectedGradientEnd =>
            Drawing.Color.FromArgb(229, 242, 237);

        public override Drawing.Color MenuItemBorder => Drawing.Color.Transparent;

        public override Drawing.Color MenuBorder => Drawing.Color.Transparent;

        public override Drawing.Color ImageMarginGradientBegin => Drawing.Color.White;

        public override Drawing.Color ImageMarginGradientMiddle => Drawing.Color.White;

        public override Drawing.Color ImageMarginGradientEnd => Drawing.Color.White;
    }

    private void UpdateTrayMenuState()
    {
        if (_showHideMenuItem is null || _mainWindow is null)
        {
            return;
        }

        _showHideMenuItem.Text = _mainWindow.IsVisible ? "隐藏 ClipNest" : "显示 ClipNest";
        _clearHistoryMenuItem!.Enabled = _viewModel?.Items.Count > 0;
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

        var result = WpfMessageBox.Show(
            "清空全部粘贴板记录？此操作无法撤销。",
            "ClipNest",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
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
