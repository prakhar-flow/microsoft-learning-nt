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
    Task<int> GetCountAsync(int actingUserId);
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

    public async Task<int> GetCountAsync(int actingUserId) =>
        await _context.Documents.CountAsync(d => d.UploadedByUserId == actingUserId);

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
