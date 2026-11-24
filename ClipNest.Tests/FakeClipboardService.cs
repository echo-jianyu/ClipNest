using System.Windows;
using ClipNest.Models;
using ClipNest.Services;

namespace ClipNest.Tests;

internal sealed class FakeClipboardService : IClipboardService
{
    public event EventHandler<ClipContent>? ContentChanged;

    public event EventHandler<ClipboardCaptureRejectedEventArgs>? CaptureRejected;

    public List<(ClipItem Item, bool IncludeFormatting)> Copies { get; } = [];

    public bool CopyResult { get; set; } = true;

    public Func<ClipItem, bool, Task<bool>>? CopyHandler { get; set; }

    public void Attach(Window window)
    {
    }

    public void Detach()
    {
    }

    public Task<bool> CopyAsync(ClipItem item, bool includeFormatting)
    {
        Copies.Add((item, includeFormatting));
        return CopyHandler?.Invoke(item, includeFormatting)
            ?? Task.FromResult(CopyResult);
    }

    public void Emit(ClipContent content)
    {
        ContentChanged?.Invoke(this, content);
    }

    public void Reject(string message)
    {
        CaptureRejected?.Invoke(
            this,
            new ClipboardCaptureRejectedEventArgs(message));
    }

    public void Dispose()
    {
    }
}
