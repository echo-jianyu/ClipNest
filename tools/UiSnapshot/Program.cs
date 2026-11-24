using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipNest;
using ClipNest.Models;
using ClipNest.Services;
using ClipNest.ViewModels;

namespace UiSnapshot;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var editMode = args.Contains("--edit", StringComparer.OrdinalIgnoreCase);
        var imageEditMode = args.Contains("--edit-image", StringComparer.OrdinalIgnoreCase);
        var searchMode = args.Contains("--search", StringComparer.OrdinalIgnoreCase);
        var confirmMode = args.Contains("--confirm", StringComparer.OrdinalIgnoreCase);
        var settingsMode = args.Contains("--settings", StringComparer.OrdinalIgnoreCase);
        var wideMode = args.Contains("--wide", StringComparer.OrdinalIgnoreCase);
        var outputArgument = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
        var outputPath = outputArgument is not null
            ? Path.GetFullPath(outputArgument)
            : Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "docs",
                imageEditMode
                    ? "clipnest-image-edit.png"
                    : editMode
                        ? "clipnest-edit.png"
                        : "clipnest-screenshot.png"));

        var application = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/ClipNest;component/Themes/Theme.xaml",
                UriKind.Absolute)
        });

        if (confirmMode)
        {
            var confirmWindow = new ConfirmWindow(
                "清空历史",
                "此操作会清空所有文字和图片记录，无法撤销，确定清空吗？",
                "清空历史");
            RenderWindowContent(confirmWindow, outputPath);
            application.Shutdown();
            return;
        }

        if (settingsMode)
        {
            var settingsWindow = new SettingsWindow(
                40,
                13.5,
                HotKeyDefinition.Default,
                hideAfterCopy: true,
                autoStartOnLogin: true);
            RenderWindowContent(settingsWindow, outputPath);
            application.Shutdown();
            return;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"ClipNest.UiSnapshot.{Guid.NewGuid():N}");
        var historyStore = new HistoryStore(Path.Combine(tempDirectory, "history.json"));
        var settingsStore = new SettingsStore(Path.Combine(tempDirectory, "settings.json"));
        var clipboard = new PreviewClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        using var window = new MainWindow(
            viewModel,
            clipboard,
            historyStore,
            settingsStore,
            new AutoStartService(Path.Combine(tempDirectory, "ClipNest.exe")),
            new AppSettings());

        clipboard.Emit(ClipContent.FromText("https://github.com/"));
        clipboard.Emit(ClipContent.FromText(
            "窗口支持任意拖动、双击隐藏，F4 随时唤醒。",
            @"{\rtf1\ansi ClipNest}",
            "<b>ClipNest</b>"));
        clipboard.Emit(ClipContent.FromImage(CreatePortraitSampleImage(), 160, 260));
        clipboard.Emit(ClipContent.FromImage(CreateSampleImage(), 320, 200));
        clipboard.Emit(ClipContent.FromText("dotnet test ClipNest.slnx -c Release"));
        clipboard.Emit(ClipContent.FromText("收到，稍后回复。"));
        clipboard.Emit(ClipContent.FromText(
            """
            SELECT project_id, COUNT(*) AS clip_count
            FROM clipboard_history
            WHERE captured_at >= CURRENT_DATE - INTERVAL '7 days'
            GROUP BY project_id
            ORDER BY clip_count DESC;
            """));

        if (editMode)
        {
            viewModel.EditCommand.Execute(viewModel.Items.First(item => item.IsText));
        }
        else if (imageEditMode)
        {
            viewModel.EditCommand.Execute(viewModel.Items.First(item => item.IsImage));
        }
        else if (searchMode)
        {
            viewModel.OpenSearchCommand.Execute(null);
            viewModel.SearchText = "ClipNest";
        }

        var previewWidth = wideMode ? 600 : 400;
        const double previewHeight = 400;
        window.Width = previewWidth;
        window.Height = previewHeight;

        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(previewWidth, previewHeight));
        content.Arrange(new Rect(0, 0, previewWidth, previewHeight));
        content.UpdateLayout();

        var previewFrame = new DispatcherFrame();
        var previewTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(350),
            DispatcherPriority.Background,
            (_, _) => previewFrame.Continue = false,
            Dispatcher.CurrentDispatcher);
        previewTimer.Start();
        Dispatcher.PushFrame(previewFrame);
        previewTimer.Stop();
        content.UpdateLayout();

        var pixelWidth = previewWidth * 2;
        const int pixelHeight = (int)(previewHeight * 2);
        var renderedWindow = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            192,
            192,
            PixelFormats.Pbgra32);
        renderedWindow.Render(content);

        var composition = new DrawingVisual();
        using (var context = composition.RenderOpen())
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(222, 233, 228)),
                null,
                new Rect(0, 0, pixelWidth, pixelHeight));
            context.DrawImage(
                renderedWindow,
                new Rect(0, 0, pixelWidth, pixelHeight));
        }

        var final = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        final.Render(composition);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(final));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);

        window.Dispose();
        historyStore.Dispose();
        application.Shutdown();
        Console.WriteLine($"Wrote {outputPath}");
    }

    private static void RenderWindowContent(
        Window window,
        string outputPath)
    {
        var content = (FrameworkElement)window.Content;
        var measureWidth = window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight
            ? double.PositiveInfinity
            : window.Width;
        var measureHeight = window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight
            ? double.PositiveInfinity
            : window.Height;
        content.Measure(new Size(measureWidth, measureHeight));
        var renderWidth = window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight
            ? content.DesiredSize.Width
            : window.Width;
        var renderHeight = window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight
            ? content.DesiredSize.Height
            : window.Height;
        content.Arrange(new Rect(0, 0, renderWidth, renderHeight));
        content.UpdateLayout();

        var pixelWidth = (int)Math.Round(renderWidth * 2);
        var pixelHeight = (int)Math.Round(renderHeight * 2);
        var rendered = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            192,
            192,
            PixelFormats.Pbgra32);
        rendered.Render(content);

        var composition = new DrawingVisual();
        using (var context = composition.RenderOpen())
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(222, 233, 228)),
                null,
                new Rect(0, 0, pixelWidth, pixelHeight));
            context.DrawImage(
                rendered,
                new Rect(0, 0, pixelWidth, pixelHeight));
        }

        var final = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        final.Render(composition);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(final));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
        Console.WriteLine($"Wrote {outputPath}");
    }

    private static byte[] CreateSampleImage()
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(245, 249, 247)),
                null,
                new Rect(0, 0, 320, 200),
                18,
                18);
            context.DrawEllipse(
                new SolidColorBrush(Color.FromRgb(23, 109, 105)),
                null,
                new Point(88, 102),
                54,
                54);
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(223, 96, 73)),
                null,
                new Rect(124, 68, 110, 68),
                16,
                16);
            var text = new FormattedText(
                "CLIP",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                26,
                Brushes.White,
                96);
            context.DrawText(text, new Point(144, 88));
        }

        var bitmap = new RenderTargetBitmap(320, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] CreatePortraitSampleImage()
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(247, 245, 239)),
                null,
                new Rect(0, 0, 160, 260),
                18,
                18);
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(23, 109, 105)),
                null,
                new Rect(24, 28, 112, 86),
                14,
                14);
            context.DrawEllipse(
                new SolidColorBrush(Color.FromRgb(223, 96, 73)),
                null,
                new Point(80, 172),
                42,
                42);
            var text = new FormattedText(
                "PORTRAIT",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                15,
                Brushes.White,
                96);
            context.DrawText(text, new Point(32, 62));
        }

        var bitmap = new RenderTargetBitmap(160, 260, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed class PreviewClipboardService : IClipboardService
    {
        public event EventHandler<ClipContent>? ContentChanged;

        public event EventHandler<ClipboardCaptureRejectedEventArgs>? CaptureRejected
        {
            add { }
            remove { }
        }

        public void Attach(Window window)
        {
        }

        public void Detach()
        {
        }

        public Task<bool> CopyAsync(ClipItem item, bool includeFormatting)
        {
            return Task.FromResult(true);
        }

        public void Emit(ClipContent content)
        {
            ContentChanged?.Invoke(this, content);
        }

        public void Dispose()
        {
        }
    }
}
