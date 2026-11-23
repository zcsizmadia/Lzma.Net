// SPDX-License-Identifier: 0BSD

using System.Diagnostics;

namespace LzmaNet.Tests;

/// <summary>
/// The chunk-at-a-time decoder for unsized and oversized blocks (XzStreamingBlockDecoder),
/// fed by native xz: single-threaded xz writes blocks without size fields.
/// </summary>
[RequiresXz]
public class StreamingDecoderTests
{
    private static byte[] MixedData(int length, int seed)
    {
        // Compressible runs, noise (stored LZMA2 chunks) and long-distance repeats,
        // so small dictionaries force many window slides.
        var rng = new Random(seed);
        byte[] data = new byte[length];
        int pos = 0;
        while (pos < length)
        {
            int n = Math.Min(length - pos, 8192 + rng.Next(40_000));
            switch (rng.Next(3))
            {
                case 0:
                    for (int i = 0; i < n; i++) data[pos + i] = (byte)('a' + rng.Next(8));
                    break;
                case 1:
                    rng.NextBytes(data.AsSpan(pos, n));
                    break;
                default:
                    int from = pos > 0 ? rng.Next(pos) : 0;
                    for (int i = 0; i < n; i++) data[pos + i] = pos > 0 ? data[from + i % (pos - from)] : (byte)i;
                    break;
            }
            pos += n;
        }
        return data;
    }

    private static byte[] Xz(string arguments, byte[] input)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "xz",
            Arguments = arguments + " -c",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var feed = Task.Run(() => { proc.StandardInput.BaseStream.Write(input); proc.StandardInput.Close(); });
        using var output = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(output);
        feed.Wait();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"xz {arguments} failed");
        return output.ToArray();
    }

    /// <summary>Decodes with odd-sized reads, optionally alternating sync and async calls.</summary>
    private static async Task<byte[]> Decode(byte[] xz, int threads, bool mixSyncAsync, long threshold,
        long maxOutput = long.MaxValue)
    {
        var options = new XzDecompressOptions { Threads = threads, StreamingThreshold = threshold, MaxOutputSize = maxOutput };
        using var dec = new XzDecompressStream(new MemoryStream(xz), options);
        using var output = new MemoryStream();
        byte[] buf = new byte[70_001];
        for (int call = 0; ; call++)
        {
            int n = mixSyncAsync && call % 2 == 1
                ? await dec.ReadAsync(buf.AsMemory(0, 7 + call % 5000))
                : dec.Read(buf, 0, call % 3 == 0 ? 6 : buf.Length);
            if (n == 0)
                return output.ToArray();
            output.Write(buf, 0, n);
        }
    }

    [Test]
    [Arguments("-1 -T1")]
    [Arguments("-1 -T1 --block-size=100KiB")]
    [Arguments("-T1 --lzma2=dict=4KiB,lc=0,lp=4,pb=4")]
    [Arguments("-T1 --lzma2=dict=64KiB,lc=4,lp=0,pb=0 --check=sha256")]
    [Arguments("-T1 --lzma2=preset=6,dict=256KiB --check=crc32")]
    [Arguments("-1 -T2 --block-size=100KiB")]
    public async Task NativeOutput_DecodesInEveryMode(string xzArguments)
    {
        byte[] data = MixedData(1_200_000, xzArguments.Length);
        byte[] xz = Xz(xzArguments, data);
        foreach (long threshold in new[] { 256L << 20, 64 * 1024 })
        foreach (int threads in new[] { 1, 4 })
        foreach (bool mix in new[] { false, true })
        {
            byte[] got = await Decode(xz, threads, mix, threshold);
            await Assert.That(got.AsSpan().SequenceEqual(data))
                .IsTrue().Because($"threshold={threshold} threads={threads} mix={mix}");
        }
    }

    [Test]
    [Arguments(36)] // 1 GiB declared dictionary
    [Arguments(39)] // 3 GiB: rejected as unsupported, as before
    public async Task HugeDeclaredDictionary_DoesNotAllocateIt(int dictByte)
    {
        byte[] data = MixedData(100_000, 3);
        byte[] xz = Xz("-1 -T1", data);
        int headerSize = (xz[12] + 1) * 4;
        var header = xz.AsSpan(12, headerSize);
        int at = header[..^4].LastIndexOfAnyExcept((byte)0);
        header[at] = (byte)dictByte;
        LzmaNet.Check.Crc32.WriteLE(header[..^4], header[^4..]);

        foreach (long maxOutput in new[] { long.MaxValue, 1 << 20 })
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                byte[] got = await Decode(xz, threads: 1, mixSyncAsync: false, threshold: 256L << 20, maxOutput);
                await Assert.That(got.AsSpan().SequenceEqual(data)).IsTrue();
            }
            catch (LzmaException) when (dictByte > 37)
            {
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            await Assert.That(allocated).IsLessThan(16L << 20);
        }
    }

    [Test]
    [Arguments(1, false)]
    [Arguments(4, true)]
    public async Task UnsizedBlocks_EveryTruncationThrows(int threads, bool mix)
    {
        byte[] data = MixedData(150_000, 4);
        byte[] xz = Xz("-T1 --lzma2=dict=16KiB --block-size=60KiB", data);
        var silent = new List<int>();
        for (int len = 0; len < xz.Length; len += 7)
        {
            try
            {
                await Decode(xz[..len], threads, mix, threshold: 32 * 1024);
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
    [Arguments(4)]
    public async Task UnsizedBlocks_EveryBitFlipIsDetectedOrHarmless(int threads)
    {
        byte[] data = MixedData(40_000, 5);
        byte[] xz = Xz("-T1 --lzma2=dict=8KiB --block-size=16KiB --check=crc32", data);
        var bad = new List<string>();
        for (int i = 0; i < xz.Length; i++)
        {
            byte[] m = (byte[])xz.Clone();
            m[i] ^= (byte)(1 << (i % 8));
            try
            {
                if (!(await Decode(m, threads, mixSyncAsync: false, threshold: 20 * 1024)).AsSpan().SequenceEqual(data))
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
        await Assert.That(bad).IsEmpty();
    }

    /// <summary>Reads until an exception and returns how many bytes came out before it.</summary>
    private static async Task<(long Returned, Exception? Error)> ReadUntilFailure(byte[] xz, int threads)
    {
        long returned = 0;
        try
        {
            using var dec = new XzDecompressStream(new MemoryStream(xz), threads);
            byte[] buf = new byte[4096];
            int n;
            while ((n = dec.Read(buf, 0, buf.Length)) > 0)
                returned += n;
        }
        catch (Exception e)
        {
            return (returned, e);
        }
        await Task.CompletedTask;
        return (returned, null);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task SmallUnsizedBlock_IsVerifiedBeforeAnyOfItIsReturned(int threads)
    {
        // Blocks up to the streaming threshold are buffered whole in every mode, so a
        // corrupt block never hands out data first, whatever the thread count.
        byte[] xz = Xz("-6 -T1", MixedData(300_000, 7));
        xz[xz.Length / 2] ^= 0x10;
        var (returned, error) = await ReadUntilFailure(xz, threads);
        await Assert.That(error).IsTypeOf<LzmaDataErrorException>();
        await Assert.That(returned).IsEqualTo(0);
    }

    private static long ReadVli(byte[] data, ref int pos)
    {
        long value = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = data[pos++];
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReadAheadError_StillDeliversTheBlocksBeforeIt(int threads)
    {
        const int block = 16 * 1024;
        byte[] data = MixedData(3 * block, 8);
        byte[] xz = XzCompressor.Compress(data, new XzCompressOptions { Preset = 1, BlockSize = block });

        // Find block 3 (sized blocks, CRC64) and make its header declare a 2 GiB dictionary,
        // which this decoder rejects while parsing the header.
        int pos = 12;
        for (int i = 0; i < 2; i++)
        {
            int headerSize = (xz[pos] + 1) * 4;
            int p = pos + 2;
            long compressed = ReadVli(xz, ref p);
            pos += headerSize + (int)compressed + (int)((4 - (compressed & 3)) & 3) + 8;
        }
        var header = xz.AsSpan(pos, (xz[pos] + 1) * 4);
        header[header[..^4].LastIndexOfAnyExcept((byte)0)] = 38;
        LzmaNet.Check.Crc32.WriteLE(header[..^4], header[^4..]);

        var (returned, error) = await ReadUntilFailure(xz, threads);
        await Assert.That(error).IsAssignableTo<LzmaException>();
        await Assert.That(returned).IsEqualTo(2L * block);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task UnsizedBlockOfTinyChunks_IsNotBufferedPastTheThreshold(int threads)
    {
        // Chunks that each declare 65536 compressed bytes for 1 output byte: buffering
        // must stop at the threshold, and streaming rejects the first bad chunk.
        byte[] valid = Xz("-1 -T1", new byte[16]);
        var input = new MemoryStream();
        input.Write(valid, 0, 12 + (valid[12] + 1) * 4); // stream header + unsized block header
        byte[] payload = new byte[65536];
        new Random(1).NextBytes(payload);
        for (int i = 0; i < 1000; i++)
        {
            input.Write(i == 0 ? [0xE0, 0, 0, 0xFF, 0xFF, 0x5D] : new byte[] { 0x80, 0, 0, 0xFF, 0xFF });
            input.Write(payload);
        }
        byte[] data = input.ToArray(); // about 64 MiB
        var options = new XzDecompressOptions { Threads = threads, StreamingThreshold = 1 << 20 };

        long before = GC.GetAllocatedBytesForCurrentThread();
        await Assert.That(() =>
        {
            using var dec = new XzDecompressStream(new MemoryStream(data), options);
            dec.CopyTo(Stream.Null);
        }).Throws<LzmaException>();
        await Assert.That(GC.GetAllocatedBytesForCurrentThread() - before).IsLessThan(16L << 20);
    }

    /// <summary>Pipes <paramref name="zeros"/> zero bytes through `xz -1 -T1` (one block,
    /// no sizes in its header) and decodes xz's stdout as it streams.</summary>
    private static async Task<long> DecodeNativeSingleBlockOfZeros(long zeros, int threads)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "xz",
            Arguments = "-1 -T1 -c",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var feed = Task.Run(async () =>
        {
            byte[] chunk = new byte[1 << 20];
            for (long left = zeros; left > 0; left -= chunk.Length)
                await proc.StandardInput.BaseStream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, left)));
            proc.StandardInput.Close();
        });
        long total = 0;
        try
        {
            using var dec = new XzDecompressStream(proc.StandardOutput.BaseStream, threads);
            byte[] buf = new byte[1 << 20];
            int n;
            while ((n = dec.Read(buf, 0, buf.Length)) > 0)
                total += n;
        }
        finally
        {
            if (!proc.HasExited) proc.Kill();
            try { await feed; } catch (IOException) { }
        }
        return total;
    }

    // Explicit: each pipes gigabytes through xz (about 15 s).
    [Test, Explicit]
    [Arguments(3L << 30, 1)]
    [Arguments(3L << 30, 4)]
    [Arguments(2_147_483_592L, 1)] // Array.MaxLength + 1: used to throw OutOfMemoryException
    public async Task NativeSingleBlockOver2GiB_Decodes(long size, int threads)
    {
        await Assert.That(await DecodeNativeSingleBlockOfZeros(size, threads)).IsEqualTo(size);
    }
}
