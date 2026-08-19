// SPDX-License-Identifier: 0BSD

using LzmaNet.Lzma;
using LzmaNet.RangeCoder;

namespace LzmaNet.Lzma2;

/// <summary>
/// LZMA2 decoder. Processes LZMA2 chunks (control byte + data) and dispatches
/// to the underlying LZMA decoder or copies uncompressed data.
/// The output buffer serves as the dictionary window: XZ blocks always begin with
/// a dictionary reset and the caller supplies a buffer for the whole block, so
/// no separate sliding-window buffer is needed.
/// </summary>
internal sealed class Lzma2Decoder : IDisposable
{
    private LzmaDecoder? _lzmaDecoder;
    private int _lc, _lp, _pb;
    private bool _needProperties;

    /// <summary>
    /// Creates a new LZMA2 decoder with the given dictionary size.
    /// </summary>
    /// <param name="dictSize">Dictionary size in bytes (from the XZ block header).
    /// Retained for API symmetry; the output buffer acts as the window.</param>
    public Lzma2Decoder(int dictSize)
    {
        if (dictSize < 1)
            throw new LzmaDataErrorException("Invalid LZMA2 dictionary size.");
        _needProperties = true;
    }

    /// <summary>
    /// Decodes a complete LZMA2 stream, also reporting bytes consumed from input.
    /// </summary>
    public int DecodeWithConsumed(ReadOnlyMemory<byte> input, Span<byte> output, out int consumed)
    {
        int outPos = DecodeInternal(input.Span, output, out consumed);
        return outPos;
    }

    /// <summary>
    /// Decodes a complete LZMA2 stream from the input buffer into the output span.
    /// </summary>
    /// <param name="input">Complete LZMA2 compressed data.</param>
    /// <param name="output">Output buffer for decompressed data.</param>
    /// <returns>Number of decompressed bytes written.</returns>
    public int Decode(ReadOnlyMemory<byte> input, Span<byte> output)
    {
        return DecodeInternal(input.Span, output, out _);
    }

    /// <summary>
    /// Decodes a complete LZMA2 stream from the input span into the output span.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return DecodeInternal(input, output, out _);
    }

    private int DecodeInternal(ReadOnlySpan<byte> span, Span<byte> output, out int consumed)
    {
        int inPos = 0;
        int outPos = 0;
        int dictStart = 0;
        bool sawEndMarker = false;

        while (inPos < span.Length)
        {
            byte control = span[inPos++];

            if (control == 0x00)
            {
                // End of LZMA2 data
                sawEndMarker = true;
                break;
            }

            if (control == 0x01 || control == 0x02)
            {
                // Uncompressed chunk
                if (control == 0x01)
                {
                    // Dictionary reset
                    dictStart = outPos;
                    _needProperties = true;
                }

                EnsureAvailable(span, inPos, 2, "Truncated LZMA2 chunk header.");
                int dataSize = ((span[inPos] << 8) | span[inPos + 1]) + 1;
                inPos += 2;

                EnsureAvailable(span, inPos, dataSize, "Truncated LZMA2 uncompressed chunk.");
                EnsureOutputAvailable(output, outPos, dataSize);
                span.Slice(inPos, dataSize).CopyTo(output.Slice(outPos, dataSize));
                outPos += dataSize;
                inPos += dataSize;
                continue;
            }

            if (control < 0x80)
                throw new LzmaDataErrorException($"Invalid LZMA2 control byte: 0x{control:X2}");

            // LZMA chunk
            bool resetDict = control >= 0xE0;
            bool resetState = control >= 0xA0;
            bool newProps = control >= 0xC0;

            // Parse sizes
            EnsureAvailable(span, inPos, 4, "Truncated LZMA2 chunk header.");
            int uncompSize = ((control & 0x1F) << 16) | (span[inPos] << 8) | span[inPos + 1];
            uncompSize++;
            inPos += 2;

            int compSize = (span[inPos] << 8) | span[inPos + 1];
            compSize++;
            inPos += 2;

            if (newProps)
            {
                EnsureAvailable(span, inPos, 1, "Truncated LZMA2 properties.");
                byte propsByte = span[inPos++];
                if (!LzmaConstants.DecodeProperties(propsByte, out _lc, out _lp, out _pb))
                    throw new LzmaDataErrorException("Invalid LZMA properties.");
                _needProperties = false;
            }

            if (_needProperties)
                throw new LzmaDataErrorException("LZMA properties not set.");

            if (resetDict)
                dictStart = outPos;

            if (_lzmaDecoder == null || (resetState && _lzmaDecoder.LcLp != _lc + _lp))
            {
                // Allocate only when the literal-coder shape changes; otherwise
                // the existing probability arrays are reused via ResetState.
                _lzmaDecoder = new LzmaDecoder(_lc, _lp, _pb);
            }
            else if (resetState)
            {
                // newProps implies resetState (control >= 0xC0), so property updates
                // always land here.
                _lzmaDecoder.SetProperties(_lc, _lp, _pb);
                _lzmaDecoder.ResetState();
            }

            // Decode LZMA chunk
            EnsureAvailable(span, inPos, compSize, "Truncated LZMA2 compressed chunk.");
            EnsureOutputAvailable(output, outPos, uncompSize);
            var rc = new RangeDecoder();
            rc.Init(span.Slice(inPos, compSize), 0);

            _lzmaDecoder.DecodeChunk(ref rc, output, ref outPos, dictStart, uncompSize);
            inPos += compSize;
        }

        if (!sawEndMarker)
            throw new LzmaDataErrorException("LZMA2 end marker is missing.");

        consumed = inPos;
        return outPos;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> input, int position, int count, string message)
    {
        if ((uint)position > (uint)input.Length || count < 0 || count > input.Length - position)
            throw new LzmaDataErrorException(message);
    }

    private static void EnsureOutputAvailable(Span<byte> output, int position, int count)
    {
        if ((uint)position > (uint)output.Length || count < 0 || count > output.Length - position)
            throw new LzmaDataErrorException("Output buffer is too small for the LZMA2 data.");
    }

    public void Dispose()
    {
        // Nothing to release; the output buffer serves as the dictionary window.
    }
}
