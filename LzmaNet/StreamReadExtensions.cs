// SPDX-License-Identifier: 0BSD

namespace LzmaNet;

/// <summary>
/// Stream reading helpers shared by the format readers.
/// </summary>
internal static class StreamReadExtensions
{
    /// <summary>
    /// Fills <paramref name="buffer"/> completely, treating a short read as
    /// truncated input.
    /// </summary>
    /// <remarks>
    /// <see cref="Stream.ReadExactly(Span{byte})"/> does the same job but reports
    /// truncation as <see cref="EndOfStreamException"/>; every caller here needs
    /// it surfaced as <see cref="LzmaDataErrorException"/> so a truncated
    /// container is indistinguishable from any other corrupt input.
    /// </remarks>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="truncatedMessage">Message for the truncation exception.</param>
    /// <exception cref="LzmaDataErrorException">The stream ended early.</exception>
    public static void ReadExact(this Stream stream, Span<byte> buffer, string truncatedMessage)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
                throw new LzmaDataErrorException(truncatedMessage);
            offset += read;
        }
    }

    /// <inheritdoc cref="ReadExact(Stream, Span{byte}, string)"/>
    public static async ValueTask ReadExactAsync(this Stream stream, Memory<byte> buffer,
        string truncatedMessage, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new LzmaDataErrorException(truncatedMessage);
            offset += read;
        }
    }
}
