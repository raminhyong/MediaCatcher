using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MediaCatcher.Core.Document;

public enum PdfImageKind
{
    Jpeg,
    Rgb
}

public sealed record PdfImageInput(int Width, int Height, byte[] Data, PdfImageKind Kind);

public static class PdfAssembler
{
    public static byte[] Build(IReadOnlyList<PdfImageInput> pages)
    {
        if (pages.Count == 0)
        {
            throw new ArgumentException("At least one page image is required.", nameof(pages));
        }

        var builder = new List<byte>();
        var offsets = new List<long>(pages.Count * 3 + 2);

        void WriteAscii(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            builder.AddRange(bytes);
        }

        void WriteBytes(ReadOnlySpan<byte> bytes) => builder.AddRange(bytes.ToArray());

        void WriteObject(int id, byte[] body)
        {
            offsets.Add(builder.Count);
            WriteAscii($"{id} 0 obj\n");
            WriteBytes(body);
            WriteAscii("\nendobj\n");
        }

        WriteAscii("%PDF-1.4\n");
        builder.AddRange(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });

        WriteObject(1, Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"));

        var kidReferences = string.Join(" ", Enumerable.Range(0, pages.Count).Select(index => $"{5 + index * 3} 0 R"));
        WriteObject(
            2,
            Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{kidReferences}] /Count {pages.Count} >>"));

        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            var imageId = 3 + index * 3;
            var contentId = 4 + index * 3;
            var pageId = 5 + index * 3;

            var imageBody = BuildImageObject(page);
            WriteObject(imageId, imageBody);

            var content = $"q\n{page.Width} 0 0 {page.Height} 0 0 cm\n/Im0 Do\nQ\n";
            WriteObject(contentId, Encoding.ASCII.GetBytes(content));

            var pageBody = Encoding.ASCII.GetBytes(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Width} {page.Height}] " +
                $"/Resources << /XObject << /Im0 {imageId} 0 R >> >> /Contents {contentId} 0 R >>");
            WriteObject(pageId, pageBody);
        }

        var xrefOffset = builder.Count;
        var objectCount = pages.Count * 3 + 2;

        WriteAscii("xref\n");
        WriteAscii($"0 {objectCount + 1}\n");
        WriteAscii("0000000000 65535 f \n");
        for (var index = 0; index < offsets.Count; index++)
        {
            WriteAscii($"{offsets[index]:D10} 00000 n \n");
        }

        WriteAscii($"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return builder.ToArray();
    }

    private static byte[] BuildImageObject(PdfImageInput page)
    {
        if (page.Kind == PdfImageKind.Jpeg)
        {
            var header =
                $"<< /Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Data.Length} >>\nstream\n";
            return Concat(Encoding.ASCII.GetBytes(header), page.Data, Encoding.ASCII.GetBytes("\nendstream"));
        }

        var compressed = Deflate(page.Data);
        var headerRgb =
            $"<< /Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} " +
            $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n";
        return Concat(Encoding.ASCII.GetBytes(headerRgb), compressed, Encoding.ASCII.GetBytes("\nendstream"));
    }

    private static byte[] Deflate(byte[] input)
    {
        using var output = new MemoryStream();
        using var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true);
        zlib.Write(input);
        zlib.Dispose();
        return output.ToArray();
    }

    private static byte[] Concat(byte[] first, byte[] second, byte[] third)
    {
        var result = new byte[first.Length + second.Length + third.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        Buffer.BlockCopy(third, 0, result, first.Length + second.Length, third.Length);
        return result;
    }

    public static (int Width, int Height) ReadJpegDimensions(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            throw new InvalidDataException("Not a JPEG image.");
        }

        var offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var marker = data[offset + 1];
            if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7)
            {
                offset += 2;
                continue;
            }

            if (offset + 4 > data.Length)
            {
                break;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..(offset + 4)]);
            if (length < 2 || offset + 2 + length > data.Length)
            {
                break;
            }

            var isSof = marker is >= 0xC0 and <= 0xCF &&
                        marker is not 0xC4 and not 0xC8 and not 0xCC;
            if (isSof)
            {
                if (offset + 9 > data.Length)
                {
                    break;
                }

                var height = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..(offset + 7)]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 7)..(offset + 9)]);
                return (width, height);
            }

            offset += 2 + length;
        }

        throw new InvalidDataException("JPEG header has no SOF marker.");
    }
}
