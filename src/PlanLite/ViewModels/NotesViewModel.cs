using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlanLite.Core.Data;
using PlanLite.Core.Models;

namespace PlanLite.ViewModels;

public sealed class NoteCategoryFilterItemViewModel
{
    public NoteCategoryFilterItemViewModel(
        NoteCategoryFilterMode mode,
        long? categoryId,
        string name,
        int count,
        bool isSelected)
    {
        Mode = mode;
        CategoryId = categoryId;
        Name = name;
        Count = count;
        IsSelected = isSelected;
        DisplayLabel = $"{name} ({count})";
    }

    public NoteCategoryFilterMode Mode { get; }
    public long? CategoryId { get; }
    public string Name { get; }
    public int Count { get; }
    public bool IsSelected { get; }
    public string DisplayLabel { get; }
}

public sealed partial class NoteCategoryManageRowViewModel : ObservableObject
{
    public NoteCategoryManageRowViewModel(NoteCategory model)
    {
        Id = model.Id;
        Name = model.Name;
        NoteCount = model.NoteCount;
        EditingName = model.Name;
    }

    public long Id { get; }
    public string Name { get; }
    public int NoteCount { get; }
    public string CountLabel => $"{NoteCount} 条";

    [ObservableProperty]
    private string _editingName = string.Empty;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isDeleteConfirming;
}

public sealed class NoteCardViewModel
{
    private const int MaxPreviewLength = 100;

    public NoteCardViewModel(NoteItem model, IReadOnlyList<string> searchTokens)
    {
        Id = model.Id;
        CategoryId = model.CategoryId;
        CategoryLabel = string.IsNullOrWhiteSpace(model.CategoryName) ? "其它" : model.CategoryName!;
        Title = model.Title;
        Content = model.Content;
        IsPinned = model.IsPinned;
        IsMasked = model.IsMasked;
        UpdatedDisplayText = model.UpdatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        PreviewText = BuildPreviewText(model.Content, model.IsMasked, searchTokens);
    }

    public long Id { get; }
    public long? CategoryId { get; }
    public string CategoryLabel { get; }
    public string Title { get; }
    public string Content { get; }
    public bool IsPinned { get; }
    public bool IsMasked { get; }
    public string PreviewText { get; }
    public string UpdatedDisplayText { get; }
    public string PinTooltip => IsPinned ? "取消置顶" : "置顶笔记";

    private static string BuildPreviewText(string content, bool isMasked, IReadOnlyList<string> searchTokens)
    {
        if (isMasked)
        {
            return "••••••••（隐私已遮罩，点击查看或直接复制）";
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return "暂无正文内容";
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        string? selectedLine = null;
        if (searchTokens.Count > 0)
        {
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                foreach (var token in searchTokens)
                {
                    if (line.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedLine = line.Trim();
                        break;
                    }
                }

                if (selectedLine is not null)
                {
                    break;
                }
            }
        }

        if (selectedLine is null)
        {
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    selectedLine = line.Trim();
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(selectedLine))
        {
            return "暂无正文内容";
        }

        return selectedLine.Length <= MaxPreviewLength
            ? selectedLine
            : string.Concat(selectedLine.AsSpan(0, MaxPreviewLength), "...");
    }
}

public sealed partial class NotesViewModel : ObservableObject
{
    private const int PageSize = 30;

    private readonly NoteRepository _repository;
    private readonly Action<long?> _openNewNoteEditor;
    private readonly Action<long> _openExistingNoteEditor;
    private readonly Action<string> _copyToClipboard;
    private readonly Action _onCategoriesMutated;
    private readonly DispatcherTimer _searchDebounceTimer;

    private int _loadedCount;

    public NotesViewModel(
        NoteRepository repository,
        Action<long?> openNewNoteEditor,
        Action<long> openExistingNoteEditor,
        Action<string> copyToClipboard,
        Action onCategoriesMutated)
    {
        _repository = repository;
        _openNewNoteEditor = openNewNoteEditor;
        _openExistingNoteEditor = openExistingNoteEditor;
        _copyToClipboard = copyToClipboard;
        _onCategoriesMutated = onCategoriesMutated;

        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ReloadNotes(reset: true);
        };
    }

    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    [ObservableProperty]
    private NoteSearchScope _searchScope = NoteSearchScope.All;

    [ObservableProperty]
    private NoteCategoryFilterMode _selectedCategoryMode = NoteCategoryFilterMode.All;

    [ObservableProperty]
    private long? _selectedCategoryId;

    [ObservableProperty]
    private bool _isCategoryManagerOpen;

    [ObservableProperty]
    private string _newCategoryName = string.Empty;

    [ObservableProperty]
    private string? _categoryErrorMessage;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private bool _hasMoreNotes;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyText = "还没有记录任何笔记，点击上方「+ 新建」开始记录";

    [ObservableProperty]
    private string _statusSummaryText = string.Empty;

    public ObservableCollection<NoteCategoryFilterItemViewModel> CategoryFilters { get; } = new();
    public ObservableCollection<NoteCategoryManageRowViewModel> ManageCategories { get; } = new();
    public ObservableCollection<NoteCardViewModel> Notes { get; } = new();

    partial void OnSearchKeywordChanged(string value)
    {
        _searchDebounceTimer.Stop();
        if (string.IsNullOrWhiteSpace(value))
        {
            ReloadNotes(reset: true);
            return;
        }

        _searchDebounceTimer.Start();
    }

    partial void OnSearchScopeChanged(NoteSearchScope value)
    {
        if (!string.IsNullOrWhiteSpace(SearchKeyword))
        {
            _searchDebounceTimer.Stop();
            ReloadNotes(reset: true);
        }
    }

    public void Reload()
    {
        ReloadCategories();
        ReloadNotes(reset: true);
    }

    [RelayCommand]
    private void SearchNow()
    {
        _searchDebounceTimer.Stop();
        ReloadNotes(reset: true);
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchKeyword = string.Empty;
    }

    [RelayCommand]
    private void SelectSearchScope(string scopeName)
    {
        if (Enum.TryParse<NoteSearchScope>(scopeName, ignoreCase: true, out var scope))
        {
            SearchScope = scope;
        }
    }

    [RelayCommand]
    private void SelectCategoryFilter(NoteCategoryFilterItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedCategoryMode = item.Mode;
        SelectedCategoryId = item.CategoryId;
        ReloadCategories();
        ReloadNotes(reset: true);
    }

    [RelayCommand]
    private void NewNote()
    {
        var defaultCatId = SelectedCategoryMode == NoteCategoryFilterMode.Category
            ? SelectedCategoryId
            : null;
        _openNewNoteEditor(defaultCatId);
    }

    [RelayCommand]
    private void OpenNote(NoteCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        _openExistingNoteEditor(card.Id);
    }

    [RelayCommand]
    private void CopyNoteContent(NoteCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        var text = !string.IsNullOrEmpty(card.Content) ? card.Content : card.Title;
        _copyToClipboard(text);
    }

    [RelayCommand]
    private void TogglePinNote(NoteCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        _repository.ToggleNotePinned(card.Id);
        ReloadNotes(reset: true);
    }

    [RelayCommand]
    private void LoadMore()
    {
        ReloadNotes(reset: false);
    }

    // ========================
    // Inline Category Manager
    // ========================

    [RelayCommand]
    private void ToggleCategoryManager()
    {
        CategoryErrorMessage = null;
        IsCategoryManagerOpen = !IsCategoryManagerOpen;
        if (IsCategoryManagerOpen)
        {
            ReloadCategories();
        }
    }

    [RelayCommand]
    private void AddCategory()
    {
        CategoryErrorMessage = null;
        try
        {
            _repository.CreateCategory(NewCategoryName);
            NewCategoryName = string.Empty;
            ReloadCategories();
            _onCategoriesMutated();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            CategoryErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void BeginRenameCategory(NoteCategoryManageRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        CategoryErrorMessage = null;
        foreach (var item in ManageCategories)
        {
            item.IsEditing = item.Id == row.Id;
            item.IsDeleteConfirming = false;
            if (item.Id == row.Id)
            {
                item.EditingName = item.Name;
            }
        }
    }

    [RelayCommand]
    private void CancelRenameCategory(NoteCategoryManageRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        CategoryErrorMessage = null;
        row.EditingName = row.Name;
        row.IsEditing = false;
    }

    [RelayCommand]
    private void SaveRenameCategory(NoteCategoryManageRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        CategoryErrorMessage = null;
        try
        {
            _repository.RenameCategory(row.Id, row.EditingName);
            ReloadCategories();
            ReloadNotes(reset: true);
            _onCategoriesMutated();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            CategoryErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void RequestDeleteCategory(NoteCategoryManageRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        CategoryErrorMessage = null;
        foreach (var item in ManageCategories)
        {
            item.IsEditing = false;
            item.IsDeleteConfirming = item.Id == row.Id;
        }
    }

    [RelayCommand]
    private void CancelDeleteCategory(NoteCategoryManageRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        row.IsDeleteConfirming = false;
    }

    [RelayCommand]
    private void ConfirmDeleteCategory(NoteCategoryManageRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        CategoryErrorMessage = null;
        _repository.DeleteCategory(row.Id);
        if (SelectedCategoryMode == NoteCategoryFilterMode.Category && SelectedCategoryId == row.Id)
        {
            SelectedCategoryMode = NoteCategoryFilterMode.All;
            SelectedCategoryId = null;
        }

        ReloadCategories();
        ReloadNotes(reset: true);
        _onCategoriesMutated();
    }

    private void ReloadCategories()
    {
        var summary = _repository.GetCategorySummary();

        if (SelectedCategoryMode == NoteCategoryFilterMode.Category &&
            (!SelectedCategoryId.HasValue || summary.Categories.All(c => c.Id != SelectedCategoryId.Value)))
        {
            SelectedCategoryMode = NoteCategoryFilterMode.All;
            SelectedCategoryId = null;
        }

        CategoryFilters.Clear();
        CategoryFilters.Add(new NoteCategoryFilterItemViewModel(
            NoteCategoryFilterMode.All,
            null,
            "全部",
            summary.TotalNoteCount,
            SelectedCategoryMode == NoteCategoryFilterMode.All));

        foreach (var cat in summary.Categories)
        {
            CategoryFilters.Add(new NoteCategoryFilterItemViewModel(
                NoteCategoryFilterMode.Category,
                cat.Id,
                cat.Name,
                cat.NoteCount,
                SelectedCategoryMode == NoteCategoryFilterMode.Category && SelectedCategoryId == cat.Id));
        }

        CategoryFilters.Add(new NoteCategoryFilterItemViewModel(
            NoteCategoryFilterMode.Uncategorized,
            null,
            "其它",
            summary.UncategorizedNoteCount,
            SelectedCategoryMode == NoteCategoryFilterMode.Uncategorized));

        ManageCategories.Clear();
        foreach (var cat in summary.Categories)
        {
            ManageCategories.Add(new NoteCategoryManageRowViewModel(cat));
        }
    }

    private void ReloadNotes(bool reset)
    {
        if (reset)
        {
            Notes.Clear();
            _loadedCount = 0;
        }

        var kw = SearchKeyword?.Trim() ?? string.Empty;
        var tokens = NoteRepository.ParseSearchTokens(kw);

        var result = _repository.QueryNotes(new NoteQuery
        {
            Keyword = kw,
            Scope = SearchScope,
            CategoryFilterMode = SelectedCategoryMode,
            CategoryId = SelectedCategoryId,
            Limit = PageSize,
            Offset = _loadedCount
        });

        foreach (var item in result.Items)
        {
            Notes.Add(new NoteCardViewModel(item, tokens));
        }

        _loadedCount = Notes.Count;
        TotalCount = result.TotalCount;
        HasMoreNotes = result.HasMore;
        IsEmpty = Notes.Count == 0;

        if (!string.IsNullOrEmpty(kw))
        {
            StatusSummaryText = TotalCount == 0
                ? "未找到匹配的笔记"
                : $"找到 {TotalCount} 条匹配笔记（标题优先排序）";
            EmptyText = $"未找到包含 “{kw}” 的笔记";
        }
        else
        {
            StatusSummaryText = TotalCount == 0
                ? string.Empty
                : $"共 {TotalCount} 条笔记（置顶优先 · 最近更新）";
            EmptyText = SelectedCategoryMode == NoteCategoryFilterMode.All
                ? "还没有记录任何笔记，点击上方「+ 新建」开始记录"
                : "当前分类下暂无笔记";
        }
    }
}
