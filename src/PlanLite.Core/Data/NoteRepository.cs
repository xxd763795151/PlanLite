using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PlanLite.Core.Models;

namespace PlanLite.Core.Data;

/// <summary>
/// Provides local SQLite persistence and prioritized search for notes and note categories.
/// </summary>
public sealed class NoteRepository
{
    public const int MaxCategoryNameLength = 20;
    private const int MaxSearchTokens = 8;

    private static readonly string[] ReservedCategoryNames =
    [
        "全部",
        "其它",
        "其他",
        "未分类"
    ];

    private readonly SqliteDatabase _database;

    public NoteRepository(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    // ========================
    // Categories
    // ========================

    public NoteCategorySummary GetCategorySummary()
    {
        using var conn = _database.OpenConnection();

        int totalCount;
        int uncategorizedCount;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = """
                SELECT
                    COUNT(1) AS total_count,
                    COALESCE(SUM(CASE WHEN category_id IS NULL THEN 1 ELSE 0 END), 0) AS uncategorized_count
                FROM notes;
                """;
            using var reader = countCmd.ExecuteReader();
            reader.Read();
            totalCount = reader.GetInt32(0);
            uncategorizedCount = reader.GetInt32(1);
        }

        var categories = LoadCategories(conn, transaction: null);
        return new NoteCategorySummary(totalCount, uncategorizedCount, categories);
    }

    public IReadOnlyList<NoteCategory> GetCategories()
    {
        using var conn = _database.OpenConnection();
        return LoadCategories(conn, transaction: null);
    }

    public NoteCategory CreateCategory(string name)
    {
        var cleanName = ValidateAndNormalizeCategoryName(name);
        var now = UtcNowString();

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        EnsureCategoryNameUnique(conn, tx, cleanName, excludeId: null);

        int nextSortOrder;
        using (var sortCmd = conn.CreateCommand())
        {
            sortCmd.Transaction = tx;
            sortCmd.CommandText = "SELECT COALESCE(MAX(sort_order), 0) + 10 FROM note_categories;";
            nextSortOrder = Convert.ToInt32(sortCmd.ExecuteScalar());
        }

        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO note_categories (name, sort_order, created_at)
                VALUES (@name, @sortOrder, @now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@name", cleanName);
            cmd.Parameters.AddWithValue("@sortOrder", nextSortOrder);
            cmd.Parameters.AddWithValue("@now", now);
            id = Convert.ToInt64(cmd.ExecuteScalar());
        }

        tx.Commit();
        return GetCategoryById(conn, transaction: null, id)!;
    }

    public NoteCategory RenameCategory(long id, string newName)
    {
        var cleanName = ValidateAndNormalizeCategoryName(newName);

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var existing = GetCategoryById(conn, tx, id)
            ?? throw new InvalidOperationException($"笔记分类 {id} 不存在。");

        EnsureCategoryNameUnique(conn, tx, cleanName, excludeId: existing.Id);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE note_categories SET name = @name WHERE id = @id;";
            cmd.Parameters.AddWithValue("@name", cleanName);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return GetCategoryById(conn, transaction: null, id)!;
    }

    public void DeleteCategory(long id)
    {
        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM note_categories WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ========================
    // Notes CRUD
    // ========================

    public NoteItem? GetNoteById(long id)
    {
        using var conn = _database.OpenConnection();
        return GetNoteById(conn, transaction: null, id);
    }

    public NoteItem CreateNote(
        string title,
        string content = "",
        long? categoryId = null,
        bool isPinned = false,
        bool isMasked = false)
    {
        var cleanTitle = RequireTitle(title);
        var rawContent = content ?? string.Empty;
        var now = UtcNowString();

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var resolvedCategoryId = ValidateCategoryId(conn, tx, categoryId);

        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO notes (
                    category_id, title, content, is_pinned, is_masked, created_at, updated_at
                )
                VALUES (
                    @categoryId, @title, @content, @isPinned, @isMasked, @now, @now
                );
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@categoryId", (object?)resolvedCategoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@title", cleanTitle);
            cmd.Parameters.AddWithValue("@content", rawContent);
            cmd.Parameters.AddWithValue("@isPinned", isPinned ? 1 : 0);
            cmd.Parameters.AddWithValue("@isMasked", isMasked ? 1 : 0);
            cmd.Parameters.AddWithValue("@now", now);
            id = Convert.ToInt64(cmd.ExecuteScalar());
        }

        tx.Commit();
        return GetNoteById(conn, transaction: null, id)!;
    }

    public NoteItem UpdateNote(
        long id,
        string title,
        string content,
        long? categoryId,
        bool isPinned,
        bool isMasked)
    {
        var cleanTitle = RequireTitle(title);
        var rawContent = content ?? string.Empty;
        var now = UtcNowString();

        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        _ = GetNoteById(conn, tx, id)
            ?? throw new InvalidOperationException($"笔记 {id} 不存在。");

        var resolvedCategoryId = ValidateCategoryId(conn, tx, categoryId);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE notes
                SET category_id = @categoryId,
                    title = @title,
                    content = @content,
                    is_pinned = @isPinned,
                    is_masked = @isMasked,
                    updated_at = @now
                WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@categoryId", (object?)resolvedCategoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@title", cleanTitle);
            cmd.Parameters.AddWithValue("@content", rawContent);
            cmd.Parameters.AddWithValue("@isPinned", isPinned ? 1 : 0);
            cmd.Parameters.AddWithValue("@isMasked", isMasked ? 1 : 0);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return GetNoteById(conn, transaction: null, id)!;
    }

    public NoteItem ToggleNotePinned(long id)
    {
        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        var existing = GetNoteById(conn, tx, id)
            ?? throw new InvalidOperationException($"笔记 {id} 不存在。");

        var nextPinned = existing.IsPinned ? 0 : 1;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE notes SET is_pinned = @isPinned WHERE id = @id;";
            cmd.Parameters.AddWithValue("@isPinned", nextPinned);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return GetNoteById(conn, transaction: null, id)!;
    }

    public void DeleteNote(long id)
    {
        using var conn = _database.OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM notes WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ========================
    // Search & Paginated Query
    // ========================

    public NoteQueryResult QueryNotes(NoteQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Clamp(query.Limit, 1, 200);
        var offset = Math.Max(0, query.Offset);
        var normalizedKeyword = query.Keyword?.Trim() ?? string.Empty;
        var tokens = ParseSearchTokens(normalizedKeyword);

        using var conn = _database.OpenConnection();

        var whereClauses = new List<string>();
        var parameters = new List<SqliteParameter>();

        switch (query.CategoryFilterMode)
        {
            case NoteCategoryFilterMode.Category when query.CategoryId.HasValue:
                whereClauses.Add("n.category_id = @filterCategoryId");
                parameters.Add(new SqliteParameter("@filterCategoryId", query.CategoryId.Value));
                break;
            case NoteCategoryFilterMode.Uncategorized:
            case NoteCategoryFilterMode.Category when !query.CategoryId.HasValue:
                whereClauses.Add("n.category_id IS NULL");
                break;
        }

        var tokenPatterns = new List<string>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var tokenLower = tokens[i].ToLowerInvariant();
            var patParamName = $"@tok{i}";
            var pattern = $"%{EscapeLikePattern(tokenLower)}%";
            tokenPatterns.Add(patParamName);
            parameters.Add(new SqliteParameter(patParamName, pattern));

            var clause = query.Scope switch
            {
                NoteSearchScope.TitleOnly => $"LOWER(n.title) LIKE {patParamName} ESCAPE '\\'",
                NoteSearchScope.ContentOnly => $"LOWER(n.content) LIKE {patParamName} ESCAPE '\\'",
                _ => $"(LOWER(n.title) LIKE {patParamName} ESCAPE '\\' OR LOWER(n.content) LIKE {patParamName} ESCAPE '\\')"
            };
            whereClauses.Add(clause);
        }

        var whereSql = whereClauses.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", whereClauses);

        int totalCount;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(1) FROM notes n {whereSql};";
            AddClonedParameters(countCmd, parameters);
            totalCount = Convert.ToInt32(countCmd.ExecuteScalar());
        }

        if (totalCount == 0 || offset >= totalCount)
        {
            return new NoteQueryResult(Array.Empty<NoteItem>(), totalCount, HasMore: false);
        }

        string orderBySql;
        var selectRankSql = "0 AS match_rank";

        if (tokens.Count > 0)
        {
            var exactPhrase = normalizedKeyword.ToLowerInvariant();
            var prefixPattern = $"{EscapeLikePattern(exactPhrase)}%";

            parameters.Add(new SqliteParameter("@exactKeyword", exactPhrase));
            parameters.Add(new SqliteParameter("@prefixKeyword", prefixPattern));

            var allTokensInTitle = string.Join(" AND ", tokenPatterns.Select(p => $"LOWER(n.title) LIKE {p} ESCAPE '\\'"));
            var anyTokenInTitle = string.Join(" OR ", tokenPatterns.Select(p => $"LOWER(n.title) LIKE {p} ESCAPE '\\'"));

            selectRankSql = $"""
                CASE
                    WHEN LOWER(n.title) = @exactKeyword THEN 0
                    WHEN LOWER(n.title) LIKE @prefixKeyword ESCAPE '\' THEN 1
                    WHEN ({allTokensInTitle}) THEN 2
                    WHEN ({anyTokenInTitle}) THEN 3
                    ELSE 4
                END AS match_rank
                """;

            orderBySql = "ORDER BY match_rank ASC, n.is_pinned DESC, n.updated_at DESC, n.id DESC";
        }
        else
        {
            orderBySql = "ORDER BY n.is_pinned DESC, n.updated_at DESC, n.id DESC";
        }

        var list = new List<NoteItem>();
        using (var queryCmd = conn.CreateCommand())
        {
            queryCmd.CommandText = $"""
                SELECT
                    n.id,
                    n.category_id,
                    c.name AS category_name,
                    n.title,
                    n.content,
                    n.is_pinned,
                    n.is_masked,
                    n.created_at,
                    n.updated_at,
                    {selectRankSql}
                FROM notes n
                LEFT JOIN note_categories c ON c.id = n.category_id
                {whereSql}
                {orderBySql}
                LIMIT @limit OFFSET @offset;
                """;
            AddClonedParameters(queryCmd, parameters);
            queryCmd.Parameters.AddWithValue("@limit", limit);
            queryCmd.Parameters.AddWithValue("@offset", offset);

            using var reader = queryCmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadNoteItem(reader));
            }
        }

        var hasMore = offset + list.Count < totalCount;
        return new NoteQueryResult(list, totalCount, hasMore);
    }

    public static IReadOnlyList<string> ParseSearchTokens(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return Array.Empty<string>();
        }

        var parts = keyword.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(Math.Min(parts.Length, MaxSearchTokens));

        foreach (var part in parts)
        {
            if (seen.Add(part))
            {
                result.Add(part);
                if (result.Count >= MaxSearchTokens)
                {
                    break;
                }
            }
        }

        return result;
    }

    // ========================
    // Internal Helpers
    // ========================

    private static IReadOnlyList<NoteCategory> LoadCategories(SqliteConnection conn, SqliteTransaction? transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT
                c.id,
                c.name,
                c.sort_order,
                c.created_at,
                (SELECT COUNT(1) FROM notes n WHERE n.category_id = c.id) AS note_count
            FROM note_categories c
            ORDER BY c.sort_order ASC, c.id ASC;
            """;

        var list = new List<NoteCategory>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadCategory(reader));
        }

        return list;
    }

    private static NoteCategory? GetCategoryById(SqliteConnection conn, SqliteTransaction? transaction, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT
                c.id,
                c.name,
                c.sort_order,
                c.created_at,
                (SELECT COUNT(1) FROM notes n WHERE n.category_id = c.id) AS note_count
            FROM note_categories c
            WHERE c.id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCategory(reader) : null;
    }

    private static NoteItem? GetNoteById(SqliteConnection conn, SqliteTransaction? transaction, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT
                n.id,
                n.category_id,
                c.name AS category_name,
                n.title,
                n.content,
                n.is_pinned,
                n.is_masked,
                n.created_at,
                n.updated_at
            FROM notes n
            LEFT JOIN note_categories c ON c.id = n.category_id
            WHERE n.id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadNoteItem(reader) : null;
    }

    private static long? ValidateCategoryId(SqliteConnection conn, SqliteTransaction tx, long? categoryId)
    {
        if (!categoryId.HasValue)
        {
            return null;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(1) FROM note_categories WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", categoryId.Value);
        var exists = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        return exists ? categoryId.Value : null;
    }

    private static string ValidateAndNormalizeCategoryName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("分类名称不能为空。", nameof(name));
        }

        if (trimmed.Length > MaxCategoryNameLength)
        {
            throw new ArgumentException($"分类名称不能超过 {MaxCategoryNameLength} 个字符。", nameof(name));
        }

        foreach (var reserved in ReservedCategoryNames)
        {
            if (string.Equals(trimmed, reserved, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"“{trimmed}”为系统保留名称，请换一个名称。", nameof(name));
            }
        }

        return trimmed;
    }

    private static void EnsureCategoryNameUnique(
        SqliteConnection conn,
        SqliteTransaction tx,
        string name,
        long? excludeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = excludeId.HasValue
            ? "SELECT COUNT(1) FROM note_categories WHERE LOWER(name) = LOWER(@name) AND id <> @excludeId;"
            : "SELECT COUNT(1) FROM note_categories WHERE LOWER(name) = LOWER(@name);";
        cmd.Parameters.AddWithValue("@name", name);
        if (excludeId.HasValue)
        {
            cmd.Parameters.AddWithValue("@excludeId", excludeId.Value);
        }

        if (Convert.ToInt32(cmd.ExecuteScalar()) > 0)
        {
            throw new InvalidOperationException($"分类“{name}”已存在。");
        }
    }

    private static string RequireTitle(string title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("笔记标题不能为空。", nameof(title));
        }

        return trimmed;
    }

    private static void AddClonedParameters(SqliteCommand target, IReadOnlyList<SqliteParameter> source)
    {
        foreach (var p in source)
        {
            target.Parameters.AddWithValue(p.ParameterName, p.Value ?? DBNull.Value);
        }
    }

    private static NoteCategory ReadCategory(SqliteDataReader reader)
    {
        return new NoteCategory
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            SortOrder = reader.GetInt32(2),
            CreatedAtUtc = ParseUtc(reader.GetString(3)),
            NoteCount = reader.GetInt32(4)
        };
    }

    private static NoteItem ReadNoteItem(SqliteDataReader reader)
    {
        return new NoteItem
        {
            Id = reader.GetInt64(0),
            CategoryId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
            CategoryName = reader.IsDBNull(2) ? null : reader.GetString(2),
            Title = reader.GetString(3),
            Content = reader.GetString(4),
            IsPinned = reader.GetInt32(5) != 0,
            IsMasked = reader.GetInt32(6) != 0,
            CreatedAtUtc = ParseUtc(reader.GetString(7)),
            UpdatedAtUtc = ParseUtc(reader.GetString(8))
        };
    }

    private static string EscapeLikePattern(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length + 4);
        foreach (var ch in input)
        {
            if (ch is '\\' or '%' or '_')
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string UtcNowString() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
