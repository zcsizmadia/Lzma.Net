// SPDX-License-Identifier: 0BSD

using System.Buffers.Binary;
using System.Security.Cryptography;

using LzmaNet.Check;
using LzmaNet.Compatibility;
using LzmaNet.Lzma;
using LzmaNet.RangeCoder;

namespace LzmaNet.Xz;

/// <summary>
/// Decodes one XZ block (LZMA2, no BCJ/Delta filters) one LZMA2 chunk at a time,
/// so memory is bounded by the declared dictionary instead of the block size.
/// Used for blocks whose header has no sizes (native single-threaded xz) and for
/// blocks too large to decode into one array.
/// </summary>
/// <remarks>
/// <para>The window is linear, like the whole-block decoder's output buffer, so
/// <see cref="LzmaDecoder.DecodeChunk"/> runs unchanged. It grows with the output
/// up to dictionary + max(dictionary, 2 MiB), never past the output limit; when
/// the next chunk does not fit, the last dictionary's worth of bytes slides to the
/// front. The slide distance is a multiple of 16, so positions keep their value
/// modulo 16 and the pb/lp position contexts are unchanged. With room for one
/// dictionary of new output per slide, sliding moves at most one byte per decoded byte.</para>
/// <para>Like liblzma, decoded data is handed out before the block's check is read:
/// a corrupt block fails at its end, after earlier chunks were returned.</para>
/// </remarks>
internal sealed class XzStreamingBlockDecoder : IDisposable
{
    private const int MaxChunkUncompressed = 1 << 21;
    private const int InitialWindow = 1 << 20;

    private readonly Stream _input;
    private readonly int _headerSize;
    private readonly int _checkType;
    private readonly XzBlock.BlockHeaderInfo _info;
    private readonly long _maxOutput;
    private readonly int _dictSize;
    private readonly int _windowCap;
    private byte[] _window;
    private readonly byte[] _chunk = new byte[1 << 16];
    private readonly byte[] _oneByte = new byte[1];
    private readonly IncrementalHash? _sha256;
    private uint _crc32;
    private ulong _crc64;

    private LzmaDecoder? _lzma;
    private int _lc, _lp, _pb;
    private bool _needProperties = true;
    private bool _needDictionaryReset = true;
    private int _pos;
    private int _dictStart;
    private long _compressed;
    private long _uncompressed;

    public byte[] Window => _window;
    public long UnpaddedSize { get; private set; }
    public long UncompressedSize => _uncompressed;

    // spareWindow: a window left by a previous block, reused to avoid reallocating.
    public XzStreamingBlockDecoder(Stream input, int headerSize, XzBlock.BlockHeaderInfo info,
        int checkType, long maxOutput, byte[]? spareWindow)
    {
        _input = input;
        _headerSize = headerSize;
        _info = info;
        _checkType = checkType;
        _maxOutput = maxOutput;
        if (info.UncompressedSize > maxOutput)
            throw new LzmaMemoryLimitException(
                $"XZ block claims {info.UncompressedSize:N0} uncompressed bytes, exceeding the configured limit.");

        _dictSize = (int)Math.Min(((long)info.DictSize + 15) & ~15L, Portable.ArrayMaxLength);
        // Never larger than the output can get: the declared dictionary alone must not
        // allocate memory that MaxOutputSize (or the block's declared size) rules out.
        long cap = (long)_dictSize + Math.Max(_dictSize, MaxChunkUncompressed) + 16;
        if (info.UncompressedSize >= 0)
            cap = Math.Min(cap, info.UncompressedSize + 16);
        cap = Math.Min(cap, Math.Min(maxOutput, long.MaxValue - 16) + 16);
        _windowCap = (int)Math.Min(cap, Portable.ArrayMaxLength);
        _window = spareWindow ?? new byte[Math.Min(_windowCap, InitialWindow)];
        if (checkType == XzConstants.CheckSha256)
            _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    public void Dispose() => _sha256?.Dispose();

    /// <summary>
    /// Decodes the next LZMA2 chunk into <see cref="Window"/>[start, start + length).
    /// Returns length -1 once the end marker, padding and check have been read and
    /// verified. The bytes stay valid until the next call.
    /// </summary>
    public (int Start, int Length) NextChunk()
        => NextChunkCore(async: false, default).GetAwaiter().GetResult();

    public ValueTask<(int Start, int Length)> NextChunkAsync(CancellationToken cancellationToken)
        => NextChunkCore(async: true, cancellationToken);

    private async ValueTask<(int Start, int Length)> NextChunkCore(bool async, CancellationToken ct)
    {
        int control = await ReadByteAsync(async, ct).ConfigureAwait(false);
        if (control == 0x00)
        {
            await FinishAsync(async, ct).ConfigureAwait(false);
            return (0, -1);
        }

        if (control is 1 or 2)
        {
            StartChunk(control);
            int size = ((await ReadByteAsync(async, ct).ConfigureAwait(false) << 8)
                        | await ReadByteAsync(async, ct).ConfigureAwait(false)) + 1;
            MakeRoom(size);
            CountCompressed(size);
            await ReadExactAsync(async, _window.AsMemory(_pos, size), ct).ConfigureAwait(false);
            int start = _pos;
            _pos += size;
            Produced(start, size);
            return (start, size);
        }

        if (control < 0x80)
            throw new LzmaDataErrorException($"Invalid LZMA2 control byte: 0x{control:X2}");

        StartChunk(control);
        int u1 = await ReadByteAsync(async, ct).ConfigureAwait(false);
        int u2 = await ReadByteAsync(async, ct).ConfigureAwait(false);
        int c1 = await ReadByteAsync(async, ct).ConfigureAwait(false);
        int c2 = await ReadByteAsync(async, ct).ConfigureAwait(false);
        int props = control >= 0xC0 ? await ReadByteAsync(async, ct).ConfigureAwait(false) : -1;
        int uncompSize = (((control & 0x1F) << 16) | (u1 << 8) | u2) + 1;
        int compSize = ((c1 << 8) | c2) + 1;
        CountCompressed(compSize);
        await ReadExactAsync(async, _chunk.AsMemory(0, compSize), ct).ConfigureAwait(false);
        return DecodeLzmaChunk(control, props, uncompSize, compSize);
    }

    /// <summary>Applies the dictionary-reset rules shared by both chunk kinds (liblzma's).</summary>
    private void StartChunk(int control)
    {
        if (control == 1 || control >= 0xE0)
        {
            _needProperties = true;
            _needDictionaryReset = false;
            _pos = _dictStart = 0;
        }
        else if (_needDictionaryReset)
        {
            throw new LzmaDataErrorException("LZMA2 data does not start with a dictionary reset.");
        }
    }

    private (int Start, int Length) DecodeLzmaChunk(int control, int props, int uncompSize, int compSize)
    {
        bool resetState = control >= 0xA0;
        if (props >= 0)
        {
            if (!LzmaConstants.DecodeProperties((byte)props, out _lc, out _lp, out _pb))
                throw new LzmaDataErrorException("Invalid LZMA properties.");
            _needProperties = false;
        }
        else if (_needProperties)
        {
            throw new LzmaDataErrorException("LZMA properties not set.");
        }

        if (_lzma == null || (resetState && _lzma.LcLp != _lc + _lp))
        {
            _lzma = new LzmaDecoder(_lc, _lp, _pb);
        }
        else if (resetState)
        {
            _lzma.SetProperties(_lc, _lp, _pb);
            _lzma.ResetState();
        }

        MakeRoom(uncompSize);
        int start = _pos;
        var rc = new RangeDecoder();
        rc.Init(_chunk.AsSpan(0, compSize), 0);
        _lzma.DecodeChunk(ref rc, _window, ref _pos, _dictStart, uncompSize);
        rc.FinishChunk();
        if (rc.Position != compSize)
            throw new LzmaDataErrorException("LZMA2 chunk compressed size does not match the coded data.");
        Produced(start, uncompSize);
        return (start, uncompSize);
    }

    private void Produced(int start, int size)
    {
        _uncompressed += size;
        switch (_checkType)
        {
            case XzConstants.CheckCrc32: _crc32 = Crc32.Compute(_window.AsSpan(start, size), _crc32); break;
            case XzConstants.CheckCrc64: _crc64 = Crc64.Compute(_window.AsSpan(start, size), _crc64); break;
            case XzConstants.CheckSha256: _sha256!.AppendData(_window, start, size); break;
        }
    }

    /// <summary>
    /// Makes room for <paramref name="size"/> more bytes at <see cref="_pos"/>: checks the
    /// output limits first, then grows the window toward its cap, then slides it,
    /// keeping at least one dictionary of history.
    /// </summary>
    private void MakeRoom(int size)
    {
        if (_uncompressed + size > _maxOutput)
            throw new LzmaMemoryLimitException();
        if (_info.UncompressedSize >= 0 && _uncompressed + size > _info.UncompressedSize)
            throw new LzmaDataErrorException("XZ block uncompressed size mismatch.");

        long needed = (long)_pos + size;
        if (needed <= _window.Length)
            return;

        if (_window.Length < _windowCap)
        {
            int grown = (int)Math.Min(_windowCap, Math.Max(needed, 2L * _window.Length));
            byte[] bigger = new byte[grown];
            Buffer.BlockCopy(_window, 0, bigger, 0, _pos);
            _window = bigger;
            if (needed <= _window.Length)
                return;
        }

        int history = _pos - _dictStart;
        int drop = (history - Math.Min(history, _dictSize)) & ~15;
        int keep = history - drop;
        if ((long)keep + size > _window.Length)
            throw new LzmaMemoryLimitException("XZ dictionary is too large for this decoder.");
        Buffer.BlockCopy(_window, _pos - keep, _window, 0, keep);
        _dictStart = 0;
        _pos = keep;
    }

    private async ValueTask FinishAsync(bool async, CancellationToken ct)
    {
        // _compressed already counts the end marker (read by ReadByteAsync); an
        // overrun of the declared Compressed Size was rejected as it happened.
        if (_info.CompressedSize >= 0 && _compressed != _info.CompressedSize)
            throw new LzmaDataErrorException("XZ block compressed size mismatch.");
        if (_info.UncompressedSize >= 0 && _uncompressed != _info.UncompressedSize)
            throw new LzmaDataErrorException("XZ block uncompressed size mismatch.");

        for (long i = (4 - (_compressed & 3)) & 3; i > 0; i--)
        {
            if (await ReadByteAsync(async, ct, compressedData: false).ConfigureAwait(false) != 0)
                throw new LzmaDataErrorException("Non-zero padding after XZ block data.");
        }
        int checkSize = XzConstants.GetCheckSize(_checkType);
        byte[] stored = new byte[checkSize];
        await ReadExactAsync(async, stored, ct).ConfigureAwait(false);
        VerifyCheck(stored);
        UnpaddedSize = _headerSize + _compressed + checkSize;
    }

    private void VerifyCheck(byte[] stored)
    {
        Span<byte> actual = stackalloc byte[32];
        switch (_checkType)
        {
            case XzConstants.CheckCrc32:
                BinaryPrimitives.WriteUInt32LittleEndian(actual, _crc32);
                Verify(actual[..4], stored, "CRC32");
                break;
            case XzConstants.CheckCrc64:
                BinaryPrimitives.WriteUInt64LittleEndian(actual, _crc64);
                Verify(actual[..8], stored, "CRC64");
                break;
            case XzConstants.CheckSha256:
                Verify(_sha256!.GetHashAndReset(), stored, "SHA-256");
                break;
        }
    }

    private static void Verify(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> stored, string name)
    {
        if (!actual.SequenceEqual(stored))
            throw new LzmaDataErrorException($"XZ block {name} check failed.");
    }

    private async ValueTask<int> ReadByteAsync(bool async, CancellationToken ct, bool compressedData = true)
    {
        int b;
        if (async)
            b = await _input.ReadAsync(_oneByte.AsMemory(), ct).ConfigureAwait(false) == 1 ? _oneByte[0] : -1;
        else
            b = _input.ReadByte();
        if (b < 0)
            throw new LzmaDataErrorException("Unexpected end of XZ stream.");
        if (compressedData)
            CountCompressed(1);
        return b;
    }

    /// <summary>Counts LZMA2 bytes, rejecting a block that overruns its declared Compressed Size.</summary>
    private void CountCompressed(int count)
    {
        _compressed += count;
        if (_info.CompressedSize >= 0 && _compressed > _info.CompressedSize)
            throw new LzmaDataErrorException("XZ block compressed size mismatch.");
    }

    private ValueTask ReadExactAsync(bool async, Memory<byte> buffer, CancellationToken ct)
    {
        if (async)
            return _input.ReadExactAsync(buffer, "Unexpected end of XZ stream.", ct);
        _input.ReadExact(buffer.Span, "Unexpected end of XZ stream.");
        return default;
    }
}
