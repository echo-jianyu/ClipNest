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
    private Forms.ToolStripMenuItem? _settingsMenuItem;
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

        _trayMenu = new ClipNestTrayMenu();

        _showHideMenuItem = CreateTrayMenuItem("隐藏 ClipNest");
        _showHideMenuItem.Click += (_, _) => ToggleWindowFromTray();
        _settingsMenuItem = CreateTrayMenuItem("设置...");
        _settingsMenuItem.Click += (_, _) => OpenSettingsFromTray();
        _clearHistoryMenuItem = CreateTrayMenuItem("清空历史");
        _clearHistoryMenuItem.Click += (_, _) => ClearHistoryFromTray();
        var exitMenuItem = CreateTrayMenuItem("退出");
        exitMenuItem.Click += async (_, _) => await ExitApplicationAsync();

        _trayMenu.Items.Add(_showHideMenuItem);
        _trayMenu.Items.Add(_settingsMenuItem);
        _trayMenu.Items.Add(_clearHistoryMenuItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator
        {
            Margin = new Forms.Padding(5, 3, 5, 3)
        });
        _trayMenu.Items.Add(exitMenuItem);
        _trayMenu.Opening += (_, _) => UpdateTrayMenuState();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayDrawingIcon,
            Text = "ClipNest",
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
            Margin = Forms.Padding.Empty,
            TextAlign = Drawing.ContentAlignment.MiddleCenter
        };
    }

    private static Drawing.Drawing2D.GraphicsPath CreateRoundedRectangle(
        Drawing.Rectangle bounds,
        int radius)
    {
        var diameter = Math.Max(1, radius * 2);
        var path = new Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(
            bounds.Right - diameter,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class ClipNestTrayMenu : Forms.ContextMenuStrip
    {
        public ClipNestTrayMenu()
        {
            ShowImageMargin = false;
            ShowCheckMargin = false;
            Font = new Drawing.Font("Microsoft YaHei UI", 10.5f);
            BackColor = Drawing.Color.White;
            ForeColor = Drawing.Color.FromArgb(29, 40, 42);
            MinimumSize = new Drawing.Size(190, 0);
            Padding = new Forms.Padding(5, 4, 5, 4);
            DropShadowEnabled = true;
            Renderer = new ClipNestTrayMenuRenderer();
        }

        protected override void OnHandleCreated(EventArgs eventArgs)
        {
            base.OnHandleCreated(eventArgs);
            UpdateRoundedRegion();
        }

        protected override void OnSizeChanged(EventArgs eventArgs)
        {
            base.OnSizeChanged(eventArgs);
            UpdateRoundedRegion();
        }

        private void UpdateRoundedRegion()
        {
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            using var path = CreateRoundedRectangle(
                new Drawing.Rectangle(0, 0, Width, Height),
                radius: 9);
            var previous = Region;
            Region = new Drawing.Region(path);
            previous?.Dispose();
        }
    }

    private sealed class ClipNestTrayMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Drawing.Color Highlight =
            Drawing.Color.FromArgb(229, 242, 237);
        private static readonly Drawing.Color Border =
            Drawing.Color.FromArgb(229, 233, 230);

        public ClipNestTrayMenuRenderer()
            : base(new TrayMenuColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(
            Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new Drawing.SolidBrush(Drawing.Color.White);
            e.Graphics.FillRectangle(brush, e.ToolStrip.ClientRectangle);
        }

        protected override void OnRenderToolStripBorder(
            Forms.ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip.Width < 4 || e.ToolStrip.Height < 4)
            {
                return;
            }

            using var path = CreateRoundedRectangle(
                new Drawing.Rectangle(
                    1,
                    1,
                    e.ToolStrip.Width - 3,
                    e.ToolStrip.Height - 3),
                radius: 8);
            using var pen = new Drawing.Pen(Border);
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            var bounds = new Drawing.Rectangle(
                3,
                1,
                Math.Max(1, e.Item.Width - 6),
                Math.Max(1, e.Item.Height - 2));
            if (!e.Item.Selected)
            {
                using var background = new Drawing.SolidBrush(Drawing.Color.White);
                e.Graphics.FillRectangle(background, bounds);
                return;
            }

            using var path = CreateRoundedRectangle(bounds, radius: 5);
            using var highlight = new Drawing.SolidBrush(Highlight);
            e.Graphics.FillPath(highlight, path);
        }

        protected override void OnRenderSeparator(
            Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Drawing.Pen(Border);
            e.Graphics.DrawLine(pen, 10, y, Math.Max(10, e.Item.Width - 10), y);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled
                ? Drawing.Color.FromArgb(29, 40, 42)
                : Drawing.Color.FromArgb(111, 125, 127);
            e.TextRectangle = new Drawing.Rectangle(
                0,
                0,
                e.ToolStrip.ClientSize.Width,
                e.Item.Height);
            e.TextFormat = Forms.TextFormatFlags.HorizontalCenter
                | Forms.TextFormatFlags.VerticalCenter
                | Forms.TextFormatFlags.SingleLine
                | Forms.TextFormatFlags.NoPrefix;
            base.OnRenderItemText(e);
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

        public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(229, 233, 230);

        public override Drawing.Color SeparatorLight => Drawing.Color.FromArgb(229, 233, 230);

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
