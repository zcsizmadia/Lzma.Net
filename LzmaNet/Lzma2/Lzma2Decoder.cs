// SPDX-License-Identifier: 0BSD

using System.Buffers;
using LzmaNet.Lzma;
using LzmaNet.RangeCoder;

namespace LzmaNet.Lzma2;

/// <summary>
/// LZMA2 decoder. Processes LZMA2 chunks (control byte + data) and dispatches
/// to the underlying LZMA decoder or copies uncompressed data.
/// </summary>
internal sealed class Lzma2Decoder : IDisposable
{
    private readonly OutputWindow _window;
    private LzmaDecoder? _lzmaDecoder;
    private int _lc, _lp, _pb;
    private bool _needProperties;

    /// <summary>
    /// Creates a new LZMA2 decoder with the given dictionary size.
    /// </summary>
    /// <param name="dictSize">Dictionary size in bytes.</param>
    public Lzma2Decoder(int dictSize)
    {
        _window = new OutputWindow(dictSize);
        _needProperties = true;
    }

    /// <summary>
    /// Decodes a complete LZMA2 stream, also reporting bytes consumed from input.
    /// </summary>
    public int DecodeWithConsumed(ReadOnlyMemory<byte> input, Span<byte> output, out int consumed)
    {
        int outPos = DecodeInternal(input, output, out consumed);
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
        return DecodeInternal(input, output, out _);
    }

    private int DecodeInternal(ReadOnlyMemory<byte> input, Span<byte> output, out int consumed)
    {
        var span = input.Span;
        int inPos = 0;
        int outPos = 0;

        while (inPos < span.Length)
        {
            byte control = span[inPos++];

            if (control == 0x00)
            {
                // End of LZMA2 data
                break;
            }

            if (control == 0x01 || control == 0x02)
            {
                // Uncompressed chunk
                if (control == 0x01)
                {
                    // Dictionary reset
                    _window.Reset();
                    _needProperties = true;
                }

                int dataSize = ((span[inPos] << 8) | span[inPos + 1]) + 1;
                inPos += 2;

                var uncompData = span.Slice(inPos, dataSize);
                _window.CopyUncompressed(uncompData, output, ref outPos);
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
            int uncompSize = ((control & 0x1F) << 16) | (span[inPos] << 8) | span[inPos + 1];
            uncompSize++;
            inPos += 2;

            int compSize = (span[inPos] << 8) | span[inPos + 1];
            compSize++;
            inPos += 2;

            if (newProps)
            {
                byte propsByte = span[inPos++];
                if (!LzmaConstants.DecodeProperties(propsByte, out _lc, out _lp, out _pb))
                    throw new LzmaDataErrorException("Invalid LZMA properties.");
                _needProperties = false;
            }

            if (_needProperties)
                throw new LzmaDataErrorException("LZMA properties not set.");

            if (resetDict)
                _window.Reset();

            if (resetState || _lzmaDecoder == null)
            {
                _lzmaDecoder = new LzmaDecoder(_lc, _lp, _pb);
            }
            else if (newProps)
            {
                _lzmaDecoder.SetProperties(_lc, _lp, _pb);
            }

            if (resetState)
                _lzmaDecoder.ResetState();

            // Decode LZMA chunk
            var chunkInput = input.Slice(inPos, compSize);
            var rc = new RangeDecoder();
            rc.Init(chunkInput, 0);

            _lzmaDecoder.DecodeLzma2Chunk(ref rc, _window, output, ref outPos, uncompSize);
            inPos += compSize;
        }

        consumed = inPos;
        return outPos;
    }

    public void Dispose()
    {
        _window.Dispose();
    }
}
