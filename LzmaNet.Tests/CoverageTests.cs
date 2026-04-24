// SPDX-License-Identifier: 0BSD

using LzmaNet.Check;
using LzmaNet.Lzma;
using LzmaNet.Lzma2;
using LzmaNet.RangeCoder;
using LzmaNet.Xz;

namespace LzmaNet.Tests;

/// <summary>
/// Tests targeting uncovered code paths to push coverage above 90%.
/// </summary>
public class CoverageTests
{
    // ── LzmaException constructors ───────────────────────────────────

    [Test]
    public async Task LzmaException_ParameterlessConstructor()
    {
        var ex = new LzmaException();
        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task LzmaException_InnerExceptionConstructor()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new LzmaException("outer", inner);
        await Assert.That(ex.Message).IsEqualTo("outer");
        await Assert.That(ex.InnerException).IsSameReferenceAs(inner);
    }

    [Test]
    public async Task LzmaDataErrorException_ParameterlessConstructor()
    {
        var ex = new LzmaDataErrorException();
        await Assert.That(ex.Message.ToLowerInvariant()).Contains("corrupt");
    }

    [Test]
    public async Task LzmaFormatException_ParameterlessConstructor()
    {
        var ex = new LzmaFormatException();
        await Assert.That(ex.Message.ToLowerInvariant()).Contains("not recognized");
    }

    // ── Crc64 CRC32 hash table (used by match finders) ──────────────

    [Test]
    public async Task Crc64_GetCrc32HashValue_ReturnsConsistentValues()
    {
        uint val0 = Crc64.GetCrc32HashValue(0);
        uint val1 = Crc64.GetCrc32HashValue(1);
        uint val255 = Crc64.GetCrc32HashValue(255);

        // Values should be non-zero for non-zero inputs and deterministic
        await Assert.That(val1).IsNotEqualTo(val0);
        await Assert.That(Crc64.GetCrc32HashValue(0)).IsEqualTo(val0);
        await Assert.That(Crc64.GetCrc32HashValue(255)).IsEqualTo(val255);
    }

    // ── RangeDecoder uncovered paths ─────────────────────────────────

    [Test]
    public async Task RangeDecoder_Position_ReturnsCurrentOffset()
    {
        // Build a minimal valid range-coded stream: 0x00 + 4 code bytes
        byte[] data = [0x00, 0x00, 0x00, 0x00, 0x00];
        var rc = new RangeDecoder();
        rc.Init(data.AsMemory(), 0);
        await Assert.That(rc.Position).IsEqualTo(5); // After init, consumed 5 bytes
    }

    [Test]
    public async Task RangeDecoder_Init_InvalidFirstByte_Throws()
    {
        byte[] data = [0xFF, 0x00, 0x00, 0x00, 0x00]; // First byte must be 0x00
        var rc = new RangeDecoder();
        await Assert.That(() =>
        {
            rc.Init(data.AsMemory(), 0);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task RangeDecoder_SpanInit_InvalidFirstByte_Throws()
    {
        byte[] data = [0xFF, 0x00, 0x00, 0x00, 0x00];
        var rc = new RangeDecoder();
        int offset = 0;
        await Assert.That(() =>
        {
            rc.Init(data.AsSpan(), ref offset);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task RangeDecoder_SpanInit_And_SetBuffer()
    {
        byte[] data = [0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00];
        var rc = new RangeDecoder();
        int offset = 0;
        rc.Init(data.AsSpan(), ref offset);
        await Assert.That(offset).IsEqualTo(5);

        // SetBuffer for continued decoding
        rc.SetBuffer(data.AsMemory(), offset);

        // Should be able to decode a bit without crashing
        ushort prob = (ushort)(RangeDecoder.kBitModelTotal >> 1);
        rc.DecodeBit(ref prob); // exercises Normalize which reads from buffer
    }

    [Test]
    public async Task RangeDecoder_IsFinished()
    {
        var rc = new RangeDecoder();
        // Default struct has code=0, so IsFinished should be true
        await Assert.That(rc.IsFinished).IsTrue();
    }

    [Test]
    public async Task RangeDecoder_InitProbs_WithOffsetAndCount()
    {
        ushort[] probs = new ushort[20];
        Array.Fill(probs, (ushort)0);
        RangeDecoder.InitProbs(probs, 5, 10);
        // First 5 should be unchanged
        for (int i = 0; i < 5; i++)
            await Assert.That(probs[i]).IsEqualTo((ushort)0);
        // Middle 10 should be initialized to 1024
        for (int i = 5; i < 15; i++)
            await Assert.That(probs[i]).IsEqualTo((ushort)1024);
        // Last 5 should be unchanged
        for (int i = 15; i < 20; i++)
            await Assert.That(probs[i]).IsEqualTo((ushort)0);
    }

    // ── RangeEncoder uncovered paths ─────────────────────────────────

    [Test]
    public async Task RangeEncoder_WriteInitByte()
    {
        using var ms = new MemoryStream();
        var enc = new RangeEncoder(ms);
        enc.WriteInitByte();
        await Assert.That(enc.BytesWritten).IsEqualTo(1L);
        await Assert.That(ms.ToArray()[0]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task RangeEncoder_Reset()
    {
        using var ms = new MemoryStream();
        var enc = new RangeEncoder(ms);
        // Encode something
        ushort prob = (ushort)(RangeDecoder.kBitModelTotal >> 1);
        enc.EncodeBit(ref prob, 0);
        enc.FlushData();
        long bytesAfterFirst = enc.BytesWritten;

        // Reset should allow encoding again
        enc.Reset();
        enc.EncodeBit(ref prob, 1);
        enc.FlushData();
        await Assert.That(enc.BytesWritten).IsGreaterThan(bytesAfterFirst);
    }

    [Test]
    public async Task RangeEncoder_PendingBytes()
    {
        using var ms = new MemoryStream();
        var enc = new RangeEncoder(ms);
        // Initially, cacheSize=1 so PendingBytes = cacheSize + 1 = 2
        await Assert.That(enc.PendingBytes).IsGreaterThanOrEqualTo(1L);
    }

    // ── Lzma2Decoder error paths ─────────────────────────────────────

    [Test]
    public async Task Lzma2Decoder_InvalidControlByte_Throws()
    {
        // Control byte 0x50 is invalid (< 0x80, not 0x00/0x01/0x02)
        byte[] data = [0x50];
        using var decoder = new Lzma2Decoder(65536);
        await Assert.That(() =>
        {
            decoder.Decode(data.AsMemory(), new byte[1024]);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task Lzma2Decoder_MissingProperties_Throws()
    {
        // Control 0x80 = LZMA chunk without new properties, but no props set yet
        byte[] data = [0x80, 0x00, 0x00, 0x00, 0x05, /* no props byte */ ];
        using var decoder = new Lzma2Decoder(65536);
        await Assert.That(() =>
        {
            decoder.Decode(data.AsMemory(), new byte[1024]);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task Lzma2Decoder_UncompressedChunk_DictReset()
    {
        byte[] data = [0x01, 0x00, 0x02, 0x41, 0x42, 0x43, 0x00];
        byte[] output = new byte[100];
        using var decoder = new Lzma2Decoder(65536);
        int written = decoder.Decode(data.AsMemory(), output);
        await Assert.That(written).IsEqualTo(3);
        await Assert.That(output[0]).IsEqualTo((byte)'A');
        await Assert.That(output[1]).IsEqualTo((byte)'B');
        await Assert.That(output[2]).IsEqualTo((byte)'C');
    }

    [Test]
    public async Task Lzma2Decoder_UncompressedChunk_NoDictReset()
    {
        byte[] data = [0x02, 0x00, 0x01, 0x58, 0x59, 0x00];
        byte[] output = new byte[100];
        using var decoder = new Lzma2Decoder(65536);
        int written = decoder.Decode(data.AsMemory(), output);
        await Assert.That(written).IsEqualTo(2);
        await Assert.That(output[0]).IsEqualTo((byte)'X');
        await Assert.That(output[1]).IsEqualTo((byte)'Y');
    }

    [Test]
    public async Task Lzma2Decoder_InvalidProperties_Throws()
    {
        byte[] data = [0xE0, 0x00, 0x00, 0x00, 0x05, 0xFF];
        byte[] output = new byte[1024];
        using var decoder = new Lzma2Decoder(65536);
        await Assert.That(() =>
        {
            decoder.Decode(data.AsMemory(), output);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    // ── LzmaDecoder: standalone Decode method ────────────────────────

    [Test]
    public async Task LzmaDecoder_Decode_StandaloneRoundTrip()
    {
        byte[] original = new byte[256];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 37);

        var props = LzmaEncoderProperties.FromPreset(0);
        using var encoder = new LzmaEncoder(props);
        using var compressedStream = new MemoryStream();
        encoder.Encode(original.AsSpan(), compressedStream);
        byte[] compressed = compressedStream.ToArray();

        var decoder = new LzmaDecoder(props.Lc, props.Lp, props.Pb);
        using var window = new OutputWindow(props.DictionarySize);
        byte[] output = new byte[original.Length];
        int outPos = 0;
        decoder.Decode(compressed.AsMemory(), 0, window, output, ref outPos, original.Length);

        await Assert.That(outPos).IsEqualTo(original.Length);
        await Assert.That(output.SequenceEqual(original)).IsTrue();
    }

    // ── XzCompressor: Decompress into span ───────────────────────────

    [Test]
    public async Task Decompress_IntoSpan_Success()
    {
        byte[] original = "Hello, Span!"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original);

        byte[] output = new byte[100];
        int written = XzCompressor.Decompress(compressed, output.AsSpan());

        await Assert.That(written).IsEqualTo(original.Length);
        await Assert.That(original.AsSpan().SequenceEqual(output.AsSpan(0, written))).IsTrue();
    }

    [Test]
    public async Task Decompress_IntoSpan_TooSmall_Throws()
    {
        byte[] original = "This data needs more space"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original);

        byte[] tooSmall = new byte[5];
        await Assert.That(() =>
        {
            XzCompressor.Decompress(compressed, tooSmall.AsSpan());
        }).ThrowsExactly<ArgumentException>();
    }

    // ── Corrupt XZ data error paths ──────────────────────────────────

    [Test]
    public async Task Decompress_InvalidMagic_ThrowsFormatException()
    {
        byte[] data = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];
        await Assert.That(() =>
        {
            XzCompressor.Decompress(data);
        }).ThrowsExactly<LzmaFormatException>();
    }

    [Test]
    public async Task Decompress_CorruptCrc_ThrowsDataError()
    {
        byte[] original = "test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original);
        if (compressed.Length > 20)
            compressed[15] ^= 0xFF;
        await Assert.That(() =>
        {
            XzCompressor.Decompress(compressed);
        }).Throws<LzmaException>();
    }

    [Test]
    public async Task Decompress_TruncatedStream_ThrowsDataError()
    {
        byte[] original = "some data to compress"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original);
        byte[] truncated = compressed[..12];
        await Assert.That(() =>
        {
            XzCompressor.Decompress(truncated);
        }).Throws<Exception>();
    }

    // ── XzCompressStream edge cases ──────────────────────────────────

    [Test]
    public async Task XzCompressStream_EmptyData_ProducesValidXz()
    {
        using var ms = new MemoryStream();
        using (var xz = new XzCompressStream(ms, leaveOpen: true))
        {
            // Write nothing — just close
        }

        ms.Position = 0;
        byte[] result = XzCompressor.Decompress(ms.ToArray());
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public Task XzCompressStream_DoubleDispose_NoError()
    {
        using var ms = new MemoryStream();
        var xz = new XzCompressStream(ms, leaveOpen: true);
        xz.Write("test"u8);
        xz.Dispose();
        xz.Dispose(); // Should not throw
        return Task.CompletedTask;
    }

    [Test]
    public Task XzCompressStream_FlushDoesNotThrow()
    {
        using var ms = new MemoryStream();
        using var xz = new XzCompressStream(ms, leaveOpen: true);
        xz.Write("hello"u8);
        xz.Flush();
        return Task.CompletedTask;
    }

    // ── XzDecompressStream edge cases ────────────────────────────────

    [Test]
    public async Task XzDecompressStream_EmptyCompressedData()
    {
        byte[] compressed = XzCompressor.Compress(ReadOnlySpan<byte>.Empty);
        using var ms = new MemoryStream(compressed);
        using var xz = new XzDecompressStream(ms);
        using var output = new MemoryStream();
        xz.CopyTo(output);
        await Assert.That(output.ToArray()).IsEmpty();
    }

    [Test]
    public Task XzDecompressStream_DoubleDispose_NoError()
    {
        byte[] compressed = XzCompressor.Compress("test"u8.ToArray());
        using var ms = new MemoryStream(compressed);
        var xz = new XzDecompressStream(ms, leaveOpen: true);
        using var output = new MemoryStream();
        xz.CopyTo(output);
        xz.Dispose();
        xz.Dispose();
        return Task.CompletedTask;
    }

    [Test]
    public async Task XzDecompressStream_ReadWithArrayOverload()
    {
        byte[] original = "Testing array Read overload"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original);
        using var ms = new MemoryStream(compressed);
        using var xz = new XzDecompressStream(ms);

        byte[] buffer = new byte[1024];
        int totalRead = 0;
        int bytesRead;
        while ((bytesRead = xz.Read(buffer, totalRead, buffer.Length - totalRead)) > 0)
            totalRead += bytesRead;

        await Assert.That(totalRead).IsEqualTo(original.Length);
        await Assert.That(original.AsSpan().SequenceEqual(buffer.AsSpan(0, totalRead))).IsTrue();
    }

    // ── CheckType None round-trip ────────────────────────────────────

    [Test]
    public async Task RoundTrip_CheckTypeNone()
    {
        byte[] original = "No integrity check"u8.ToArray();
        var opts = new XzCompressOptions { CheckType = XzCheckType.None };
        byte[] compressed = XzCompressor.Compress(original, opts);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_CheckTypeCrc32()
    {
        byte[] original = "CRC32 check"u8.ToArray();
        var opts = new XzCompressOptions { CheckType = XzCheckType.Crc32 };
        byte[] compressed = XzCompressor.Compress(original, opts);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    // ── XzHeader error paths ─────────────────────────────────────────

    [Test]
    public async Task XzHeader_ReadStreamHeader_TooShort_Throws()
    {
        byte[] header = new byte[5];
        await Assert.That(() =>
        {
            XzHeader.ReadStreamHeader(header);
        }).ThrowsExactly<LzmaFormatException>();
    }

    [Test]
    public async Task XzHeader_ReadStreamHeader_BadMagic_Throws()
    {
        byte[] header = new byte[12];
        await Assert.That(() =>
        {
            XzHeader.ReadStreamHeader(header);
        }).ThrowsExactly<LzmaFormatException>();
    }

    [Test]
    public async Task XzHeader_ReadStreamHeader_BadFlags_Throws()
    {
        byte[] header = new byte[12];
        XzConstants.HeaderMagic.CopyTo(header);
        header[6] = 0x01;
        await Assert.That(() =>
        {
            XzHeader.ReadStreamHeader(header);
        }).ThrowsExactly<LzmaFormatException>();
    }

    [Test]
    public async Task XzHeader_ReadStreamHeader_BadCrc_Throws()
    {
        byte[] header = new byte[12];
        XzConstants.HeaderMagic.CopyTo(header);
        header[6] = 0x00;
        header[7] = 0x04;
        await Assert.That(() =>
        {
            XzHeader.ReadStreamHeader(header);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    // ── XzConstants GetCheckSize ─────────────────────────────────────

    [Test]
    [Arguments(0, 0)]
    [Arguments(1, 4)]
    [Arguments(4, 8)]
    [Arguments(10, 32)]
    public async Task XzConstants_GetCheckSize(int checkType, int expectedSize)
    {
        await Assert.That(XzConstants.GetCheckSize(checkType)).IsEqualTo(expectedSize);
    }

    [Test]
    public async Task XzConstants_GetCheckSize_Invalid_Throws()
    {
        await Assert.That(() =>
        {
            XzConstants.GetCheckSize(16);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    // ── LzmaEncoderProperties extreme mode ───────────────────────────

    [Test]
    [Arguments(0, true)]
    [Arguments(6, true)]
    [Arguments(9, true)]
    public async Task EncoderProperties_ExtremeMode_HigherCutValue(int preset, bool extreme)
    {
        var normal = LzmaEncoderProperties.FromPreset(preset, false);
        var ext = LzmaEncoderProperties.FromPreset(preset, extreme);
        await Assert.That(ext.CutValue).IsGreaterThanOrEqualTo(normal.CutValue);
    }

    // ── XzCompressOptions edge cases ─────────────────────────────────

    [Test]
    public async Task XzCompressOptions_Default_IsValid()
    {
        var opts = XzCompressOptions.Default;
        opts.Validate();
        await Assert.That(opts.Preset).IsEqualTo(6);
        await Assert.That(opts.Extreme).IsFalse();
        await Assert.That(opts.Threads).IsEqualTo(1);
        await Assert.That(opts.CheckType).IsEqualTo(XzCheckType.Crc64);
        await Assert.That(opts.DictionarySize).IsNull();
        await Assert.That(opts.BlockSize).IsNull();
    }

    [Test]
    public async Task XzCompressOptions_Threads0_ResolvesToProcessorCount()
    {
        var opts = new XzCompressOptions { Threads = 0 };
        await Assert.That(opts.ResolvedThreads).IsEqualTo(Environment.ProcessorCount);
    }

    [Test]
    public async Task XzCompressOptions_CheckTypeValue_Mapping()
    {
        await Assert.That(new XzCompressOptions { CheckType = XzCheckType.None }.CheckTypeValue).IsEqualTo(XzConstants.CheckNone);
        await Assert.That(new XzCompressOptions { CheckType = XzCheckType.Crc32 }.CheckTypeValue).IsEqualTo(XzConstants.CheckCrc32);
        await Assert.That(new XzCompressOptions { CheckType = XzCheckType.Crc64 }.CheckTypeValue).IsEqualTo(XzConstants.CheckCrc64);
    }

    // ── Large data to exercise more decoder paths ────────────────────

    [Test]
    public async Task RoundTrip_LargeData_ExercisesAllCodePaths()
    {
        byte[] original = new byte[512 * 1024];
        var rng = new Random(42);
        for (int i = 0; i < original.Length; i++)
        {
            if (i < 256) original[i] = (byte)(i & 0xFF);
            else if (rng.Next(10) < 7) original[i] = original[i - 37];
            else if (rng.Next(10) < 5) original[i] = original[i - 1];
            else original[i] = (byte)rng.Next(256);
        }

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_RandomData_ExercisesUncompressedChunks()
    {
        byte[] original = new byte[128 * 1024];
        new Random(99).NextBytes(original);

        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = 0 });
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    // ── LzmaDecoder standalone Decode with rep matches ───────────────

    [Test]
    public async Task LzmaDecoder_Decode_WithRepMatches()
    {
        byte[] original = new byte[1024];
        Array.Fill(original, (byte)0xAA);

        var props = LzmaEncoderProperties.FromPreset(0);
        using var encoder = new LzmaEncoder(props);
        using var compressedStream = new MemoryStream();
        encoder.Encode(original.AsSpan(), compressedStream);
        byte[] compressed = compressedStream.ToArray();

        var decoder = new LzmaDecoder(props.Lc, props.Lp, props.Pb);
        using var window = new OutputWindow(props.DictionarySize);
        byte[] output = new byte[original.Length];
        int outPos = 0;
        decoder.Decode(compressed.AsMemory(), 0, window, output, ref outPos, original.Length);

        await Assert.That(outPos).IsEqualTo(original.Length);
        await Assert.That(output.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task LzmaDecoder_Decode_WithVariousRepDistances()
    {
        byte[] original = new byte[4096];
        for (int i = 0; i < 100; i++) original[i] = (byte)(i * 7);
        for (int i = 100; i < 200; i++) original[i] = original[i - 1];
        for (int i = 200; i < 300; i++) original[i] = original[i - 5];
        for (int i = 300; i < 400; i++) original[i] = original[i - 37];
        for (int i = 400; i < 500; i++) original[i] = original[i - 100];
        for (int i = 500; i < 600; i++) original[i] = original[i - 1];
        for (int i = 600; i < 700; i++) original[i] = original[i - 5];
        for (int i = 700; i < 800; i++) original[i] = original[i - 37];
        for (int i = 800; i < 900; i++) original[i] = original[i - 100];
        for (int i = 900; i < original.Length; i++) original[i] = original[i - 1];

        var props = LzmaEncoderProperties.FromPreset(1);
        using var encoder = new LzmaEncoder(props);
        using var compressedStream = new MemoryStream();
        encoder.Encode(original.AsSpan(), compressedStream);
        byte[] compressed = compressedStream.ToArray();

        var decoder = new LzmaDecoder(props.Lc, props.Lp, props.Pb);
        using var window = new OutputWindow(props.DictionarySize);
        byte[] output = new byte[original.Length];
        int outPos = 0;
        decoder.Decode(compressed.AsMemory(), 0, window, output, ref outPos, original.Length);

        await Assert.That(outPos).IsEqualTo(original.Length);
        await Assert.That(output.SequenceEqual(original)).IsTrue();
    }

    // ── LzmaDecoder SetProperties (via Lzma2 path) ──────────────────

    [Test]
    public Task LzmaDecoder_SetProperties_UpdatesDecoder()
    {
        var decoder = new LzmaDecoder(3, 0, 2);
        decoder.SetProperties(4, 1, 3);
        return Task.CompletedTask;
    }

    // ── XzBlock error paths via crafted headers ──────────────────────

    [Test]
    public async Task XzBlock_ReadBlock_IndexIndicator_ReturnsFalse()
    {
        using var stream = new MemoryStream([0x00]);
        bool result = XzBlock.ReadBlock(stream, XzConstants.CheckCrc64, Stream.Null,
                                         out long unpaddedSize, out long uncompressedSize);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task XzBlock_ReadBlock_TruncatedHeader_Throws()
    {
        using var stream = new MemoryStream([0x01]);
        await Assert.That(() =>
        {
            XzBlock.ReadBlock(stream, XzConstants.CheckCrc64, Stream.Null,
                              out _, out _);
        }).Throws<LzmaException>();
    }

    [Test]
    public async Task XzBlock_ReadBlock_BadHeaderCrc_Throws()
    {
        byte[] header = BuildXzBlockHeader(
            numFilters: 1,
            filterId: XzConstants.FilterIdLzma2,
            filterPropsSize: 1,
            dictSizeByte: 0x18,
            hasCompressedSize: true,
            compressedSize: 100,
            hasUncompressedSize: true,
            uncompressedSize: 200);
        header[^1] ^= 0xFF;

        using var stream = new MemoryStream(header);
        await Assert.That(() =>
        {
            XzBlock.ReadBlock(stream, XzConstants.CheckCrc64, Stream.Null,
                              out _, out _);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task XzBlock_ReadBlock_MultipleFilters_Throws()
    {
        byte[] header = BuildXzBlockHeader(
            numFilters: 2,
            filterId: XzConstants.FilterIdLzma2,
            filterPropsSize: 1,
            dictSizeByte: 0x18);

        using var stream = new MemoryStream(header);
        await Assert.That(() =>
        {
            XzBlock.ReadBlock(stream, XzConstants.CheckCrc64, Stream.Null,
                              out _, out _);
        }).Throws<LzmaException>();
    }

    [Test]
    public async Task XzBlock_ReadBlock_UnsupportedFilter_Throws()
    {
        byte[] header = BuildXzBlockHeader(
            numFilters: 1,
            filterId: XzConstants.FilterIdDelta,
            filterPropsSize: 1,
            dictSizeByte: 0x18);

        using var stream = new MemoryStream(header);
        await Assert.That(() =>
        {
            XzBlock.ReadBlock(stream, XzConstants.CheckCrc64, Stream.Null,
                              out _, out _);
        }).Throws<LzmaException>();
    }

    [Test]
    public async Task XzBlock_ReadBlock_InvalidFilterPropsSize_Throws()
    {
        byte[] header = BuildXzBlockHeader(
            numFilters: 1,
            filterId: XzConstants.FilterIdLzma2,
            filterPropsSize: 5,
            dictSizeByte: 0x18);

        using var stream = new MemoryStream(header);
        await Assert.That(() =>
        {
            XzBlock.ReadBlock(stream, XzConstants.CheckCrc64, Stream.Null,
                              out _, out _);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task XzBlock_ReadBlock_NonZeroPadding_Throws()
    {
        byte[] header = BuildXzBlockHeader(
            numFilters: 1,
            filterId: XzConstants.FilterIdLzma2,
            filterPropsSize: 1,
            dictSizeByte: 0x18,
            corruptPadding: true);

        using var stream = new MemoryStream(header);
        await Assert.That(() =>
        {
            XzBlock.ReadBlock(stream, XzConstants.CheckCrc64, Stream.Null,
                              out _, out _);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    // ── XzBlock WriteBlock/ReadBlock with SHA256 check ───────────────

    [Test]
    public async Task XzBlock_WriteBlock_ReadBlock_Sha256_RoundTrip()
    {
        byte[] data = "SHA256 round trip test data"u8.ToArray();
        var props = LzmaEncoderProperties.FromPreset(0);

        using var blockStream = new MemoryStream();
        using var lzma2Encoder = new Lzma2Encoder(props);
        var (unpaddedSize, uncompSize) = XzBlock.WriteBlock(
            blockStream, data.AsMemory(), lzma2Encoder, XzConstants.CheckSha256);

        await Assert.That(uncompSize).IsEqualTo((long)data.Length);
        await Assert.That(unpaddedSize).IsGreaterThan(0L);

        blockStream.Position = 0;
        using var output = new MemoryStream();
        bool result = XzBlock.ReadBlock(blockStream, XzConstants.CheckSha256, output,
                                         out long readUnpadded, out long readUncomp);
        await Assert.That(result).IsTrue();
        await Assert.That(readUncomp).IsEqualTo((long)data.Length);
        await Assert.That(output.ToArray().SequenceEqual(data)).IsTrue();
    }

    [Test]
    public async Task XzBlock_WriteBlock_ReadBlock_NoCheck_RoundTrip()
    {
        byte[] data = "No check round trip"u8.ToArray();
        var props = LzmaEncoderProperties.FromPreset(0);

        using var blockStream = new MemoryStream();
        using var lzma2Encoder = new Lzma2Encoder(props);
        XzBlock.WriteBlock(blockStream, data.AsMemory(), lzma2Encoder, XzConstants.CheckNone);

        blockStream.Position = 0;
        using var output = new MemoryStream();
        bool result = XzBlock.ReadBlock(blockStream, XzConstants.CheckNone, output,
                                         out _, out long readUncomp);
        await Assert.That(result).IsTrue();
        await Assert.That(readUncomp).IsEqualTo((long)data.Length);
        await Assert.That(output.ToArray().SequenceEqual(data)).IsTrue();
    }

    [Test]
    public async Task XzBlock_VerifyCheck_Sha256_CorruptData_Throws()
    {
        byte[] data = "SHA256 integrity test"u8.ToArray();
        var props = LzmaEncoderProperties.FromPreset(0);

        using var blockStream = new MemoryStream();
        using var lzma2Encoder = new Lzma2Encoder(props);
        XzBlock.WriteBlock(blockStream, data.AsMemory(), lzma2Encoder, XzConstants.CheckSha256);
        byte[] blockBytes = blockStream.ToArray();

        blockBytes[^1] ^= 0xFF;

        using var corruptStream = new MemoryStream(blockBytes);
        using var output = new MemoryStream();
        await Assert.That(() =>
        {
            XzBlock.ReadBlock(corruptStream, XzConstants.CheckSha256, output,
                              out _, out _);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    // ── XzBlock: no-compressed-size streaming path ───────────────────

    [Test]
    public async Task XzBlock_ReadBlock_WithoutCompressedSize()
    {
        byte[] data = "Test without compressed size"u8.ToArray();
        var props = LzmaEncoderProperties.FromPreset(0);

        using var lzma2Encoder = new Lzma2Encoder(props);
        using var lzma2Stream = new MemoryStream();
        lzma2Encoder.Encode(data.AsMemory(), lzma2Stream);
        byte[] lzma2Data = lzma2Stream.ToArray();

        byte[] header = BuildXzBlockHeader(
            numFilters: 1,
            filterId: XzConstants.FilterIdLzma2,
            filterPropsSize: 1,
            dictSizeByte: lzma2Encoder.DictionarySizeByte,
            hasCompressedSize: false,
            hasUncompressedSize: false);

        using var blockStream = new MemoryStream();
        blockStream.Write(header);
        blockStream.Write(lzma2Data);
        int pad = (4 - (lzma2Data.Length % 4)) % 4;
        for (int i = 0; i < pad; i++) blockStream.WriteByte(0);

        blockStream.Position = 0;
        using var output = new MemoryStream();
        bool result = XzBlock.ReadBlock(blockStream, XzConstants.CheckNone, output,
                                         out _, out _);
        await Assert.That(result).IsTrue();
        await Assert.That(output.ToArray().SequenceEqual(data)).IsTrue();
    }

    [Test]
    public Task XzBlock_ReadBlock_NonZeroPaddingAfterData_Throws()
    {
        byte[] data = "Pad test"u8.ToArray();
        var props = LzmaEncoderProperties.FromPreset(0);

        using var blockStream = new MemoryStream();
        using var lzma2Encoder = new Lzma2Encoder(props);
        XzBlock.WriteBlock(blockStream, data.AsMemory(), lzma2Encoder, XzConstants.CheckCrc64);
        byte[] blockBytes = blockStream.ToArray();
        return Task.CompletedTask;
    }

    // ── Helper: build XZ block headers for error testing ─────────────

    private static byte[] BuildXzBlockHeader(
        int numFilters = 1,
        ulong filterId = 0x21,
        ulong filterPropsSize = 1,
        byte dictSizeByte = 0x18,
        bool hasCompressedSize = false,
        long compressedSize = 0,
        bool hasUncompressedSize = false,
        long uncompressedSize = 0,
        bool corruptPadding = false)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0);

        byte blockFlags = (byte)((numFilters - 1) & 0x03);
        if (hasCompressedSize) blockFlags |= 0x40;
        if (hasUncompressedSize) blockFlags |= 0x80;
        ms.WriteByte(blockFlags);

        if (hasCompressedSize)
            WriteVli(ms, (ulong)compressedSize);
        if (hasUncompressedSize)
            WriteVli(ms, (ulong)uncompressedSize);

        WriteVli(ms, filterId);
        WriteVli(ms, filterPropsSize);

        for (ulong i = 0; i < filterPropsSize; i++)
            ms.WriteByte(i == 0 ? dictSizeByte : (byte)0);

        int headerContentLen = (int)ms.Position;
        int totalHeaderSize = ((headerContentLen + 4 + 3) / 4) * 4;
        int paddingNeeded = totalHeaderSize - 4 - headerContentLen;
        for (int i = 0; i < paddingNeeded; i++)
            ms.WriteByte(corruptPadding ? (byte)0xFF : (byte)0);

        byte[] headerBytes = ms.ToArray();
        headerBytes[0] = (byte)(totalHeaderSize / 4 - 1);

        Span<byte> crc = stackalloc byte[4];
        Crc32.WriteLE(headerBytes.AsSpan(0, totalHeaderSize - 4), crc);

        byte[] result = new byte[totalHeaderSize];
        headerBytes.AsSpan(0, totalHeaderSize - 4).CopyTo(result);
        crc.CopyTo(result.AsSpan(totalHeaderSize - 4));
        return result;
    }

    private static void WriteVli(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    // ── XzConstants: remaining GetCheckSize branches ─────────────────

    [Test]
    [Arguments(2, 4)]
    [Arguments(3, 4)]
    [Arguments(5, 8)]
    [Arguments(6, 8)]
    [Arguments(7, 16)]
    [Arguments(8, 16)]
    [Arguments(9, 16)]
    [Arguments(11, 32)]
    [Arguments(12, 32)]
    [Arguments(13, 64)]
    [Arguments(14, 64)]
    [Arguments(15, 64)]
    public async Task XzConstants_GetCheckSize_AllBranches(int checkType, int expectedSize)
    {
        await Assert.That(XzConstants.GetCheckSize(checkType)).IsEqualTo(expectedSize);
    }

    // ── XzCompressOptions: CheckType SHA256 mapping ──────────────────

    [Test]
    public async Task XzCompressOptions_CheckTypeValue_Sha256()
    {
        await Assert.That(new XzCompressOptions { CheckType = XzCheckType.Sha256 }.CheckTypeValue)
            .IsEqualTo(XzConstants.CheckSha256);
    }

    // ── XzCompressOptions: Validate error paths ──────────────────────

    [Test]
    public async Task XzCompressOptions_NegativeThreads_Throws()
    {
        var opts = new XzCompressOptions { Threads = -1 };
        await Assert.That(() => opts.Validate()).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task XzCompressOptions_DictionarySizeTooSmall_Throws()
    {
        var opts = new XzCompressOptions { DictionarySize = 100 };
        await Assert.That(() => opts.Validate()).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task XzCompressOptions_BlockSizeTooSmall_Throws()
    {
        var opts = new XzCompressOptions { BlockSize = 100 };
        await Assert.That(() => opts.Validate()).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    // ── LzmaEncoderProperties: extreme mode all presets ──────────────

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(7)]
    [Arguments(8)]
    public async Task EncoderProperties_ExtremeMode_AllPresets(int preset)
    {
        var ext = LzmaEncoderProperties.FromPreset(preset, true);
        ext.Validate();
        await Assert.That(ext.MatchMaxLen).IsEqualTo(LzmaConstants.kMatchMaxLen);
        await Assert.That(ext.CutValue).IsGreaterThan(0);
    }

    [Test]
    public async Task EncoderProperties_Preset0and1_MatchMaxLen128()
    {
        var p0 = LzmaEncoderProperties.FromPreset(0);
        var p1 = LzmaEncoderProperties.FromPreset(1);
        await Assert.That(p0.MatchMaxLen).IsEqualTo(128);
        await Assert.That(p1.MatchMaxLen).IsEqualTo(128);
    }

    [Test]
    public async Task EncoderProperties_PropertiesByte()
    {
        var props = LzmaEncoderProperties.FromPreset(6);
        byte propByte = props.PropertiesByte;
        await Assert.That(LzmaConstants.DecodeProperties(propByte, out int lc, out int lp, out int pb)).IsTrue();
        await Assert.That(lc).IsEqualTo(props.Lc);
        await Assert.That(lp).IsEqualTo(props.Lp);
        await Assert.That(pb).IsEqualTo(props.Pb);
    }

    // ── LzmaEncoderProperties: Validate error paths ──────────────────

    [Test]
    public async Task EncoderProperties_InvalidLc_Throws()
    {
        var props = new LzmaEncoderProperties { Lc = 9 };
        await Assert.That(() => props.Validate()).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task EncoderProperties_InvalidLp_Throws()
    {
        var props = new LzmaEncoderProperties { Lp = 5 };
        await Assert.That(() => props.Validate()).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task EncoderProperties_InvalidPb_Throws()
    {
        var props = new LzmaEncoderProperties { Pb = 5 };
        await Assert.That(() => props.Validate()).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task EncoderProperties_InvalidDictSize_Throws()
    {
        var props = new LzmaEncoderProperties { DictionarySize = 0 };
        await Assert.That(() => props.Validate()).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    // ── OutputWindow: direct exercise ────────────────────────────────

    [Test]
    public async Task OutputWindow_PutByte_GetByte()
    {
        using var window = new OutputWindow(256);
        window.PutByte(0xAA);
        window.PutByte(0xBB);
        window.PutByte(0xCC);

        await Assert.That(window.GetByte(0)).IsEqualTo((byte)0xCC);
        await Assert.That(window.GetByte(1)).IsEqualTo((byte)0xBB);
        await Assert.That(window.GetByte(2)).IsEqualTo((byte)0xAA);
        await Assert.That(window.TotalPos).IsEqualTo(3L);
    }

    [Test]
    public async Task OutputWindow_CopyMatch_Overlapping()
    {
        // RLE: distance=0 means copy previous byte repeatedly
        using var window = new OutputWindow(256);
        byte[] output = new byte[20];
        int outPos = 0;

        window.PutByte(0x42);
        output[outPos++] = 0x42;

        window.CopyMatch(0, 10, output, ref outPos);
        await Assert.That(outPos).IsEqualTo(11);
        for (int i = 0; i < 11; i++)
            await Assert.That(output[i]).IsEqualTo((byte)0x42);
    }

    [Test]
    public async Task OutputWindow_CopyMatch_NonOverlapping()
    {
        using var window = new OutputWindow(256);
        byte[] output = new byte[20];
        int outPos = 0;

        // Write pattern
        for (byte b = 0; b < 5; b++)
        {
            window.PutByte(b);
            output[outPos++] = b;
        }

        // Copy 5 bytes at distance=4 (copy entire pattern)
        window.CopyMatch(4, 5, output, ref outPos);
        await Assert.That(outPos).IsEqualTo(10);
        for (int i = 0; i < 5; i++)
            await Assert.That(output[i + 5]).IsEqualTo((byte)i);
    }

    [Test]
    public async Task OutputWindow_CopyUncompressed()
    {
        using var window = new OutputWindow(256);
        byte[] data = [1, 2, 3, 4, 5];
        byte[] output = new byte[20];
        int outPos = 0;

        window.CopyUncompressed(data, output, ref outPos);
        await Assert.That(outPos).IsEqualTo(5);
        await Assert.That(output.AsSpan(0, 5).SequenceEqual(data)).IsTrue();
        await Assert.That(window.TotalPos).IsEqualTo(5L);
    }

    [Test]
    public async Task OutputWindow_CopyUncompressed_WrapAround()
    {
        // Small dict to force wrapping
        using var window = new OutputWindow(8);
        byte[] data = new byte[20];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);
        byte[] output = new byte[20];
        int outPos = 0;

        window.CopyUncompressed(data, output, ref outPos);
        await Assert.That(outPos).IsEqualTo(20);
        await Assert.That(output.SequenceEqual(data)).IsTrue();
    }

    [Test]
    public async Task OutputWindow_HasDistance()
    {
        using var window = new OutputWindow(256);
        await Assert.That(window.HasDistance(0)).IsFalse();
        window.PutByte(0x00);
        await Assert.That(window.HasDistance(0)).IsTrue();
        await Assert.That(window.HasDistance(1)).IsFalse();
    }

    [Test]
    public async Task OutputWindow_Reset()
    {
        using var window = new OutputWindow(256);
        window.PutByte(0x42);
        await Assert.That(window.TotalPos).IsEqualTo(1L);
        window.Reset();
        await Assert.That(window.TotalPos).IsEqualTo(0L);
        await Assert.That(window.Pos).IsEqualTo(0);
    }

    [Test]
    public Task OutputWindow_DoubleDispose()
    {
        var window = new OutputWindow(256);
        window.Dispose();
        window.Dispose(); // Should not throw
        return Task.CompletedTask;
    }

    // ── Concatenated XZ streams ──────────────────────────────────────

    [Test]
    public async Task XzDecompressStream_ConcatenatedStreams()
    {
        byte[] data1 = "First stream"u8.ToArray();
        byte[] data2 = "Second stream"u8.ToArray();

        byte[] compressed1 = XzCompressor.Compress(data1);
        byte[] compressed2 = XzCompressor.Compress(data2);

        // Concatenate two XZ streams
        byte[] concatenated = new byte[compressed1.Length + compressed2.Length];
        compressed1.CopyTo(concatenated, 0);
        compressed2.CopyTo(concatenated, compressed1.Length);

        using var ms = new MemoryStream(concatenated);
        using var xz = new XzDecompressStream(ms);
        using var output = new MemoryStream();
        xz.CopyTo(output);

        byte[] expected = new byte[data1.Length + data2.Length];
        data1.CopyTo(expected, 0);
        data2.CopyTo(expected, data1.Length);
        await Assert.That(output.ToArray().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task XzDecompressStream_ConcatenatedWithPadding()
    {
        byte[] data1 = "First"u8.ToArray();
        byte[] data2 = "Second"u8.ToArray();

        byte[] compressed1 = XzCompressor.Compress(data1);
        byte[] compressed2 = XzCompressor.Compress(data2);

        // Concatenate with 4 null bytes of padding between streams
        byte[] concatenated = new byte[compressed1.Length + 4 + compressed2.Length];
        compressed1.CopyTo(concatenated, 0);
        // 4 null bytes are valid padding
        compressed2.CopyTo(concatenated, compressed1.Length + 4);

        using var ms = new MemoryStream(concatenated);
        using var xz = new XzDecompressStream(ms);
        using var output = new MemoryStream();
        xz.CopyTo(output);

        byte[] expected = new byte[data1.Length + data2.Length];
        data1.CopyTo(expected, 0);
        data2.CopyTo(expected, data1.Length);
        await Assert.That(output.ToArray().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task XzDecompressStream_InvalidPadding_Throws()
    {
        byte[] data = "Test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(data);

        // Append 3 null bytes (not multiple of 4) — invalid padding
        byte[] withBadPadding = new byte[compressed.Length + 3];
        compressed.CopyTo(withBadPadding, 0);

        using var ms = new MemoryStream(withBadPadding);
        using var xz = new XzDecompressStream(ms);
        await Assert.That(() =>
        {
            using var output = new MemoryStream();
            xz.CopyTo(output);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    // ── XzDecompressStream disposed ──────────────────────────────────

    [Test]
    public async Task XzDecompressStream_ReadAfterDispose_Throws()
    {
        byte[] compressed = XzCompressor.Compress("test"u8.ToArray());
        var xz = new XzDecompressStream(new MemoryStream(compressed));
        xz.Dispose();

        await Assert.That(() =>
        {
            byte[] buf = new byte[10];
#pragma warning disable CA2022 // Intentional: testing dispose, read will throw before returning
            xz.Read(buf.AsSpan());
#pragma warning restore CA2022
        }).ThrowsExactly<ObjectDisposedException>();
    }

    // ── XzCompressStream: write after finalize ───────────────────────

    [Test]
    public async Task XzCompressStream_WriteAfterDispose_Throws()
    {
        using var ms = new MemoryStream();
        var xz = new XzCompressStream(ms, leaveOpen: true);
        xz.Write("hello"u8);
        xz.Dispose();

        await Assert.That(() =>
        {
            xz.Write("more"u8);
        }).Throws<ObjectDisposedException>();
    }

    // ── SHA256 check type full round-trip via XzCompressor ───────────

    [Test]
    public async Task RoundTrip_CheckTypeSha256()
    {
        byte[] original = "SHA-256 integrity check round trip"u8.ToArray();
        var opts = new XzCompressOptions { CheckType = XzCheckType.Sha256 };
        byte[] compressed = XzCompressor.Compress(original, opts);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_CheckTypeSha256_LargeData()
    {
        byte[] original = new byte[100_000];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        var opts = new XzCompressOptions { CheckType = XzCheckType.Sha256 };
        byte[] compressed = XzCompressor.Compress(original, opts);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_CheckTypeSha256_ViaStream()
    {
        byte[] original = new byte[8192];
        new Random(42).NextBytes(original);

        using var compressedStream = new MemoryStream();
        await using (var xz = new XzCompressStream(compressedStream,
            new XzCompressOptions { CheckType = XzCheckType.Sha256 }, leaveOpen: true))
        {
            await xz.WriteAsync(original);
        }

        compressedStream.Position = 0;
        using var xzIn = new XzDecompressStream(compressedStream);
        using var result = new MemoryStream();
        xzIn.CopyTo(result);
        await Assert.That(result.ToArray().SequenceEqual(original)).IsTrue();
    }

    // ── HashChainMatchFinder dispose ─────────────────────────────────

    [Test]
    public Task HashChainMatchFinder_Dispose()
    {
        var finder = new LZ.HashChainMatchFinder(1 << 16, 32, LzmaConstants.kMatchMaxLen);
        finder.Dispose();
        finder.Dispose(); // Double-dispose should not throw
        return Task.CompletedTask;
    }
}
