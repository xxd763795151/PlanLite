using Microsoft.Data.Sqlite;
using PlanLite.Core.Data;
using PlanLite.Core.Models;
using Xunit;

namespace PlanLite.Core.Tests;

public sealed class NoteRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDatabase _db;
    private readonly NoteRepository _repo;

    public NoteRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"planlite-notes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _db = new SqliteDatabase(Path.Combine(_tempDir, "planlite.db"));
        _db.Initialize();
        _repo = new NoteRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore transient cleanup failures.
        }
    }

    [Fact]
    public void Initialize_SeedsDefaultCategoriesOnceAndDoesNotResurrectDeletedDefaults()
    {
        var initial = _repo.GetCategories();
        Assert.Equal(new[] { "账号", "脚本", "链接" }, initial.Select(c => c.Name).ToArray());

        var scriptCat = initial.Single(c => c.Name == "脚本");
        _repo.DeleteCategory(scriptCat.Id);

        _db.Initialize();
        var afterReinit = _repo.GetCategories();
        Assert.Equal(new[] { "账号", "链接" }, afterReinit.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void CategoryCrud_ValidatesRulesAndMovesDeletedCategoryNotesToUncategorized()
    {
        Assert.Throws<ArgumentException>(() => _repo.CreateCategory("   "));
        Assert.Throws<ArgumentException>(() => _repo.CreateCategory(new string('A', NoteRepository.MaxCategoryNameLength + 1)));
        Assert.Throws<ArgumentException>(() => _repo.CreateCategory("全部"));
        Assert.Throws<ArgumentException>(() => _repo.CreateCategory("其它"));
        Assert.Throws<ArgumentException>(() => _repo.CreateCategory("未分类"));

        var custom = _repo.CreateCategory("运维命令");
        Assert.Throws<InvalidOperationException>(() => _repo.CreateCategory("运维命令"));

        var renamed = _repo.RenameCategory(custom.Id, "服务器");
        Assert.Equal("服务器", renamed.Name);
        Assert.Throws<InvalidOperationException>(() => _repo.RenameCategory(renamed.Id, "账号"));

        var note = _repo.CreateNote("跳板机", "ssh root@10.0.0.1", renamed.Id);
        Assert.Equal(renamed.Id, note.CategoryId);
        Assert.Equal("服务器", note.CategoryName);

        _repo.DeleteCategory(renamed.Id);
        var updatedNote = _repo.GetNoteById(note.Id);
        Assert.NotNull(updatedNote);
        Assert.Null(updatedNote!.CategoryId);
        Assert.Null(updatedNote.CategoryName);

        var summary = _repo.GetCategorySummary();
        Assert.Equal(1, summary.TotalNoteCount);
        Assert.Equal(1, summary.UncategorizedNoteCount);
    }

    [Fact]
    public void NoteCrud_PreservesRawIndentationNewlinesAndPinPreservesUpdatedAt()
    {
        var scriptCat = _repo.GetCategories().Single(c => c.Name == "脚本");
        const string rawScript = "  #!/bin/bash\n\tfor i in 1 2 3; do\n\t  echo \"$i\"\n\tdone\n";

        var created = _repo.CreateNote("  部署脚本  ", rawScript, scriptCat.Id, isPinned: false, isMasked: false);
        Assert.Equal("部署脚本", created.Title);
        Assert.Equal(rawScript, created.Content);
        Assert.False(created.IsPinned);

        var toggled = _repo.ToggleNotePinned(created.Id);
        Assert.True(toggled.IsPinned);
        Assert.Equal(created.UpdatedAtUtc, toggled.UpdatedAtUtc);
        Assert.Equal(rawScript, toggled.Content);

        const string updatedRaw = "    SELECT * FROM users\r\n    WHERE name LIKE '%admin%';  ";
        var updated = _repo.UpdateNote(created.Id, "SQL 查询", updatedRaw, null, isPinned: true, isMasked: true);
        Assert.Null(updated.CategoryId);
        Assert.Equal(updatedRaw, updated.Content);
        Assert.True(updated.IsMasked);

        _repo.DeleteNote(created.Id);
        Assert.Null(_repo.GetNoteById(created.Id));
    }

    [Fact]
    public void QueryNotes_RanksTitleMatchesAboveContentAndHandlesMultiTokenEscapesAndFilters()
    {
        var cats = _repo.GetCategories();
        var accountCat = cats.Single(c => c.Name == "账号");
        var scriptCat = cats.Single(c => c.Name == "脚本");

        var contentOnly = _repo.CreateNote("生产服务器巡检", "登录 gitee 拉取代码", scriptCat.Id);
        var mixedHit = _repo.CreateNote("代码平台信息", "常用 git 与 gitee 镜像", accountCat.Id);
        var titleContainsAll = _repo.CreateNote("公司内部 gitee 仓库", "https://example.local", accountCat.Id);
        var titlePrefix = _repo.CreateNote("gitee 密码与令牌", "token=abc_100%", accountCat.Id, isMasked: true);
        var titleExact = _repo.CreateNote("Gitee", "主账号说明", accountCat.Id);
        var uncategorizedSpecial = _repo.CreateNote("SQL_通配符\\转义%测试", "LIKE '%100_ok\\%'", null);

        // 1. Relevance ranking: Exact > Prefix > Title Contains > Content Only
        var rankResult = _repo.QueryNotes(new NoteQuery { Keyword = "gitee" });
        Assert.Equal(
            new[] { titleExact.Id, titlePrefix.Id, titleContainsAll.Id, mixedHit.Id, contentOnly.Id },
            rankResult.Items.Select(n => n.Id).ToArray());

        // 2. Multi-token AND across title + content (case-insensitive)
        var multiToken = _repo.QueryNotes(new NoteQuery { Keyword = "GITEE 令牌" });
        var singleHit = Assert.Single(multiToken.Items);
        Assert.Equal(titlePrefix.Id, singleHit.Id);

        // 2b. Partial title match ranks ahead of content-only match when all tokens are satisfied
        var allInContent = _repo.CreateNote("备忘记录", "代码托管平台 gitee 地址", scriptCat.Id);
        var mixedRank = _repo.QueryNotes(new NoteQuery { Keyword = "代码 gitee" });
        Assert.Equal(
            new[] { mixedHit.Id, allInContent.Id, contentOnly.Id },
            mixedRank.Items.Select(n => n.Id).ToArray());

        // 3. Search scope filters
        var titleOnly = _repo.QueryNotes(new NoteQuery { Keyword = "gitee", Scope = NoteSearchScope.TitleOnly });
        Assert.Equal(3, titleOnly.TotalCount);
        Assert.DoesNotContain(titleOnly.Items, x => x.Id == contentOnly.Id);

        var contentOnlyScope = _repo.QueryNotes(new NoteQuery { Keyword = "gitee", Scope = NoteSearchScope.ContentOnly });
        Assert.Equal(3, contentOnlyScope.TotalCount);
        Assert.Contains(contentOnlyScope.Items, x => x.Id == contentOnly.Id);
        Assert.Contains(contentOnlyScope.Items, x => x.Id == mixedHit.Id);
        Assert.Contains(contentOnlyScope.Items, x => x.Id == allInContent.Id);

        // 4. Category filter (category vs uncategorized)
        var uncategorizedResult = _repo.QueryNotes(new NoteQuery
        {
            CategoryFilterMode = NoteCategoryFilterMode.Uncategorized
        });
        Assert.Equal(uncategorizedSpecial.Id, Assert.Single(uncategorizedResult.Items).Id);

        // 5. Escaped LIKE wildcards: %, _, \
        var percentHit = _repo.QueryNotes(new NoteQuery { Keyword = "100%" });
        Assert.Equal(titlePrefix.Id, Assert.Single(percentHit.Items).Id);

        var backslashHit = _repo.QueryNotes(new NoteQuery { Keyword = "\\转义%" });
        Assert.Equal(uncategorizedSpecial.Id, Assert.Single(backslashHit.Items).Id);
    }

    [Fact]
    public void QueryNotes_PaginatesSixHundredNotesCorrectlyAndPinnedStayFirst()
    {
        var cats = _repo.GetCategories();
        var scriptCat = cats.Single(c => c.Name == "脚本");

        var pinnedId = _repo.CreateNote("核心置顶脚本", "kubectl get pods -A", scriptCat.Id, isPinned: true).Id;

        using (var conn = _db.OpenConnection())
        using (var tx = conn.BeginTransaction())
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO notes (category_id, title, content, is_pinned, is_masked, created_at, updated_at)
                VALUES (@catId, @title, @content, 0, 0, @ts, @ts);
                """;
            var catParam = cmd.Parameters.Add("@catId", SqliteType.Integer);
            var titleParam = cmd.Parameters.Add("@title", SqliteType.Text);
            var contentParam = cmd.Parameters.Add("@content", SqliteType.Text);
            var tsParam = cmd.Parameters.Add("@ts", SqliteType.Text);

            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 600; i++)
            {
                catParam.Value = i % 2 == 0 ? scriptCat.Id : DBNull.Value;
                titleParam.Value = $"批量笔记-{i:D4}";
                contentParam.Value = i == 345 ? "包含特殊关键词 docker-compose up -d" : $"内容编号 {i}";
                tsParam.Value = baseTime.AddMinutes(i).ToString("O");
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        var page1 = _repo.QueryNotes(new NoteQuery { Limit = 30, Offset = 0 });
        Assert.Equal(601, page1.TotalCount);
        Assert.True(page1.HasMore);
        Assert.Equal(30, page1.Items.Count);
        Assert.Equal(pinnedId, page1.Items[0].Id);

        var page2 = _repo.QueryNotes(new NoteQuery { Limit = 30, Offset = 30 });
        Assert.Equal(30, page2.Items.Count);
        Assert.True(page2.HasMore);
        Assert.DoesNotContain(page2.Items, x => x.Id == pinnedId);

        var search = _repo.QueryNotes(new NoteQuery { Keyword = "docker-compose" });
        Assert.Equal(1, search.TotalCount);
        Assert.Equal("批量笔记-0345", Assert.Single(search.Items).Title);
    }
}
