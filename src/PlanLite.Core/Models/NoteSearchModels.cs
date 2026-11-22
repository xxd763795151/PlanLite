namespace PlanLite.Core.Models;

public enum NoteSearchScope
{
    All = 0,
    TitleOnly = 1,
    ContentOnly = 2
}

public enum NoteCategoryFilterMode
{
    All = 0,
    Category = 1,
    Uncategorized = 2
}

public sealed class NoteQuery
{
    public string? Keyword { get; set; }
    public NoteSearchScope Scope { get; set; } = NoteSearchScope.All;
    public NoteCategoryFilterMode CategoryFilterMode { get; set; } = NoteCategoryFilterMode.All;
    public long? CategoryId { get; set; }
    public int Limit { get; set; } = 30;
    public int Offset { get; set; }
}

public sealed record NoteQueryResult(
    IReadOnlyList<NoteItem> Items,
    int TotalCount,
    bool HasMore);

public sealed record NoteCategorySummary(
    int TotalNoteCount,
    int UncategorizedNoteCount,
    IReadOnlyList<NoteCategory> Categories);
