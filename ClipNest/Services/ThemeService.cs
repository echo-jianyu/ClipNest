using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipNest.Infrastructure;
using ClipNest.Models;
using Microsoft.Win32;

namespace ClipNest.Services;

public sealed class ThemeService : IDisposable
{
    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    private const string ImmersiveColorSet = "ImmersiveColorSet";

    private static readonly Uri LightThemeUri = new(
        "pack://application:,,,/ClipNest;component/Themes/LightTheme.xaml",
        UriKind.Absolute);
    private static readonly Uri DarkThemeUri = new(
        "pack://application:,,,/ClipNest;component/Themes/DarkTheme.xaml",
        UriKind.Absolute);
    private static readonly Uri HighContrastThemeUri = new(
        "pack://application:,,,/ClipNest;component/Themes/HighContrastTheme.xaml",
        UriKind.Absolute);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private HwndSource? _windowSource;
    private HwndSourceHook? _windowHook;
    private AppThemeMode _mode = AppThemeMode.FollowSystem;
    private ThemeVariant _activeVariant = ThemeVariant.Light;
    private bool _disposed;

    public ThemeService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(80),
            DispatcherPriority.Background,
            (_, _) => RefreshForSystemChange(),
            dispatcher);
        _refreshTimer.Stop();
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
    }

    public AppThemeMode Mode => _mode;

    public bool IsDark => _activeVariant == ThemeVariant.Dark;

    public event EventHandler? ThemeChanged;

    public void ApplyTheme(AppThemeMode mode)
    {
        if (!Enum.IsDefined(typeof(AppThemeMode), mode))
        {
            mode = AppThemeMode.FollowSystem;
        }

        _mode = mode;
        ApplyVariant(ResolveVariant(mode), force: true);
    }

    public void AttachToWindow(Window window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _windowSource = HwndSource.FromHwnd(handle);
        if (_windowSource is null)
        {
            return;
        }

        _windowHook = WindowMessageHook;
        _windowSource.AddHook(_windowHook);
    }

    public void RefreshSystemTheme()
    {
        if (_disposed)
        {
            return;
        }

        var resolvedVariant = ResolveVariant(_mode);
        if (_mode == AppThemeMode.FollowSystem
            || _activeVariant == ThemeVariant.HighContrast
            || resolvedVariant == ThemeVariant.HighContrast)
        {
            ApplyVariant(resolvedVariant, force: false);
        }
    }

    public static bool IsSystemUsingDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            var value = key?.GetValue(AppsUseLightThemeValue);
            return value switch
            {
                int intValue => intValue == 0,
                uint uintValue => uintValue == 0,
                long longValue => longValue == 0,
                byte byteValue => byteValue == 0,
                _ => false
            };
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException)
        {
            return false;
        }
    }

    private static bool ShouldUseDarkTheme(AppThemeMode mode)
    {
        return mode switch
        {
            AppThemeMode.Dark => true,
            AppThemeMode.Light => false,
            _ => IsSystemUsingDarkTheme()
        };
    }

    private static ThemeVariant ResolveVariant(AppThemeMode mode)
    {
        return SystemParameters.HighContrast
            ? ThemeVariant.HighContrast
            : ShouldUseDarkTheme(mode)
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
    }

    private void ApplyVariant(ThemeVariant variant, bool force)
    {
        if (!force && variant == _activeVariant)
        {
            return;
        }

        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var variantDictionary = new ResourceDictionary
        {
            Source = variant switch
            {
                ThemeVariant.Dark => DarkThemeUri,
                ThemeVariant.HighContrast => HighContrastThemeUri,
                _ => LightThemeUri
            }
        };
        ReplaceVariantDictionary(application.Resources, variantDictionary);
        _activeVariant = variant;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ReplaceVariantDictionary(
        ResourceDictionary applicationResources,
        ResourceDictionary variantDictionary)
    {
        var mergedDictionaries = applicationResources.MergedDictionaries;
        for (var index = 0; index < mergedDictionaries.Count; index++)
        {
            var source = mergedDictionaries[index].Source?.OriginalString;
            if (source?.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase) == true
                || source?.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) == true
                || source?.EndsWith("HighContrastTheme.xaml", StringComparison.OrdinalIgnoreCase) == true)
            {
                mergedDictionaries[index] = variantDictionary;
                return;
            }
        }

        mergedDictionaries.Add(variantDictionary);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != NativeMethods.WmSettingChange
            || _mode != AppThemeMode.FollowSystem)
        {
            return IntPtr.Zero;
        }

        var settingName = lParam == IntPtr.Zero
            ? null
            : Marshal.PtrToStringUni(lParam);
        if (string.Equals(
                settingName,
                ImmersiveColorSet,
                StringComparison.OrdinalIgnoreCase))
        {
            QueueRefresh();
        }

        return IntPtr.Zero;
    }

    private void SystemEvents_UserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs eventArgs)
    {
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }));
    }

    private void RefreshForSystemChange()
    {
        _refreshTimer.Stop();
        if (_disposed)
        {
            return;
        }

        RefreshSystemTheme();
    }

    private enum ThemeVariant
    {
        Light,
        Dark,
        HighContrast
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        if (_windowSource is not null && _windowHook is not null)
        {
            _windowSource.RemoveHook(_windowHook);
        }

        _windowSource = null;
        _windowHook = null;
    }
}
