using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ContosoDashboard.Tests.Integration;

public class DocumentAuthorizationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly string _storageRoot;
    private readonly DocumentService _service;

    private int _ownerId;
    private int _strangerId;
    private int _adminId;
    private int _managerId;
    private int _projectId;

    public DocumentAuthorizationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), "contoso-auth-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_storageRoot);

        _service = new DocumentService(
            _context,
            new TempStorage(_storageRoot),
            new FileValidationService(),
            new NotificationService(_context),
            NullLogger<DocumentService>.Instance);

        SeedPeople();
    }

    private void SeedPeople()
    {
        var owner = new User { Email = "owner@test.invalid", DisplayName = "Owner", Role = UserRole.Employee, Department = "Engineering" };
        var stranger = new User { Email = "stranger@test.invalid", DisplayName = "Stranger", Role = UserRole.Employee, Department = "Sales" };
        var admin = new User { Email = "admin@test.invalid", DisplayName = "Admin", Role = UserRole.Administrator, Department = "IT" };
        var manager = new User { Email = "pm@test.invalid", DisplayName = "Manager", Role = UserRole.ProjectManager, Department = "Engineering" };

        _context.Users.AddRange(owner, stranger, admin, manager);
        _context.SaveChanges();

        _ownerId = owner.UserId;
        _strangerId = stranger.UserId;
        _adminId = admin.UserId;
        _managerId = manager.UserId;

        var project = new Project { Name = "Test Project", ProjectManagerId = _managerId, Status = ProjectStatus.Active };
        _context.Projects.Add(project);
        _context.SaveChanges();
        _projectId = project.ProjectId;

        _context.ProjectMembers.Add(new ProjectMember { ProjectId = _projectId, UserId = _ownerId });
        _context.SaveChanges();
    }

    private static MemoryStream Pdf() => new(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 });

    private async Task<Document> UploadAsync(int? projectId = null, string title = "Doc")
    {
        var request = new DocumentUploadRequest(
            title, null, DocumentCategories.PersonalFiles, null, projectId, null,
            "file.pdf", "application/pdf", 8, Pdf());

        var result = await _service.UploadAsync(request, _ownerId);
        Assert.True(result.Succeeded, result.Error);
        return result.Document!;
    }

    [Fact]
    public async Task A_stranger_cannot_read_a_document_by_id()
    {
        var document = await UploadAsync();

        Assert.Null(await _service.GetByIdAsync(document.DocumentId, _strangerId));
    }

    [Fact]
    public async Task An_unauthorized_read_is_indistinguishable_from_a_nonexistent_one()
    {
        var document = await UploadAsync();

        var unauthorized = await _service.GetByIdAsync(document.DocumentId, _strangerId);
        var nonexistent = await _service.GetByIdAsync(999999, _strangerId);

        Assert.Null(unauthorized);
        Assert.Null(nonexistent);
    }

    [Fact]
    public async Task An_owner_can_read_their_own_document()
    {
        var document = await UploadAsync();

        Assert.NotNull(await _service.GetByIdAsync(document.DocumentId, _ownerId));
    }

    [Fact]
    public async Task An_administrator_can_read_any_document()
    {
        var document = await UploadAsync();

        Assert.NotNull(await _service.GetByIdAsync(document.DocumentId, _adminId));
    }

    [Fact]
    public async Task A_project_manager_can_read_documents_in_their_project()
    {
        var document = await UploadAsync(projectId: _projectId);

        Assert.NotNull(await _service.GetByIdAsync(document.DocumentId, _managerId));
    }

    [Fact]
    public async Task Search_never_returns_a_document_the_user_cannot_access()
    {
        await UploadAsync(title: "Confidential Salary Review");

        var results = await _service.SearchAsync("Confidential", _strangerId, DocumentQuery.Default);

        Assert.Empty(results.Items);
    }

    [Fact]
    public async Task Search_matches_title_for_an_authorized_user()
    {
        await UploadAsync(title: "Quarterly Budget");

        var results = await _service.SearchAsync("Budget", _ownerId, DocumentQuery.Default);

        Assert.Single(results.Items);
    }

    [Fact]
    public async Task A_stranger_cannot_open_document_content()
    {
        var document = await UploadAsync();

        Assert.Null(await _service.OpenContentAsync(document.DocumentId, _strangerId));
    }

    [Fact]
    public async Task Opening_content_records_a_download_activity()
    {
        var document = await UploadAsync();

        await using var stream = await _service.OpenContentAsync(document.DocumentId, _ownerId);

        Assert.NotNull(stream);
        Assert.Single(await _context.DocumentActivities
            .Where(a => a.Action == DocumentActions.Download).ToListAsync());
    }

    [Fact]
    public async Task A_missing_file_yields_null_rather_than_throwing()
    {
        var document = await UploadAsync();
        File.Delete(Path.Combine(_storageRoot, document.StoragePath));

        Assert.Null(await _service.OpenContentAsync(document.DocumentId, _ownerId));
    }

    [Fact]
    public async Task A_non_owner_cannot_edit_metadata()
    {
        var document = await UploadAsync();
        var update = new DocumentMetadataUpdate("Hijacked", null, DocumentCategories.Reports, null);

        Assert.False(await _service.UpdateMetadataAsync(document.DocumentId, update, _strangerId));
        Assert.False(await _service.UpdateMetadataAsync(document.DocumentId, update, _adminId));
    }

    [Fact]
    public async Task An_owner_can_edit_metadata()
    {
        var document = await UploadAsync();
        var update = new DocumentMetadataUpdate("Renamed", "New description", DocumentCategories.Reports, "a,B,a");

        Assert.True(await _service.UpdateMetadataAsync(document.DocumentId, update, _ownerId));

        var reloaded = await _context.Documents.FindAsync(document.DocumentId);
        Assert.Equal("Renamed", reloaded!.Title);
        Assert.Equal(DocumentCategories.Reports, reloaded.Category);
        Assert.Equal("a,b", reloaded.Tags);
    }

    [Fact]
    public async Task Replacing_a_file_swaps_storage_and_removes_the_superseded_file()
    {
        var document = await UploadAsync();
        var originalPath = document.StoragePath;

        var replacement = new DocumentFileReplacement("new.pdf", "application/pdf", 8, Pdf());
        Assert.True(await _service.ReplaceFileAsync(document.DocumentId, replacement, _ownerId));

        var reloaded = await _context.Documents.FindAsync(document.DocumentId);
        Assert.NotEqual(originalPath, reloaded!.StoragePath);
        Assert.False(File.Exists(Path.Combine(_storageRoot, originalPath)));
        Assert.True(File.Exists(Path.Combine(_storageRoot, reloaded.StoragePath)));
    }

    [Fact]
    public async Task A_stranger_cannot_delete_a_document()
    {
        var document = await UploadAsync();

        Assert.False(await _service.DeleteAsync(document.DocumentId, _strangerId));
        Assert.Equal(1, await _context.Documents.CountAsync());
    }

    [Fact]
    public async Task An_owner_can_delete_and_the_file_goes_with_it()
    {
        var document = await UploadAsync();
        var path = Path.Combine(_storageRoot, document.StoragePath);

        Assert.True(await _service.DeleteAsync(document.DocumentId, _ownerId));
        Assert.Equal(0, await _context.Documents.CountAsync());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_project_manager_can_delete_a_document_in_their_project()
    {
        var document = await UploadAsync(projectId: _projectId);

        Assert.True(await _service.DeleteAsync(document.DocumentId, _managerId));
    }

    [Fact]
    public async Task Deleting_a_shared_document_removes_shares_and_notifies_recipients()
    {
        var document = await UploadAsync();
        Assert.True(await _service.ShareAsync(document.DocumentId, _strangerId, _ownerId));

        Assert.True(await _service.DeleteAsync(document.DocumentId, _ownerId));

        Assert.Equal(0, await _context.DocumentShares.CountAsync());
        Assert.Contains(await _context.Notifications.ToListAsync(),
            n => n.UserId == _strangerId && n.Type == NotificationType.DocumentDeleted);
    }

    [Fact]
    public async Task A_recipient_can_read_a_shared_document_but_not_change_it()
    {
        var document = await UploadAsync();
        await _service.ShareAsync(document.DocumentId, _strangerId, _ownerId);

        Assert.NotNull(await _service.GetByIdAsync(document.DocumentId, _strangerId));
        Assert.False(await _service.UpdateMetadataAsync(
            document.DocumentId, new DocumentMetadataUpdate("x", null, DocumentCategories.Other, null), _strangerId));
        Assert.False(await _service.DeleteAsync(document.DocumentId, _strangerId));
    }

    [Fact]
    public async Task Sharing_twice_is_idempotent_and_does_not_notify_again()
    {
        var document = await UploadAsync();

        Assert.True(await _service.ShareAsync(document.DocumentId, _strangerId, _ownerId));
        Assert.True(await _service.ShareAsync(document.DocumentId, _strangerId, _ownerId));

        Assert.Equal(1, await _context.DocumentShares.CountAsync());
        Assert.Single(await _context.Notifications
            .Where(n => n.Type == NotificationType.DocumentShared).ToListAsync());
    }

    [Fact]
    public async Task A_document_cannot_be_shared_with_its_own_owner()
    {
        var document = await UploadAsync();

        Assert.False(await _service.ShareAsync(document.DocumentId, _ownerId, _ownerId));
    }

    [Fact]
    public async Task Only_the_owner_can_share()
    {
        var document = await UploadAsync();

        Assert.False(await _service.ShareAsync(document.DocumentId, _adminId, _strangerId));
    }

    [Fact]
    public async Task Shared_with_me_excludes_the_users_own_documents()
    {
        var document = await UploadAsync();
        await _service.ShareAsync(document.DocumentId, _strangerId, _ownerId);

        var forOwner = await _service.GetSharedWithMeAsync(_ownerId, DocumentQuery.Default);
        var forStranger = await _service.GetSharedWithMeAsync(_strangerId, DocumentQuery.Default);

        Assert.Empty(forOwner.Items);
        Assert.Single(forStranger.Items);
    }

    [Fact]
    public async Task Project_documents_are_empty_for_a_non_member()
    {
        await UploadAsync(projectId: _projectId);

        var results = await _service.GetProjectDocumentsAsync(_projectId, _strangerId, DocumentQuery.Default);

        Assert.Empty(results.Items);
    }

    [Fact]
    public async Task Deleting_a_project_disassociates_its_documents_rather_than_deleting_them()
    {
        var document = await UploadAsync(projectId: _projectId);

        var project = await _context.Projects.FindAsync(_projectId);
        _context.Projects.Remove(project!);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Documents.FindAsync(document.DocumentId);

        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.ProjectId);
    }

    [Fact]
    public async Task Only_an_administrator_can_read_reports()
    {
        await UploadAsync();

        Assert.Null(await _service.GetReportAsync(_ownerId));
        Assert.Null(await _service.GetReportAsync(_strangerId));
        Assert.NotNull(await _service.GetReportAsync(_adminId));
    }

    [Fact]
    public async Task Sorting_and_filtering_apply_in_the_query()
    {
        await UploadAsync(title: "Alpha");
        await UploadAsync(title: "Zulu");

        var byTitle = await _service.GetMyDocumentsAsync(_ownerId,
            DocumentQuery.Default with { SortBy = DocumentSortColumn.Title, Descending = false });

        Assert.Equal("Alpha", byTitle.Items[0].Title);
        Assert.Equal("Zulu", byTitle.Items[1].Title);

        var filtered = await _service.GetMyDocumentsAsync(_ownerId,
            DocumentQuery.Default with { Category = DocumentCategories.Reports });

        Assert.Empty(filtered.Items);
    }

    [Fact]
    public async Task Paging_reports_totals_correctly()
    {
        for (var i = 0; i < 5; i++)
        {
            await UploadAsync(title: $"Doc {i}");
        }

        var firstPage = await _service.GetMyDocumentsAsync(_ownerId,
            DocumentQuery.Default with { PageSize = 2, Page = 1 });

        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(5, firstPage.TotalCount);
        Assert.Equal(3, firstPage.TotalPages);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true);
    }

    private sealed class TempStorage : IFileStorageService
    {
        private readonly string _root;
        public TempStorage(string root) => _root = root;

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
}
