using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MediaCatcher.Core.Document;

public sealed record PngImage(int Width, int Height, byte[] Rgb);

public static class PngDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static PngImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < Signature.Length || !data[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG image.");
        }

        int width = 0;
        int height = 0;
        int bitDepth = 0;
        int colorType = 0;
        int interlace = 0;
        using var idat = new MemoryStream();

        var offset = Signature.Length;
        while (offset + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data[offset..(offset + 4)]);
            var type = Encoding.ASCII.GetString(data[(offset + 4)..(offset + 8)]);
            var payloadStart = offset + 8;
            var payloadEnd = payloadStart + length;

            if (length < 0 || payloadEnd + 4 > data.Length)
            {
                throw new InvalidDataException("Truncated PNG chunk.");
            }

            var payload = data[payloadStart..payloadEnd];
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(payload[0..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(payload[4..8]);
                    bitDepth = payload[8];
                    colorType = payload[9];
                    interlace = payload[12];
                    break;
                case "IDAT":
                    idat.Write(payload);
                    break;
                case "IEND":
                    return DecodePixels(width, height, bitDepth, colorType, interlace, idat.ToArray());
            }

            offset = payloadEnd + 4;
        }

        throw new InvalidDataException("PNG image ended before IEND.");
    }

    private static PngImage DecodePixels(int width, int height, int bitDepth, int colorType, int interlace, byte[] idat)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("Invalid PNG dimensions.");
        }

        if (interlace != 0)
        {
            throw new NotSupportedException("Interlaced PNG images are not supported.");
        }

        if (bitDepth != 8)
        {
            throw new NotSupportedException($"PNG bit depth {bitDepth} is not supported; only 8-bit images are supported.");
        }

        var bytesPerPixel = colorType switch
        {
            0 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new NotSupportedException($"PNG color type {colorType} is not supported.")
        };

        var stride = width * bytesPerPixel;
        var raw = Decompress(idat);
        var expected = height * (stride + 1);
        if (raw.Length < expected)
        {
            throw new InvalidDataException("PNG pixel data is truncated.");
        }

        var filtered = new byte[expected];
        Array.Copy(raw, filtered, expected);

        var pixels = Unfilter(filtered, height, stride, bytesPerPixel);
        return new PngImage(width, height, ToRgb(pixels, width, height, colorType, bytesPerPixel));
    }

    private static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Unfilter(byte[] filtered, int height, int stride, int bytesPerPixel)
    {
        var result = new byte[height * stride];
        var previous = new byte[stride];

        for (var row = 0; row < height; row++)
        {
            var filterType = filtered[row * (stride + 1)];
            var sourceStart = row * (stride + 1) + 1;
            var targetStart = row * stride;

            for (var index = 0; index < stride; index++)
            {
                var raw = filtered[sourceStart + index];
                var left = index >= bytesPerPixel ? result[targetStart + index - bytesPerPixel] : 0;
                var up = previous[index];
                var upLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : 0;

                var value = filterType switch
                {
                    0 => raw,
                    1 => (byte)(raw + left),
                    2 => (byte)(raw + up),
                    3 => (byte)(raw + ((left + up) / 2)),
                    4 => (byte)(raw + Paeth(left, up, upLeft)),
                    _ => throw new InvalidDataException($"Unknown PNG filter type {filterType}.")
                };

                result[targetStart + index] = value;
                previous[index] = value;
            }
        }

        return result;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var prediction = left + up - upLeft;
        var distanceLeft = Math.Abs(prediction - left);
        var distanceUp = Math.Abs(prediction - up);
        var distanceUpLeft = Math.Abs(prediction - upLeft);
        if (distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft)
        {
            return left;
        }

        return distanceUp <= distanceUpLeft ? up : upLeft;
    }

    private static byte[] ToRgb(byte[] pixels, int width, int height, int colorType, int bytesPerPixel)
    {
        var rgb = new byte[width * height * 3];
        var output = 0;

        for (var index = 0; index < pixels.Length; index += bytesPerPixel)
        {
            switch (colorType)
            {
                case 0:
                    var gray = pixels[index];
                    rgb[output] = gray;
                    rgb[output + 1] = gray;
                    rgb[output + 2] = gray;
                    break;
                case 2:
                    rgb[output] = pixels[index];
                    rgb[output + 1] = pixels[index + 1];
                    rgb[output + 2] = pixels[index + 2];
                    break;
                case 4:
                    var grayAlpha = pixels[index + 1];
                    rgb[output] = Blend(pixels[index], grayAlpha);
                    rgb[output + 1] = rgb[output];
                    rgb[output + 2] = rgb[output];
                    break;
                case 6:
                    var alpha = pixels[index + 3];
                    rgb[output] = Blend(pixels[index], alpha);
                    rgb[output + 1] = Blend(pixels[index + 1], alpha);
                    rgb[output + 2] = Blend(pixels[index + 2], alpha);
                    break;
            }

            output += 3;
        }

        return rgb;
    }

    private static byte Blend(byte channel, byte alpha) =>
        (byte)((channel * alpha + 255 * (255 - alpha)) / 255);
}
