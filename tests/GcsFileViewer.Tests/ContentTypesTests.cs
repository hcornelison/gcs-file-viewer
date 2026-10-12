using GcsFileViewer.Storage;

namespace GcsFileViewer.Tests;

public class ContentTypesTests
{
    [Theory]
    [InlineData("report.pdf", "application/pdf", "application/pdf")]
    [InlineData("data.parquet", "application/octet-stream", "PARQUET file")]
    [InlineData("data.csv", null, "CSV file")]
    [InlineData("logs/2026/app.log", "", "LOG file")]
    [InlineData("Makefile", null, "File")]
    [InlineData("Makefile", "application/octet-stream", "File")]
    public void Describe_prefers_a_specific_content_type_then_the_extension(string name, string? contentType, string expected) =>
        Assert.Equal(expected, ContentTypes.Describe(name, contentType));

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("photos/2026/IMG_1.JPG", "image/jpeg")]
    [InlineData("data.json", "application/json")]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("page.html", "text/html")]
    [InlineData("picture.png", "image/png")]
    public void Guess_knows_common_extensions(string name, string expected) =>
        Assert.Equal(expected, ContentTypes.Guess(name));

    [Theory]
    [InlineData("Makefile")]
    [InlineData("data.this-is-not-an-extension")]
    public void Guess_leaves_unknown_types_to_the_server(string name) =>
        Assert.Null(ContentTypes.Guess(name));
}
