using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Infrastructure;
using ClipNest.Models;
using ClipNest.Services;

namespace ClipNest.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public const int HistoryLimit = 50;

    private readonly IClipboardService _clipboardService;
    private readonly ObservableCollection<ClipItem> _items = [];

    private bool _isEditing;
    private ClipItem? _editingItem;
    private string _editText = string.Empty;
    private bool _isEditingImage;
    private ClipItem? _editingImageItem;
    private BitmapSource? _workingImage;
    private ImageSource? _imageEditPreview;
    private ClipItem? _lastDeletedItem;
    private int _lastDeletedIndex;
    private bool _disposed;

    public MainViewModel(IClipboardService clipboardService, IEnumerable<ClipItem> initialItems)
    {
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        if (initialItems is null)
        {
            throw new ArgumentNullException(nameof(initialItems));
        }

        foreach (var item in initialItems)
        {
            item.NormalizeAfterLoad();
            _items.Add(item);
        }

        TrimToLimit();
        Resort();

        CopyFormattedCommand = new RelayCommand<ClipItem>(
            item => Copy(item, includeFormatting: true));
        CopyPlainCommand = new RelayCommand<ClipItem>(
            item => Copy(item, includeFormatting: false),
            item => item.IsText);
        EditCommand = new RelayCommand<ClipItem>(
            BeginEdit,
            item => item.IsText || item.IsImage);
        DeleteCommand = new RelayCommand<ClipItem>(Delete);
        TogglePinCommand = new RelayCommand<ClipItem>(TogglePin);
        SaveEditCommand = new RelayCommand(SaveEdit, () => IsEditing);
        CancelEditCommand = new RelayCommand(CancelEdit, () => IsEditing);
        RotateImageLeftCommand = new RelayCommand(
            () => RotateImage(-90),
            () => IsEditingImage && _workingImage is not null);
        RotateImageRightCommand = new RelayCommand(
            () => RotateImage(90),
            () => IsEditingImage && _workingImage is not null);
        SaveImageEditCommand = new RelayCommand(
            SaveImageEdit,
            () => IsEditingImage && _workingImage is not null);
        CancelImageEditCommand = new RelayCommand(CancelImageEdit, () => IsEditingImage);
        UndoDeleteCommand = new RelayCommand(UndoDelete, () => _lastDeletedItem is not null);

        _clipboardService.ContentChanged += ClipboardContentChanged;
    }

    public ObservableCollection<ClipItem> Items => _items;

    public RelayCommand<ClipItem> CopyFormattedCommand { get; }

    public RelayCommand<ClipItem> CopyPlainCommand { get; }

    public RelayCommand<ClipItem> EditCommand { get; }

    public RelayCommand<ClipItem> DeleteCommand { get; }

    public RelayCommand<ClipItem> TogglePinCommand { get; }

    public RelayCommand SaveEditCommand { get; }

    public RelayCommand CancelEditCommand { get; }

    public RelayCommand RotateImageLeftCommand { get; }

    public RelayCommand RotateImageRightCommand { get; }

    public RelayCommand SaveImageEditCommand { get; }

    public RelayCommand CancelImageEditCommand { get; }

    public RelayCommand UndoDeleteCommand { get; }

    public bool HasItems => _items.Count > 0;

    public string HistorySummary
    {
        get
        {
            if (_items.Count == 0)
            {
                return "等待复制";
            }

            var pinnedCount = _items.Count(item => item.Pinned);
            return pinnedCount == 0
                ? $"{_items.Count} 条"
                : $"{_items.Count} 条 · {pinnedCount} 置顶";
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value))
            {
                SaveEditCommand.RaiseCanExecuteChanged();
                CancelEditCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ClipItem? EditingItem
    {
        get => _editingItem;
        private set => SetProperty(ref _editingItem, value);
    }

    public ClipItem? EditingImageItem
    {
        get => _editingImageItem;
        private set => SetProperty(ref _editingImageItem, value);
    }

    public string EditText
    {
        get => _editText;
        set => SetProperty(ref _editText, value);
    }

    public bool IsEditingImage
    {
        get => _isEditingImage;
        private set
        {
            if (SetProperty(ref _isEditingImage, value))
            {
                RotateImageLeftCommand.RaiseCanExecuteChanged();
                RotateImageRightCommand.RaiseCanExecuteChanged();
                SaveImageEditCommand.RaiseCanExecuteChanged();
                CancelImageEditCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ImageSource? ImageEditPreview
    {
        get => _imageEditPreview;
        private set => SetProperty(ref _imageEditPreview, value);
    }

    public bool CanUndoDelete => _lastDeletedItem is not null;

    public event EventHandler? HistoryChanged;

    public event Action<string, bool>? NotificationRequested;

    public void ClearHistory()
    {
        if (_items.Count == 0)
        {
            return;
        }

        _items.Clear();
        _lastDeletedItem = null;
        CloseAllEditors();
        NotifyHistoryChanged();
        NotificationRequested?.Invoke("历史记录已清空", false);
    }

    private void ClipboardContentChanged(object? sender, ClipContent content)
    {
        var existing = _items.FirstOrDefault(item => item.Signature == content.Signature);
        if (existing is not null)
        {
            existing.Apply(content);
            var existingIndex = _items.IndexOf(existing);
            if (existingIndex > 0)
            {
                _items.Move(existingIndex, 0);
            }
        }
        else
        {
            _items.Insert(0, ClipItem.Create(content));
        }

        TrimToLimit();
        Resort();

        if (EditingItem is not null && !_items.Contains(EditingItem))
        {
            CloseEditor();
        }

        if (EditingImageItem is not null && !_items.Contains(EditingImageItem))
        {
            CloseImageEditor();
        }

        NotifyHistoryChanged();
    }

    private void Copy(ClipItem item, bool includeFormatting)
    {
        var copied = _clipboardService.Copy(item, includeFormatting);
        if (!copied)
        {
            NotificationRequested?.Invoke("复制失败，请稍后重试", false);
            return;
        }

        var message = item.IsImage
            ? "图片已复制"
            : includeFormatting
                ? "已复制，格式已保留"
                : "已复制纯文本";

        NotificationRequested?.Invoke(message, false);
    }

    private void BeginEdit(ClipItem item)
    {
        CloseAllEditors();

        if (item.IsImage)
        {
            BeginImageEdit(item);
            return;
        }

        if (!item.IsText)
        {
            return;
        }

        EditingItem = item;
        EditText = item.PlainText;
        IsEditing = true;
    }

    private void SaveEdit()
    {
        if (EditingItem is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(EditText))
        {
            NotificationRequested?.Invoke("内容不能为空", false);
            return;
        }

        EditingItem.UpdatePlainText(EditText);
        CloseEditor();
        NotifyHistoryChanged();
        NotificationRequested?.Invoke("记录已更新", false);
    }

    private void CancelEdit()
    {
        CloseEditor();
    }

    private void BeginImageEdit(ClipItem item)
    {
        if (!item.IsImage || string.IsNullOrWhiteSpace(item.ImagePngBase64))
        {
            return;
        }

        try
        {
            var image = DecodeImage(Convert.FromBase64String(item.ImagePngBase64));
            EditingImageItem = item;
            _workingImage = image;
            ImageEditPreview = image;
            IsEditingImage = true;
        }
        catch (FormatException)
        {
            NotificationRequested?.Invoke("图片数据损坏，无法编辑", false);
        }
        catch (NotSupportedException)
        {
            NotificationRequested?.Invoke("图片格式不受支持", false);
        }
    }

    private void RotateImage(int angle)
    {
        if (_workingImage is null)
        {
            return;
        }

        var rotated = new TransformedBitmap(
            _workingImage,
            new RotateTransform(angle));
        rotated.Freeze();
        _workingImage = rotated;
        ImageEditPreview = rotated;
    }

    private void SaveImageEdit()
    {
        if (EditingImageItem is null || _workingImage is null)
        {
            return;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(_workingImage));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        EditingImageItem.ReplaceImage(
            stream.ToArray(),
            _workingImage.PixelWidth,
            _workingImage.PixelHeight);
        CloseImageEditor();
        Resort();
        NotifyHistoryChanged();
        NotificationRequested?.Invoke("图片已更新", false);
    }

    private void CancelImageEdit()
    {
        CloseImageEditor();
    }

    private void Delete(ClipItem item)
    {
        var index = _items.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        if (ReferenceEquals(EditingItem, item))
        {
            CloseEditor();
        }

        if (ReferenceEquals(EditingImageItem, item))
        {
            CloseImageEditor();
        }

        _lastDeletedItem = item;
        _lastDeletedIndex = index;
        _items.RemoveAt(index);
        UndoDeleteCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndoDelete));
        NotifyHistoryChanged();
        NotificationRequested?.Invoke("记录已删除", true);
    }

    private void UndoDelete()
    {
        if (_lastDeletedItem is null)
        {
            return;
        }

        var index = Math.Min(Math.Max(_lastDeletedIndex, 0), _items.Count);
        _items.Insert(index, _lastDeletedItem);
        _lastDeletedItem = null;
        TrimToLimit();
        Resort();
        UndoDeleteCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndoDelete));
        NotifyHistoryChanged();
        NotificationRequested?.Invoke("已撤销删除", false);
    }

    private void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        Resort();
        NotifyHistoryChanged();
        NotificationRequested?.Invoke(item.Pinned ? "已置顶" : "已取消置顶", false);
    }

    private void TrimToLimit()
    {
        while (_items.Count > HistoryLimit)
        {
            var removable = _items.LastOrDefault(item => !item.Pinned)
                ?? _items[_items.Count - 1];
            _items.Remove(removable);
        }
    }

    private void Resort()
    {
        var ordered = _items
            .Select((item, index) => (Item: item, Index: index))
            .OrderByDescending(entry => entry.Item.Pinned)
            .ThenByDescending(entry => entry.Item.UpdatedAt)
            .ThenByDescending(entry => entry.Item.Sequence)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToList();

        for (var targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            var currentIndex = _items.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex)
            {
                _items.Move(currentIndex, targetIndex);
            }
        }
    }

    private void CloseEditor()
    {
        EditingItem = null;
        EditText = string.Empty;
        IsEditing = false;
    }

    private void CloseImageEditor()
    {
        EditingImageItem = null;
        _workingImage = null;
        ImageEditPreview = null;
        IsEditingImage = false;
    }

    private void CloseAllEditors()
    {
        CloseEditor();
        CloseImageEditor();
    }

    private static BitmapImage DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HistorySummary));
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clipboardService.ContentChanged -= ClipboardContentChanged;
    }
}
