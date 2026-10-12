using System.Globalization;
using GcsFileViewer.Storage;

namespace GcsFileViewer.Tests;

public class BrowserItemTests
{
    public BrowserItemTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1 TB")]
    [InlineData(long.MaxValue, "8192 PB")]
    public void FormatSize_uses_the_largest_fitting_unit(long bytes, string expected) =>
        Assert.Equal(expected, BrowserItem.FormatSize(bytes));

    [Fact]
    public void SizeDisplay_is_blank_for_items_without_a_size() =>
        Assert.Equal("", FakeGcsService.Folder("b", "docs/").SizeDisplay);

    [Fact]
    public void Uri_points_at_the_object_inside_its_bucket() =>
        Assert.Equal("gs://b/docs/a.txt", FakeGcsService.File("b", "docs/a.txt").Uri);

    [Fact]
    public void Uri_of_a_bucket_is_the_bucket_root() =>
        Assert.Equal("gs://b/", FakeGcsService.Bucket("b").Uri);

    [Fact]
    public void SortKey_puts_folders_ahead_of_files_and_ignores_case()
    {
        BrowserItem[] items =
        [
            FakeGcsService.File("b", "apple.txt"),
            FakeGcsService.Folder("b", "Zebra/"),
            FakeGcsService.File("b", "Banana.txt"),
        ];

        var sorted = items.OrderBy(i => i.SortKey, StringComparer.Ordinal).Select(i => i.Name);

        Assert.Equal(["Zebra", "apple.txt", "Banana.txt"], sorted);
    }

    [Fact]
    public void IsContainer_is_true_for_buckets_and_folders_only()
    {
        Assert.True(FakeGcsService.Bucket("b").IsContainer);
        Assert.True(FakeGcsService.Folder("b", "docs/").IsContainer);
        Assert.False(FakeGcsService.File("b", "a.txt").IsContainer);
    }
}
