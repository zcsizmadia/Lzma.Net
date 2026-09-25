// SPDX-License-Identifier: 0BSD

using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using LzmaNet.Check;

namespace LzmaNet.Tests.NetFx;

/// <summary>
/// Exercises the netstandard2.0 asset on .NET Framework, the runtime it is
/// actually shipped to.
/// </summary>
/// <remarks>
/// This is deliberately a targeted suite rather than the whole of
/// <c>LzmaNet.Tests</c>. The main suite is written against modern APIs
/// (<c>Array.Fill</c>, <c>Array.MaxLength</c>, span <c>Stream.Write</c>,
/// <c>RuntimeHelpers.GetSubArray</c>, <c>Process.WaitForExitAsync</c>) and would
/// need a compat layer of its own to compile here, spreading conditionals
/// through shared test code for the benefit of one host.
///
/// What is framework-specific is not the LZMA logic — that is covered on five
/// assets already — but the shims the netstandard2.0 build relies on. Each test
/// below drives one of them through the public API, on the BCL where they
/// actually have to work.
/// </remarks>
public class NetFrameworkTests
{
    [Test]
    public async Task RunsTheNetStandard20AssetOnNetFramework()
    {
        string? asset = typeof(XzCompressor).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        await Assert.That(asset).IsEqualTo(".NETStandard,Version=v2.0");
        await Assert.That(RuntimeInformation.FrameworkDescription).StartsWith(".NET Framework");
    }

    [Test]
    public async Task NoCarrylessMultiplyPathOnThisAsset()
    {
        // System.Runtime.Intrinsics does not exist in netstandard2.0, so every
        // CRC below went through slicing-by-8.
        await Assert.That(CrcFolding.IsSupported).IsFalse();
    }

    // ── Check types: drives Portable.Sha256's ComputeHash branch ──

    [Test]
    [Arguments(XzCheckType.None)]
    [Arguments(XzCheckType.Crc32)]
    [Arguments(XzCheckType.Crc64)]
    [Arguments(XzCheckType.Sha256)]
    public async Task RoundTripsWithEveryCheckType(XzCheckType check)
    {
        byte[] original = Sample(96 * 1024, seed: 3);

        byte[] compressed = XzCompressor.Compress(
            original, new XzCompressOptions { CheckType = check });
        byte[] back = XzCompressor.Decompress(compressed);

        await Assert.That(back.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task CrcsMatchTheStandardVectors()
    {
        await Assert.That(Crc32.Compute("123456789"u8.ToArray())).IsEqualTo(0xCBF43926u);
        await Assert.That(Crc64.Compute("123456789"u8.ToArray())).IsEqualTo(0x995DC9BBDF1939FAul);
    }

    // ── Presets: drives the BitOperations polyfill through the match finders ──

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(6)]
    [Arguments(9)]
    public async Task RoundTripsAtPreset(int preset)
    {
        byte[] original = Sample(256 * 1024, seed: preset + 17);

        byte[] compressed = XzCompressor.Compress(
            original, new XzCompressOptions { Preset = preset });
        byte[] back = XzCompressor.Decompress(compressed);

        await Assert.That(back.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTripsWithBcjAndDelta()
    {
        byte[] original = Sample(64 * 1024, seed: 5);

        foreach (var filter in new[] { XzFilterType.X86, XzFilterType.Arm64, XzFilterType.Delta })
        {
            byte[] compressed = XzCompressor.Compress(
                original, new XzCompressOptions { Filter = filter, DeltaDistance = 4 });
            byte[] back = XzCompressor.Decompress(compressed);
            await Assert.That(back.SequenceEqual(original)).IsTrue();
        }
    }

    // ── Streaming: drives StreamCompat's rent-and-copy bridge ──

    [Test]
    public async Task StreamingRoundTrips()
    {
        byte[] original = Sample(512 * 1024, seed: 7);

        using var storage = new MemoryStream();
        using (var xz = new XzCompressStream(storage, leaveOpen: true))
        {
            // Written in chunks so the bridge is crossed repeatedly.
            for (int offset = 0; offset < original.Length; offset += 8192)
            {
                int count = Math.Min(8192, original.Length - offset);
                xz.Write(original, offset, count);
            }
        }

        storage.Position = 0;
        using var output = new MemoryStream();
        using (var xz = new XzDecompressStream(storage))
            xz.CopyTo(output);

        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task AsyncStreamingRoundTripsAndDisposesAsynchronously()
    {
        byte[] original = Sample(256 * 1024, seed: 11);

        using var storage = new MemoryStream();
        // await using drives the IAsyncDisposable this asset declares itself.
        await using (var xz = new XzCompressStream(storage, leaveOpen: true))
            await xz.WriteAsync(original, 0, original.Length);

        storage.Position = 0;
        using var output = new MemoryStream();
        await using (var xz = new XzDecompressStream(storage))
            await xz.CopyToAsync(output);

        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task MultiThreadedRoundTrips()
    {
        byte[] original = Sample(2 * 1024 * 1024, seed: 13);

        byte[] compressed = XzCompressor.Compress(
            original, new XzCompressOptions { Threads = 4, BlockSize = 256 * 1024 });
        byte[] back = XzCompressor.Decompress(compressed, threads: 4);

        await Assert.That(back.SequenceEqual(original)).IsTrue();
    }

    // ── Seekable reads: drives the index parsing and its range slicing ──

    [Test]
    public async Task SeeksWithinAMultiBlockStream()
    {
        byte[] original = Sample(1024 * 1024, seed: 19);
        byte[] compressed = XzCompressor.Compress(
            original, new XzCompressOptions { BlockSize = 128 * 1024 });

        using var xz = new XzSeekableStream(new MemoryStream(compressed));
        await Assert.That(xz.Length).IsEqualTo((long)original.Length);

        foreach (int position in new[] { 0, 1000, 300_000, 700_000, original.Length - 4096 })
        {
            xz.Position = position;
            byte[] buffer = new byte[4096];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = xz.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }

            await Assert.That(buffer.Take(read).SequenceEqual(
                original.Skip(position).Take(read))).IsTrue();
        }
    }

    // ── Legacy .lzma: drives the Array.MaxLength forward ──

    [Test]
    public async Task LzmaAloneRoundTrips()
    {
        byte[] original = Sample(128 * 1024, seed: 23);

        using var storage = new MemoryStream();
        using (var lzma = new LzmaAloneCompressStream(storage, leaveOpen: true))
            lzma.Write(original, 0, original.Length);

        storage.Position = 0;
        using var output = new MemoryStream();
        using (var lzma = new LzmaAloneDecompressStream(storage))
            lzma.CopyTo(output);

        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
    }

    // ── Guards ──

    [Test]
    public async Task RejectsCorruptData()
    {
        byte[] compressed = XzCompressor.Compress(Sample(8192, seed: 29));
        compressed[compressed.Length - 20] ^= 0xFF;

        await Assert.That(() => XzCompressor.Decompress(compressed))
            .ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task EnforcesMaxOutputSize()
    {
        byte[] compressed = XzCompressor.Compress(Sample(512 * 1024, seed: 31));

        await Assert.That(() => XzCompressor.Decompress(
                compressed, new XzDecompressOptions { MaxOutputSize = 1024 }))
            .ThrowsExactly<LzmaMemoryLimitException>();
    }

    [Test]
    public async Task RoundTripsEmptyInput()
    {
        byte[] compressed = XzCompressor.Compress(Array.Empty<byte>());
        await Assert.That(XzCompressor.Decompress(compressed).Length).IsEqualTo(0);
    }

    /// <summary>Compressible but not trivial: a repeating window over random data.</summary>
    private static byte[] Sample(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        for (int i = 4096; i < length; i++)
            data[i] = data[i - 4096];
        return data;
    }
}
