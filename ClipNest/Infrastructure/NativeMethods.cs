using System.Runtime.InteropServices;

namespace ClipNest.Infrastructure;

internal static class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExAppWindow = 0x00040000;
    internal const int WmHotKey = 0x0312;
    internal const int WmClipboardUpdate = 0x031D;
    internal const uint ModNoRepeat = 0x4000;
    internal const uint VkF4 = 0x73;

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

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetClipboardSequenceNumber();

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
}
