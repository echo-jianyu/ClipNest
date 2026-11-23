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
            Convert.FromBase64String(OnePixelPngBase64),
            1,
            1));
        var image = viewModel.Items[0];

        Assert.True(viewModel.EditCommand.CanExecute(image));
        viewModel.EditCommand.Execute(image);
        Assert.True(viewModel.IsEditingImage);
        Assert.NotNull(viewModel.ImageEditPreview);

        viewModel.RotateImageRightCommand.Execute(null);
        viewModel.SaveImageEditCommand.Execute(null);

        Assert.False(viewModel.IsEditingImage);
        Assert.NotNull(image.ImagePngBase64);
        Assert.Equal(1, image.PixelWidth);
        Assert.Equal(1, image.PixelHeight);
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
        clipboard.Emit(ClipContent.FromText("alpha"));

        viewModel.CopyFormattedCommand.Execute(viewModel.Items[0]);
        viewModel.CopyPlainCommand.Execute(viewModel.Items[0]);

        Assert.Single(viewModel.Items);
        Assert.Equal(2, clipboard.Copies.Count);
        Assert.True(clipboard.Copies[0].IncludeFormatting);
        Assert.False(clipboard.Copies[1].IncludeFormatting);
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
    }
}
