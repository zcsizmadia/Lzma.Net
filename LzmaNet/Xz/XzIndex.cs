// SPDX-License-Identifier: 0BSD

using System.Buffers.Binary;

using LzmaNet.Check;

namespace LzmaNet.Xz;

/// <summary>
/// Reads and writes XZ index sections.
/// The index contains records of (unpadded size, uncompressed size) for each block.
/// </summary>
internal static class XzIndex
{
    /// <summary>
    /// Reads and validates the XZ index from the stream.
    /// </summary>
    /// <param name="stream">Input stream positioned at the start of the index (after the 0x00 indicator).</param>
    /// <param name="records">Receives the list of (unpaddedSize, uncompressedSize) records.</param>
    /// <returns>Size of the index in bytes (including the indicator byte).</returns>
    public static long ReadIndex(Stream stream, out List<(long unpaddedSize, long uncompressedSize)> records)
    {
        using var indexData = new MemoryStream();
        indexData.WriteByte(0x00);

        ulong numRecords = ReadMultibyteIntAndCopy(stream, indexData);
        if (numRecords > int.MaxValue)
            throw new LzmaDataErrorException("Too many records in XZ index.");

        records = new List<(long, long)>((int)Math.Min(numRecords, 1024));
        for (ulong i = 0; i < numRecords; i++)
        {
            long unpaddedSize = (long)ReadMultibyteIntAndCopy(stream, indexData);
            long uncompressedSize = (long)ReadMultibyteIntAndCopy(stream, indexData);
            records.Add((unpaddedSize, uncompressedSize));
        }

        // Padding to 4-byte alignment
        int indexContentSize = (int)indexData.Length;
        int paddedSize = ((indexContentSize + 3) / 4) * 4;
        int paddingSize = paddedSize - indexContentSize;
        for (int i = 0; i < paddingSize; i++)
        {
            int b = stream.ReadByte();
            if (b < 0) throw new LzmaDataErrorException("Unexpected end of XZ index.");
            if (b != 0) throw new LzmaDataErrorException("Non-zero padding in XZ index.");
            indexData.WriteByte((byte)b);
        }

        // Read CRC32
        Span<byte> crcBuf = stackalloc byte[4];
        ReadExact(stream, crcBuf);

        // Verify CRC32
        if (!Crc32.Verify(indexData.GetBuffer().AsSpan(0, (int)indexData.Length), crcBuf))
            throw new LzmaDataErrorException("XZ index CRC32 mismatch.");

        return indexData.Length + crcBuf.Length;
    }

    /// <summary>
    /// Asynchronously reads and validates the XZ index from the stream.
    /// </summary>
    public static async Task<(long Size, List<(long unpaddedSize, long uncompressedSize)> Records)>
        ReadIndexAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var indexData = new MemoryStream();
        indexData.WriteByte(0x00);

        ulong numRecords = await ReadMultibyteIntAndCopyAsync(stream, indexData, cancellationToken)
            .ConfigureAwait(false);
        if (numRecords > int.MaxValue)
            throw new LzmaDataErrorException("Too many records in XZ index.");

        var records = new List<(long, long)>((int)Math.Min(numRecords, 1024));
        for (ulong i = 0; i < numRecords; i++)
        {
            long unpaddedSize = (long)await ReadMultibyteIntAndCopyAsync(
                stream, indexData, cancellationToken).ConfigureAwait(false);
            long uncompressedSize = (long)await ReadMultibyteIntAndCopyAsync(
                stream, indexData, cancellationToken).ConfigureAwait(false);
            records.Add((unpaddedSize, uncompressedSize));
        }

        int paddingSize = (4 - (int)(indexData.Length & 3)) & 3;
        byte[] oneByte = new byte[1];
        for (int i = 0; i < paddingSize; i++)
        {
            await ReadExactAsync(stream, oneByte, cancellationToken).ConfigureAwait(false);
            if (oneByte[0] != 0)
                throw new LzmaDataErrorException("Non-zero padding in XZ index.");
            indexData.WriteByte(0);
        }

        byte[] crc = new byte[4];
        await ReadExactAsync(stream, crc, cancellationToken).ConfigureAwait(false);
        if (!Crc32.Verify(indexData.GetBuffer().AsSpan(0, (int)indexData.Length), crc))
            throw new LzmaDataErrorException("XZ index CRC32 mismatch.");

        return (indexData.Length + crc.Length, records);
    }

    /// <summary>
    /// Writes the XZ index to the output stream.
    /// </summary>
    /// <param name="output">Output stream.</param>
    /// <param name="records">List of (unpaddedSize, uncompressedSize) tuples.</param>
    /// <returns>Total size of the index (including indicator, padding, CRC32).</returns>
    public static long WriteIndex(Stream output, IReadOnlyList<(long unpaddedSize, long uncompressedSize)> records)
    {
        using var indexData = new MemoryStream();

        // Index indicator
        indexData.WriteByte(0x00);

        // Number of records
        WriteMultibyteInt(indexData, (ulong)records.Count);

        // Records
        foreach (var (unpaddedSize, uncompressedSize) in records)
        {
            WriteMultibyteInt(indexData, (ulong)unpaddedSize);
            WriteMultibyteInt(indexData, (ulong)uncompressedSize);
        }

        // Padding
        int contentSize = (int)indexData.Length;
        int paddedSize = ((contentSize + 3) / 4) * 4;
        int paddingSize = paddedSize - contentSize;
        for (int i = 0; i < paddingSize; i++)
            indexData.WriteByte(0);

        // Write index data
        int indexLength = (int)indexData.Length;
        ReadOnlySpan<byte> indexBytes = indexData.GetBuffer().AsSpan(0, indexLength);
        output.Write(indexBytes);

        // CRC32
        Span<byte> crc = stackalloc byte[4];
        Crc32.WriteLE(indexBytes, crc);
        output.Write(crc);

        return indexLength + crc.Length;
    }

    /// <summary>
    /// Asynchronously writes the XZ index to the output stream.
    /// </summary>
    /// <param name="output">Output stream.</param>
    /// <param name="records">List of (unpaddedSize, uncompressedSize) tuples.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total size of the index (including indicator, padding, CRC32).</returns>
    public static async Task<long> WriteIndexAsync(Stream output,
        IReadOnlyList<(long unpaddedSize, long uncompressedSize)> records,
        CancellationToken cancellationToken = default)
    {
        using var indexData = new MemoryStream();

        // Index indicator
        indexData.WriteByte(0x00);

        // Number of records
        WriteMultibyteInt(indexData, (ulong)records.Count);

        // Records
        foreach (var (unpaddedSize, uncompressedSize) in records)
        {
            WriteMultibyteInt(indexData, (ulong)unpaddedSize);
            WriteMultibyteInt(indexData, (ulong)uncompressedSize);
        }

        // Padding
        int contentSize = (int)indexData.Length;
        int paddedSize = ((contentSize + 3) / 4) * 4;
        int paddingSize = paddedSize - contentSize;
        for (int i = 0; i < paddingSize; i++)
            indexData.WriteByte(0);

        // Write index data async
        int indexLength = (int)indexData.Length;
        ReadOnlyMemory<byte> indexBytes = indexData.GetBuffer().AsMemory(0, indexLength);
        await output.WriteAsync(indexBytes, cancellationToken).ConfigureAwait(false);

        // CRC32
        byte[] crc = new byte[4];
        Crc32.WriteLE(indexBytes.Span, crc);
        await output.WriteAsync(crc, cancellationToken).ConfigureAwait(false);

        return indexLength + crc.Length;
    }

    private static ulong ReadMultibyteIntAndCopy(Stream stream, MemoryStream copy)
    {
        ulong result = 0;
        int shift = 0;
        for (int byteIndex = 0; byteIndex < 9; byteIndex++)
        {
            int b = stream.ReadByte();
            if (b < 0) throw new LzmaDataErrorException("Unexpected end of XZ index.");
            copy.WriteByte((byte)b);
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (byteIndex > 0 && (b & 0x7F) == 0)
                    throw new LzmaDataErrorException("Non-canonical multibyte integer in XZ index.");
                return result;
            }
            shift += 7;
        }
        throw new LzmaDataErrorException("Multibyte integer overflow in XZ index.");
    }

    private static async ValueTask<ulong> ReadMultibyteIntAndCopyAsync(
        Stream stream, MemoryStream copy, CancellationToken cancellationToken)
    {
        byte[] oneByte = new byte[1];
        ulong result = 0;
        int shift = 0;
        for (int byteIndex = 0; byteIndex < 9; byteIndex++)
        {
            await ReadExactAsync(stream, oneByte, cancellationToken).ConfigureAwait(false);
            byte b = oneByte[0];
            copy.WriteByte(b);
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (byteIndex > 0 && (b & 0x7F) == 0)
                    throw new LzmaDataErrorException("Non-canonical multibyte integer in XZ index.");
                return result;
            }
            shift += 7;
        }
        throw new LzmaDataErrorException("Multibyte integer overflow in XZ index.");
    }

    private static void WriteMultibyteInt(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
        => stream.ReadExact(buffer, "Unexpected end of stream.");

    private static ValueTask ReadExactAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        => stream.ReadExactAsync(buffer, "Unexpected end of stream.", cancellationToken);
}