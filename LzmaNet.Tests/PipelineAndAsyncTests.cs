// SPDX-License-Identifier: 0BSD

namespace LzmaNet.Tests;

/// <summary>
/// Tests for the multi-threaded compression pipeline and asynchronous parallel
/// block decompression.
/// </summary>
public class PipelineAndAsyncTests
{
    private static byte[] MakeData(int size, int seed = 11)
    {
        byte[] data = new byte[size];
        var rng = new Random(seed);
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 256 < 200 ? i % 37 + i / 65536 : rng.Next(256));
        return data;
    }

    // ── Multi-threaded compression pipeline ──────────────────────────

    [Test]
    [Arguments(2)]
    [Arguments(4)]
    [Arguments(16)]
    public async Task Pipeline_RoundTrip(int threads)
    {
        byte[] original = MakeData(4 * 1024 * 1024);
        var opts = new XzCompressOptions { Preset = 1, Threads = threads, BlockSize = 256 * 1024 };

        byte[] compressed = XzCompressor.Compress(original, opts);
        byte[] decompressed = XzCompressor.Decompress(compressed, threads);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task Pipeline_OutputIsByteIdenticalToSingleThreaded()
    {
        byte[] original = MakeData(3 * 1024 * 1024 + 12345); // partial final block
        var optsMt = new XzCompressOptions { Preset = 1, Threads = 8, BlockSize = 256 * 1024 };
        var opts1t = new XzCompressOptions { Preset = 1, Threads = 1, BlockSize = 256 * 1024 };

        byte[] mt = XzCompressor.Compress(original, optsMt);
        byte[] st = XzCompressor.Compress(original, opts1t);
        await Assert.That(mt.SequenceEqual(st)).IsTrue();
    }

    [Test]
    public async Task Pipeline_ManySmallWrites_RoundTrip()
    {
        // Feed the stream in odd-sized writes so block extraction happens at
        // various buffer fill levels.
        byte[] original = MakeData(2 * 1024 * 1024 + 777);
        using var output = new MemoryStream();
        using (var xz = new XzCompressStream(output,
            new XzCompressOptions { Preset = 1, Threads = 4, BlockSize = 128 * 1024 }, leaveOpen: true))
        {
            int pos = 0;
            var rng = new Random(3);
            while (pos < original.Length)
            {
                int n = Math.Min(original.Length - pos, rng.Next(1, 200_000));
                xz.Write(original, pos, n);
                pos += n;
            }
        }

        byte[] decompressed = XzCompressor.Decompress(output.ToArray());
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task Pipeline_AsyncWrite_RoundTrip()
    {
        byte[] original = MakeData(2 * 1024 * 1024);
        using var output = new MemoryStream();
        var xz = new XzCompressStream(output,
            new XzCompressOptions { Preset = 1, Threads = 4, BlockSize = 128 * 1024 }, leaveOpen: true);
        await using (xz.ConfigureAwait(false))
        {
            int pos = 0;
            while (pos < original.Length)
            {
                int n = Math.Min(original.Length - pos, 300_000);
                await xz.WriteAsync(original.AsMemory(pos, n));
                pos += n;
            }
        }

        byte[] decompressed = XzCompressor.Decompress(output.ToArray());
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task Pipeline_InputSmallerThanOneBlock_RoundTrip()
    {
        byte[] original = MakeData(10_000);
        var opts = new XzCompressOptions { Preset = 1, Threads = 8, BlockSize = 64 * 1024 };
        byte[] compressed = XzCompressor.Compress(original, opts);
        await Assert.That(XzCompressor.Decompress(compressed).SequenceEqual(original)).IsTrue();
    }

    // ── Async parallel block decompression ───────────────────────────

    [Test]
    [Arguments(2)]
    [Arguments(8)]
    public async Task AsyncParallelDecode_RoundTrip(int threads)
    {
        byte[] original = MakeData(4 * 1024 * 1024);
        byte[] compressed = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = 1, BlockSize = 128 * 1024 });

        using var input = new MemoryStream(compressed, 0, compressed.Length, false, publiclyVisible: true);
        var xz = new XzDecompressStream(input, new XzDecompressOptions { Threads = threads }, leaveOpen: true);
        await using (xz.ConfigureAwait(false))
        {
            using var output = new MemoryStream();
            await xz.CopyToAsync(output);
            await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
        }
    }

    [Test]
    public async Task AsyncParallelDecode_SmallReads_RoundTrip()
    {
        byte[] original = MakeData(1024 * 1024);
        byte[] compressed = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = 1, BlockSize = 64 * 1024 });

        using var input = new MemoryStream(compressed);
        var xz = new XzDecompressStream(input, new XzDecompressOptions { Threads = 4 }, leaveOpen: true);
        await using (xz.ConfigureAwait(false))
        {
            using var output = new MemoryStream();
            byte[] buf = new byte[999];
            int read;
            while ((read = await xz.ReadAsync(buf)) > 0)
                output.Write(buf, 0, read);
            await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
        }
    }

    [Test]
    public async Task AsyncParallelDecode_ConcatenatedStreams_RoundTrip()
    {
        byte[] data1 = MakeData(300 * 1024, seed: 1);
        byte[] data2 = MakeData(200 * 1024, seed: 2);
        var copts = new XzCompressOptions { Preset = 1, BlockSize = 64 * 1024 };
        using var ms = new MemoryStream();
        ms.Write(XzCompressor.Compress(data1, copts));
        ms.Write(new byte[8]); // stream padding
        ms.Write(XzCompressor.Compress(data2, copts));

        byte[] expected = new byte[data1.Length + data2.Length];
        data1.CopyTo(expected, 0);
        data2.CopyTo(expected, data1.Length);

        using var input = new MemoryStream(ms.ToArray());
        var xz = new XzDecompressStream(input, new XzDecompressOptions { Threads = 4 }, leaveOpen: true);
        await using (xz.ConfigureAwait(false))
        {
            using var output = new MemoryStream();
            await xz.CopyToAsync(output);
            await Assert.That(output.ToArray().SequenceEqual(expected)).IsTrue();
        }
    }

    [Test]
    public async Task AsyncParallelDecode_CorruptData_ThrowsLzmaException()
    {
        byte[] original = MakeData(1024 * 1024);
        byte[] compressed = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = 1, BlockSize = 128 * 1024 });
        compressed[compressed.Length / 2] ^= 0xFF;

        using var input = new MemoryStream(compressed);
        var xz = new XzDecompressStream(input, new XzDecompressOptions { Threads = 4 }, leaveOpen: true);
        await using (xz.ConfigureAwait(false))
        {
            using var output = new MemoryStream();
            await Assert.That(async () => await xz.CopyToAsync(output))
                .Throws<LzmaException>();
        }
    }

    [Test]
    public async Task AsyncParallelDecode_MaxOutputSize_Enforced()
    {
        byte[] compressed = XzCompressor.Compress(new byte[1024 * 1024],
            new XzCompressOptions { Preset = 1, BlockSize = 64 * 1024 });

        using var input = new MemoryStream(compressed);
        var xz = new XzDecompressStream(input,
            new XzDecompressOptions { Threads = 4, MaxOutputSize = 128 * 1024 }, leaveOpen: true);
        await using (xz.ConfigureAwait(false))
        {
            using var output = new MemoryStream();
            await Assert.That(async () => await xz.CopyToAsync(output))
                .ThrowsExactly<LzmaMemoryLimitException>();
        }
    }

    [Test]
    public async Task AsyncParallelDecode_MatchesSyncOutput()
    {
        byte[] original = MakeData(2 * 1024 * 1024);
        byte[] compressed = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = 1, BlockSize = 128 * 1024 });

        byte[] syncResult = XzCompressor.Decompress(compressed, threads: 4);

        using var input = new MemoryStream(compressed);
        var xz = new XzDecompressStream(input, new XzDecompressOptions { Threads = 4 }, leaveOpen: true);
        byte[] asyncResult;
        await using (xz.ConfigureAwait(false))
        {
            using var output = new MemoryStream();
            await xz.CopyToAsync(output);
            asyncResult = output.ToArray();
        }

        await Assert.That(asyncResult.SequenceEqual(syncResult)).IsTrue();
        await Assert.That(asyncResult.SequenceEqual(original)).IsTrue();
    }
}
