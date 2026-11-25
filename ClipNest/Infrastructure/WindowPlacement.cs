using System.Windows;
using System.Windows.Interop;

namespace ClipNest.Infrastructure;

internal static class WindowPlacement
{
    internal static void CenterOnCursor(Window window, double maximumHeightRatio = 1)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        ConstrainToCursorScreen(window, maximumHeightRatio);
        window.Loaded += (_, _) => CenterNativeWindowOnCursor(window);
    }

    internal static void ConstrainToCursorScreen(
        Window window,
        double maximumHeightRatio = 1)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        window.SourceInitialized += (_, _) =>
            ApplyWorkAreaMaximum(window, maximumHeightRatio);
    }

    internal static void ConstrainToWindowScreen(
        Window window,
        Window referenceWindow,
        double maximumHeightRatio = 1)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        if (referenceWindow is null)
        {
            throw new ArgumentNullException(nameof(referenceWindow));
        }

        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(referenceWindow).Handle;
            if (handle == IntPtr.Zero)
            {
                ApplyWorkAreaMaximum(window, maximumHeightRatio);
                return;
            }

            var monitor = NativeMethods.MonitorFromWindow(
                handle,
                NativeMethods.MonitorDefaultToNearest);
            if (!TryGetMonitorWorkArea(
                    monitor,
                    out var workArea,
                    out var dpiScale))
            {
                ApplyWorkAreaMaximum(window, maximumHeightRatio);
                return;
            }

            ApplyWorkAreaMaximum(
                window,
                maximumHeightRatio,
                workArea,
                dpiScale);
        };
    }

    internal static void MoveTrayAnchorToCursor(Window anchor)
    {
        if (anchor is null)
        {
            throw new ArgumentNullException(nameof(anchor));
        }

        if (!NativeMethods.GetCursorPos(out var point))
        {
            return;
        }

        var handle = new WindowInteropHelper(anchor).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            point.X,
            point.Y,
            1,
            1,
            NativeMethods.SwpNoZOrder
            | NativeMethods.SwpNoActivate
            | NativeMethods.SwpNoOwnerZOrder);
    }

    private static void ApplyWorkAreaMaximum(Window window, double maximumHeightRatio)
    {
        if (maximumHeightRatio >= 1
            || !TryGetCursorWorkArea(out var workArea, out var dpiScale))
        {
            return;
        }

        ApplyWorkAreaMaximum(
            window,
            maximumHeightRatio,
            workArea,
            dpiScale);
    }

    private static void ApplyWorkAreaMaximum(
        Window window,
        double maximumHeightRatio,
        NativeMethods.RectStruct workArea,
        double dpiScale)
    {
        if (maximumHeightRatio >= 1)
        {
            return;
        }

        var maximumHeight = Math.Max(
            window.MinHeight,
            (workArea.Height / dpiScale) * maximumHeightRatio);
        window.MaxHeight = maximumHeight;
    }

    private static void CenterNativeWindowOnCursor(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero
            || !NativeMethods.GetWindowRect(handle, out var windowRect)
            || !TryGetCursorWorkArea(out var workArea, out _))
        {
            return;
        }

        var x = workArea.Left
            + Math.Max(0, (workArea.Width - windowRect.Width) / 2);
        var y = workArea.Top
            + Math.Max(0, (workArea.Height - windowRect.Height) / 2);
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            x,
            y,
            0,
            0,
            NativeMethods.SwpNoSize
            | NativeMethods.SwpNoZOrder
            | NativeMethods.SwpNoActivate
            | NativeMethods.SwpNoOwnerZOrder);
    }

    private static bool TryGetCursorWorkArea(
        out NativeMethods.RectStruct workArea,
        out double dpiScale)
    {
        workArea = default;
        dpiScale = 1;
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromPoint(
            point,
            NativeMethods.MonitorDefaultToNearest);
        return TryGetMonitorWorkArea(monitor, out workArea, out dpiScale);
    }

    private static bool TryGetMonitorWorkArea(
        IntPtr monitor,
        out NativeMethods.RectStruct workArea,
        out double dpiScale)
    {
        workArea = default;
        dpiScale = 1;
        var info = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        if (monitor == IntPtr.Zero
            || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        workArea = info.Work;
        if (NativeMethods.GetDpiForMonitor(
                monitor,
                dpiType: 0,
                out var dpiX,
                out _) == 0
            && dpiX > 0)
        {
            dpiScale = dpiX / 96d;
        }

        return true;
    }
}
