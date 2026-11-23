// SPDX-License-Identifier: 0BSD

namespace LzmaNet.Tests;

/// <summary>
/// <see cref="XzDecompressStream"/> on damaged input and after failures: errors
/// are never silent, and a stream that failed keeps failing.
/// </summary>
public class DecompressStreamFailureTests
{
    private static byte[] Compressible(int length, int seed)
    {
        var rng = new Random(seed);
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)('a' + rng.Next(8));
        return data;
    }

    private static byte[] Noise(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] DecodeAll(byte[] xz, int threads = 1)
    {
        using var dec = new XzDecompressStream(new MemoryStream(xz), threads);
        using var output = new MemoryStream();
        dec.CopyTo(output);
        return output.ToArray();
    }

    private static async Task<byte[]> DecodeAllAsync(byte[] xz, int threads)
    {
        await using var dec = new XzDecompressStream(new MemoryStream(xz), threads);
        using var output = new MemoryStream();
        await dec.CopyToAsync(output);
        return output.ToArray();
    }

    [Test]
    public async Task ReadAfterADataError_KeepsFailing()
    {
        const int block = 4096;
        // Incompressible data is stored in LZMA2 uncompressed chunks, so the
        // first payload byte of block 1 sits at a computable offset.
        byte[] xz = XzCompressor.Compress(Noise(2 * block, 4), new XzCompressOptions { BlockSize = block, Preset = 0 });
        int headerSize = (xz[12] + 1) * 4;
        xz[12 + headerSize + 3] ^= 0x01; // flip a stored byte of block 1: CRC64 mismatch

        using var dec = new XzDecompressStream(new MemoryStream(xz));
        byte[] buf = new byte[block];
        await Assert.That(() => dec.Read(buf, 0, buf.Length)).Throws<LzmaDataErrorException>();
        // Used to return block 2's bytes here, resuming after the corrupt block.
        await Assert.That(() => dec.Read(buf, 0, buf.Length)).Throws<LzmaDataErrorException>();
    }

    /// <summary>Yields 1 KiB per ReadAsync and cancels its token on the Nth call.</summary>
    private sealed class CancellingSource(byte[] data, int cancelOnRead) : MemoryStream(data)
    {
        private readonly CancellationTokenSource _cts = new();
        private int _reads;
        public CancellationToken Token => _cts.Token;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (++_reads == cancelOnRead)
                _cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Read(buffer.Span[..Math.Min(buffer.Length, 1024)]);
        }

        // The netstandard2.0 asset reads through this overload.
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Test]
    [Arguments(1, 40)]
    [Arguments(4, 200)]
    public async Task ReadAsyncAfterACancellation_NeverReportsCorruption(int threads, int cancelOnRead)
    {
        byte[] plain = Compressible(1 << 20, 5);
        byte[] xz = XzCompressor.Compress(plain, new XzCompressOptions { Preset = 1, BlockSize = 64 * 1024 });
        var source = new CancellingSource(xz, cancelOnRead);
        await using var dec = new XzDecompressStream(source, threads);
        var got = new MemoryStream();
        byte[] buf = new byte[4096];
        await Assert.That(async () =>
        {
            int n;
            while ((n = await dec.ReadAsync(buf, source.Token)) > 0)
                got.Write(buf, 0, n);
        }).Throws<OperationCanceledException>();

        // The input was left mid-block. Later reads used to report "XZ block header
        // CRC32 mismatch", calling a valid file corrupt. They may still hand out blocks
        // that were complete, but must end with the cancellation.
        await Assert.That(async () =>
        {
            int n;
            while ((n = await dec.ReadAsync(buf)) > 0)
                got.Write(buf, 0, n);
        }).Throws<OperationCanceledException>();
        await Assert.That(plain.AsSpan().StartsWith(got.ToArray())).IsTrue();
    }

    [Test]
    public async Task FirstLzma2ChunkWithoutADictionaryReset_IsRejected()
    {
        byte[] xz = XzCompressor.Compress(Compressible(10_000, 6), new XzCompressOptions { Preset = 1 });
        int chunk = 12 + (xz[12] + 1) * 4;
        await Assert.That(xz[chunk] & 0xE0).IsEqualTo(0xE0);
        xz[chunk] = (byte)(0xC0 | (xz[chunk] & 0x1F)); // new props + state reset, no dictionary reset
        // xz 5.8: "Compressed data is corrupt".
        await Assert.That(() => DecodeAll(xz)).Throws<LzmaDataErrorException>();
    }

    private static (byte[] Xz, byte[] Plain, HashSet<int> ValidLengths) ConcatenatedFixture()
    {
        byte[] a = Compressible(9000, 7);
        byte[] b = Compressible(3000, 8);
        byte[] xa = XzCompressor.Compress(a, new XzCompressOptions { Preset = 0, BlockSize = 4096, CheckType = XzCheckType.Crc32 });
        byte[] xb = XzCompressor.Compress(b, new XzCompressOptions { Preset = 0, CheckType = XzCheckType.Sha256 });
        byte[] xz = [.. xa, 0, 0, 0, 0, .. xb, 0, 0, 0, 0, 0, 0, 0, 0];
        var valid = new HashSet<int>
        {
            xa.Length, xa.Length + 4, xa.Length + 4 + xb.Length,
            xa.Length + 4 + xb.Length + 4, xz.Length,
        };
        return (xz, [.. a, .. b], valid);
    }

    [Test]
    [Arguments(1, false)]
    [Arguments(3, false)]
    [Arguments(1, true)]
    [Arguments(3, true)]
    public async Task EveryTruncationOfConcatenatedStreams_ThrowsOrIsAValidPrefix(int threads, bool async)
    {
        var (xz, plain, valid) = ConcatenatedFixture();
        var silent = new List<int>();
        for (int len = 0; len < xz.Length; len++)
        {
            byte[] cut = xz[..len];
            try
            {
                byte[] got = async ? await DecodeAllAsync(cut, threads) : DecodeAll(cut, threads);
                if (!valid.Contains(len) || !plain.AsSpan().StartsWith(got))
                    silent.Add(len);
            }
            catch (LzmaException)
            {
            }
        }
        await Assert.That(silent).IsEmpty();
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    public async Task EveryBitFlip_IsDetectedOrHarmless(int threads)
    {
        var (xz, plain, _) = ConcatenatedFixture();
        var bad = new List<string>();
        for (int i = 0; i < xz.Length; i++)
        {
            for (int bit = 0; bit < 8; bit += 3)
            {
                byte[] m = (byte[])xz.Clone();
                m[i] ^= (byte)(1 << bit);
                try
                {
                    if (!DecodeAll(m, threads).AsSpan().SequenceEqual(plain))
                        bad.Add($"{i}: wrong output");
                }
                catch (LzmaException)
                {
                }
                catch (Exception e)
                {
                    bad.Add($"{i}: {e.GetType().Name}");
                }
            }
        }
        await Assert.That(bad).IsEmpty();
    }
}
