using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public class DocumentUploadTests : IDisposable
{
    private const int NiKangUserId = 4;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly string _storageRoot;
    private readonly DocumentService _service;

    public DocumentUploadTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), "contoso-doc-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_storageRoot);

        _service = new DocumentService(
            _context,
            new TestFileStorageService(_storageRoot),
            new FileValidationService(),
            new NotificationService(_context),
            NullLogger<DocumentService>.Instance);
    }

    private static MemoryStream Pdf() =>
        new(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 });

    private static DocumentUploadRequest Request(
        string title = "Test Document",
        string category = DocumentCategories.PersonalFiles,
        string fileName = "test.pdf",
        long size = 8,
        int? projectId = null,
        string? tags = null,
        Stream? content = null) =>
        new(title, null, category, tags, projectId, null, fileName, "application/pdf", size, content ?? Pdf());

    [Fact]
    public async Task A_valid_upload_creates_one_row_and_one_file()
    {
        var result = await _service.UploadAsync(Request(), NiKangUserId);

        Assert.True(result.Succeeded);
        Assert.Equal("Test Document", result.Document!.Title);
        Assert.Equal(DocumentCategories.PersonalFiles, result.Document.Category);

        Assert.Equal(1, await _context.Documents.CountAsync());
        Assert.True(File.Exists(Path.Combine(_storageRoot, result.Document.StoragePath)));
    }

    [Fact]
    public async Task An_upload_writes_exactly_one_upload_activity()
    {
        await _service.UploadAsync(Request(), NiKangUserId);

        var activity = Assert.Single(await _context.DocumentActivities.ToListAsync());
        Assert.Equal(DocumentActions.Upload, activity.Action);
        Assert.Equal(NiKangUserId, activity.UserId);
    }

    [Fact]
    public async Task The_storage_path_never_contains_the_user_supplied_filename()
    {
        var result = await _service.UploadAsync(
            Request(fileName: "../../etc/passwd.pdf"), NiKangUserId);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("passwd", result.Document!.StoragePath);
        Assert.DoesNotContain("..", result.Document.StoragePath);
        Assert.Equal("../../etc/passwd.pdf", result.Document.OriginalFileName);
    }

    [Fact]
    public async Task An_oversized_file_is_rejected_leaving_no_row_and_no_file()
    {
        var result = await _service.UploadAsync(
            Request(size: 26 * 1024 * 1024), NiKangUserId);

        Assert.False(result.Succeeded);
        Assert.Contains("25 MB", result.Error);
        Assert.Equal(0, await _context.Documents.CountAsync());
        Assert.Empty(Directory.GetFiles(_storageRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_disallowed_type_is_rejected()
    {
        var result = await _service.UploadAsync(Request(fileName: "tool.exe"), NiKangUserId);

        Assert.False(result.Succeeded);
        Assert.Equal(0, await _context.Documents.CountAsync());
    }

    [Fact]
    public async Task A_missing_title_is_rejected()
    {
        var result = await _service.UploadAsync(Request(title: "  "), NiKangUserId);

        Assert.False(result.Succeeded);
        Assert.Contains("title", result.Error);
    }

    [Fact]
    public async Task An_invalid_category_is_rejected()
    {
        var result = await _service.UploadAsync(Request(category: "Made Up"), NiKangUserId);

        Assert.False(result.Succeeded);
        Assert.Contains("category", result.Error);
    }

    [Fact]
    public async Task More_than_ten_tags_is_rejected()
    {
        var tags = string.Join(",", Enumerable.Range(1, 11).Select(i => $"tag{i}"));

        var result = await _service.UploadAsync(Request(tags: tags), NiKangUserId);

        Assert.False(result.Succeeded);
        Assert.Contains("10 tags", result.Error);
    }

    [Fact]
    public async Task Tags_are_lowercased_and_deduplicated()
    {
        var result = await _service.UploadAsync(Request(tags: "Budget, budget , Q4"), NiKangUserId);

        Assert.True(result.Succeeded);
        Assert.Equal("budget,q4", result.Document!.Tags);
    }

    [Fact]
    public async Task Uploading_to_a_project_the_user_does_not_belong_to_is_rejected()
    {
        var project = new Project
        {
            Name = "Closed Project",
            ProjectManagerId = 1,
            Status = ProjectStatus.Active
        };
        _context.Projects.Add(project);
        await _context.SaveChangesAsync();

        var result = await _service.UploadAsync(
            Request(projectId: project.ProjectId), NiKangUserId);

        Assert.False(result.Succeeded);
        Assert.Contains("member of", result.Error);
        Assert.Equal(0, await _context.Documents.CountAsync());
    }

    [Fact]
    public async Task A_failure_saving_metadata_removes_the_file_it_already_wrote()
    {
        var storage = new TestFileStorageService(_storageRoot);
        var service = new DocumentService(
            new ThrowingDbContext(_connection),
            storage,
            new FileValidationService(),
            new NotificationService(_context),
            NullLogger<DocumentService>.Instance);

        var result = await service.UploadAsync(Request(), NiKangUserId);

        Assert.False(result.Succeeded);
        Assert.Contains("could not be stored", result.Error);
        Assert.Empty(Directory.GetFiles(_storageRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task My_documents_returns_only_the_acting_users_documents()
    {
        await _service.UploadAsync(Request(title: "Mine"), NiKangUserId);
        await _service.UploadAsync(Request(title: "Theirs"), 1);

        var mine = await _service.GetMyDocumentsAsync(NiKangUserId);

        Assert.Single(mine);
        Assert.Equal("Mine", mine[0].Title);
        Assert.Equal(1, await _service.GetCountAsync(NiKangUserId));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    private sealed class TestFileStorageService : IFileStorageService
    {
        private readonly string _root;

        public TestFileStorageService(string root) => _root = root;

        public async Task<string> UploadAsync(Stream content, string storagePath, string contentType)
        {
            var full = Path.Combine(_root, storagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await using var file = File.Create(full);
            await content.CopyToAsync(file);
            return storagePath;
        }

        public Task<Stream?> DownloadAsync(string storagePath)
        {
            var full = Path.Combine(_root, storagePath);
            return Task.FromResult<Stream?>(File.Exists(full) ? File.OpenRead(full) : null);
        }

        public Task DeleteAsync(string storagePath)
        {
            var full = Path.Combine(_root, storagePath);
            if (File.Exists(full)) File.Delete(full);
            return Task.CompletedTask;
        }

        public Task<string> GetUrlAsync(string storagePath, TimeSpan expiration) =>
            Task.FromResult($"/test/{storagePath}");
    }

    private sealed class ThrowingDbContext : ApplicationDbContext
    {
        public ThrowingDbContext(SqliteConnection connection)
            : base(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("simulated metadata write failure");
    }
}
