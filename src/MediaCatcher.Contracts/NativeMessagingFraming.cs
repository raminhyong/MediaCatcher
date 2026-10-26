using System.Buffers.Binary;

namespace MediaCatcher.Contracts;

public static class NativeMessagingFraming
{
    public static async Task<byte[]?> ReadMessageAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var headerRead = await ReadExactlyOrEofAsync(input, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead != 4)
        {
            throw new InvalidDataException("Native messaging stream ended inside the length header.");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        if (length > Protocol.MaxNativeMessageBytes)
        {
            throw new InvalidDataException($"Native message exceeds the {Protocol.MaxNativeMessageBytes} byte limit.");
        }

        var payload = new byte[length];
        var payloadRead = await ReadExactlyOrEofAsync(input, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead != payload.Length)
        {
            throw new InvalidDataException("Native messaging stream ended inside the message body.");
        }

        return payload;
    }

    public static async Task WriteMessageAsync(
        Stream output,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > Protocol.MaxNativeMessageBytes)
        {
            throw new InvalidDataException($"Native message exceeds the {Protocol.MaxNativeMessageBytes} byte limit.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static Task WriteMessageAsync(Stream output, string json, CancellationToken cancellationToken) =>
        WriteMessageAsync(
            output,
            System.Text.Encoding.UTF8.GetBytes(json),
            cancellationToken);

    private static async Task<int> ReadExactlyOrEofAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return totalRead;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
