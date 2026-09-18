namespace ContosoDashboard.Services;

public record FileValidationResult(bool IsValid, string? Error)
{
    public static FileValidationResult Success() => new(true, null);
    public static FileValidationResult Failure(string error) => new(false, error);
}

/// The seam where real anti-malware scanning belongs. The shipped implementation checks
/// extension, declared type, size and leading bytes only - it is not a malware scanner.
public interface IFileValidationService
{
    FileValidationResult Validate(string fileName, string contentType, long sizeBytes, Stream content);
}

public class FileValidationService : IFileValidationService
{
    public const long MaxFileSizeBytes = 25 * 1024 * 1024;
    public const int MaxFileNameLength = 255;

    private static readonly IReadOnlyDictionary<string, string> AllowedExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".ppt"] = "application/vnd.ms-powerpoint",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".txt"] = "text/plain",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png"
        };

    private static readonly string AllowedList =
        "PDF, Word, Excel, PowerPoint, text, JPEG and PNG files";

    public FileValidationResult Validate(string fileName, string contentType, long sizeBytes, Stream content)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FileValidationResult.Failure("A file must be selected.");
        }

        if (fileName.Length > MaxFileNameLength)
        {
            return FileValidationResult.Failure(
                $"The file name exceeds the {MaxFileNameLength} character limit.");
        }

        if (sizeBytes <= 0)
        {
            return FileValidationResult.Failure("The selected file is empty.");
        }

        if (sizeBytes > MaxFileSizeBytes)
        {
            return FileValidationResult.Failure(
                $"The file exceeds the 25 MB limit. This file is {sizeBytes / 1024d / 1024d:F1} MB.");
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension) || !AllowedExtensions.ContainsKey(extension))
        {
            return FileValidationResult.Failure($"Unsupported file type. Allowed types are {AllowedList}.");
        }

        if (!HasMatchingSignature(extension, content))
        {
            return FileValidationResult.Failure(
                "The file contents do not match its extension and cannot be accepted.");
        }

        return FileValidationResult.Success();
    }

    private static bool HasMatchingSignature(string extension, Stream content)
    {
        if (!content.CanSeek)
        {
            return true;
        }

        var origin = content.Position;
        var header = new byte[8];
        content.Position = 0;
        var read = content.Read(header, 0, header.Length);
        content.Position = origin;

        if (read < 4)
        {
            return false;
        }

        return extension.ToLowerInvariant() switch
        {
            ".pdf" => StartsWith(header, 0x25, 0x50, 0x44, 0x46),
            ".png" => StartsWith(header, 0x89, 0x50, 0x4E, 0x47),
            ".jpg" or ".jpeg" => StartsWith(header, 0xFF, 0xD8, 0xFF),
            ".docx" or ".xlsx" or ".pptx" => StartsWith(header, 0x50, 0x4B, 0x03, 0x04),
            ".doc" or ".xls" or ".ppt" => StartsWith(header, 0xD0, 0xCF, 0x11, 0xE0),
            _ => true
        };
    }

    private static bool StartsWith(byte[] header, params int[] expected) =>
        expected.Length <= header.Length &&
        !expected.Where((b, i) => header[i] != (byte)b).Any();
}
