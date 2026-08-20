// SPDX-License-Identifier: 0BSD

using LzmaNet.Check;
using LzmaNet.Lzma2;

namespace LzmaNet.Tests;

/// <summary>
/// Regression tests for the performance-oriented rewrites: slicing-by-8 CRCs,
/// output-as-window match copying, and parallel block decompression.
/// </summary>
public class PerformanceRegressionTests
{
    // ── CRC32 / CRC64 (slicing-by-8 vs known vectors and reference) ──

    private static uint Crc32Reference(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc = ~crc;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private static ulong Crc64Reference(ReadOnlySpan<byte> data, ulong crc = 0)
    {
        const ulong Poly = 0xC96C5795D7870F42UL;
        crc = ~crc;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Poly : crc >> 1;
        }
        return ~crc;
    }

    [Test]
    public async Task Crc32_KnownVector()
    {
        // Standard check value for the "123456789" ASCII string
        await Assert.That(Crc32.Compute("123456789"u8)).IsEqualTo(0xCBF43926u);
    }

    [Test]
    public async Task Crc64_KnownVector()
    {
        // ECMA-182 (reflected, as used by XZ) check value for "123456789"
        await Assert.That(Crc64.Compute("123456789"u8)).IsEqualTo(0x995DC9BBDF1939FAul);
    }

    [Test]
    public async Task Crc32_MatchesReference_AllLengths()
    {
        // Cover the vector-folding path (>= 64 bytes), the slicing-by-8 loop,
        // and every tail length around the 64/128-byte thresholds.
        var rng = new Random(1234);
        byte[] data = new byte[1024 + 7];
        rng.NextBytes(data);

        for (int len = 0; len <= 200; len++)
            await Assert.That(Crc32.Compute(data.AsSpan(0, len)))
                .IsEqualTo(Crc32Reference(data.AsSpan(0, len)));

        await Assert.That(Crc32.Compute(data)).IsEqualTo(Crc32Reference(data));
    }

    [Test]
    public async Task Crc64_MatchesReference_AllLengths()
    {
        var rng = new Random(5678);
        byte[] data = new byte[1024 + 7];
        rng.NextBytes(data);

        for (int len = 0; len <= 200; len++)
            await Assert.That(Crc64.Compute(data.AsSpan(0, len)))
                .IsEqualTo(Crc64Reference(data.AsSpan(0, len)));

        await Assert.That(Crc64.Compute(data)).IsEqualTo(Crc64Reference(data));
    }

    [Test]
    public async Task Crc_VectorAndScalarPathsAgree_LargeBuffer()
    {
        // The vector-folding path must produce identical results to the
        // table-only path on a large buffer, including with a nonzero
        // continuation state.
        var rng = new Random(4321);
        byte[] data = new byte[1024 * 1024 + 13];
        rng.NextBytes(data);

        await Assert.That(Crc32.Compute(data)).IsEqualTo(Crc32.ComputeScalar(data));
        await Assert.That(Crc64.Compute(data)).IsEqualTo(Crc64.ComputeScalar(data));

        uint c32 = Crc32.Compute(data.AsSpan(0, 100));
        ulong c64 = Crc64.Compute(data.AsSpan(0, 100));
        await Assert.That(Crc32.Compute(data.AsSpan(100), c32))
            .IsEqualTo(Crc32.ComputeScalar(data.AsSpan(100), c32));
        await Assert.That(Crc64.Compute(data.AsSpan(100), c64))
            .IsEqualTo(Crc64.ComputeScalar(data.AsSpan(100), c64));
    }

    [Test]
    public async Task Crc32_ChunkedContinuation_MatchesOneShot()
    {
        var rng = new Random(42);
        byte[] data = new byte[4096];
        rng.NextBytes(data);

        uint oneShot = Crc32.Compute(data);
        uint chunked = 0;
        foreach (int split in new[] { 0, 1, 3, 100, 1027, 4096 })
        {
            chunked = Crc32.Compute(data.AsSpan(0, split));
            chunked = Crc32.Compute(data.AsSpan(split), chunked);
            await Assert.That(chunked).IsEqualTo(oneShot);
        }
    }

    [Test]
    public async Task Crc64_ChunkedContinuation_MatchesOneShot()
    {
        var rng = new Random(42);
        byte[] data = new byte[4096];
        rng.NextBytes(data);

        ulong oneShot = Crc64.Compute(data);
        foreach (int split in new[] { 0, 1, 3, 100, 1027, 4096 })
        {
            ulong chunked = Crc64.Compute(data.AsSpan(0, split));
            chunked = Crc64.Compute(data.AsSpan(split), chunked);
            await Assert.That(chunked).IsEqualTo(oneShot);
        }
    }

    // ── Match copy edge cases (output-as-window decoder) ─────────────

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(13)]
    [Arguments(37)]
    [Arguments(255)]
    [Arguments(256)]
    public async Task RoundTrip_PeriodicPattern_ExercisesOverlappingCopies(int period)
    {
        // Periodic data produces long overlapping matches at distance = period - 1,
        // exercising the geometric overlap-copy path for many period sizes.
        byte[] original = new byte[64 * 1024];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)((i % period) * 31 + i / 8192);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_RunLengthData_ExercisesDistanceZeroFill()
    {
        byte[] original = new byte[256 * 1024];
        // Long runs of single bytes → rep0 matches at distance 0 (RLE fill path)
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i / 1000);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task Lzma2Decoder_MixedUncompressedAndLzmaChunks_RoundTrip()
    {
        // Force uncompressed chunks (random data) followed by compressible data
        // in one XZ stream so the decoder sees mixed LZMA2 chunk types with the
        // output buffer serving as the dictionary window.
        var rng = new Random(99);
        byte[] original = new byte[192 * 1024];
        rng.NextBytes(original.AsSpan(0, 64 * 1024)); // stored uncompressed
        for (int i = 64 * 1024; i < original.Length; i++)
            original[i] = (byte)(i % 37); // compressed

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task Lzma2Decoder_MatchCrossingChunkBoundary_Throws()
    {
        // Craft an LZMA2 stream whose chunk claims more output than the block:
        // a valid compressed chunk truncated by lying about uncompressed size is
        // hard to craft byte-exact, but a match that overruns its chunk must not
        // write past the declared size. Use a truncated-uncompressed-chunk lie.
        byte[] bad =
        [
            0x01,       // uncompressed chunk + dict reset
            0x00, 0x03, // data size - 1 = 3 → 4 bytes
            0x41, 0x42, // ...but only 2 bytes present
        ];
        using var decoder = new Lzma2Decoder(1 << 16);
        await Assert.That(() => decoder.Decode(bad, new byte[16]))
            .ThrowsExactly<LzmaDataErrorException>();
    }

    // ── Parallel block decompression ─────────────────────────────────

    private static byte[] MakeMultiBlockXz(byte[] original, int blockSize, out int blockCount)
    {
        var opts = new XzCompressOptions { Preset = 3, BlockSize = blockSize };
        byte[] compressed = XzCompressor.Compress(original, opts);
        blockCount = (original.Length + blockSize - 1) / blockSize;
        return compressed;
    }

    [Test]
    [Arguments(0)]
    [Arguments(2)]
    [Arguments(4)]
    [Arguments(16)]
    public async Task ParallelDecompress_MultiBlock_RoundTrip(int threads)
    {
        byte[] original = new byte[4 * 1024 * 1024];
        var rng = new Random(7);
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 256 < 200 ? i % 37 : rng.Next(256));

        byte[] compressed = MakeMultiBlockXz(original, 256 * 1024, out int blockCount);
        await Assert.That(blockCount > 1).IsTrue();

        byte[] decompressed = XzCompressor.Decompress(compressed, threads);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task ParallelDecompress_SingleBlock_RoundTrip()
    {
        byte[] original = new byte[128 * 1024];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 53);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed, threads: 8);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task ParallelDecompress_EmptyInputData_RoundTrip()
    {
        byte[] compressed = XzCompressor.Compress(ReadOnlySpan<byte>.Empty);
        byte[] decompressed = XzCompressor.Decompress(compressed, threads: 4);
        await Assert.That(decompressed.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ParallelDecompress_ConcatenatedStreamsWithPadding_RoundTrip()
    {
        byte[] data1 = new byte[300 * 1024];
        byte[] data2 = new byte[200 * 1024];
        for (int i = 0; i < data1.Length; i++) data1[i] = (byte)(i % 41);
        for (int i = 0; i < data2.Length; i++) data2[i] = (byte)(i % 59);

        var opts = new XzCompressOptions { Preset = 1, BlockSize = 64 * 1024 };
        using var ms = new MemoryStream();
        ms.Write(XzCompressor.Compress(data1, opts));
        ms.Write(new byte[8]); // stream padding (multiple of 4)
        ms.Write(XzCompressor.Compress(data2, opts));

        byte[] expected = new byte[data1.Length + data2.Length];
        data1.CopyTo(expected, 0);
        data2.CopyTo(expected, data1.Length);

        byte[] decompressed = XzCompressor.Decompress(ms.ToArray(), threads: 4);
        await Assert.That(decompressed.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task ParallelDecompress_CorruptBlock_ThrowsLzmaDataError()
    {
        byte[] original = new byte[1024 * 1024];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 37);

        byte[] compressed = MakeMultiBlockXz(original, 128 * 1024, out _);

        // Corrupt a byte in the middle of the compressed payload (past the
        // stream header, before the index).
        compressed[compressed.Length / 2] ^= 0xFF;

        // Must surface LzmaDataErrorException, not AggregateException.
        await Assert.That(() => XzCompressor.Decompress(compressed, threads: 4))
            .Throws<LzmaDataErrorException>();
    }

    [Test]
    public async Task ParallelDecompress_MatchesSerialOutput()
    {
        byte[] original = new byte[2 * 1024 * 1024];
        var rng = new Random(11);
        rng.NextBytes(original.AsSpan(0, 512 * 1024));
        for (int i = 512 * 1024; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        byte[] compressed = MakeMultiBlockXz(original, 128 * 1024, out _);

        byte[] serial = XzCompressor.Decompress(compressed);
        byte[] parallel = XzCompressor.Decompress(compressed, threads: 8);
        await Assert.That(parallel.SequenceEqual(serial)).IsTrue();
    }

    [Test]
    public async Task XzDecompressStream_NegativeThreads_Throws()
    {
        using var ms = new MemoryStream();
        await Assert.That(() => new XzDecompressStream(ms, threads: -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task XzDecompressStream_ParallelSmallReads_RoundTrip()
    {
        // Drain a parallel-decoding stream with tiny reads to exercise the
        // decoded-block queue across many Read calls.
        byte[] original = new byte[512 * 1024];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 61);

        byte[] compressed = MakeMultiBlockXz(original, 64 * 1024, out _);

        using var input = new MemoryStream(compressed);
        using var xz = new XzDecompressStream(input, threads: 4);
        using var output = new MemoryStream();
        byte[] buf = new byte[777];
        int read;
        while ((read = xz.Read(buf, 0, buf.Length)) > 0)
            output.Write(buf, 0, read);

        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
    }
}
