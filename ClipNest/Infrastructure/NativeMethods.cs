using System.Runtime.InteropServices;

namespace ClipNest.Infrastructure;

internal static class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExAppWindow = 0x00040000;
    internal const int WmHotKey = 0x0312;
    internal const int WmClipboardUpdate = 0x031D;
    internal const int WmMouseActivate = 0x0021;
    internal const int WmSettingChange = 0x001A;
    internal const int MaNoActivate = 3;
    internal const uint ModNoRepeat = 0x4000;
    internal const uint VkF4 = 0x73;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint EventSystemForeground = 0x0003;
    internal const uint WineventOutOfContext = 0x0000;
    internal const uint WineventSkipOwnProcess = 0x0002;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint SwpNoOwnerZOrder = 0x0200;
    internal const uint GaRoot = 0x0002;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(PointStruct point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out PointStruct point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hwnd, out RectStruct rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(PointStruct point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLong64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLong64(IntPtr hwnd, int index, IntPtr newLong);

    internal static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLong64(hwnd, index)
            : new IntPtr(GetWindowLong32(hwnd, index));
    }

    internal static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLong64(hwnd, index, newLong)
            : new IntPtr(SetWindowLong32(hwnd, index, newLong.ToInt32()));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        public int Size;
        public RectStruct Monitor;
        public RectStruct Work;
        public uint Flags;
    }
}
