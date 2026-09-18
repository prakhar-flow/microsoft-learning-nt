namespace ContosoDashboard.Services;

public interface IFileStorageService
{
    Task<string> UploadAsync(Stream content, string storagePath, string contentType);
    Task<Stream?> DownloadAsync(string storagePath);
    Task DeleteAsync(string storagePath);
    Task<string> GetUrlAsync(string storagePath, TimeSpan expiration);
}

public class LocalFileStorageService : IFileStorageService
{
    private readonly string _rootPath;

    public LocalFileStorageService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration["DocumentStorage:RootPath"] ?? "Storage/uploads";
        _rootPath = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);
    }

    public async Task<string> UploadAsync(Stream content, string storagePath, string contentType)
    {
        var fullPath = ResolveFullPath(storagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var file = File.Create(fullPath);
        await content.CopyToAsync(file);

        return storagePath;
    }

    public Task<Stream?> DownloadAsync(string storagePath)
    {
        var fullPath = ResolveFullPath(storagePath);
        if (!File.Exists(fullPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = File.OpenRead(fullPath);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(string storagePath)
    {
        var fullPath = ResolveFullPath(storagePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    // expiration is accepted and ignored locally so the signature survives a move to blob storage.
    public Task<string> GetUrlAsync(string storagePath, TimeSpan expiration) =>
        Task.FromResult($"/api/documents/content?path={Uri.EscapeDataString(storagePath)}");

    private string ResolveFullPath(string storagePath)
    {
        var combined = Path.GetFullPath(Path.Combine(_rootPath, storagePath));
        var root = Path.GetFullPath(_rootPath);

        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved storage path escapes the storage root.");
        }

        return combined;
    }
}
