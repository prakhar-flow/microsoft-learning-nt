using Microsoft.EntityFrameworkCore;
using ContosoDashboard.Data;
using ContosoDashboard.Models;

namespace ContosoDashboard.Services;

public record DocumentUploadRequest(
    string Title,
    string? Description,
    string Category,
    string? Tags,
    int? ProjectId,
    int? TaskItemId,
    string OriginalFileName,
    string ContentType,
    long FileSizeBytes,
    Stream Content);

public record DocumentUploadResult(bool Succeeded, Document? Document, string? Error)
{
    public static DocumentUploadResult Success(Document document) => new(true, document, null);
    public static DocumentUploadResult Failure(string error) => new(false, null, error);
}

public interface IDocumentService
{
    Task<DocumentUploadResult> UploadAsync(DocumentUploadRequest request, int actingUserId);
    Task<List<Document>> GetMyDocumentsAsync(int actingUserId);
    Task<DocumentPage> GetMyDocumentsAsync(int actingUserId, DocumentQuery query);
    Task<DocumentPage> GetProjectDocumentsAsync(int projectId, int actingUserId, DocumentQuery query);
    Task<DocumentPage> GetSharedWithMeAsync(int actingUserId, DocumentQuery query);
    Task<DocumentPage> SearchAsync(string term, int actingUserId, DocumentQuery query);
    Task<Document?> GetByIdAsync(int documentId, int actingUserId);
    Task<Stream?> OpenContentAsync(int documentId, int actingUserId);
    Task<bool> UpdateMetadataAsync(int documentId, DocumentMetadataUpdate update, int actingUserId);
    Task<bool> ReplaceFileAsync(int documentId, DocumentFileReplacement replacement, int actingUserId);
    Task<bool> DeleteAsync(int documentId, int actingUserId);
    Task<bool> ShareAsync(int documentId, int recipientUserId, int actingUserId);
    Task<List<Document>> GetTaskDocumentsAsync(int taskItemId, int actingUserId);
    Task<List<Document>> GetRecentAsync(int actingUserId, int count);
    Task<int> GetCountAsync(int actingUserId);
    Task<DocumentReport?> GetReportAsync(int actingUserId);
}

public class DocumentService : IDocumentService
{
    private const int MaxTags = 10;
    private const int MaxTagLength = 50;

    private readonly ApplicationDbContext _context;
    private readonly IFileStorageService _storage;
    private readonly IFileValidationService _validation;
    private readonly INotificationService _notifications;
    private readonly ILogger<DocumentService> _logger;

    public DocumentService(
        ApplicationDbContext context,
        IFileStorageService storage,
        IFileValidationService validation,
        INotificationService notifications,
        ILogger<DocumentService> logger)
    {
        _context = context;
        _storage = storage;
        _validation = validation;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<DocumentUploadResult> UploadAsync(DocumentUploadRequest request, int actingUserId)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return DocumentUploadResult.Failure("A title is required.");
        }

        if (!DocumentCategories.All.Contains(request.Category))
        {
            return DocumentUploadResult.Failure("A valid category is required.");
        }

        var validation = _validation.Validate(
            request.OriginalFileName, request.ContentType, request.FileSizeBytes, request.Content);

        if (!validation.IsValid)
        {
            return DocumentUploadResult.Failure(validation.Error!);
        }

        var tags = NormalizeTags(request.Tags, out var tagError);
        if (tagError != null)
        {
            return DocumentUploadResult.Failure(tagError);
        }

        if (request.ProjectId.HasValue &&
            !await IsProjectMemberAsync(request.ProjectId.Value, actingUserId))
        {
            return DocumentUploadResult.Failure(
                "You can only add documents to projects you are a member of.");
        }

        var storagePath = BuildStoragePath(actingUserId, request.ProjectId, request.OriginalFileName);

        request.Content.Position = 0;
        await _storage.UploadAsync(request.Content, storagePath, request.ContentType);

        var document = new Document
        {
            Title = request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Category = request.Category,
            Tags = tags,
            OriginalFileName = request.OriginalFileName,
            StoragePath = storagePath,
            FileSizeBytes = request.FileSizeBytes,
            ContentType = request.ContentType,
            UploadedByUserId = actingUserId,
            ProjectId = request.ProjectId,
            TaskItemId = request.TaskItemId,
            UploadedDate = DateTime.UtcNow,
            UpdatedDate = DateTime.UtcNow
        };

        try
        {
            _context.Documents.Add(document);
            _context.DocumentActivities.Add(new DocumentActivity
            {
                Document = document,
                UserId = actingUserId,
                Action = DocumentActions.Upload
            });

            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // The file is already on disk but has no metadata row; remove it so no orphan survives.
            _logger.LogError(ex, "Saving document metadata failed; removing {StoragePath}.", storagePath);
            await _storage.DeleteAsync(storagePath);
            return DocumentUploadResult.Failure(
                "The document could not be stored. Please try again.");
        }

        if (document.ProjectId.HasValue)
        {
            await NotifyProjectMembersAsync(document, actingUserId);
        }

        return DocumentUploadResult.Success(document);
    }

    public async Task<List<Document>> GetMyDocumentsAsync(int actingUserId) =>
        await _context.Documents
            .Where(d => d.UploadedByUserId == actingUserId)
            .Include(d => d.Project)
            .OrderByDescending(d => d.UploadedDate)
            .ToListAsync();

    public Task<DocumentPage> GetMyDocumentsAsync(int actingUserId, DocumentQuery query) =>
        PageAsync(_context.Documents.Where(d => d.UploadedByUserId == actingUserId), query);

    public async Task<DocumentPage> GetProjectDocumentsAsync(int projectId, int actingUserId, DocumentQuery query)
    {
        if (!await IsProjectMemberAsync(projectId, actingUserId) && !await IsAdministratorAsync(actingUserId))
        {
            return new DocumentPage(Array.Empty<Document>(), 0, query.Page, query.PageSize);
        }

        return await PageAsync(_context.Documents.Where(d => d.ProjectId == projectId), query);
    }

    public Task<DocumentPage> GetSharedWithMeAsync(int actingUserId, DocumentQuery query) =>
        PageAsync(
            _context.Documents.Where(d =>
                d.UploadedByUserId != actingUserId &&
                d.Shares.Any(sh => sh.SharedWithUserId == actingUserId)),
            query);

    public async Task<DocumentPage> SearchAsync(string term, int actingUserId, DocumentQuery query)
    {
        var accessible = await AccessibleDocumentsAsync(actingUserId);

        if (!string.IsNullOrWhiteSpace(term))
        {
            var pattern = $"%{term.Trim()}%";
            accessible = accessible.Where(d =>
                EF.Functions.Like(d.Title, pattern) ||
                (d.Description != null && EF.Functions.Like(d.Description, pattern)) ||
                (d.Tags != null && EF.Functions.Like(d.Tags, pattern)) ||
                EF.Functions.Like(d.UploadedByUser.DisplayName, pattern) ||
                (d.Project != null && EF.Functions.Like(d.Project.Name, pattern)));
        }

        return await PageAsync(accessible, query);
    }

    public async Task<Document?> GetByIdAsync(int documentId, int actingUserId)
    {
        var document = await _context.Documents
            .Include(d => d.Project)
            .Include(d => d.UploadedByUser)
            .Include(d => d.Shares)
            .FirstOrDefaultAsync(d => d.DocumentId == documentId);

        if (document == null)
        {
            return null;
        }

        // Unauthorized and nonexistent are deliberately indistinguishable, so that
        // enumerating identifiers reveals nothing about which documents exist.
        return await CanReadAsync(document, actingUserId) ? document : null;
    }

    public async Task<Stream?> OpenContentAsync(int documentId, int actingUserId)
    {
        var document = await GetByIdAsync(documentId, actingUserId);
        if (document == null)
        {
            return null;
        }

        var content = await _storage.DownloadAsync(document.StoragePath);
        if (content == null)
        {
            _logger.LogWarning(
                "Document {DocumentId} has no file at {StoragePath}.", documentId, document.StoragePath);
            return null;
        }

        await RecordActivityAsync(documentId, actingUserId, DocumentActions.Download);
        return content;
    }

    public async Task<bool> UpdateMetadataAsync(int documentId, DocumentMetadataUpdate update, int actingUserId)
    {
        var document = await _context.Documents.FindAsync(documentId);
        if (document == null || document.UploadedByUserId != actingUserId)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(update.Title) || !DocumentCategories.All.Contains(update.Category))
        {
            return false;
        }

        var tags = NormalizeTags(update.Tags, out var tagError);
        if (tagError != null)
        {
            return false;
        }

        document.Title = update.Title.Trim();
        document.Description = string.IsNullOrWhiteSpace(update.Description) ? null : update.Description.Trim();
        document.Category = update.Category;
        document.Tags = tags;
        document.UpdatedDate = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ReplaceFileAsync(int documentId, DocumentFileReplacement replacement, int actingUserId)
    {
        var document = await _context.Documents.FindAsync(documentId);
        if (document == null || document.UploadedByUserId != actingUserId)
        {
            return false;
        }

        var validation = _validation.Validate(
            replacement.OriginalFileName, replacement.ContentType, replacement.FileSizeBytes, replacement.Content);

        if (!validation.IsValid)
        {
            return false;
        }

        var supersededPath = document.StoragePath;
        var newPath = BuildStoragePath(actingUserId, document.ProjectId, replacement.OriginalFileName);

        replacement.Content.Position = 0;
        await _storage.UploadAsync(replacement.Content, newPath, replacement.ContentType);

        document.StoragePath = newPath;
        document.OriginalFileName = replacement.OriginalFileName;
        document.ContentType = replacement.ContentType;
        document.FileSizeBytes = replacement.FileSizeBytes;
        document.UpdatedDate = DateTime.UtcNow;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Replacing document {DocumentId} failed; removing {Path}.", documentId, newPath);
            await _storage.DeleteAsync(newPath);
            return false;
        }

        // Only once the row points at the new file is the old one safe to remove.
        await _storage.DeleteAsync(supersededPath);
        return true;
    }

    public async Task<bool> DeleteAsync(int documentId, int actingUserId)
    {
        var document = await _context.Documents
            .Include(d => d.Shares)
            .Include(d => d.Project)
            .FirstOrDefaultAsync(d => d.DocumentId == documentId);

        if (document == null || !await CanDeleteAsync(document, actingUserId))
        {
            return false;
        }

        var recipients = document.Shares.Select(sh => sh.SharedWithUserId).ToList();
        var title = document.Title;
        var storagePath = document.StoragePath;

        await RecordActivityAsync(documentId, actingUserId, DocumentActions.Delete);

        _context.Documents.Remove(document);
        await _context.SaveChangesAsync();

        await _storage.DeleteAsync(storagePath);

        foreach (var recipient in recipients)
        {
            await _notifications.CreateNotificationAsync(new Notification
            {
                UserId = recipient,
                Title = "Shared document deleted",
                Message = $"\"{title}\" was deleted by its owner and is no longer available.",
                Type = NotificationType.DocumentDeleted,
                Priority = NotificationPriority.Informational
            });
        }

        return true;
    }

    public async Task<bool> ShareAsync(int documentId, int recipientUserId, int actingUserId)
    {
        var document = await _context.Documents.FindAsync(documentId);
        if (document == null || document.UploadedByUserId != actingUserId)
        {
            return false;
        }

        if (recipientUserId == document.UploadedByUserId)
        {
            return false;
        }

        if (!await _context.Users.AnyAsync(u => u.UserId == recipientUserId))
        {
            return false;
        }

        var alreadyShared = await _context.DocumentShares.AnyAsync(sh =>
            sh.DocumentId == documentId && sh.SharedWithUserId == recipientUserId);

        if (alreadyShared)
        {
            return true;
        }

        _context.DocumentShares.Add(new DocumentShare
        {
            DocumentId = documentId,
            SharedWithUserId = recipientUserId,
            SharedByUserId = actingUserId
        });

        await _context.SaveChangesAsync();
        await RecordActivityAsync(documentId, actingUserId, DocumentActions.Share);

        await _notifications.CreateNotificationAsync(new Notification
        {
            UserId = recipientUserId,
            Title = "Document shared with you",
            Message = $"\"{document.Title}\" was shared with you.",
            Type = NotificationType.DocumentShared,
            Priority = NotificationPriority.Informational
        });

        return true;
    }

    public async Task<List<Document>> GetTaskDocumentsAsync(int taskItemId, int actingUserId)
    {
        var accessible = await AccessibleDocumentsAsync(actingUserId);

        return await accessible
            .Where(d => d.TaskItemId == taskItemId)
            .Include(d => d.UploadedByUser)
            .OrderByDescending(d => d.UploadedDate)
            .ToListAsync();
    }

    public async Task<List<Document>> GetRecentAsync(int actingUserId, int count) =>
        await _context.Documents
            .Where(d => d.UploadedByUserId == actingUserId)
            .Include(d => d.Project)
            .OrderByDescending(d => d.UploadedDate)
            .Take(count)
            .ToListAsync();

    public async Task<int> GetCountAsync(int actingUserId) =>
        await _context.Documents.CountAsync(d => d.UploadedByUserId == actingUserId);

    public async Task<DocumentReport?> GetReportAsync(int actingUserId)
    {
        if (!await IsAdministratorAsync(actingUserId))
        {
            return null;
        }

        var types = await _context.Documents
            .GroupBy(d => d.ContentType)
            .Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(10)
            .ToListAsync();

        var uploaders = await _context.Documents
            .GroupBy(d => d.UploadedByUser.DisplayName)
            .Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(10)
            .ToListAsync();

        var patterns = await _context.DocumentActivities
            .GroupBy(a => a.Action)
            .Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToListAsync();

        return new DocumentReport(
            types.Select(t => (t.Key, t.Count)).ToList(),
            uploaders.Select(u => (u.Key, u.Count)).ToList(),
            patterns.Select(p => (p.Key, p.Count)).ToList());
    }

    // The permission filter is composed into the query itself, so a document the user
    // may not see is never materialized.
    private async Task<IQueryable<Document>> AccessibleDocumentsAsync(int actingUserId)
    {
        var user = await _context.Users.FindAsync(actingUserId);
        if (user == null)
        {
            return _context.Documents.Where(d => false);
        }

        if (user.Role == UserRole.Administrator)
        {
            return _context.Documents;
        }

        var department = user.Department;

        return _context.Documents.Where(d =>
            d.UploadedByUserId == actingUserId ||
            d.Shares.Any(sh => sh.SharedWithUserId == actingUserId) ||
            (d.ProjectId != null && d.Project!.ProjectManagerId == actingUserId) ||
            (d.ProjectId != null && d.Project!.ProjectMembers.Any(pm => pm.UserId == actingUserId)) ||
            (user.Role == UserRole.TeamLead && department != null &&
             d.UploadedByUser.Department == department));
    }

    private async Task<bool> CanReadAsync(Document document, int actingUserId)
    {
        var accessible = await AccessibleDocumentsAsync(actingUserId);
        return await accessible.AnyAsync(d => d.DocumentId == document.DocumentId);
    }

    private async Task<bool> CanDeleteAsync(Document document, int actingUserId)
    {
        if (document.UploadedByUserId == actingUserId)
        {
            return true;
        }

        return document.ProjectId != null &&
               await _context.Projects.AnyAsync(p =>
                   p.ProjectId == document.ProjectId && p.ProjectManagerId == actingUserId);
    }

    private async Task<bool> IsAdministratorAsync(int userId) =>
        await _context.Users.AnyAsync(u => u.UserId == userId && u.Role == UserRole.Administrator);

    private async Task RecordActivityAsync(int documentId, int userId, string action)
    {
        _context.DocumentActivities.Add(new DocumentActivity
        {
            DocumentId = documentId,
            UserId = userId,
            Action = action
        });

        await _context.SaveChangesAsync();
    }

    private static async Task<DocumentPage> PageAsync(IQueryable<Document> source, DocumentQuery query)
    {
        source = ApplyFilters(source, query);

        var total = await source.CountAsync();

        var page = Math.Max(1, query.Page);
        var pageSize = query.PageSize <= 0 ? 25 : query.PageSize;

        var items = await ApplySort(source, query)
            .Include(d => d.Project)
            .Include(d => d.UploadedByUser)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new DocumentPage(items, total, page, pageSize);
    }

    private static IQueryable<Document> ApplyFilters(IQueryable<Document> source, DocumentQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            source = source.Where(d => d.Category == query.Category);
        }

        if (query.ProjectId.HasValue)
        {
            source = source.Where(d => d.ProjectId == query.ProjectId);
        }

        if (query.FromDate.HasValue)
        {
            source = source.Where(d => d.UploadedDate >= query.FromDate.Value);
        }

        if (query.ToDate.HasValue)
        {
            source = source.Where(d => d.UploadedDate <= query.ToDate.Value);
        }

        return source;
    }

    private static IQueryable<Document> ApplySort(IQueryable<Document> source, DocumentQuery query) =>
        (query.SortBy, query.Descending) switch
        {
            (DocumentSortColumn.Title, true) => source.OrderByDescending(d => d.Title),
            (DocumentSortColumn.Title, false) => source.OrderBy(d => d.Title),
            (DocumentSortColumn.Category, true) => source.OrderByDescending(d => d.Category),
            (DocumentSortColumn.Category, false) => source.OrderBy(d => d.Category),
            (DocumentSortColumn.FileSize, true) => source.OrderByDescending(d => d.FileSizeBytes),
            (DocumentSortColumn.FileSize, false) => source.OrderBy(d => d.FileSizeBytes),
            (_, false) => source.OrderBy(d => d.UploadedDate),
            _ => source.OrderByDescending(d => d.UploadedDate)
        };

    private async Task<bool> IsProjectMemberAsync(int projectId, int userId) =>
        await _context.Projects.AnyAsync(p =>
            p.ProjectId == projectId &&
            (p.ProjectManagerId == userId || p.ProjectMembers.Any(pm => pm.UserId == userId)));

    private async Task NotifyProjectMembersAsync(Document document, int actingUserId)
    {
        var recipients = await _context.ProjectMembers
            .Where(pm => pm.ProjectId == document.ProjectId && pm.UserId != actingUserId)
            .Select(pm => pm.UserId)
            .ToListAsync();

        foreach (var userId in recipients)
        {
            await _notifications.CreateNotificationAsync(new Notification
            {
                UserId = userId,
                Title = "New project document",
                Message = $"\"{document.Title}\" was added to a project you belong to.",
                Type = NotificationType.DocumentAddedToProject,
                Priority = NotificationPriority.Informational
            });
        }
    }

    // The storage path is derived entirely from generated values - no part of the
    // user-supplied file name reaches the filesystem.
    private static string BuildStoragePath(int userId, int? projectId, string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        var scope = projectId.HasValue ? projectId.Value.ToString() : "personal";
        return $"{userId}/{scope}/{Guid.NewGuid()}{extension}";
    }

    private static string? NormalizeTags(string? raw, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var tags = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();

        if (tags.Count > MaxTags)
        {
            error = $"A document can have at most {MaxTags} tags.";
            return null;
        }

        if (tags.Any(t => t.Length > MaxTagLength))
        {
            error = $"Each tag must be {MaxTagLength} characters or fewer.";
            return null;
        }

        return string.Join(",", tags);
    }
}
