using MediaCatcher.Core.Models;
using MediaCatcher.Core.Routing;
using MediaCatcher.Core.Utils;

namespace MediaCatcher.Core.Tests;

public class FilenamePolicyTests
{
    [Theory]
    [InlineData("hello.mp4", "hello.mp4")]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j.mp4", "a_b_c_d_e_f_g_h_i_j.mp4")]
    [InlineData("  .hidden..mp4  ", "hidden_.mp4")]
    [InlineData("ชื่อไฟล์ทดสอบ.mp4", "ชื่อไฟล์ทดสอบ.mp4")]
    public void Sanitize_CleansInvalidCharacters(string input, string expected)
    {
        Assert.Equal(expected, FilenamePolicy.Sanitize(input));
    }

    [Fact]
    public void Sanitize_PrefixesReservedDeviceNames()
    {
        Assert.Equal("_CON.txt", FilenamePolicy.Sanitize("CON.txt"));
        Assert.Equal("_PRN.bin", FilenamePolicy.Sanitize("PRN"));
    }

    [Fact]
    public void Sanitize_TrimsToMaximumLength()
    {
        var name = new string('a', 300) + ".mp4";
        var sanitized = FilenamePolicy.Sanitize(name);
        Assert.True(sanitized.Length <= 140);
        Assert.EndsWith(".mp4", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureWithinRoot_RejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "mediacatcher-root");
        Assert.Throws<UnauthorizedAccessException>(() =>
            FilenamePolicy.EnsureWithinRoot(root, Path.Combine(root, "..", "escape.txt")));
    }

    [Fact]
    public void EnsureUniquePath_AddsSuffix()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mediacatcher-unique-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "file.txt"), "x");
            var unique = FilenamePolicy.EnsureUniquePath(directory, "file.txt");
            Assert.EndsWith("file (1).txt", unique, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public class ContentDispositionParserTests
{
    [Fact]
    public void ExtractsPlainFilename()
    {
        Assert.Equal(
            "report.pdf",
            ContentDispositionParser.ExtractFileName("attachment; filename=\"report.pdf\""));
    }

    [Fact]
    public void PrefersExtendedUtf8Filename()
    {
        var header = "attachment; filename=\"fallback.bin\"; filename*=UTF-8''%E0%B8%97%E0%B8%94%E0%B8%AA%E0%B8%AD%E0%B8%9A.bin";
        Assert.Equal("ทดสอบ.bin", ContentDispositionParser.ExtractFileName(header));
    }

    [Fact]
    public void ReturnsNullForMissingFilename()
    {
        Assert.Null(ContentDispositionParser.ExtractFileName("inline"));
        Assert.Null(ContentDispositionParser.ExtractFileName(null));
    }
}

public class CategoryAndRoutingTests
{
    [Theory]
    [InlineData("movie.MP4", MediaCategories.Video)]
    [InlineData("song.mp3", MediaCategories.Audio)]
    [InlineData("photo.webp", MediaCategories.Images)]
    [InlineData("manual.pdf", MediaCategories.Documents)]
    [InlineData("bundle.zip", MediaCategories.Archives)]
    [InlineData("setup.exe", MediaCategories.Programs)]
    [InlineData("file.xyz", MediaCategories.Other)]
    public void CategoryResolver_MapsExtensions(string fileName, string expected)
    {
        Assert.Equal(expected, CategoryResolver.FromFileName(fileName));
    }

    [Theory]
    [InlineData("https://example.com/file.mp4", DownloadEngineKind.Http)]
    [InlineData("https://example.com/live.m3u8", DownloadEngineKind.YtDlp)]
    [InlineData("https://example.com/manifest.mpd", DownloadEngineKind.YtDlp)]
    [InlineData("https://www.youtube.com/watch?v=abc", DownloadEngineKind.YtDlp)]
    [InlineData("https://youtu.be/abc", DownloadEngineKind.YtDlp)]
    public void EngineRouter_ChoosesExpectedEngine(string url, DownloadEngineKind expected)
    {
        Assert.Equal(expected, EngineRouter.Choose(url));
    }

    [Fact]
    public void EngineRouter_ExplicitEngineWins()
    {
        Assert.Equal(
            DownloadEngineKind.Http,
            EngineRouter.Choose("https://www.youtube.com/watch?v=abc", explicitEngine: "http"));
    }

    [Theory]
    [InlineData("file:///C:/test.mp4")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,hello")]
    [InlineData("blob:https://example.com/abc")]
    [InlineData("magnet:?xt=urn:btih:abc")]
    public void UrlPolicy_RejectsUnsafeSchemes(string url)
    {
        Assert.False(UrlPolicy.IsSupported(url));
    }

    [Fact]
    public void UrlPolicy_AcceptsHttpAndHttps()
    {
        Assert.True(UrlPolicy.IsSupported("http://example.com/a.bin"));
        Assert.True(UrlPolicy.IsSupported("https://example.com/a.bin"));
    }
}
