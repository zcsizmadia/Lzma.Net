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
        long startPos = stream.Position - 1; // -1 for the 0x00 indicator already read

        using var indexData = new MemoryStream();
        indexData.WriteByte(0x00);

        ulong numRecords = ReadMultibyteIntAndCopy(stream, indexData);

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
        if (!Crc32.Verify(indexData.ToArray().AsSpan(), crcBuf))
            throw new LzmaDataErrorException("XZ index CRC32 mismatch.");

        return stream.Position - startPos;
    }

    /// <summary>
    /// Writes the XZ index to the output stream.
    /// </summary>
    /// <param name="output">Output stream.</param>
    /// <param name="records">List of (unpaddedSize, uncompressedSize) tuples.</param>
    /// <returns>Total size of the index (including indicator, padding, CRC32).</returns>
    public static long WriteIndex(Stream output, IReadOnlyList<(long unpaddedSize, long uncompressedSize)> records)
    {
        long startPos = output.Position;

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
        byte[] indexBytes = indexData.ToArray();
        output.Write(indexBytes);

        // CRC32
        Span<byte> crc = stackalloc byte[4];
        Crc32.WriteLE(indexBytes.AsSpan(), crc);
        output.Write(crc);

        return output.Position - startPos;
    }

    private static ulong ReadMultibyteIntAndCopy(Stream stream, MemoryStream copy)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0) throw new LzmaDataErrorException("Unexpected end of XZ index.");
            copy.WriteByte((byte)b);
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
            if (shift > 63)
                throw new LzmaDataErrorException("Multibyte integer overflow in XZ index.");
        }
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
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
                throw new LzmaDataErrorException("Unexpected end of stream.");
            offset += read;
        }
    }
}
