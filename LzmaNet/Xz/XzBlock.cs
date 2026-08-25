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
    internal readonly record struct BlockBufferResult(
        bool HasBlock, byte[]? Buffer, int Length, long UnpaddedSize, long UncompressedSize);

    /// <summary>
    /// Reads and decodes a single XZ block from the stream.
    /// Returns false if an index indicator (0x00) is found instead of a block.
    /// </summary>
    public static bool ReadBlock(Stream stream, int checkType, Stream output,
                                  out long unpaddedSize, out long uncompressedSize)
    {
        bool hasBlock = ReadBlockToBuffer(stream, checkType, out byte[]? buffer,
            out int length, out unpaddedSize, out uncompressedSize);
        if (!hasBlock)
            return false;

        try
        {
            output.Write(buffer!.AsSpan(0, length));
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer!);
        }
    }

    /// <summary>
    /// Reads and decodes a block, transferring ownership of a pooled output buffer to the caller.
    /// <paramref name="maxOutputSize"/> caps the block's claimed uncompressed size;
    /// exceeding it throws <see cref="LzmaMemoryLimitException"/> BEFORE any large
    /// allocation happens (decompression-bomb protection).
    /// </summary>
    internal static bool ReadBlockToBuffer(Stream stream, int checkType,
        out byte[]? outputBuffer, out int outputLength,
        out long unpaddedSize, out long uncompressedSize,
        long maxOutputSize = long.MaxValue)
    {
        outputBuffer = null;
        outputLength = 0;
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
                compressedSizeField = ToSupportedSize(
                    ReadMultibyteInt(headerBuf.AsSpan(0, headerDataLen), ref pos),
                    "XZ block compressed size");

            if (hasUncompressedSize)
                uncompSizeField = ToSupportedSize(
                    ReadMultibyteInt(headerBuf.AsSpan(0, headerDataLen), ref pos),
                    "XZ block uncompressed size");

            // Read all filters
            var filterInfos = new (ulong id, byte[] props)[numFilters];
            int lzma2DictSize = 0;
            for (int f = 0; f < numFilters; f++)
            {
                ulong filterId = ReadMultibyteInt(headerBuf.AsSpan(0, headerDataLen), ref pos);
                ulong filterPropsSizeValue = ReadMultibyteInt(
                    headerBuf.AsSpan(0, headerDataLen), ref pos);
                if (filterPropsSizeValue > (ulong)(headerDataLen - pos))
                    throw new LzmaDataErrorException("XZ filter properties exceed the block header.");
                int filterPropsSize = (int)filterPropsSizeValue;

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
            (IBcjFilter Filter, uint StartPosition)[]? bcjFilters = null;
            if (numFilters > 1)
            {
                bcjFilters = new (IBcjFilter, uint)[numFilters - 1];
                for (int f = 0; f < numFilters - 1; f++)
                {
                    bcjFilters[f] = (
                        FilterFactory.Create(filterInfos[f].id, filterInfos[f].props),
                        FilterFactory.GetStartPosition(filterInfos[f].id, filterInfos[f].props));
                }
            }

            // Remaining bytes in header should be zero padding
            for (int i = pos; i < headerDataLen; i++)
            {
                if (headerBuf[i] != 0)
                    throw new LzmaDataErrorException("Non-zero padding in XZ block header.");
            }

            // Read compressed data
            long compDataSize;
            ReadOnlyMemory<byte> compressedData;
            byte[]? compressedRented = null;

            if (hasCompressedSize)
            {
                compDataSize = compressedSizeField;
                int compressedLength = (int)compDataSize;
                if (stream is MemoryStream memoryStream
                    && memoryStream.TryGetBuffer(out ArraySegment<byte> segment)
                    && memoryStream.Position <= memoryStream.Length - compressedLength)
                {
                    compressedData = segment.Array!.AsMemory(
                        segment.Offset + (int)memoryStream.Position, compressedLength);
                    memoryStream.Position += compressedLength;
                }
                else
                {
                    compressedRented = ReadToPooledBuffer(stream, compressedLength);
                    compressedData = compressedRented.AsMemory(0, compressedLength);
                }
            }
            else
            {
                // LZMA2 chunk headers let us stop exactly at the end marker.
                compDataSize = ReadCompressedDataWithoutSize(stream, lzma2DictSize,
                    hasUncompressedSize ? uncompSizeField : -1,
                    bcjFilters, checkType, out outputBuffer, out outputLength,
                    out uncompressedSize, out unpaddedSize, headerSize, maxOutputSize);
                return true; // Already handled output, padding, and check
            }

            try
            {
                // Decode LZMA2
                long decodedSize = GetLzma2UncompressedSize(compressedData.Span);
                if (hasUncompressedSize && decodedSize != uncompSizeField)
                    throw new LzmaDataErrorException("XZ block uncompressed size mismatch.");
                if (decodedSize > maxOutputSize)
                    throw new LzmaMemoryLimitException(
                        $"XZ block claims {decodedSize:N0} uncompressed bytes, exceeding the configured limit.");

                byte[] decompBuf = DecodeLzma2(
                    compressedData, lzma2DictSize, decodedSize,
                    out int decompressed);

                try
                {
                    // Apply BCJ filters in reverse order (decode direction)
                    if (bcjFilters != null)
                    {
                        for (int f = bcjFilters.Length - 1; f >= 0; f--)
                            bcjFilters[f].Filter.Decode(
                                decompBuf.AsSpan(0, decompressed), bcjFilters[f].StartPosition);
                    }

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
                    outputBuffer = decompBuf;
                    outputLength = decompressed;
                    uncompressedSize = decompressed;
                }
                finally
                {
                    if (!ReferenceEquals(decompBuf, outputBuffer) && decompBuf.Length > 0)
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
    /// Reads one complete raw (still compressed) block from the stream without decoding it.
    /// Returns false if an index indicator (0x00) is found instead of a block.
    /// The returned stream is positioned at 0 and exposable, so
    /// <see cref="ReadBlockToBuffer"/> can decode it without copying.
    /// Used by parallel block decompression to separate I/O from CPU-bound decoding.
    /// </summary>
    internal static bool ReadRawBlock(Stream stream, int checkType, out MemoryStream? rawBlock)
    {
        rawBlock = null;

        int headerSizeByte = stream.ReadByte();
        if (headerSizeByte < 0)
            throw new LzmaDataErrorException("Unexpected end of XZ stream.");
        if (headerSizeByte == 0)
            return false; // Index indicator

        var raw = new MemoryStream();
        try
        {
            int headerSize = (headerSizeByte + 1) * 4;
            byte[] header = ArrayPool<byte>.Shared.Rent(headerSize);
            long compressedSize;
            try
            {
                header[0] = (byte)headerSizeByte;
                ReadExact(stream, header.AsSpan(1, headerSize - 1));
                raw.Write(header, 0, headerSize);

                int headerDataLength = headerSize - 4;
                if (!Crc32.Verify(header.AsSpan(0, headerDataLength),
                        header.AsSpan(headerDataLength, 4)))
                    throw new LzmaDataErrorException("XZ block header CRC32 mismatch.");

                byte flags = header[1];
                if ((flags & 0x3C) != 0)
                    throw new LzmaDataErrorException("Reserved bits set in XZ block flags.");

                if ((flags & 0x40) != 0)
                {
                    int pos = 2;
                    compressedSize = ToSupportedSize(
                        ReadMultibyteInt(header.AsSpan(0, headerDataLength), ref pos),
                        "XZ block compressed size");
                    CopyExact(stream, raw, (int)compressedSize);
                }
                else
                {
                    long start = raw.Length;
                    CopyLzma2Stream(stream, raw);
                    compressedSize = raw.Length - start;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
            }

            int paddingSize = (4 - ((int)compressedSize & 3)) & 3;
            int checkSize = XzConstants.GetCheckSize(checkType);
            CopyExact(stream, raw, paddingSize + checkSize);

            raw.Position = 0;
            rawBlock = raw;
            return true;
        }
        catch
        {
            raw.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Asynchronously reads one complete block from the source, then performs CPU-bound decoding.
    /// </summary>
    internal static async ValueTask<BlockBufferResult> ReadBlockToBufferAsync(
        Stream stream, int checkType, long maxOutputSize = long.MaxValue,
        CancellationToken cancellationToken = default)
    {
        using MemoryStream? rawBlock = await ReadRawBlockAsync(stream, checkType, cancellationToken)
            .ConfigureAwait(false);
        if (rawBlock == null)
            return new BlockBufferResult(false, null, 0, 0, 0);

        bool hasBlock = ReadBlockToBuffer(rawBlock, checkType, out byte[]? buffer,
            out int length, out long unpaddedSize, out long uncompressedSize, maxOutputSize);
        return new BlockBufferResult(hasBlock, buffer, length, unpaddedSize, uncompressedSize);
    }

    /// <summary>
    /// Asynchronously reads one complete raw (still compressed) block from the
    /// stream without decoding it, or returns null when the index indicator is
    /// found instead. The returned stream is positioned at 0 and exposable.
    /// Used by async parallel block decompression to separate I/O from CPU work.
    /// </summary>
    internal static async ValueTask<MemoryStream?> ReadRawBlockAsync(
        Stream stream, int checkType, CancellationToken cancellationToken = default)
    {
        var rawBlock = new MemoryStream();
        byte[] oneByte = new byte[1];
        await ReadExactAsync(stream, oneByte, cancellationToken).ConfigureAwait(false);
        int headerSizeByte = oneByte[0];
        if (headerSizeByte == 0)
        {
            rawBlock.Dispose();
            return null;
        }

        int headerSize = (headerSizeByte + 1) * 4;
        byte[] header = ArrayPool<byte>.Shared.Rent(headerSize);
        try
        {
            header[0] = (byte)headerSizeByte;
            await ReadExactAsync(stream, header.AsMemory(1, headerSize - 1), cancellationToken)
                .ConfigureAwait(false);
            await rawBlock.WriteAsync(header.AsMemory(0, headerSize), cancellationToken)
                .ConfigureAwait(false);

            int headerDataLength = headerSize - 4;
            if (headerDataLength < 2)
                throw new LzmaDataErrorException("XZ block header is too short.");
            if (!Crc32.Verify(header.AsSpan(0, headerDataLength),
                    header.AsSpan(headerDataLength, 4)))
                throw new LzmaDataErrorException("XZ block header CRC32 mismatch.");

            byte flags = header[1];
            if ((flags & 0x3C) != 0)
                throw new LzmaDataErrorException("Reserved bits set in XZ block flags.");
            bool hasCompressedSize = (flags & 0x40) != 0;
            long compressedSize;
            if (hasCompressedSize)
            {
                int pos = 2;
                compressedSize = ToSupportedSize(
                    ReadMultibyteInt(header.AsSpan(0, headerDataLength), ref pos),
                    "XZ block compressed size");
                await CopyExactAsync(stream, rawBlock, (int)compressedSize, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                (compressedSize, _) = await CopyLzma2StreamAsync(
                    stream, rawBlock, cancellationToken).ConfigureAwait(false);
            }

            int paddingSize = (4 - ((int)compressedSize & 3)) & 3;
            int checkSize = XzConstants.GetCheckSize(checkType);
            await CopyExactAsync(stream, rawBlock, paddingSize + checkSize, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            rawBlock.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }

        rawBlock.Position = 0;
        return rawBlock;
    }

    /// <summary>
    /// Decodes LZMA2 data into an exactly sized pooled buffer.
    /// Returns a buffer from ArrayPool that must be returned by the caller.
    /// </summary>
    private static byte[] DecodeLzma2(ReadOnlyMemory<byte> compressedData, int dictSize,
        long uncompressedSize, out int written)
    {
        if (uncompressedSize < 0 || uncompressedSize > int.MaxValue)
            throw new LzmaDataErrorException("XZ block is too large for this decoder.");

        int size = (int)uncompressedSize;
        byte[] buf = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
        try
        {
            using var decoder = new Lzma2Decoder(dictSize);
            written = decoder.Decode(compressedData, buf.AsSpan(0, size));
            if (written != size)
                throw new LzmaDataErrorException("XZ block uncompressed size mismatch.");
            return buf;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }
    }

    /// <summary>
    /// Handles the case where compressed size is not in the block header.
    /// Parses LZMA2 chunk headers so no bytes beyond the block are consumed.
    /// </summary>
    private static long ReadCompressedDataWithoutSize(Stream stream, int dictSize,
        long expectedUncompSize, (IBcjFilter Filter, uint StartPosition)[]? bcjFilters,
        int checkType, out byte[] outputBuffer, out int outputLength,
        out long uncompressedSize, out long unpaddedSize, int headerSize,
        long maxOutputSize)
    {
        outputBuffer = null!;
        outputLength = 0;
        using var compMs = new MemoryStream();
        long decodedSize = CopyLzma2Stream(stream, compMs);
        if (expectedUncompSize >= 0 && decodedSize != expectedUncompSize)
            throw new LzmaDataErrorException("XZ block uncompressed size mismatch.");
        if (decodedSize > maxOutputSize)
            throw new LzmaMemoryLimitException(
                $"XZ block claims {decodedSize:N0} uncompressed bytes, exceeding the configured limit.");

        int compressedSize = checked((int)compMs.Length);
        byte[] decompBuf = DecodeLzma2(
            compMs.GetBuffer().AsMemory(0, compressedSize), dictSize, decodedSize,
            out int decompressed);
        try
        {
            if (bcjFilters != null)
            {
                for (int f = bcjFilters.Length - 1; f >= 0; f--)
                    bcjFilters[f].Filter.Decode(
                        decompBuf.AsSpan(0, decompressed), bcjFilters[f].StartPosition);
            }

            int paddingSize = (4 - (compressedSize & 3)) & 3;
            for (int i = 0; i < paddingSize; i++)
            {
                int value = stream.ReadByte();
                if (value < 0)
                    throw new LzmaDataErrorException("Unexpected end of XZ stream.");
                if (value != 0)
                    throw new LzmaDataErrorException("Non-zero padding after XZ block data.");
            }

            int checkSize = XzConstants.GetCheckSize(checkType);
            if (checkSize > 0)
            {
                Span<byte> check = stackalloc byte[64];
                ReadExact(stream, check[..checkSize]);
                VerifyCheck(checkType, decompBuf.AsSpan(0, decompressed), check[..checkSize]);
            }

            outputBuffer = decompBuf;
            outputLength = decompressed;
            uncompressedSize = decompressed;
            unpaddedSize = headerSize + compressedSize + checkSize;
            return compressedSize;
        }
        finally
        {
            if (!ReferenceEquals(decompBuf, outputBuffer))
                ArrayPool<byte>.Shared.Return(decompBuf);
        }
    }

    /// <summary>
    /// Writes a single XZ block (header + optional BCJ/Delta filter + LZMA2 data
    /// + padding + check). The integrity check is computed over the original
    /// (pre-filter) data, per the XZ specification.
    /// </summary>
    /// <returns>Tuple of (unpadded size, uncompressed size) for the index.</returns>
    public static (long unpaddedSize, long uncompressedSize) WriteBlock(
        Stream output, ReadOnlyMemory<byte> uncompressedData,
        Lzma2Encoder encoder, int checkType,
        ulong filterId = 0, byte[]? filterProps = null)
    {
        // Apply the optional pre-compression filter to a copy of the data.
        byte[]? filteredRented = null;
        ReadOnlyMemory<byte> lzma2Input = uncompressedData;
        if (filterId != 0)
        {
            filteredRented = ArrayPool<byte>.Shared.Rent(Math.Max(uncompressedData.Length, 1));
            uncompressedData.Span.CopyTo(filteredRented);
            FilterFactory.Create(filterId, filterProps)
                .Encode(filteredRented.AsSpan(0, uncompressedData.Length), 0);
            lzma2Input = filteredRented.AsMemory(0, uncompressedData.Length);
        }

        try
        {

        // Encode LZMA2 data to memory
        using var lzma2Stream = new MemoryStream();
        encoder.Encode(lzma2Input, lzma2Stream);
        int compressedLength = (int)lzma2Stream.Length;
        var compressedData = lzma2Stream.GetBuffer().AsSpan(0, compressedLength);

        // Build block header
        //   1 byte: header size / 4 - 1
        //   1 byte: block flags (filter count, has compressed size, has uncompressed size)
        //   VLI: compressed size
        //   VLI: uncompressed size
        //   [optional: VLI filter ID + VLI props size + props for the BCJ/Delta filter]
        //   VLI: filter ID (0x21 = LZMA2)
        //   VLI: filter props size (1)
        //   1 byte: LZMA2 dict size byte
        //   padding to 4-byte boundary
        //   4 bytes: CRC32

        using var headerStream = new MemoryStream();
        headerStream.WriteByte(0); // placeholder for size byte

        int numFilters = filterId != 0 ? 2 : 1;
        byte blockFlags = (byte)(numFilters - 1);
        blockFlags |= 0x40;     // has compressed size
        blockFlags |= 0x80;     // has uncompressed size (bit 7 should be set)
        headerStream.WriteByte(blockFlags);

        WriteMultibyteInt(headerStream, (ulong)compressedLength);
        WriteMultibyteInt(headerStream, (ulong)uncompressedData.Length);

        // Optional BCJ/Delta filter entry (must precede LZMA2 in the chain)
        if (filterId != 0)
        {
            WriteMultibyteInt(headerStream, filterId);
            WriteMultibyteInt(headerStream, (ulong)(filterProps?.Length ?? 0));
            if (filterProps is { Length: > 0 })
                headerStream.Write(filterProps, 0, filterProps.Length);
        }

        // Filter: LZMA2 (always last)
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

        // Write check (over the ORIGINAL, pre-filter data per the XZ spec)
        int checkSize = XzConstants.GetCheckSize(checkType);
        if (checkSize > 0)
        {
            WriteCheck(output, checkType, uncompressedData.Span, checkSize);
        }

        // Unpadded size = header + compressed data + check (no padding included)
        long unpaddedSize = totalHeaderSize + compressedLength + checkSize;
        return (unpaddedSize, uncompressedData.Length);

        }
        finally
        {
            if (filteredRented != null)
                ArrayPool<byte>.Shared.Return(filteredRented);
        }
    }

    /// <summary>
    /// Asynchronously writes a single XZ block (header + optional filter + LZMA2
    /// data + padding + check).
    /// </summary>
    /// <returns>Tuple of (unpadded size, uncompressed size) for the index.</returns>
    public static async Task<(long unpaddedSize, long uncompressedSize)> WriteBlockAsync(
        Stream output, ReadOnlyMemory<byte> uncompressedData,
        Lzma2Encoder encoder, int checkType,
        ulong filterId = 0, byte[]? filterProps = null,
        CancellationToken cancellationToken = default)
    {
        byte[]? filteredRented = null;
        ReadOnlyMemory<byte> lzma2Input = uncompressedData;
        if (filterId != 0)
        {
            filteredRented = ArrayPool<byte>.Shared.Rent(Math.Max(uncompressedData.Length, 1));
            uncompressedData.Span.CopyTo(filteredRented);
            FilterFactory.Create(filterId, filterProps)
                .Encode(filteredRented.AsSpan(0, uncompressedData.Length), 0);
            lzma2Input = filteredRented.AsMemory(0, uncompressedData.Length);
        }

        try
        {

        // Encode LZMA2 data to memory (CPU-bound, stays sync)
        using var lzma2Stream = new MemoryStream();
        encoder.Encode(lzma2Input, lzma2Stream);
        int compressedLength = (int)lzma2Stream.Length;

        // Build block header (same as sync version)
        using var headerStream = new MemoryStream();
        headerStream.WriteByte(0);

        int numFilters = filterId != 0 ? 2 : 1;
        byte blockFlags = (byte)(numFilters - 1);
        blockFlags |= 0x40;
        blockFlags |= 0x80;
        headerStream.WriteByte(blockFlags);

        WriteMultibyteInt(headerStream, (ulong)compressedLength);
        WriteMultibyteInt(headerStream, (ulong)uncompressedData.Length);

        if (filterId != 0)
        {
            WriteMultibyteInt(headerStream, filterId);
            WriteMultibyteInt(headerStream, (ulong)(filterProps?.Length ?? 0));
            if (filterProps is { Length: > 0 })
                headerStream.Write(filterProps, 0, filterProps.Length);
        }

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

        // Write check (over the ORIGINAL, pre-filter data per the XZ spec)
        int checkSize = XzConstants.GetCheckSize(checkType);
        if (checkSize > 0)
        {
            byte[] checkBuf = ComputeCheck(checkType, uncompressedData.Span, checkSize);
            await output.WriteAsync(checkBuf, cancellationToken).ConfigureAwait(false);
        }

        long unpaddedSize = totalHeaderSize + compressedLength + checkSize;
        return (unpaddedSize, uncompressedData.Length);

        }
        finally
        {
            if (filteredRented != null)
                ArrayPool<byte>.Shared.Return(filteredRented);
        }
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

    private static long CopyLzma2Stream(Stream input, Stream output)
    {
        long uncompressedSize = 0;
        Span<byte> sizes = stackalloc byte[4];
        while (true)
        {
            int control = ReadRequiredByte(input, "Unexpected end of LZMA2 stream.");
            output.WriteByte((byte)control);
            if (control == 0)
                return uncompressedSize;

            if (control is 1 or 2)
            {
                int high = ReadRequiredByte(input, "Truncated LZMA2 chunk header.");
                int low = ReadRequiredByte(input, "Truncated LZMA2 chunk header.");
                output.WriteByte((byte)high);
                output.WriteByte((byte)low);
                int dataSize = (high << 8 | low) + 1;
                uncompressedSize = AddUncompressedSize(uncompressedSize, dataSize);
                CopyExact(input, output, dataSize);
                continue;
            }

            if (control < 0x80)
                throw new LzmaDataErrorException($"Invalid LZMA2 control byte: 0x{control:X2}.");

            ReadExact(input, sizes);
            output.Write(sizes);
            int chunkUncompressedSize = (((control & 0x1F) << 16)
                | (sizes[0] << 8) | sizes[1]) + 1;
            int compressedSize = ((sizes[2] << 8) | sizes[3]) + 1;
            uncompressedSize = AddUncompressedSize(uncompressedSize, chunkUncompressedSize);

            if (control >= 0xC0)
                output.WriteByte((byte)ReadRequiredByte(input, "Truncated LZMA2 properties."));
            CopyExact(input, output, compressedSize);
        }
    }

    private static async ValueTask<(long CompressedSize, long UncompressedSize)> CopyLzma2StreamAsync(
        Stream input, Stream output, CancellationToken cancellationToken)
    {
        long compressedSize = 0;
        long uncompressedSize = 0;
        byte[] oneByte = new byte[1];
        byte[] sizes = new byte[4];
        while (true)
        {
            await ReadExactAsync(input, oneByte, cancellationToken).ConfigureAwait(false);
            int control = oneByte[0];
            await output.WriteAsync(oneByte, cancellationToken).ConfigureAwait(false);
            compressedSize++;
            if (control == 0)
                return (compressedSize, uncompressedSize);

            if (control is 1 or 2)
            {
                await ReadExactAsync(input, sizes.AsMemory(0, 2), cancellationToken)
                    .ConfigureAwait(false);
                await output.WriteAsync(sizes.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
                compressedSize += 2;
                int dataSize = ((sizes[0] << 8) | sizes[1]) + 1;
                uncompressedSize = AddUncompressedSize(uncompressedSize, dataSize);
                await CopyExactAsync(input, output, dataSize, cancellationToken).ConfigureAwait(false);
                compressedSize += dataSize;
                continue;
            }

            if (control < 0x80)
                throw new LzmaDataErrorException($"Invalid LZMA2 control byte: 0x{control:X2}.");

            await ReadExactAsync(input, sizes, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(sizes, cancellationToken).ConfigureAwait(false);
            compressedSize += 4;
            int chunkUncompressedSize = (((control & 0x1F) << 16)
                | (sizes[0] << 8) | sizes[1]) + 1;
            int chunkCompressedSize = ((sizes[2] << 8) | sizes[3]) + 1;
            uncompressedSize = AddUncompressedSize(uncompressedSize, chunkUncompressedSize);

            if (control >= 0xC0)
            {
                await ReadExactAsync(input, oneByte, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(oneByte, cancellationToken).ConfigureAwait(false);
                compressedSize++;
            }
            await CopyExactAsync(input, output, chunkCompressedSize, cancellationToken)
                .ConfigureAwait(false);
            compressedSize += chunkCompressedSize;
        }
    }

    private static long GetLzma2UncompressedSize(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        long uncompressedSize = 0;
        while (pos < data.Length)
        {
            int control = data[pos++];
            if (control == 0)
            {
                if (pos != data.Length)
                    throw new LzmaDataErrorException("Trailing data after the LZMA2 end marker.");
                return uncompressedSize;
            }

            if (control is 1 or 2)
            {
                EnsureAvailable(data, pos, 2, "Truncated LZMA2 chunk header.");
                int dataSize = ((data[pos] << 8) | data[pos + 1]) + 1;
                pos += 2;
                EnsureAvailable(data, pos, dataSize, "Truncated LZMA2 uncompressed chunk.");
                pos += dataSize;
                uncompressedSize = AddUncompressedSize(uncompressedSize, dataSize);
                continue;
            }

            if (control < 0x80)
                throw new LzmaDataErrorException($"Invalid LZMA2 control byte: 0x{control:X2}.");

            EnsureAvailable(data, pos, 4, "Truncated LZMA2 chunk header.");
            int chunkUncompressedSize = (((control & 0x1F) << 16)
                | (data[pos] << 8) | data[pos + 1]) + 1;
            int compressedSize = ((data[pos + 2] << 8) | data[pos + 3]) + 1;
            pos += 4;
            if (control >= 0xC0)
            {
                EnsureAvailable(data, pos, 1, "Truncated LZMA2 properties.");
                pos++;
            }
            EnsureAvailable(data, pos, compressedSize, "Truncated LZMA2 compressed chunk.");
            pos += compressedSize;
            uncompressedSize = AddUncompressedSize(uncompressedSize, chunkUncompressedSize);
        }

        throw new LzmaDataErrorException("LZMA2 end marker is missing.");
    }

    private static long AddUncompressedSize(long total, int chunkSize)
    {
        if (total > int.MaxValue - chunkSize)
            throw new LzmaDataErrorException("XZ block is too large for this decoder.");
        return total + chunkSize;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int position, int count, string message)
    {
        if ((uint)position > (uint)data.Length || count < 0 || count > data.Length - position)
            throw new LzmaDataErrorException(message);
    }

    private static int ReadRequiredByte(Stream stream, string message)
    {
        int value = stream.ReadByte();
        if (value < 0)
            throw new LzmaDataErrorException(message);
        return value;
    }

    private static void CopyExact(Stream input, Stream output, int count)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(count, 65536));
        try
        {
            while (count > 0)
            {
                int read = input.Read(buffer, 0, Math.Min(count, buffer.Length));
                if (read == 0)
                    throw new LzmaDataErrorException("Unexpected end of LZMA2 chunk.");
                output.Write(buffer, 0, read);
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] ReadToPooledBuffer(Stream input, int count)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, Math.Min(count, 65536)));
        int offset = 0;
        try
        {
            while (offset < count)
            {
                if (offset == buffer.Length)
                {
                    int newSize = (int)Math.Min((long)count, (long)buffer.Length * 2);
                    byte[] larger = ArrayPool<byte>.Shared.Rent(newSize);
                    buffer.AsSpan(0, offset).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                int read = input.Read(buffer, offset, Math.Min(count - offset, buffer.Length - offset));
                if (read == 0)
                    throw new LzmaDataErrorException("Unexpected end of XZ block data.");
                offset += read;
            }
            return buffer;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static async ValueTask CopyExactAsync(
        Stream input, Stream output, int count, CancellationToken cancellationToken)
    {
        if (count == 0)
            return;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(count, 65536));
        try
        {
            while (count > 0)
            {
                int read = await input.ReadAsync(
                    buffer.AsMemory(0, Math.Min(count, buffer.Length)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    throw new LzmaDataErrorException("Unexpected end of stream.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long ToSupportedSize(ulong value, string fieldName)
    {
        if (value > int.MaxValue)
            throw new LzmaDataErrorException($"{fieldName} exceeds the supported block size.");
        return (long)value;
    }

    private static ulong ReadMultibyteInt(ReadOnlySpan<byte> buf, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        for (int byteIndex = 0; byteIndex < 9; byteIndex++)
        {
            if ((uint)pos >= (uint)buf.Length)
                throw new LzmaDataErrorException("Truncated multibyte integer.");
            byte b = buf[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (byteIndex > 0 && (b & 0x7F) == 0)
                    throw new LzmaDataErrorException("Non-canonical multibyte integer.");
                return result;
            }
            shift += 7;
        }
        throw new LzmaDataErrorException("Multibyte integer overflow.");
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
