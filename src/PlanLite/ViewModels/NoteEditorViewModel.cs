using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlanLite.Core.Data;
using PlanLite.Core.Models;

namespace PlanLite.ViewModels;

public sealed record NoteCategoryOption(long? Id, string Name);

public sealed partial class NoteEditorViewModel : ObservableObject
{
    private readonly NoteRepository _repository;
    private readonly Action<string?> _onSaved;
    private readonly Action<string> _copyToClipboard;
    private readonly System.Windows.Threading.DispatcherTimer _autoSaveTimer;

    private bool _suppressAutoSave;
    private bool _hasUnsavedChanges;
    private bool _userTouchedMask;
    private bool _autoMaskOnAccountEligible;

    public NoteEditorViewModel(
        NoteRepository repository,
        Action<string?> onSaved,
        Action<string> copyToClipboard)
    {
        _repository = repository;
        _onSaved = onSaved;
        _copyToClipboard = copyToClipboard;

        _autoSaveTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _autoSaveTimer.Tick += (_, _) => CommitPendingChanges();
    }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isNewNote;

    [ObservableProperty]
    private long _noteId;

    [ObservableProperty]
    private string _headerTitle = "新建笔记";

    [ObservableProperty]
    private string _headerSubtitle = "支持置顶、隐私遮罩与自动保存";

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private NoteCategoryOption? _selectedCategory;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isMasked;

    [ObservableProperty]
    private bool _isContentRevealed = true;

    [ObservableProperty]
    private bool _isDeleteConfirmVisible;

    [ObservableProperty]
    private string? _validationMessage;

    public ObservableCollection<NoteCategoryOption> CategoryOptions { get; } = new();

    public bool ShowMaskedPlaceholder => IsMasked && !IsContentRevealed;

    public void OpenNew(long? defaultCategoryId = null)
    {
        _suppressAutoSave = true;
        _autoSaveTimer.Stop();
        _hasUnsavedChanges = false;
        _userTouchedMask = false;
        _autoMaskOnAccountEligible = true;
        IsDeleteConfirmVisible = false;
        ValidationMessage = null;

        IsNewNote = true;
        NoteId = 0;
        HeaderTitle = "新建笔记";
        HeaderSubtitle = "填写标题后将自动保存 · 保留原始缩进与换行";
        Title = string.Empty;
        Content = string.Empty;
        IsPinned = false;

        LoadCategoryOptions(defaultCategoryId);
        IsMasked = IsAccountCategory(SelectedCategory);
        IsContentRevealed = true;
        OnPropertyChanged(nameof(ShowMaskedPlaceholder));

        IsOpen = true;
        _suppressAutoSave = false;
    }

    public void OpenExisting(long noteId)
    {
        var note = _repository.GetNoteById(noteId);
        if (note is null)
        {
            return;
        }

        _suppressAutoSave = true;
        _autoSaveTimer.Stop();
        _hasUnsavedChanges = false;
        _userTouchedMask = true;
        _autoMaskOnAccountEligible = false;
        IsDeleteConfirmVisible = false;
        ValidationMessage = null;

        IsNewNote = false;
        NoteId = note.Id;
        HeaderTitle = "笔记详情（自动保存）";
        HeaderSubtitle = $"最近更新 {note.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        Title = note.Title;
        Content = note.Content;
        IsPinned = note.IsPinned;
        IsMasked = note.IsMasked;
        IsContentRevealed = !note.IsMasked;
        OnPropertyChanged(nameof(ShowMaskedPlaceholder));

        LoadCategoryOptions(note.CategoryId);

        IsOpen = true;
        _suppressAutoSave = false;
    }

    public void RefreshCategoriesIfOpen()
    {
        if (!IsOpen)
        {
            return;
        }

        var currentCategoryId = SelectedCategory?.Id;
        _suppressAutoSave = true;
        LoadCategoryOptions(currentCategoryId);
        _suppressAutoSave = false;
    }

    public void FlushIfOpen()
    {
        if (!IsOpen)
        {
            return;
        }

        CommitPendingChanges(forceImmediate: true);
    }

    public void CloseWithoutSaving()
    {
        _autoSaveTimer.Stop();
        _hasUnsavedChanges = false;
        IsDeleteConfirmVisible = false;
        ValidationMessage = null;
        IsOpen = false;
    }

    private void LoadCategoryOptions(long? selectedCategoryId)
    {
        CategoryOptions.Clear();
        CategoryOptions.Add(new NoteCategoryOption(null, "其它（未分类）"));

        foreach (var cat in _repository.GetCategories())
        {
            CategoryOptions.Add(new NoteCategoryOption(cat.Id, cat.Name));
        }

        SelectedCategory = CategoryOptions.FirstOrDefault(o => o.Id == selectedCategoryId)
            ?? CategoryOptions[0];
    }

    private static bool IsAccountCategory(NoteCategoryOption? option) =>
        option is not null && string.Equals(option.Name, "账号", StringComparison.OrdinalIgnoreCase);

    partial void OnTitleChanged(string value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        ValidationMessage = string.IsNullOrWhiteSpace(value)
            ? (IsNewNote ? null : "标题不能为空")
            : null;
        ScheduleAutoSave();
    }

    partial void OnContentChanged(string value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        ScheduleAutoSave();
    }

    partial void OnSelectedCategoryChanged(NoteCategoryOption? value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        if (_autoMaskOnAccountEligible && !_userTouchedMask)
        {
            _suppressAutoSave = true;
            IsMasked = IsAccountCategory(value);
            if (!IsMasked)
            {
                IsContentRevealed = true;
            }
            OnPropertyChanged(nameof(ShowMaskedPlaceholder));
            _suppressAutoSave = false;
        }

        CommitPendingChanges(forceImmediate: true);
    }

    partial void OnIsPinnedChanged(bool value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        CommitPendingChanges(forceImmediate: true);
    }

    partial void OnIsMaskedChanged(bool value)
    {
        if (!value)
        {
            IsContentRevealed = true;
        }

        OnPropertyChanged(nameof(ShowMaskedPlaceholder));

        if (_suppressAutoSave)
        {
            return;
        }

        _userTouchedMask = true;
        CommitPendingChanges(forceImmediate: true);
    }

    partial void OnIsContentRevealedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMaskedPlaceholder));
    }

    [RelayCommand]
    private void TogglePin()
    {
        IsPinned = !IsPinned;
    }

    [RelayCommand]
    private void ToggleMask()
    {
        _userTouchedMask = true;
        var next = !IsMasked;
        IsMasked = next;
        if (!next)
        {
            IsContentRevealed = true;
        }
        else if (IsNewNote)
        {
            IsContentRevealed = true;
        }
    }

    [RelayCommand]
    private void ToggleRevealContent()
    {
        if (!IsMasked)
        {
            IsContentRevealed = true;
            return;
        }

        IsContentRevealed = !IsContentRevealed;
    }

    [RelayCommand]
    private void CopyContent()
    {
        if (!string.IsNullOrEmpty(Content))
        {
            _copyToClipboard(Content);
        }
        else if (!string.IsNullOrWhiteSpace(Title))
        {
            _copyToClipboard(Title.Trim());
        }
    }

    [RelayCommand]
    private void RequestDelete()
    {
        if (IsNewNote)
        {
            CloseWithoutSaving();
            return;
        }

        IsDeleteConfirmVisible = true;
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsDeleteConfirmVisible = false;
    }

    [RelayCommand]
    private void ConfirmDelete()
    {
        _autoSaveTimer.Stop();
        _hasUnsavedChanges = false;
        IsDeleteConfirmVisible = false;

        if (!IsNewNote && NoteId > 0)
        {
            _repository.DeleteNote(NoteId);
        }

        IsOpen = false;
        _onSaved("已删除笔记");
    }

    [RelayCommand]
    private void SaveAndClose()
    {
        if (IsNewNote && string.IsNullOrWhiteSpace(Title))
        {
            if (string.IsNullOrWhiteSpace(Content))
            {
                CloseWithoutSaving();
                return;
            }

            ValidationMessage = "请输入笔记标题后再保存";
            return;
        }

        if (!CommitPendingChanges(forceImmediate: true))
        {
            return;
        }

        IsDeleteConfirmVisible = false;
        IsOpen = false;
    }

    [RelayCommand]
    private void Close()
    {
        if (IsNewNote && string.IsNullOrWhiteSpace(Title))
        {
            CloseWithoutSaving();
            return;
        }

        CommitPendingChanges(forceImmediate: true);
        IsDeleteConfirmVisible = false;
        IsOpen = false;
    }

    private void ScheduleAutoSave()
    {
        _hasUnsavedChanges = true;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private bool CommitPendingChanges(bool forceImmediate = false)
    {
        _autoSaveTimer.Stop();
        if (!IsOpen || (!_hasUnsavedChanges && !forceImmediate))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(Title))
        {
            if (!IsNewNote)
            {
                ValidationMessage = "笔记标题不能为空";
            }

            return false;
        }

        ValidationMessage = null;
        _hasUnsavedChanges = false;

        if (IsNewNote)
        {
            var created = _repository.CreateNote(
                Title,
                Content,
                SelectedCategory?.Id,
                IsPinned,
                IsMasked);

            _suppressAutoSave = true;
            IsNewNote = false;
            NoteId = created.Id;
            HeaderTitle = "笔记详情（自动保存）";
            HeaderSubtitle = $"最近更新 {created.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
            _suppressAutoSave = false;

            _onSaved(null);
            return true;
        }

        var updated = _repository.UpdateNote(
            NoteId,
            Title,
            Content,
            SelectedCategory?.Id,
            IsPinned,
            IsMasked);

        _suppressAutoSave = true;
        HeaderSubtitle = $"最近更新 {updated.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        _suppressAutoSave = false;

        _onSaved(null);
        return true;
    }
}
