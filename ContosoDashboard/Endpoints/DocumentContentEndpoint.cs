using System.Net.Mime;
using System.Security.Claims;
using System.Text;
using ContosoDashboard.Services;

namespace ContosoDashboard.Endpoints;

public static class DocumentContentEndpoint
{
    public static void MapDocumentContentEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/documents/{id:int}/content", HandleAsync).RequireAuthorization();
    }

    private static async Task<IResult> HandleAsync(
        int id,
        string? disposition,
        ClaimsPrincipal principal,
        IDocumentService documentService)
    {
        var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out var userId))
        {
            return Results.Unauthorized();
        }

        var document = await documentService.GetByIdAsync(id, userId);
        if (document == null)
        {
            // Identical to the missing-file case below: an unauthorized caller cannot tell
            // whether the document exists.
            return Results.NotFound();
        }

        var content = await documentService.OpenContentAsync(id, userId);
        if (content == null)
        {
            return Results.NotFound();
        }

        var inline = string.Equals(disposition, "inline", StringComparison.OrdinalIgnoreCase);

        return Results.File(
            content,
            document.ContentType,
            fileDownloadName: inline ? null : SanitizeFileName(document.OriginalFileName),
            enableRangeProcessing: true);
    }

    // Keeps the user's own file name for the download while ensuring nothing in it can
    // break out of the header or reference a path.
    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            builder.Append(char.IsControl(c) || c == '"' || c == '\\' || c == '/' ? '_' : c);
        }

        var sanitized = builder.ToString().Trim();
        return string.IsNullOrEmpty(sanitized) ? "document" : sanitized;
    }
}
