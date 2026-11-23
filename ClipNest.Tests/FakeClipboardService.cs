using System.Windows;
using ClipNest.Models;
using ClipNest.Services;

namespace ClipNest.Tests;

internal sealed class FakeClipboardService : IClipboardService
{
    public event EventHandler<ClipContent>? ContentChanged;

    public List<(ClipItem Item, bool IncludeFormatting)> Copies { get; } = [];

    public bool CopyResult { get; set; } = true;

    public void Attach(Window window)
    {
    }

    public void Detach()
    {
    }

    public bool Copy(ClipItem item, bool includeFormatting)
    {
        Copies.Add((item, includeFormatting));
        return CopyResult;
    }

    public void Emit(ClipContent content)
    {
        ContentChanged?.Invoke(this, content);
    }

    public void Dispose()
    {
    }
}
