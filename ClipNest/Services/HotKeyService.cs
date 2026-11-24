using System.Windows;
using System.Windows.Interop;
using ClipNest.Infrastructure;
using ClipNest.Models;

namespace ClipNest.Services;

public sealed class HotKeyService : IDisposable
{
    private const int HotKeyId = 0x434C;

    private IntPtr _windowHandle;
    private HwndSource? _source;
    private bool _registered;
    private HotKeyDefinition _hotKey = HotKeyDefinition.Default;

    public event EventHandler? Pressed;

    public HotKeyDefinition CurrentHotKey => _hotKey;

    public bool IsRegistered => _registered;

    public bool Attach(Window window, HotKeyDefinition hotKey)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        if (hotKey is null)
        {
            throw new ArgumentNullException(nameof(hotKey));
        }

        if (_windowHandle != IntPtr.Zero)
        {
            throw new InvalidOperationException("HotKeyService is already attached.");
        }

        _windowHandle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        if (_source is null)
        {
            _windowHandle = IntPtr.Zero;
            return false;
        }

        _source.AddHook(WindowMessageHook);
        _hotKey = hotKey;
        _registered = TryRegister(hotKey);
        return _registered;
    }

    public bool UpdateHotKey(HotKeyDefinition hotKey)
    {
        if (hotKey is null)
        {
            throw new ArgumentNullException(nameof(hotKey));
        }

        if (_windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var previous = _hotKey;
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, HotKeyId);
            _registered = false;
        }

        if (TryRegister(hotKey))
        {
            _hotKey = hotKey;
            return true;
        }

        _registered = TryRegister(previous);
        _hotKey = previous;
        return false;
    }

    public void Suspend()
    {
        if (!_registered || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_windowHandle, HotKeyId);
        _registered = false;
    }

    public bool Resume()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (_registered)
        {
            return true;
        }

        _registered = TryRegister(_hotKey);
        return _registered;
    }

    private bool TryRegister(HotKeyDefinition hotKey)
    {
        return NativeMethods.RegisterHotKey(
            _windowHandle,
            HotKeyId,
            hotKey.Modifiers | NativeMethods.ModNoRepeat,
            hotKey.VirtualKey);
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
        _hotKey = HotKeyDefinition.Default;
    }
}
