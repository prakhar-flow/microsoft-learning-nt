namespace ContosoDashboard.Services;

public enum DocumentSortColumn
{
    UploadedDate,
    Title,
    Category,
    FileSize
}

public record DocumentQuery
{
    public DocumentSortColumn SortBy { get; init; } = DocumentSortColumn.UploadedDate;
    public bool Descending { get; init; } = true;
    public string? Category { get; init; }
    public int? ProjectId { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;

    public static DocumentQuery Default => new();
}

public record DocumentPage(IReadOnlyList<Models.Document> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public record DocumentMetadataUpdate(string Title, string? Description, string Category, string? Tags);

public record DocumentFileReplacement(
    string OriginalFileName,
    string ContentType,
    long FileSizeBytes,
    Stream Content);

public record DocumentReport(
    IReadOnlyList<(string ContentType, int Count)> MostUploadedTypes,
    IReadOnlyList<(string UserName, int Count)> MostActiveUploaders,
    IReadOnlyList<(string Action, int Count)> AccessPatterns);
