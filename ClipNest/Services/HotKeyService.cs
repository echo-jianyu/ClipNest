using System.Windows;
using System.Windows.Interop;
using ClipNest.Infrastructure;

namespace ClipNest.Services;

public sealed class HotKeyService : IDisposable
{
    private const int HotKeyId = 0x434C;

    private IntPtr _windowHandle;
    private HwndSource? _source;
    private bool _registered;

    public event EventHandler? Pressed;

    public bool Attach(Window window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        if (_windowHandle != IntPtr.Zero)
        {
            throw new InvalidOperationException("HotKeyService is already attached.");
        }

        _windowHandle = new WindowInteropHelper(window).Handle;
        _registered = NativeMethods.RegisterHotKey(
            _windowHandle,
            HotKeyId,
            NativeMethods.ModNoRepeat,
            NativeMethods.VkF4);

        if (!_registered)
        {
            _windowHandle = IntPtr.Zero;
            return false;
        }

        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WindowMessageHook);
        return true;
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == NativeMethods.WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, HotKeyId);
        }

        _source?.RemoveHook(WindowMessageHook);
        _source = null;
        _windowHandle = IntPtr.Zero;
        _registered = false;
    }
}
