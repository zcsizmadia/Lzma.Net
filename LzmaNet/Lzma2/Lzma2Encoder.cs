// SPDX-License-Identifier: 0BSD

using System.Buffers;
using LzmaNet.Lzma;

namespace LzmaNet.Lzma2;

/// <summary>
/// LZMA2 encoder. Splits input into chunks and encodes each using LZMA or stores
/// uncompressed, with appropriate LZMA2 control headers.
/// </summary>
internal sealed class Lzma2Encoder : IDisposable
{
    private readonly LzmaEncoderProperties _props;
    private readonly int _chunkSize;
    private LzmaEncoder? _encoder;

    /// <summary>
    /// Gets the LZMA2 dictionary size property byte for XZ headers.
    /// </summary>
    public byte DictionarySizeByte { get; }

    /// <summary>
    /// Creates a new LZMA2 encoder with the given properties.
    /// </summary>
    public Lzma2Encoder(LzmaEncoderProperties props)
    {
        _props = props;
        // LZMA2 compressed size field is 16 bits (max 65536 bytes).
        // LZMA2 uncompressed size field is 21 bits (max 2 MiB).
        // Limit uncompressed chunk size to avoid overflowing the 16-bit
        // compressed size field. LZMA worst-case expansion is ~input + input/64,
        // so capping uncompressed at 64 KB keeps compressed safely under 64 KB.
        _chunkSize = Math.Min(1 << 16, Math.Min(1 << 21, props.DictionarySize * 2));
        if (_chunkSize < 4096) _chunkSize = 4096;
        DictionarySizeByte = EncodeDictSize(props.DictionarySize);
    }

    /// <summary>
    /// Encodes the input data as a complete LZMA2 stream.
    /// </summary>
    /// <param name="input">Uncompressed data.</param>
    /// <param name="output">Stream to write LZMA2 data to.</param>
    public void Encode(ReadOnlyMemory<byte> input, Stream output)
    {
        int pos = 0;
        int remaining = input.Length;

        // Reuse a single MemoryStream across chunks to avoid per-chunk allocation
        using var compressedStream = new MemoryStream(_chunkSize);

        while (remaining > 0)
        {
            int thisChunk = Math.Min(remaining, _chunkSize);
            var chunkData = input.Slice(pos, thisChunk);

            // Try LZMA compression
            compressedStream.SetLength(0);
            if (_encoder == null)
            {
                _encoder = new LzmaEncoder(_props);
            }

            // Both 0xE0 (first chunk) and 0xA0 (subsequent chunks) indicate
            // state reset to the decoder, so the encoder must also reset state.
            _encoder.ResetState();

            _encoder.EncodeForLzma2(chunkData, compressedStream);
            int compressedLen = (int)compressedStream.Length;

            if (compressedLen < thisChunk && compressedLen <= 65536)
            {
                // LZMA is smaller — write LZMA chunk
                // Use full reset (0xE0) for every chunk since each is encoded independently
                WriteLzmaChunk(output, chunkData.Span,
                    compressedStream.GetBuffer().AsSpan(0, compressedLen));
            }
            else
            {
                // Store uncompressed
                WriteUncompressedChunk(output, chunkData.Span);
            }

            pos += thisChunk;
            remaining -= thisChunk;
        }

        // End marker
        output.WriteByte(0x00);
    }

    private void WriteLzmaChunk(Stream output, ReadOnlySpan<byte> uncompressed,
                                 ReadOnlySpan<byte> compressed)
    {
        int uncompSize = uncompressed.Length - 1; // stored as size-1
        int compSize = compressed.Length - 1;     // stored as size-1

        // Full reset: dictionary + state + new properties (0xE0)
        // Each chunk is encoded independently with a fresh encoder state.
        byte control = (byte)(0xE0 | ((uncompSize >> 16) & 0x1F));

        output.WriteByte(control);

        // Uncompressed size (16 bits remaining)
        output.WriteByte((byte)(uncompSize >> 8));
        output.WriteByte((byte)uncompSize);

        // Compressed size (16 bits)
        output.WriteByte((byte)(compSize >> 8));
        output.WriteByte((byte)compSize);

        // Properties byte
        output.WriteByte(_props.PropertiesByte);

        // Compressed data
        output.Write(compressed);
    }

    private void WriteUncompressedChunk(Stream output, ReadOnlySpan<byte> data)
    {
        // Write in segments of up to 64KB (LZMA2 uncompressed chunk limit)
        int pos = 0;
        while (pos < data.Length)
        {
            int segSize = Math.Min(data.Length - pos, 0x10000); // 64KB max per uncompressed chunk
            int sizeVal = segSize - 1; // stored as size-1

            // Control byte: 0x01 for dict reset (each chunk is independent)
            output.WriteByte((byte)(pos == 0 ? 0x01 : 0x02));

            // Data size (16 bits)
            output.WriteByte((byte)(sizeVal >> 8));
            output.WriteByte((byte)sizeVal);

            // Data
            output.Write(data.Slice(pos, segSize));
            pos += segSize;
        }
    }

    /// <summary>
    /// Encodes a dictionary size value into the single-byte format used in XZ block headers.
    /// </summary>
    internal static byte EncodeDictSize(int dictSize)
    {
        if (dictSize <= 4096) return 0;

        // Find the encoding: bit_i such that 2^bit_i or 2^bit_i + 2^(bit_i-1) >= dictSize
        for (int i = 1; i <= 38; i++)
        {
            int logBase = 12 + i / 2;
            if (logBase >= 31) return 40;
            int val = (i & 1) == 0
                ? 1 << logBase
                : (1 << logBase) + (1 << (logBase - 1));
            if (val >= dictSize)
                return (byte)i;
        }
        return 40;
    }

    /// <summary>
    /// Decodes a dictionary size byte from XZ block headers.
    /// </summary>
    internal static int DecodeDictSize(byte encoded)
    {
        if (encoded == 0) return 4096;
        if (encoded > 40) throw new LzmaDataErrorException("Invalid LZMA2 dictionary size byte.");

        int logBase = 12 + encoded / 2;
        if ((encoded & 1) == 0)
            return 1 << logBase;
        return (1 << logBase) + (1 << (logBase - 1));
    }

    public void Dispose()
    {
        _encoder?.Dispose();
    }
}
