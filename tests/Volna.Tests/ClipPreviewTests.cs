using Volna.Core;
using Xunit;

namespace Volna.Tests;

public class ClipPreviewTests
{
    [Fact]
    public void A_preview_requests_only_the_selected_remote_section()
    {
        var args = ClipPreview.Arguments("https://example.org/film", 3600, 3630, "preview-cache", "", Array.Empty<string>());
        Assert.Equal("*3600-3630", args[args.IndexOf("--download-sections") + 1]);
        Assert.Equal("preview-cache", args[args.IndexOf("--paths") + 1]);
        Assert.Contains("--force-keyframes-at-cuts", args);
        Assert.DoesNotContain("--write-subs", args);
        Assert.DoesNotContain("--embed-thumbnail", args);
        Assert.Equal("https://example.org/film", args[^1]);
    }

    [Fact]
    public void A_long_selection_has_a_bounded_preview()
    {
        var args = ClipPreview.Arguments("https://example.org/film", 3600, 7200, "preview-cache", "", Array.Empty<string>());
        Assert.Equal("*3600-3720", args[args.IndexOf("--download-sections") + 1]);
        Assert.Equal(3630, ClipPreview.EndFor(3600, 3630));
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(10, 9)]
    [InlineData(10, 10)]
    [InlineData(double.NaN, 10)]
    [InlineData(0, double.PositiveInfinity)]
    public void Invalid_preview_ranges_are_rejected(double start, double end) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipPreview.EndFor(start, end));

    [Fact]
    public void Local_files_cannot_be_passed_as_remote_preview_sources() =>
        Assert.Throws<ArgumentException>(() => ClipPreview.Arguments("file:///private.txt", 0, 10, "preview-cache", "", Array.Empty<string>()));
}
