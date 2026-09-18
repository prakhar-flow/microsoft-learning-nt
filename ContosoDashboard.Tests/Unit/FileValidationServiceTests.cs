using ContosoDashboard.Services;
using Xunit;

namespace ContosoDashboard.Tests.Unit;

public class FileValidationServiceTests
{
    private readonly FileValidationService _validator = new();

    private static MemoryStream Pdf() => new(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 });
    private static MemoryStream Png() => new(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    private static MemoryStream Jpeg() => new(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 });
    private static MemoryStream Docx() => new(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00 });

    [Fact]
    public void Accepts_a_valid_pdf()
    {
        var result = _validator.Validate("report.pdf", "application/pdf", 1024, Pdf());
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("photo.png", "image/png")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("notes.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public void Accepts_each_allowed_type(string fileName, string contentType)
    {
        Stream content = Path.GetExtension(fileName) switch
        {
            ".png" => Png(),
            ".jpg" => Jpeg(),
            _ => Docx()
        };

        Assert.True(_validator.Validate(fileName, contentType, 2048, content).IsValid);
    }

    [Fact]
    public void Rejects_a_file_over_the_25MB_limit()
    {
        var result = _validator.Validate("big.pdf", "application/pdf", 26 * 1024 * 1024, Pdf());

        Assert.False(result.IsValid);
        Assert.Contains("25 MB", result.Error);
    }

    [Fact]
    public void Accepts_a_file_exactly_at_the_limit()
    {
        var result = _validator.Validate(
            "edge.pdf", "application/pdf", FileValidationService.MaxFileSizeBytes, Pdf());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Rejects_a_zero_byte_file()
    {
        var result = _validator.Validate("empty.pdf", "application/pdf", 0, new MemoryStream());

        Assert.False(result.IsValid);
        Assert.Contains("empty", result.Error);
    }

    [Fact]
    public void Rejects_a_disallowed_extension()
    {
        var result = _validator.Validate("tool.exe", "application/octet-stream", 1024, Pdf());

        Assert.False(result.IsValid);
        Assert.Contains("Unsupported file type", result.Error);
    }

    [Fact]
    public void Rejects_an_executable_renamed_as_a_pdf()
    {
        var mzHeader = new MemoryStream(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 });

        var result = _validator.Validate("payload.pdf", "application/pdf", 1024, mzHeader);

        Assert.False(result.IsValid);
        Assert.Contains("do not match its extension", result.Error);
    }

    [Fact]
    public void Rejects_a_filename_over_255_characters()
    {
        var name = new string('a', 256) + ".pdf";

        var result = _validator.Validate(name, "application/pdf", 1024, Pdf());

        Assert.False(result.IsValid);
        Assert.Contains("255", result.Error);
    }
}
