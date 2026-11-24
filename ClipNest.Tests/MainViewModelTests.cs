using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Controls;
using ClipNest.Models;
using ClipNest.ViewModels;

namespace ClipNest.Tests;

public sealed class MainViewModelTests
{
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public void ClipboardContent_IsInsertedNewestFirst()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);

        clipboard.Emit(ClipContent.FromText("first"));
        clipboard.Emit(ClipContent.FromText("second"));

        Assert.Equal(2, viewModel.Items.Count);
        Assert.Equal("second", viewModel.Items[0].PlainText);
        Assert.Equal("first", viewModel.Items[1].PlainText);
    }

    [Fact]
    public void RejectedOversizedClipboardText_ShowsNotificationWithoutAddingHistory()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        var notifications = new List<(string Message, bool CanUndo)>();
        viewModel.NotificationRequested += (message, canUndo) =>
            notifications.Add((message, canUndo));

        clipboard.Reject("oversized");

        Assert.Empty(viewModel.Items);
        Assert.Equal(
            (Message: "oversized", CanUndo: false),
            Assert.Single(notifications));
    }

    [Fact]
    public void DuplicateClipboardContent_UpdatesExistingItemAndMovesItToTop()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);

        clipboard.Emit(ClipContent.FromText("alpha"));
        clipboard.Emit(ClipContent.FromText("beta"));
        clipboard.Emit(ClipContent.FromText("alpha"));

        Assert.Equal(2, viewModel.Items.Count);
        Assert.Equal("alpha", viewModel.Items[0].PlainText);
        Assert.Single(viewModel.Items, item => item.PlainText == "alpha");
    }

    [Fact]
    public void History_IsTrimmedToLimitAndKeepsPinnedItems()
    {
        using var clipboard = new FakeClipboardService();
        var pinnedItem = ClipItem.Create(ClipContent.FromText("pinned"));
        pinnedItem.Pinned = true;
        using var viewModel = new MainViewModel(clipboard, [pinnedItem]);

        for (var index = 0; index < 60; index++)
        {
            clipboard.Emit(ClipContent.FromText($"record-{index}"));
        }

        Assert.Equal(MainViewModel.HistoryLimit, viewModel.Items.Count);
        Assert.Same(pinnedItem, viewModel.Items[0]);
        Assert.Equal("record-59", viewModel.Items[1].PlainText);
    }

    [Fact]
    public void TogglePin_MovesItemToTopAndUnpinRestoresChronologicalOrder()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);

        clipboard.Emit(ClipContent.FromText("alpha"));
        clipboard.Emit(ClipContent.FromText("beta"));
        clipboard.Emit(ClipContent.FromText("gamma"));

        var alpha = viewModel.Items.Single(item => item.PlainText == "alpha");
        viewModel.TogglePinCommand.Execute(alpha);

        Assert.Equal("alpha", viewModel.Items[0].PlainText);
        Assert.True(alpha.Pinned);

        viewModel.TogglePinCommand.Execute(alpha);

        Assert.Equal("gamma", viewModel.Items[0].PlainText);
        Assert.False(alpha.Pinned);
    }

    [Fact]
    public void EditingText_UpdatesRecordAndRemovesRichFormats()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        clipboard.Emit(ClipContent.FromText("original", @"{\rtf1 original}", "<b>original</b>"));
        var item = viewModel.Items[0];

        viewModel.EditCommand.Execute(item);
        viewModel.EditText = "edited";
        viewModel.SaveEditCommand.Execute(null);

        Assert.False(viewModel.IsEditing);
        Assert.Equal("edited", item.PlainText);
        Assert.Null(item.Rtf);
        Assert.Null(item.Html);
    }

    [Fact]
    public void EditingOlderText_MovesRecordToNewestPosition()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        clipboard.Emit(ClipContent.FromText("alpha"));
        clipboard.Emit(ClipContent.FromText("beta"));
        clipboard.Emit(ClipContent.FromText("gamma"));
        var alpha = viewModel.Items.Single(item => item.PlainText == "alpha");

        viewModel.EditCommand.Execute(alpha);
        viewModel.EditText = "alpha edited";
        viewModel.SaveEditCommand.Execute(null);

        Assert.Same(alpha, viewModel.Items[0]);
    }

    [Fact]
    public void EditingTextToExistingContent_MergesDuplicateRecords()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        clipboard.Emit(ClipContent.FromText("alpha"));
        clipboard.Emit(ClipContent.FromText("beta"));
        var alpha = viewModel.Items.Single(item => item.PlainText == "alpha");

        viewModel.EditCommand.Execute(alpha);
        viewModel.EditText = "beta";
        viewModel.SaveEditCommand.Execute(null);

        var remaining = Assert.Single(viewModel.Items);
        Assert.Equal("beta", remaining.PlainText);
        Assert.Equal(remaining, viewModel.Items[0]);
    }

    [Fact]
    public void DeleteAndUndo_RestoresDeletedRecord()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        clipboard.Emit(ClipContent.FromText("alpha"));
        clipboard.Emit(ClipContent.FromText("beta"));
        var beta = viewModel.Items[0];

        viewModel.DeleteCommand.Execute(beta);

        Assert.Single(viewModel.Items);
        Assert.True(viewModel.CanUndoDelete);

        viewModel.UndoDeleteCommand.Execute(null);

        Assert.Equal(2, viewModel.Items.Count);
        Assert.Contains(beta, viewModel.Items);
        Assert.False(viewModel.CanUndoDelete);
    }

    [Fact]
    public void UndoDelete_IsProtectedWhenHistoryIsAlreadyAtLimit()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        for (var index = 0; index < MainViewModel.HistoryLimit; index++)
        {
            clipboard.Emit(ClipContent.FromText($"record-{index}"));
        }

        var deleted = viewModel.Items[viewModel.Items.Count - 1];
        viewModel.DeleteCommand.Execute(deleted);
        clipboard.Emit(ClipContent.FromText("replacement"));
        viewModel.UndoDeleteCommand.Execute(null);

        Assert.Equal(MainViewModel.HistoryLimit, viewModel.Items.Count);
        Assert.Contains(deleted, viewModel.Items);
    }

    [Fact]
    public void PlainTextCopy_IsDisabledForImageRecords()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        clipboard.Emit(ClipContent.FromImage([1, 2, 3], 10, 10));
        var image = viewModel.Items[0];

        Assert.False(viewModel.CopyPlainCommand.CanExecute(image));
        Assert.True(viewModel.CopyFormattedCommand.CanExecute(image));
    }

    [Fact]
    public void ImageEdit_CanRotateAndSave()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        clipboard.Emit(ClipContent.FromImage(
            EncodePng(400, 200),
            400,
            200));
        var image = viewModel.Items[0];

        Assert.True(viewModel.EditCommand.CanExecute(image));
        viewModel.EditCommand.Execute(image);
        Assert.True(viewModel.IsEditingImage);
        Assert.NotNull(viewModel.ImageEditPreview);

        viewModel.RotateImageRightCommand.Execute(null);
        viewModel.SaveImageEditCommand.Execute(null);

        Assert.False(viewModel.IsEditingImage);
        Assert.NotNull(image.ImagePngBase64);
        Assert.Equal(200, image.PixelWidth);
        Assert.Equal(400, image.PixelHeight);
    }

    [Fact]
    public void SavingAnImageEditThatMatchesExistingHistory_MergesDuplicates()
    {
        using var clipboard = new FakeClipboardService();
        var bytes = EncodePng(40, 20);
        var edited = ClipItem.Create(ClipContent.FromImage(bytes, 40, 20));
        var existing = ClipItem.Create(ClipContent.FromImage(bytes, 40, 20));
        using var viewModel = new MainViewModel(clipboard, [edited, existing]);

        viewModel.EditCommand.Execute(edited);
        viewModel.SaveImageEditCommand.Execute(null);

        var remaining = Assert.Single(viewModel.Items);
        Assert.Same(existing, remaining);
    }

    [Fact]
    public void ImagePinAndDeleteCommands_OperateOnImageRecords()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        clipboard.Emit(ClipContent.FromImage(
            Convert.FromBase64String(OnePixelPngBase64),
            1,
            1));
        var image = viewModel.Items[0];

        viewModel.TogglePinCommand.Execute(image);
        Assert.True(image.Pinned);

        viewModel.DeleteCommand.Execute(image);
        Assert.Empty(viewModel.Items);
    }

    [Fact]
    public void AppCopy_DelegatesToClipboardWithoutCreatingAnotherRecord()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        var notifications = new List<(string Message, bool CanUndo)>();
        viewModel.NotificationRequested += (message, canUndo) =>
            notifications.Add((message, canUndo));
        clipboard.Emit(ClipContent.FromText("alpha"));

        viewModel.CopyFormattedCommand.Execute(viewModel.Items[0]);
        viewModel.CopyPlainCommand.Execute(viewModel.Items[0]);

        Assert.Single(viewModel.Items);
        Assert.Equal(2, clipboard.Copies.Count);
        Assert.True(clipboard.Copies[0].IncludeFormatting);
        Assert.False(clipboard.Copies[1].IncludeFormatting);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Copy_DoesNotBlockAndCoalescesLatestRapidRequest()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        clipboard.Emit(ClipContent.FromText("first"));
        clipboard.Emit(ClipContent.FromText("second"));
        var firstCopy = new TaskCompletionSource<bool>();
        clipboard.CopyHandler = (_, _) => firstCopy.Task;
        var first = viewModel.Items[1];
        var second = viewModel.Items[0];

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        viewModel.CopyFormattedCommand.Execute(first);
        viewModel.CopyFormattedCommand.Execute(second);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 250);
        Assert.Single(clipboard.Copies);
        Assert.Same(first, clipboard.Copies[0].Item);

        clipboard.CopyHandler = (_, _) => Task.FromResult(true);
        firstCopy.SetResult(true);
        for (var attempt = 0; attempt < 20 && clipboard.Copies.Count < 2; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(2, clipboard.Copies.Count);
        Assert.Same(second, clipboard.Copies[1].Item);
    }

    [Fact]
    public void ClearHistory_RemovesEveryRecord()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        clipboard.Emit(ClipContent.FromText("alpha"));
        clipboard.Emit(ClipContent.FromText("beta"));

        viewModel.ClearHistory();

        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.HasItems);
        Assert.False(viewModel.CanUndoDelete);
        Assert.False(viewModel.UndoDeleteCommand.CanExecute(null));
    }

    [Fact]
    public void NewClipboardContent_SurvivesWhenHistoryIsAllPinned()
    {
        using var clipboard = new FakeClipboardService();
        var pinnedItems = Enumerable.Range(0, MainViewModel.HistoryLimit)
            .Select(index =>
            {
                var item = ClipItem.Create(ClipContent.FromText($"pinned-{index}"));
                item.Pinned = true;
                return item;
            })
            .ToList();
        using var viewModel = new MainViewModel(clipboard, pinnedItems);

        clipboard.Emit(ClipContent.FromText("new content"));

        Assert.Equal(MainViewModel.HistoryLimit, viewModel.Items.Count);
        Assert.Contains(viewModel.Items, item => item.PlainText == "new content");
    }

    [Fact]
    public void TogglePin_RejectsPinningBeyondConfiguredLimit()
    {
        using var clipboard = new FakeClipboardService();
        var pinnedItems = Enumerable.Range(0, MainViewModel.MaxPinnedItems)
            .Select(index =>
            {
                var item = ClipItem.Create(ClipContent.FromText($"pinned-{index}"));
                item.Pinned = true;
                return item;
            })
            .ToList();
        var extra = ClipItem.Create(ClipContent.FromText("extra"));
        var allItems = pinnedItems.Append(extra).ToList();
        using var viewModel = new MainViewModel(clipboard, allItems);

        viewModel.TogglePinCommand.Execute(extra);

        Assert.False(extra.Pinned);
    }

    [Fact]
    public void SearchAndFilter_UpdateVisibleItems()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        clipboard.Emit(ClipContent.FromText("alpha note"));
        clipboard.Emit(ClipContent.FromText("beta note"));
        clipboard.Emit(ClipContent.FromImage(
            Convert.FromBase64String(OnePixelPngBase64),
            2,
            2));

        viewModel.OpenSearchCommand.Execute(null);
        viewModel.SearchText = "alpha";
        Assert.Single(viewModel.VisibleItems);

        viewModel.SearchText = string.Empty;
        viewModel.SetFilterCommand.Execute("Image");
        Assert.Single(viewModel.VisibleItems);
        Assert.True(viewModel.VisibleItems[0].IsImage);
    }

    [Fact]
    public void ApplyDisplaySettings_UpdatesExistingAndNewCards()
    {
        using var clipboard = new FakeClipboardService();
        using var viewModel = new MainViewModel(clipboard, []);
        clipboard.Emit(ClipContent.FromText("existing"));
        var existing = viewModel.Items[0];

        viewModel.ApplyDisplaySettings(fontSize: 16);
        clipboard.Emit(ClipContent.FromText("new item"));

        Assert.Equal(16, existing.DisplayFontSize);
        Assert.Equal(16, viewModel.Items[0].DisplayFontSize);
    }

    private static byte[] EncodePng(int width, int height)
    {
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
