using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Infrastructure;
using ClipNest.Models;
using ClipNest.Services;

namespace ClipNest.ViewModels;

public enum ClipHistoryFilter
{
    All,
    Text,
    Image,
    Pinned
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public const int HistoryLimit = 50;
    public const int MaxPinnedItems = 40;

    private readonly IClipboardService _clipboardService;
    private readonly ObservableCollection<ClipItem> _items = [];
    private readonly ObservableCollection<ClipItem> _visibleItems = [];

    private bool _isEditing;
    private ClipItem? _editingItem;
    private string _editText = string.Empty;
    private bool _isEditingImage;
    private ClipItem? _editingImageItem;
    private BitmapSource? _workingImage;
    private ImageSource? _imageEditPreview;
    private ClipItem? _lastDeletedItem;
    private int _lastDeletedIndex;
    private ClipItem? _pendingCopyItem;
    private bool _pendingCopyIncludeFormatting;
    private bool _copyLoopRunning;
    private bool _isSearchOpen;
    private string _searchText = string.Empty;
    private ClipHistoryFilter _activeFilter = ClipHistoryFilter.All;
    private int _cardTextLimit = ClipItem.DefaultDisplayTextLimit;
    private double _cardFontSize = ClipItem.DefaultDisplayFontSize;
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
            item => QueueCopy(item, includeFormatting: true));
        CopyPlainCommand = new RelayCommand<ClipItem>(
            item => QueueCopy(item, includeFormatting: false),
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
        OpenSearchCommand = new RelayCommand(OpenSearch);
        CloseSearchCommand = new RelayCommand(CloseSearch, () => IsSearchOpen);
        SetFilterCommand = new RelayCommand<string>(SetFilter);

        _clipboardService.ContentChanged += ClipboardContentChanged;
    }

    public ObservableCollection<ClipItem> Items => _items;

    public ObservableCollection<ClipItem> VisibleItems => _visibleItems;

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

    public RelayCommand OpenSearchCommand { get; }

    public RelayCommand CloseSearchCommand { get; }

    public RelayCommand<string> SetFilterCommand { get; }

    public bool HasItems => _items.Count > 0;

    public bool HasVisibleItems => _visibleItems.Count > 0;

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

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        private set
        {
            if (SetProperty(ref _isSearchOpen, value))
            {
                CloseSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public ClipHistoryFilter ActiveFilter
    {
        get => _activeFilter;
        private set
        {
            if (SetProperty(ref _activeFilter, value))
            {
                OnPropertyChanged(nameof(IsAllFilter));
                OnPropertyChanged(nameof(IsTextFilter));
                OnPropertyChanged(nameof(IsImageFilter));
                OnPropertyChanged(nameof(IsPinnedFilter));
                ApplyFilter();
            }
        }
    }

    public bool IsAllFilter => ActiveFilter == ClipHistoryFilter.All;

    public bool IsTextFilter => ActiveFilter == ClipHistoryFilter.Text;

    public bool IsImageFilter => ActiveFilter == ClipHistoryFilter.Image;

    public bool IsPinnedFilter => ActiveFilter == ClipHistoryFilter.Pinned;

    public string EmptyStateTitle => HasItems
        ? "没有匹配的记录"
        : "粘贴板还是空的";

    public string EmptyStateDetail => HasItems
        ? "换个关键词或筛选条件试试"
        : string.Empty;

    public ImageSource? ImageEditPreview
    {
        get => _imageEditPreview;
        private set => SetProperty(ref _imageEditPreview, value);
    }

    public bool CanUndoDelete => _lastDeletedItem is not null;

    public event EventHandler? HistoryChanged;

    public event EventHandler? ItemCopied;

    public event Action<string, bool>? NotificationRequested;

    public void ApplyDisplaySettings(int textLimit, double fontSize)
    {
        _cardTextLimit = Math.Max(1, textLimit);
        _cardFontSize = Math.Max(1, fontSize);
        foreach (var item in _items)
        {
            item.ApplyDisplaySettings(_cardTextLimit, _cardFontSize);
        }
    }

    public void ClearHistory()
    {
        if (_items.Count == 0)
        {
            return;
        }

        _items.Clear();
        _visibleItems.Clear();
        _lastDeletedItem = null;
        CloseAllEditors();
        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDetail));
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
            var newItem = ClipItem.Create(content);
            newItem.ApplyDisplaySettings(_cardTextLimit, _cardFontSize);
            _items.Insert(0, newItem);
            TrimToLimit(newItem);
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

    private void QueueCopy(ClipItem item, bool includeFormatting)
    {
        _pendingCopyItem = item;
        _pendingCopyIncludeFormatting = includeFormatting;
        if (!_copyLoopRunning)
        {
            _ = RunCopyQueueAsync();
        }
    }

    private async Task RunCopyQueueAsync()
    {
        _copyLoopRunning = true;
        try
        {
            while (!_disposed && _pendingCopyItem is not null)
            {
                var item = _pendingCopyItem;
                var includeFormatting = _pendingCopyIncludeFormatting;
                _pendingCopyItem = null;

                bool copied;
                try
                {
                    copied = await _clipboardService
                        .CopyAsync(item, includeFormatting)
                        .ConfigureAwait(true);
                }
                catch (Exception exception) when (
                    exception is FormatException
                    or IOException
                    or UnauthorizedAccessException
                    or System.Runtime.InteropServices.ExternalException
                    or InvalidOperationException
                    or NotSupportedException
                    or ObjectDisposedException)
                {
                    copied = false;
                }

                if (!copied)
                {
                    NotificationRequested?.Invoke("复制失败，请稍后重试", false);
                    continue;
                }

                ItemCopied?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _copyLoopRunning = false;
            if (!_disposed && _pendingCopyItem is not null)
            {
                _ = RunCopyQueueAsync();
            }
        }
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
        Resort();
        NotifyHistoryChanged();
        NotificationRequested?.Invoke("记录已更新", false);
    }

    private void CancelEdit()
    {
        CloseEditor();
    }

    private void BeginImageEdit(ClipItem item)
    {
        if (!item.IsImage)
        {
            return;
        }

        try
        {
            var bytes = item.GetImageBytes();
            if (bytes is null)
            {
                NotificationRequested?.Invoke("图片数据不可用，无法编辑", false);
                return;
            }

            var image = DecodeImage(bytes);
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
        ApplyFilter();
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
        if (!item.Pinned
            && _items.Count(candidate => candidate.Pinned) >= MaxPinnedItems)
        {
            NotificationRequested?.Invoke(
                $"最多支持置顶 {MaxPinnedItems} 条记录",
                false);
            return;
        }

        item.Pinned = !item.Pinned;
        Resort();
        NotifyHistoryChanged();
        NotificationRequested?.Invoke(item.Pinned ? "已置顶" : "已取消置顶", false);
    }

    private void TrimToLimit(ClipItem? protectedItem = null)
    {
        while (_items.Count > HistoryLimit)
        {
            var removable = _items.LastOrDefault(
                    item => !item.Pinned && !ReferenceEquals(item, protectedItem))
                ?? _items.LastOrDefault(item => !ReferenceEquals(item, protectedItem))
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

        ApplyFilter();
    }

    private void OpenSearch()
    {
        IsSearchOpen = true;
    }

    private void CloseSearch()
    {
        SearchText = string.Empty;
        ActiveFilter = ClipHistoryFilter.All;
        IsSearchOpen = false;
    }

    private void SetFilter(string filter)
    {
        if (Enum.TryParse(filter, ignoreCase: true, out ClipHistoryFilter parsed))
        {
            ActiveFilter = parsed;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var matches = _items.Where(item => MatchesFilter(item, query)).ToList();

        _visibleItems.Clear();
        foreach (var item in matches)
        {
            _visibleItems.Add(item);
        }

        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDetail));
    }

    private bool MatchesFilter(ClipItem item, string query)
    {
        if (ActiveFilter == ClipHistoryFilter.Text && !item.IsText)
        {
            return false;
        }

        if (ActiveFilter == ClipHistoryFilter.Image && !item.IsImage)
        {
            return false;
        }

        if (ActiveFilter == ClipHistoryFilter.Pinned && !item.Pinned)
        {
            return false;
        }

        return query.Length == 0
            || (item.IsText
                && item.PlainText.IndexOf(
                    query,
                    StringComparison.OrdinalIgnoreCase) >= 0);
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
