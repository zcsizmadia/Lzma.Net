// SPDX-License-Identifier: 0BSD

namespace LzmaNet.Tests;

/// <summary>
/// Tests for the BCJ/Delta filter encoding, seekable random access,
/// output-size limits, and decoder robustness against corrupted input.
/// </summary>
public class FilterSeekAndLimitTests
{
    // ── BCJ/Delta filter encoding ────────────────────────────────────

    private static byte[] MakeExecutableLikeData(int size)
    {
        // x86-flavored: E8 (call) opcodes with rel32 offsets pointing at a small
        // set of "function addresses". Relative offsets differ at every call
        // site, but after BCJ's rel->abs conversion they become identical
        // 4-byte sequences — exactly the redundancy the filter exposes.
        var data = new byte[size];
        var rng = new Random(77);
        int[] functions = new int[40];
        for (int f = 0; f < functions.Length; f++)
            functions[f] = rng.Next(size);

        int i = 0;
        while (i < size - 8)
        {
            if (rng.Next(4) == 0)
            {
                int target = functions[rng.Next(functions.Length)];
                int rel = target - (i + 5); // rel32 is relative to the next instruction
                data[i++] = 0xE8;
                data[i++] = (byte)rel;
                data[i++] = (byte)(rel >> 8);
                data[i++] = (byte)(rel >> 16);
                data[i++] = (byte)(rel >> 24);
            }
            else
            {
                data[i] = (byte)(0x40 + (i % 32));
                i++;
            }
        }
        return data;
    }

    [Test]
    [Arguments(XzFilterType.X86)]
    [Arguments(XzFilterType.Arm)]
    [Arguments(XzFilterType.ArmThumb)]
    [Arguments(XzFilterType.Arm64)]
    [Arguments(XzFilterType.PowerPc)]
    [Arguments(XzFilterType.Sparc)]
    [Arguments(XzFilterType.Ia64)]
    [Arguments(XzFilterType.RiscV)]
    [Arguments(XzFilterType.Delta)]
    public async Task Filter_RoundTrip(XzFilterType filter)
    {
        byte[] original = MakeExecutableLikeData(300 * 1024);
        var opts = new XzCompressOptions { Preset = 3, Filter = filter, DeltaDistance = 4 };

        byte[] compressed = XzCompressor.Compress(original, opts);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task Filter_X86_ImprovesRatioOnExecutableLikeData()
    {
        byte[] original = MakeExecutableLikeData(1024 * 1024);

        byte[] plain = XzCompressor.Compress(original, new XzCompressOptions { Preset = 6 });
        byte[] filtered = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = 6, Filter = XzFilterType.X86 });

        await Assert.That(filtered.Length < plain.Length).IsTrue();
        await Assert.That(XzCompressor.Decompress(filtered).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task Filter_MultiThreaded_RoundTrip()
    {
        byte[] original = MakeExecutableLikeData(2 * 1024 * 1024);
        var opts = new XzCompressOptions
        {
            Preset = 3,
            Filter = XzFilterType.X86,
            Threads = 4,
            BlockSize = 256 * 1024,
        };

        byte[] compressed = XzCompressor.Compress(original, opts);
        byte[] decompressed = XzCompressor.Decompress(compressed, threads: 4);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task Filter_Delta_RoundTripWithDistance()
    {
        // Stride-3 "RGB" data — exactly what Delta is for.
        byte[] original = new byte[256 * 1024];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)((i / 3) + (i % 3) * 80);

        var opts = new XzCompressOptions { Preset = 3, Filter = XzFilterType.Delta, DeltaDistance = 3 };
        byte[] compressed = XzCompressor.Compress(original, opts);
        await Assert.That(XzCompressor.Decompress(compressed).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task Filter_InvalidDeltaDistance_Throws()
    {
        var opts = new XzCompressOptions { Filter = XzFilterType.Delta, DeltaDistance = 0 };
        await Assert.That(() => XzCompressor.Compress(new byte[16], opts))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    // ── Seekable random access ───────────────────────────────────────

    private static (byte[] Original, byte[] Compressed) MakeMultiBlock(int size, int blockSize)
    {
        byte[] original = new byte[size];
        var rng = new Random(11);
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 256 < 200 ? i % 37 + i / 65536 : rng.Next(256));
        var opts = new XzCompressOptions { Preset = 1, BlockSize = blockSize };
        return (original, XzCompressor.Compress(original, opts));
    }

    [Test]
    public async Task Seekable_RandomPositions_MatchOriginal()
    {
        var (original, compressed) = MakeMultiBlock(2 * 1024 * 1024, 128 * 1024);
        using var xz = new XzSeekableStream(new MemoryStream(compressed));

        await Assert.That(xz.Length).IsEqualTo((long)original.Length);
        await Assert.That(xz.BlockCount > 1).IsTrue();

        var rng = new Random(5);
        byte[] buf = new byte[8192];
        for (int i = 0; i < 50; i++)
        {
            long pos = rng.Next(original.Length);
            xz.Position = pos;
            int read = xz.Read(buf, 0, buf.Length);
            int expected = (int)Math.Min(buf.Length, original.Length - pos);
            await Assert.That(read).IsEqualTo(expected);
            await Assert.That(buf.AsSpan(0, read).SequenceEqual(original.AsSpan((int)pos, read))).IsTrue();
        }
    }

    [Test]
    public async Task Seekable_ReadsAcrossBlockBoundaries()
    {
        var (original, compressed) = MakeMultiBlock(512 * 1024, 64 * 1024);
        using var xz = new XzSeekableStream(new MemoryStream(compressed));

        // Read a span crossing several block boundaries in one call.
        byte[] buf = new byte[200 * 1024];
        xz.Position = 50 * 1024;
        int read = xz.Read(buf, 0, buf.Length);
        await Assert.That(read).IsEqualTo(buf.Length);
        await Assert.That(buf.AsSpan().SequenceEqual(original.AsSpan(50 * 1024, buf.Length))).IsTrue();
    }

    [Test]
    public async Task Seekable_SequentialFullRead_MatchesDecompress()
    {
        var (original, compressed) = MakeMultiBlock(768 * 1024, 64 * 1024);
        using var xz = new XzSeekableStream(new MemoryStream(compressed));
        using var output = new MemoryStream();
        xz.CopyTo(output);
        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task Seekable_ConcatenatedStreamsWithPadding()
    {
        var (orig1, comp1) = MakeMultiBlock(300 * 1024, 64 * 1024);
        var (orig2, comp2) = MakeMultiBlock(200 * 1024, 64 * 1024);
        using var ms = new MemoryStream();
        ms.Write(comp1);
        ms.Write(new byte[12]); // stream padding (multiple of 4)
        ms.Write(comp2);

        using var xz = new XzSeekableStream(new MemoryStream(ms.ToArray()));
        await Assert.That(xz.Length).IsEqualTo((long)(orig1.Length + orig2.Length));

        // Read across the stream boundary.
        byte[] buf = new byte[64 * 1024];
        xz.Position = orig1.Length - 32 * 1024;
        int read = xz.Read(buf, 0, buf.Length);
        await Assert.That(read).IsEqualTo(buf.Length);
        await Assert.That(buf.AsSpan(0, 32 * 1024).SequenceEqual(orig1.AsSpan(orig1.Length - 32 * 1024))).IsTrue();
        await Assert.That(buf.AsSpan(32 * 1024).SequenceEqual(orig2.AsSpan(0, 32 * 1024))).IsTrue();
    }

    [Test]
    public async Task Seekable_SeekOrigins_Work()
    {
        var (original, compressed) = MakeMultiBlock(256 * 1024, 64 * 1024);
        using var xz = new XzSeekableStream(new MemoryStream(compressed));

        await Assert.That(xz.Seek(1000, SeekOrigin.Begin)).IsEqualTo(1000L);
        await Assert.That(xz.Seek(24, SeekOrigin.Current)).IsEqualTo(1024L);
        await Assert.That(xz.Seek(-1024, SeekOrigin.End)).IsEqualTo((long)original.Length - 1024);

        byte[] buf = new byte[1024];
        await Assert.That(xz.Read(buf, 0, buf.Length)).IsEqualTo(1024);
        await Assert.That(buf.AsSpan().SequenceEqual(original.AsSpan(original.Length - 1024))).IsTrue();
        await Assert.That(xz.Read(buf, 0, buf.Length)).IsEqualTo(0); // EOF
    }

    [Test]
    public async Task Seekable_EmptyInput_HasZeroLength()
    {
        byte[] compressed = XzCompressor.Compress(ReadOnlySpan<byte>.Empty);
        using var xz = new XzSeekableStream(new MemoryStream(compressed));
        await Assert.That(xz.Length).IsEqualTo(0L);
        await Assert.That(xz.Read(new byte[16], 0, 16)).IsEqualTo(0);
    }

    [Test]
    public async Task Seekable_NonSeekableStream_Throws()
    {
        byte[] compressed = XzCompressor.Compress(new byte[100]);
        using var storage = new MemoryStream(compressed);
        using var nonSeekable = new StreamAndFormatRegressionTests.NonSeekableStream(
            storage, canRead: true, canWrite: false);
        await Assert.That(() => new XzSeekableStream(nonSeekable))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task Seekable_WorksWithFilteredStreams()
    {
        byte[] original = MakeExecutableLikeData(512 * 1024);
        var opts = new XzCompressOptions
        {
            Preset = 3,
            Filter = XzFilterType.X86,
            BlockSize = 64 * 1024,
        };
        byte[] compressed = XzCompressor.Compress(original, opts);

        using var xz = new XzSeekableStream(new MemoryStream(compressed));
        byte[] buf = new byte[10000];
        xz.Position = 123456;
        int read = xz.Read(buf, 0, buf.Length);
        await Assert.That(read).IsEqualTo(buf.Length);
        await Assert.That(buf.AsSpan().SequenceEqual(original.AsSpan(123456, buf.Length))).IsTrue();
    }

    // ── Output size limit (decompression-bomb protection) ────────────

    [Test]
    public async Task MaxOutputSize_AllowsExactSize()
    {
        byte[] original = new byte[100 * 1024];
        Array.Fill(original, (byte)0x42);
        byte[] compressed = XzCompressor.Compress(original);

        var opts = new XzDecompressOptions { MaxOutputSize = original.Length };
        byte[] decompressed = XzCompressor.Decompress(compressed, opts);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task MaxOutputSize_RejectsOversizedOutput()
    {
        // Highly compressible: 1 MB of zeros compresses to ~1 KB, so a small
        // MaxOutputSize must reject it long before 1 MB is allocated.
        byte[] compressed = XzCompressor.Compress(new byte[1024 * 1024]);

        var opts = new XzDecompressOptions { MaxOutputSize = 64 * 1024 };
        await Assert.That(() => XzCompressor.Decompress(compressed, opts))
            .ThrowsExactly<LzmaMemoryLimitException>();
    }

    [Test]
    public async Task MaxOutputSize_EnforcedAcrossMultipleBlocks()
    {
        byte[] original = new byte[512 * 1024];
        var opts = new XzCompressOptions { Preset = 1, BlockSize = 64 * 1024 };
        byte[] compressed = XzCompressor.Compress(original, opts);

        // Each block is small, but the total exceeds the limit.
        var dopts = new XzDecompressOptions { MaxOutputSize = 256 * 1024 };
        await Assert.That(() => XzCompressor.Decompress(compressed, dopts))
            .ThrowsExactly<LzmaMemoryLimitException>();
    }

    [Test]
    public async Task MaxOutputSize_EnforcedInParallelMode()
    {
        byte[] original = new byte[1024 * 1024];
        var opts = new XzCompressOptions { Preset = 1, BlockSize = 64 * 1024 };
        byte[] compressed = XzCompressor.Compress(original, opts);

        var dopts = new XzDecompressOptions { Threads = 4, MaxOutputSize = 256 * 1024 };
        await Assert.That(() => XzCompressor.Decompress(compressed, dopts))
            .ThrowsExactly<LzmaMemoryLimitException>();
    }

    [Test]
    public async Task MaxOutputSize_InvalidValue_Throws()
    {
        var opts = new XzDecompressOptions { MaxOutputSize = 0 };
        await Assert.That(() => new XzDecompressStream(new MemoryStream(), opts))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    // ── Robustness: mutated and truncated input must fail cleanly ────

    [Test]
    public async Task CorruptedInput_AlwaysFailsWithLzmaException()
    {
        var (original, compressed) = MakeMultiBlock(256 * 1024, 64 * 1024);
        var dopts = new XzDecompressOptions { MaxOutputSize = 4 * original.Length };

        int survived = 0;
        for (int i = 0; i < 400; i++)
        {
            byte[] mutated = (byte[])compressed.Clone();
            int pos = (i * 104729) % mutated.Length; // deterministic spread
            mutated[pos] ^= (byte)(1 + (i % 255));

            try
            {
                byte[] result = XzCompressor.Decompress(mutated, dopts);
                // A mutation may land in a dead spot (e.g., padding already
                // validated as zero would throw; but e.g. unused bits can
                // survive) — surviving is acceptable, crashing is not.
                survived++;
                _ = result;
            }
            catch (LzmaException)
            {
                // Expected: corrupt data must surface as an LzmaException family
                // error, never IndexOutOfRange/Overflow/OOM etc.
            }
        }

        // Sanity: the vast majority of single-byte corruptions must be caught.
        await Assert.That(survived < 40).IsTrue();
    }

    [Test]
    public async Task TruncatedInput_AlwaysFailsWithLzmaException()
    {
        var (_, compressed) = MakeMultiBlock(256 * 1024, 64 * 1024);
        var dopts = new XzDecompressOptions { MaxOutputSize = 1024 * 1024 };

        int notThrown = 0;
        for (int len = 0; len < compressed.Length; len += 101)
        {
            try
            {
                XzCompressor.Decompress(compressed.AsSpan(0, len), dopts);
                notThrown++;
            }
            catch (LzmaException)
            {
                // Expected.
            }
        }
        await Assert.That(notThrown).IsEqualTo(0);
    }

    [Test]
    public async Task CorruptedInput_ParallelMode_FailsWithLzmaException()
    {
        var (_, compressed) = MakeMultiBlock(512 * 1024, 64 * 1024);
        var dopts = new XzDecompressOptions { Threads = 4, MaxOutputSize = 2 * 1024 * 1024 };

        int caught = 0;
        for (int i = 0; i < 100; i++)
        {
            byte[] mutated = (byte[])compressed.Clone();
            mutated[(i * 52361) % mutated.Length] ^= 0xFF;
            try
            {
                XzCompressor.Decompress(mutated, dopts);
            }
            catch (LzmaException)
            {
                caught++;
            }
        }
        await Assert.That(caught > 80).IsTrue();
    }
}
