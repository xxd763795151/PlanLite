namespace PlanLite.Core.Models;

public sealed class NoteCategory
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int NoteCount { get; set; }
}
