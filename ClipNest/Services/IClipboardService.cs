using System.Windows;
using ClipNest.Models;

namespace ClipNest.Services;

public interface IClipboardService : IDisposable
{
    event EventHandler<ClipContent>? ContentChanged;

    void Attach(Window window);

    void Detach();

    Task<bool> CopyAsync(ClipItem item, bool includeFormatting);
}
