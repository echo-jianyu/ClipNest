using System.Windows;
using ClipNest.Models;

namespace ClipNest.Services;

public interface IClipboardService : IDisposable
{
    event EventHandler<ClipContent>? ContentChanged;

    event EventHandler<ClipboardCaptureRejectedEventArgs>? CaptureRejected;

    void Attach(Window window);

    void Detach();

    Task<bool> CopyAsync(ClipItem item, bool includeFormatting);
}

public sealed class ClipboardCaptureRejectedEventArgs : EventArgs
{
    public ClipboardCaptureRejectedEventArgs(string message)
    {
        Message = message;
    }

    public string Message { get; }
}
