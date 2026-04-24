// SPDX-License-Identifier: 0BSD

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using LzmaNet.Check;
using LzmaNet.Filters;
using LzmaNet.Lzma2;

namespace LzmaNet.Xz;

/// <summary>
/// Reads and writes XZ blocks (block header + LZMA2 data + padding + check).
/// </summary>
internal static class XzBlock
{
    /// <summary>
    /// Reads and decodes a single XZ block from the stream.
    /// Returns false if an index indicator (0x00) is found instead of a block.
    /// </summary>
    public static bool ReadBlock(Stream stream, int checkType, Stream output,
                                  out long unpaddedSize, out long uncompressedSize)
    {
        unpaddedSize = 0;
        uncompressedSize = 0;

        // Read block header size byte (0 = index indicator)
        int headerSizeByte = stream.ReadByte();
        if (headerSizeByte < 0)
            throw new LzmaDataErrorException("Unexpected end of XZ stream.");
        if (headerSizeByte == 0)
            return false; // Index indicator

        int headerSize = (headerSizeByte + 1) * 4;
        byte[] headerBuf = ArrayPool<byte>.Shared.Rent(headerSize);
        try
        {
            headerBuf[0] = (byte)headerSizeByte;
            ReadExact(stream, headerBuf.AsSpan(1, headerSize - 1));

            // Verify CRC32 of header
            int headerDataLen = headerSize - 4;
            if (!Crc32.Verify(headerBuf.AsSpan(0, headerDataLen),
                              headerBuf.AsSpan(headerDataLen, 4)))
            {
                throw new LzmaDataErrorException("XZ block header CRC32 mismatch.");
            }

            // Parse block header
            int pos = 1;
            byte blockFlags = headerBuf[pos++];
            int numFilters = (blockFlags & 0x03) + 1;
            bool hasCompressedSize = (blockFlags & 0x40) != 0;
            bool hasUncompressedSize = (blockFlags & 0x80) != 0;

            // Check reserved bits (bits 2-5 must be 0)
            if ((blockFlags & 0x3C) != 0)
                throw new LzmaDataErrorException("Reserved bits set in XZ block flags.");

            long compressedSizeField = 0;
            long uncompSizeField = 0;

            if (hasCompressedSize)
                compressedSizeField = (long)ReadMultibyteInt(headerBuf, ref pos);

            if (hasUncompressedSize)
                uncompSizeField = (long)ReadMultibyteInt(headerBuf, ref pos);

            // Read all filters
            var filterInfos = new (ulong id, byte[] props)[numFilters];
            int lzma2DictSize = 0;
            for (int f = 0; f < numFilters; f++)
            {
                ulong filterId = ReadMultibyteInt(headerBuf, ref pos);
                int filterPropsSize = (int)ReadMultibyteInt(headerBuf, ref pos);

                byte[] filterProps = new byte[filterPropsSize];
                headerBuf.AsSpan(pos, filterPropsSize).CopyTo(filterProps);
                pos += filterPropsSize;

                if (!FilterFactory.IsSupported(filterId))
                    throw new LzmaException($"Unsupported XZ filter: 0x{filterId:X}.");

                filterInfos[f] = (filterId, filterProps);
            }

            // Last filter must be LZMA2
            if (filterInfos[numFilters - 1].id != XzConstants.FilterIdLzma2)
                throw new LzmaException("Last filter in XZ block must be LZMA2.");

            // Decode LZMA2 dict size from last filter's properties
            var lzma2Props = filterInfos[numFilters - 1].props;
            if (lzma2Props.Length != 1)
                throw new LzmaDataErrorException("Invalid LZMA2 filter properties size.");
            lzma2DictSize = Lzma2Encoder.DecodeDictSize(lzma2Props[0]);

            // Create BCJ/Delta filters for non-LZMA2 filters
            IBcjFilter[]? bcjFilters = null;
            if (numFilters > 1)
            {
                bcjFilters = new IBcjFilter[numFilters - 1];
                for (int f = 0; f < numFilters - 1; f++)
                    bcjFilters[f] = FilterFactory.Create(filterInfos[f].id, filterInfos[f].props);
            }

            // Remaining bytes in header should be zero padding
            for (int i = pos; i < headerDataLen; i++)
            {
                if (headerBuf[i] != 0)
                    throw new LzmaDataErrorException("Non-zero padding in XZ block header.");
            }

            // Read compressed data
            long compDataSize;
            byte[] compressedData;
            byte[]? compressedRented = null;

            if (hasCompressedSize)
            {
                compDataSize = compressedSizeField;
                compressedRented = ArrayPool<byte>.Shared.Rent((int)compDataSize);
                compressedData = compressedRented;
                ReadExact(stream, compressedData.AsSpan(0, (int)compDataSize));
            }
            else
            {
                // No compressed size in header — read using CountingStream to track bytes consumed
                compDataSize = ReadCompressedDataWithoutSize(stream, lzma2DictSize,
                    hasUncompressedSize ? uncompSizeField : -1,
                    bcjFilters, checkType, output,
                    out uncompressedSize, out unpaddedSize, headerSize);
                return true; // Already handled output, padding, and check
            }

            try
            {
                // Decode LZMA2
                byte[] decompBuf = DecodeLzma2(compressedData.AsMemory(0, (int)compDataSize),
                    lzma2DictSize, hasUncompressedSize, uncompSizeField);
                int decompressed = hasUncompressedSize ? (int)uncompSizeField : decompBuf.Length;

                try
                {
                    // Apply BCJ filters in reverse order (decode direction)
                    if (bcjFilters != null)
                    {
                        for (int f = bcjFilters.Length - 1; f >= 0; f--)
                            bcjFilters[f].Decode(decompBuf.AsSpan(0, decompressed), 0);
                    }

                    output.Write(decompBuf.AsSpan(0, decompressed));
                    uncompressedSize = decompressed;

                    // Padding to 4-byte alignment
                    int paddingSize = (4 - (int)(compDataSize % 4)) % 4;
                    if (paddingSize > 0)
                    {
                        Span<byte> padBuf = stackalloc byte[paddingSize];
                        ReadExact(stream, padBuf);
                        for (int i = 0; i < paddingSize; i++)
                        {
                            if (padBuf[i] != 0)
                                throw new LzmaDataErrorException("Non-zero padding after XZ block data.");
                        }
                    }

                    // Read and verify check
                    int checkSize = XzConstants.GetCheckSize(checkType);
                    if (checkSize > 0)
                    {
                        Span<byte> checkBuf = stackalloc byte[64];
                        checkBuf = checkBuf[..checkSize];
                        ReadExact(stream, checkBuf);
                        VerifyCheck(checkType, decompBuf.AsSpan(0, decompressed), checkBuf);
                    }

                    unpaddedSize = headerSize + compDataSize + checkSize;
                }
                finally
                {
                    if (decompBuf.Length > 0)
                        ArrayPool<byte>.Shared.Return(decompBuf);
                }
            }
            finally
            {
                if (compressedRented != null)
                    ArrayPool<byte>.Shared.Return(compressedRented);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuf);
        }

        return true;
    }

    /// <summary>
    /// Decodes LZMA2 data with automatic buffer growth when uncompressed size is unknown.
    /// Returns a buffer from ArrayPool that must be returned by the caller.
    /// </summary>
    private static byte[] DecodeLzma2(ReadOnlyMemory<byte> compressedData, int dictSize,
        bool hasUncompressedSize, long uncompSizeField)
    {
        if (hasUncompressedSize)
        {
            int size = (int)uncompSizeField;
            byte[] buf = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
            using var decoder = new Lzma2Decoder(dictSize);
            int written = decoder.Decode(compressedData, buf.AsSpan());
            if (written != size)
                throw new LzmaDataErrorException("XZ block uncompressed size mismatch.");
            return buf;
        }
        else
        {
            // Unknown uncompressed size — use growable buffer
            int capacity = Math.Max(compressedData.Length * 8, 65536);
            byte[] buf = ArrayPool<byte>.Shared.Rent(capacity);
            try
            {
                using var decoder = new Lzma2Decoder(dictSize);
                int written = decoder.Decode(compressedData, buf.AsSpan());
                return buf;
            }
            catch (IndexOutOfRangeException)
            {
                // Buffer too small — retry with larger buffer
                ArrayPool<byte>.Shared.Return(buf);
                capacity *= 4;
                buf = ArrayPool<byte>.Shared.Rent(capacity);
                using var decoder2 = new Lzma2Decoder(dictSize);
                decoder2.Decode(compressedData, buf.AsSpan());
                return buf;
            }
        }
    }

    /// <summary>
    /// Handles the case where compressed size is not in the block header.
    /// Reads bytes one-at-a-time from stream, feeding them to LZMA2 decoder.
    /// Falls back to reading all remaining data and using the LZMA2 end marker.
    /// </summary>
    private static long ReadCompressedDataWithoutSize(Stream stream, int dictSize,
        long expectedUncompSize, IBcjFilter[]? bcjFilters, int checkType,
        Stream output, out long uncompressedSize, out long unpaddedSize, int headerSize)
    {
        // We must read the compressed data without knowing its size.
        // LZMA2 has an explicit end marker (control byte 0x00), so we read
        // all remaining compressed+index+footer data and rely on the decoder.
        // This is only correct for single-block streams without further blocks.
        // We read in chunks and look for the LZMA2 end pattern.
        using var compMs = new MemoryStream();
        byte[] readBuf = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int bytesRead;
            while ((bytesRead = stream.Read(readBuf, 0, readBuf.Length)) > 0)
                compMs.Write(readBuf, 0, bytesRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuf);
        }

        byte[] allData = compMs.ToArray();

        // Try progressively larger slices of allData as LZMA2 input.
        // The LZMA2 decoder will stop at the 0x00 end marker and tell us how much it consumed.
        using var decoder = new Lzma2Decoder(dictSize);
        long knownUncompSize = expectedUncompSize >= 0
            ? expectedUncompSize
            : TryGetTotalUncompressedSize(allData, checkType);
        int capacity = knownUncompSize > 0 && knownUncompSize <= int.MaxValue
            ? (int)knownUncompSize
            : Math.Max(allData.Length * 4, 65536);
        byte[] decompBuf = ArrayPool<byte>.Shared.Rent(capacity);
        int decompressed;
        try
        {
            decompressed = decoder.DecodeWithConsumed(allData.AsMemory(), decompBuf.AsSpan(), out int consumed);

            if (expectedUncompSize >= 0 && decompressed != (int)expectedUncompSize)
                throw new LzmaDataErrorException("XZ block uncompressed size mismatch.");

            // Apply BCJ filters in reverse
            if (bcjFilters != null)
            {
                for (int f = bcjFilters.Length - 1; f >= 0; f--)
                    bcjFilters[f].Decode(decompBuf.AsSpan(0, decompressed), 0);
            }

            output.Write(decompBuf.AsSpan(0, decompressed));
            uncompressedSize = decompressed;

            // The remaining bytes after consumed are: padding + check + index + footer + possible next streams
            long compDataSize = consumed;

            // Read padding from remaining data
            int remainPos = consumed;
            int paddingSize = (4 - (int)(compDataSize % 4)) % 4;
            for (int i = 0; i < paddingSize; i++)
            {
                if (remainPos >= allData.Length)
                    throw new LzmaDataErrorException("Unexpected end of stream.");
                if (allData[remainPos++] != 0)
                    throw new LzmaDataErrorException("Non-zero padding after XZ block data.");
            }

            // Read and verify check
            int checkSize = XzConstants.GetCheckSize(checkType);
            if (checkSize > 0)
            {
                if (remainPos + checkSize > allData.Length)
                    throw new LzmaDataErrorException("Unexpected end of stream.");
                VerifyCheck(checkType, decompBuf.AsSpan(0, decompressed),
                    allData.AsSpan(remainPos, checkSize));
                remainPos += checkSize;
            }

            unpaddedSize = headerSize + compDataSize + checkSize;

            // Push remaining bytes back — create a new MemoryStream with leftover
            // and copy it back to the original stream if it supports seeking
            if (remainPos < allData.Length && stream.CanSeek)
            {
                stream.Seek(-(allData.Length - remainPos), SeekOrigin.Current);
            }

            return compDataSize;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(decompBuf);
        }
    }

    /// <summary>
    /// Writes a single XZ block (header + LZMA2 data + padding + check).
    /// </summary>
    /// <returns>Tuple of (unpadded size, uncompressed size) for the index.</returns>
    public static (long unpaddedSize, long uncompressedSize) WriteBlock(
        Stream output, ReadOnlyMemory<byte> uncompressedData,
        Lzma2Encoder encoder, int checkType)
    {
        long blockStart = output.Position;

        // Encode LZMA2 data to memory
        using var lzma2Stream = new MemoryStream();
        encoder.Encode(uncompressedData, lzma2Stream);
        int compressedLength = (int)lzma2Stream.Length;
        var compressedData = lzma2Stream.GetBuffer().AsSpan(0, compressedLength);

        // Build block header
        //   1 byte: header size / 4 - 1
        //   1 byte: block flags (1 filter, has compressed size, has uncompressed size)
        //   VLI: compressed size
        //   VLI: uncompressed size
        //   VLI: filter ID (0x21 = LZMA2)
        //   VLI: filter props size (1)
        //   1 byte: LZMA2 dict size byte
        //   padding to 4-byte boundary
        //   4 bytes: CRC32

        using var headerStream = new MemoryStream();
        headerStream.WriteByte(0); // placeholder for size byte

        byte blockFlags = 0x00;  // 1 filter = 0x00
        blockFlags |= 0x40;     // has compressed size
        blockFlags |= 0x80;     // has uncompressed size (bit 7 should be set)
        headerStream.WriteByte(blockFlags);

        WriteMultibyteInt(headerStream, (ulong)compressedLength);
        WriteMultibyteInt(headerStream, (ulong)uncompressedData.Length);

        // Filter: LZMA2
        WriteMultibyteInt(headerStream, XzConstants.FilterIdLzma2);
        WriteMultibyteInt(headerStream, 1); // props size
        headerStream.WriteByte(encoder.DictionarySizeByte);

        // Pad to 4-byte boundary (header includes size byte and CRC)
        int headerContentLen = (int)headerStream.Position;
        int totalHeaderSize = ((headerContentLen + 4 + 3) / 4) * 4; // round up to 4
        int paddingNeeded = totalHeaderSize - 4 - headerContentLen;
        for (int i = 0; i < paddingNeeded; i++)
            headerStream.WriteByte(0);

        // Set header size byte
        byte[] headerBytes = headerStream.ToArray();
        headerBytes[0] = (byte)(totalHeaderSize / 4 - 1);

        // Compute and append CRC32
        Span<byte> crc = stackalloc byte[4];
        Crc32.WriteLE(headerBytes.AsSpan(0, totalHeaderSize - 4), crc);

        output.Write(headerBytes);
        output.Write(crc);

        // Write compressed data
        output.Write(compressedData);

        // Padding for compressed data to 4-byte alignment
        int dataPadding = (4 - (compressedLength % 4)) % 4;
        for (int i = 0; i < dataPadding; i++)
            output.WriteByte(0);

        // Write check
        int checkSize = XzConstants.GetCheckSize(checkType);
        if (checkSize > 0)
        {
            WriteCheck(output, checkType, uncompressedData.Span, checkSize);
        }

        // Unpadded size = header + compressed data + check (no padding included)
        long unpaddedSize = totalHeaderSize + compressedLength + checkSize;
        return (unpaddedSize, uncompressedData.Length);
    }

    /// <summary>
    /// Asynchronously writes a single XZ block (header + LZMA2 data + padding + check).
    /// </summary>
    /// <returns>Tuple of (unpadded size, uncompressed size) for the index.</returns>
    public static async Task<(long unpaddedSize, long uncompressedSize)> WriteBlockAsync(
        Stream output, ReadOnlyMemory<byte> uncompressedData,
        Lzma2Encoder encoder, int checkType, CancellationToken cancellationToken = default)
    {
        long blockStart = output.Position;

        // Encode LZMA2 data to memory (CPU-bound, stays sync)
        using var lzma2Stream = new MemoryStream();
        encoder.Encode(uncompressedData, lzma2Stream);
        int compressedLength = (int)lzma2Stream.Length;

        // Build block header (same as sync version)
        using var headerStream = new MemoryStream();
        headerStream.WriteByte(0);

        byte blockFlags = 0x00;
        blockFlags |= 0x40;
        blockFlags |= 0x80;
        headerStream.WriteByte(blockFlags);

        WriteMultibyteInt(headerStream, (ulong)compressedLength);
        WriteMultibyteInt(headerStream, (ulong)uncompressedData.Length);

        WriteMultibyteInt(headerStream, XzConstants.FilterIdLzma2);
        WriteMultibyteInt(headerStream, 1);
        headerStream.WriteByte(encoder.DictionarySizeByte);

        int headerContentLen = (int)headerStream.Position;
        int totalHeaderSize = ((headerContentLen + 4 + 3) / 4) * 4;
        int paddingNeeded = totalHeaderSize - 4 - headerContentLen;
        for (int i = 0; i < paddingNeeded; i++)
            headerStream.WriteByte(0);

        byte[] headerBytes = headerStream.ToArray();
        headerBytes[0] = (byte)(totalHeaderSize / 4 - 1);

        byte[] crc = new byte[4];
        Crc32.WriteLE(headerBytes.AsSpan(0, totalHeaderSize - 4), crc);

        // Write header + CRC async
        await output.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(crc, cancellationToken).ConfigureAwait(false);

        // Write compressed data async
        await output.WriteAsync(
            lzma2Stream.GetBuffer().AsMemory(0, compressedLength), cancellationToken).ConfigureAwait(false);

        // Padding for compressed data to 4-byte alignment
        int dataPadding = (4 - (compressedLength % 4)) % 4;
        if (dataPadding > 0)
        {
            await output.WriteAsync(new byte[dataPadding], cancellationToken).ConfigureAwait(false);
        }

        // Write check
        int checkSize = XzConstants.GetCheckSize(checkType);
        if (checkSize > 0)
        {
            byte[] checkBuf = ComputeCheck(checkType, uncompressedData.Span, checkSize);
            await output.WriteAsync(checkBuf, cancellationToken).ConfigureAwait(false);
        }

        long unpaddedSize = totalHeaderSize + compressedLength + checkSize;
        return (unpaddedSize, uncompressedData.Length);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static byte[] ComputeCheck(int checkType, ReadOnlySpan<byte> data, int checkSize)
    {
        byte[] checkBuf = new byte[checkSize];

        switch (checkType)
        {
            case XzConstants.CheckNone:
                break;
            case XzConstants.CheckCrc32:
                Crc32.WriteLE(data, checkBuf);
                break;
            case XzConstants.CheckCrc64:
                Crc64.WriteLE(data, checkBuf);
                break;
            case XzConstants.CheckSha256:
                System.Security.Cryptography.SHA256.HashData(data, checkBuf);
                break;
        }

        return checkBuf;
    }

    private static void WriteCheck(Stream output, int checkType, ReadOnlySpan<byte> data, int checkSize)
    {
        Span<byte> checkBuf = stackalloc byte[64];
        checkBuf = checkBuf[..checkSize];

        switch (checkType)
        {
            case XzConstants.CheckNone:
                break;
            case XzConstants.CheckCrc32:
                Crc32.WriteLE(data, checkBuf);
                break;
            case XzConstants.CheckCrc64:
                Crc64.WriteLE(data, checkBuf);
                break;
            case XzConstants.CheckSha256:
                SHA256.HashData(data, checkBuf);
                break;
            default:
                checkBuf.Clear(); // Unknown check — write zeros
                break;
        }

        output.Write(checkBuf);
    }

    private static void VerifyCheck(int checkType, ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected)
    {
        switch (checkType)
        {
            case XzConstants.CheckNone:
                break;
            case XzConstants.CheckCrc32:
                if (!Crc32.Verify(data, expected))
                    throw new LzmaDataErrorException("XZ block CRC32 check failed.");
                break;
            case XzConstants.CheckCrc64:
                if (!Crc64.Verify(data, expected))
                    throw new LzmaDataErrorException("XZ block CRC64 check failed.");
                break;
            case XzConstants.CheckSha256:
                Span<byte> hash = stackalloc byte[32];
                SHA256.HashData(data, hash);
                if (!hash.SequenceEqual(expected[..32]))
                    throw new LzmaDataErrorException("XZ block SHA-256 check failed.");
                break;
            default:
                // Unknown check type — skip verification
                break;
        }
    }

    /// <summary>
    /// Tries to determine the total uncompressed size from the XZ index and footer
    /// embedded at the end of <paramref name="allData"/>.
    /// Returns -1 if the size cannot be determined.
    /// </summary>
    private static long TryGetTotalUncompressedSize(byte[] allData, int checkType)
    {
        const int footerSize = XzConstants.StreamFooterSize;
        if (allData.Length < footerSize)
            return -1;

        try
        {
            var footer = allData.AsSpan(allData.Length - footerSize, footerSize);
            long indexSize = XzHeader.ReadStreamFooter(footer, checkType);

            long indexStartLong = (long)allData.Length - footerSize - indexSize;
            if (indexStartLong < 0 || indexStartLong > int.MaxValue)
                return -1;

            int indexStart = (int)indexStartLong;
            if (allData[indexStart] != 0x00)
                return -1; // Not a valid index indicator

            using var ms = new MemoryStream(allData, writable: false);
            ms.Seek(indexStart + 1, SeekOrigin.Begin);
            XzIndex.ReadIndex(ms, out var records);

            long total = 0;
            foreach (var (_, uncompressedSize) in records)
                total += uncompressedSize;
            return total;
        }
        catch
        {
            return -1;
        }
    }

    private static ulong ReadMultibyteInt(ReadOnlySpan<byte> buf, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = buf[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
            if (shift > 63)
                throw new LzmaDataErrorException("Multibyte integer overflow.");
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
