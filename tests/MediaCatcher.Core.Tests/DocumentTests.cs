using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MediaCatcher.Core.Document;
using MediaCatcher.Core.Models;
using MediaCatcher.Core.Routing;

namespace MediaCatcher.Core.Tests;

public class DocumentRoutingTests
{
    [Theory]
    [InlineData("https://anyflip.com/abc/def", DownloadEngineKind.Document)]
    [InlineData("https://online.fliphtml5.com/abc/def", DownloadEngineKind.Document)]
    [InlineData("https://www.anyflip.com/x/y", DownloadEngineKind.Document)]
    [InlineData("https://example.com/book.pdf", DownloadEngineKind.Http)]
    public void EngineRouter_RoutesDocumentHosts(string url, DownloadEngineKind expected)
    {
        Assert.Equal(expected, EngineRouter.Choose(url));
    }

    [Fact]
    public void EngineRouter_ExplicitDocumentEngineWins()
    {
        Assert.Equal(
            DownloadEngineKind.Document,
            EngineRouter.Choose("https://example.com/book", explicitEngine: "document"));
    }
}

public class FlipbookParserTests
{
    [Fact]
    public void ExtractJsonObject_StripsJsAssignment()
    {
        var json = FlipbookParser.ExtractJsonObject("window.jscfg = {\"title\":\"Book\",\"x\":1};");
        Assert.Equal("{\"title\":\"Book\",\"x\":1}", json);
    }

    [Fact]
    public void ExtractJsonObject_HandlesNestedBraces()
    {
        var json = FlipbookParser.ExtractJsonObject("var c = {\"a\":{\"b\":[1,2,3]}};");
        Assert.Equal("{\"a\":{\"b\":[1,2,3]}}", json);
    }

    [Fact]
    public void ExtractPageUrls_ReadsFliphtml5Pages()
    {
        const string config = """
            {"title":"My Book","fliphtml5_pages":[
              {"n":["https://cdn.example.com/files/page/1.jpg"]},
              {"n":["https://cdn.example.com/files/page/2.jpg"]}
            ]}
            """;
        var pages = FlipbookParser.ExtractPageUrls(config, new Uri("https://online.fliphtml5.com/abc/"));
        Assert.Equal(2, pages.Count);
        Assert.Equal("https://cdn.example.com/files/page/1.jpg", pages[0]);
        Assert.Equal("https://cdn.example.com/files/page/2.jpg", pages[1]);
    }

    [Fact]
    public void ExtractPageUrls_ResolvesRelativeUrls()
    {
        const string config = """
            {"pages":[{"n":["files/page/1.jpg"]},{"n":["files/page/2.jpg"]}]}
            """;
        var pages = FlipbookParser.ExtractPageUrls(config, new Uri("https://example.com/book/"));
        Assert.Equal("https://example.com/book/files/page/1.jpg", pages[0]);
        Assert.Equal("https://example.com/book/files/page/2.jpg", pages[1]);
    }

    [Fact]
    public void ExtractPageUrls_ExcludesThumbnails()
    {
        const string config = """
            {"pages":[{"n":["https://cdn.example.com/thumb/1.jpg","https://cdn.example.com/files/page/1.jpg"]}]}
            """;
        var pages = FlipbookParser.ExtractPageUrls(config, new Uri("https://example.com/"));
        var result = Assert.Single(pages);
        Assert.Equal("https://cdn.example.com/files/page/1.jpg", result);
    }

    [Fact]
    public void ExtractTitle_FindsTitle()
    {
        var title = FlipbookParser.ExtractTitle("{\"title\":\"The Book Title\"}");
        Assert.Equal("The Book Title", title);
    }

    [Fact]
    public void FindConfigScriptUrl_ResolvesAbsoluteAndRelative()
    {
        var absolute = FlipbookParser.FindConfigScriptUrl(
            "<script src=\"https://cdn.example.com/config.js?v=1\"></script>",
            new Uri("https://example.com/book"));
        Assert.Equal("https://cdn.example.com/config.js?v=1", absolute);

        var relative = FlipbookParser.FindConfigScriptUrl(
            "<script src=\"config.js\"></script>",
            new Uri("https://example.com/book"));
        Assert.Equal("https://example.com/config.js", relative);
    }
}

public class PdfAssemblerTests
{
    [Fact]
    public void Build_ProducesValidPdfHeaderAndPageCount()
    {
        var pages = new[]
        {
            new PdfImageInput(2, 2, new byte[12], PdfImageKind.Rgb),
            new PdfImageInput(3, 1, new byte[9], PdfImageKind.Rgb)
        };

        var pdf = PdfAssembler.Build(pages);
        var text = Encoding.ASCII.GetString(pdf);

        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.Contains("/Count 2", text, StringComparison.Ordinal);
        Assert.Contains("startxref", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF", text.TrimEnd('\n', '\r'), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EmbedsJpegWithDctDecode()
    {
        var pages = new[] { new PdfImageInput(10, 10, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, PdfImageKind.Jpeg) };
        var pdf = PdfAssembler.Build(pages);
        var text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/Filter /DCTDecode", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsEmptyPages()
    {
        Assert.Throws<ArgumentException>(() => PdfAssembler.Build([]));
    }

    [Fact]
    public void ReadJpegDimensions_ReadsSofMarker()
    {
        var jpeg = new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x02, 0x00, 0x03, 0x03,
            0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00
        };

        var (width, height) = PdfAssembler.ReadJpegDimensions(jpeg);
        Assert.Equal(3, width);
        Assert.Equal(2, height);
    }
}

public class PngDecoderTests
{
    [Fact]
    public void Decode_RoundTripsRgbImage()
    {
        const int width = 2;
        const int height = 2;
        var rgb = new byte[]
        {
            255, 0, 0, 0, 255, 0,
            0, 0, 255, 255, 255, 255
        };

        var pngBytes = BuildPng(width, height, rgb);
        var decoded = PngDecoder.Decode(pngBytes);

        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(rgb, decoded.Rgb);
    }

    [Fact]
    public void Decode_RejectsNonPng()
    {
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(new byte[] { 1, 2, 3, 4 }));
    }

    private static byte[] BuildPng(int width, int height, byte[] rgb)
    {
        var stride = width * 3;
        var raw = new byte[height * (stride + 1)];
        for (var row = 0; row < height; row++)
        {
            raw[row * (stride + 1)] = 0;
            Array.Copy(rgb, row * stride, raw, row * (stride + 1) + 1, stride);
        }

        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            compressed = output.ToArray();
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;

        using var stream = new MemoryStream();
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WriteChunk(stream, "IHDR", ihdr);
        WriteChunk(stream, "IDAT", compressed);
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(typeBytes);
        stream.Write(data);
        stream.Write(new byte[4]);
    }
}
