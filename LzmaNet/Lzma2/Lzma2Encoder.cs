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
    /// The dictionary carries across chunks (like xz): the first chunk performs a
    /// full reset (0xE0 / 0x01) and later chunks continue with control 0x80, so
    /// matches can reference data up to a full dictionary behind the current
    /// position instead of being limited to one 64 KB chunk.
    /// </summary>
    /// <param name="input">Uncompressed data.</param>
    /// <param name="output">Stream to write LZMA2 data to.</param>
    public void Encode(ReadOnlyMemory<byte> input, Stream output)
    {
        var span = input.Span;
        int pos = 0;
        int remaining = input.Length;

        // Reuse a single MemoryStream across chunks to avoid per-chunk allocation
        using var compressedStream = new MemoryStream(_chunkSize);

        _encoder ??= new LzmaEncoder(_props);
        _encoder.ResetState();
        _encoder.ResetDictionary();

        // The binary-tree finder REQUIRES the whole block up front: its early
        // subtree adoption assumes the length limit never grows for later
        // insertions, which per-chunk feeding would violate at every chunk tail
        // (corrupting the tree). Symbol lengths are still capped at chunk
        // boundaries by the encoder itself.
        //
        // The hash chain has no such constraint, and feeding it the whole block
        // defeats its window slide: the slide only runs from SetInput, so a
        // single up-front call leaves the buffer grown to the full block instead
        // of settling at window + cyclic. That costs a second copy of every
        // in-flight block once blocks are much larger than the dictionary.
        bool feedWholeBlock = _props.UseBinaryTree;
        if (feedWholeBlock)
            _encoder.Append(span);

        bool firstChunk = true;      // block start: dictionary reset must be signaled
        bool propsSent = false;      // properties byte sent in this block yet?
        bool needStateReset = false; // required after a stored-uncompressed chunk

        while (remaining > 0)
        {
            int thisChunk = Math.Min(remaining, _chunkSize);

            if (!feedWholeBlock)
                _encoder.Append(span.Slice(pos, thisChunk));

            if (needStateReset)
                _encoder.ResetState();

            compressedStream.SetLength(0);
            long written = _encoder.EncodeChunk(span, pos, thisChunk, compressedStream,
                sizeLimit: thisChunk);
            int compressedLen = (int)compressedStream.Length;

            if (written >= 0 && compressedLen < thisChunk && compressedLen <= 65536)
            {
                // LZMA chunk. Reset bits: 3 = dict+state+props (block start),
                // 2 = state+props, 1 = state only, 0 = continuation.
                int resetBits = firstChunk ? 3 : needStateReset ? (propsSent ? 1 : 2) : 0;
                WriteLzmaChunk(output, span.Slice(pos, thisChunk),
                    compressedStream.GetBuffer().AsSpan(0, compressedLen), resetBits);
                propsSent |= resetBits >= 2;
                needStateReset = false;
            }
            else
            {
                // Store uncompressed. The encoder's probability state has diverged
                // from what the decoder sees (the LZMA output was discarded), and
                // the LZMA2 spec requires a state reset on the next LZMA chunk
                // after an uncompressed chunk anyway.
                WriteUncompressedChunk(output, span.Slice(pos, thisChunk), dictReset: firstChunk);
                needStateReset = true;
            }

            firstChunk = false;
            pos += thisChunk;
            remaining -= thisChunk;
        }

        // End marker
        output.WriteByte(0x00);
    }

    private void WriteLzmaChunk(Stream output, ReadOnlySpan<byte> uncompressed,
                                 ReadOnlySpan<byte> compressed, int resetBits)
    {
        int uncompSize = uncompressed.Length - 1; // stored as size-1
        int compSize = compressed.Length - 1;     // stored as size-1

        byte control = (byte)(0x80 | (resetBits << 5) | ((uncompSize >> 16) & 0x1F));

        output.WriteByte(control);

        // Uncompressed size (16 bits remaining)
        output.WriteByte((byte)(uncompSize >> 8));
        output.WriteByte((byte)uncompSize);

        // Compressed size (16 bits)
        output.WriteByte((byte)(compSize >> 8));
        output.WriteByte((byte)compSize);

        // Properties byte only when the control announces new properties
        if (resetBits >= 2)
            output.WriteByte(_props.PropertiesByte);

        // Compressed data
        output.Write(compressed);
    }

    private static void WriteUncompressedChunk(Stream output, ReadOnlySpan<byte> data, bool dictReset)
    {
        // Write in segments of up to 64KB (LZMA2 uncompressed chunk limit)
        int pos = 0;
        while (pos < data.Length)
        {
            int segSize = Math.Min(data.Length - pos, 0x10000); // 64KB max per uncompressed chunk
            int sizeVal = segSize - 1; // stored as size-1

            // Control byte: 0x01 resets the dictionary (block start only);
            // 0x02 continues the dictionary.
            output.WriteByte((byte)(dictReset && pos == 0 ? 0x01 : 0x02));

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
        long value = (encoded & 1) == 0
            ? 1L << logBase
            : (1L << logBase) + (1L << (logBase - 1));
        if (value > int.MaxValue)
            throw new LzmaDataErrorException("LZMA2 dictionary size is too large for this decoder.");
        return (int)value;
    }

    public void Dispose()
    {
        _encoder?.Dispose();
    }
}