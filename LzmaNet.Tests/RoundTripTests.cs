// SPDX-License-Identifier: 0BSD

namespace LzmaNet.Tests;

/// <summary>
/// Round-trip compression/decompression tests ensuring data integrity
/// through the full XZ pipeline.
/// </summary>
public class RoundTripTests
{
    [Test]
    public async Task RoundTrip_EmptyData()
    {
        byte[] original = [];
        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_SingleByte()
    {
        byte[] original = [42];
        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_SmallData()
    {
        byte[] original = "Hello, World!"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_AllZeros()
    {
        byte[] original = new byte[4096];
        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();

        await Assert.That(compressed.Length < original.Length).IsTrue();
    }

    [Test]
    public async Task RoundTrip_RepeatingPattern()
    {
        byte[] original = new byte[10000];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_RandomData_1KB()
    {
        byte[] original = new byte[1024];
        new Random(12345).NextBytes(original);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_RandomData_64KB()
    {
        byte[] original = new byte[65536];
        new Random(54321).NextBytes(original);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(6)]
    [Arguments(9)]
    public async Task RoundTrip_AllPresetLevels(int preset)
    {
        byte[] original = new byte[2048];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 7 + i / 13);

        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = preset });
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments(XzCheckType.None)]
    [Arguments(XzCheckType.Crc32)]
    [Arguments(XzCheckType.Crc64)]
    public async Task RoundTrip_CheckTypes(XzCheckType checkType)
    {
        byte[] original = "Test data with different check types"u8.ToArray();

        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { CheckType = checkType });
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_TextData()
    {
        string text = string.Join("\n", Enumerable.Range(0, 100)
            .Select(i => $"Line {i}: The quick brown fox jumps over the lazy dog."));
        byte[] original = System.Text.Encoding.UTF8.GetBytes(text);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();

        await Assert.That(compressed.Length < original.Length / 2).IsTrue();
    }

    [Test]
    public async Task RoundTrip_LargeCompressibleData()
    {
        byte[] pattern = "ABCDEFGHIJKLMNOP"u8.ToArray();
        byte[] original = new byte[100_000];
        for (int i = 0; i < original.Length; i++)
            original[i] = pattern[i % pattern.Length];

        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = 1 });
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_DecompressIntoSpan()
    {
        byte[] original = "Span-based decompression test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original);

        byte[] buffer = new byte[1024];
        int written = XzCompressor.Decompress(compressed, buffer.AsSpan());

        await Assert.That(written).IsEqualTo(original.Length);
        await Assert.That(buffer[..written].SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_ViaStreams()
    {
        byte[] original = new byte[5000];
        new Random(99).NextBytes(original);

        using var compressedStream = new MemoryStream();
        using (var xzOut = new XzCompressStream(compressedStream, leaveOpen: true))
        {
            xzOut.Write(original);
        }

        compressedStream.Position = 0;
        using var xzIn = new XzDecompressStream(compressedStream);
        using var resultStream = new MemoryStream();
        xzIn.CopyTo(resultStream);

        await Assert.That(resultStream.ToArray().SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_StreamWriteInChunks()
    {
        byte[] original = new byte[10000];
        new Random(777).NextBytes(original);

        using var compressedStream = new MemoryStream();
        using (var xzOut = new XzCompressStream(compressedStream, leaveOpen: true))
        {
            int pos = 0;
            int chunkSize = 137;
            while (pos < original.Length)
            {
                int len = Math.Min(chunkSize, original.Length - pos);
                xzOut.Write(original.AsSpan(pos, len));
                pos += len;
            }
        }

        compressedStream.Position = 0;
        byte[] decompressed = XzCompressor.Decompress(compressedStream.ToArray());
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_StreamReadInChunks()
    {
        byte[] original = "Read this data in small chunks for testing"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original);

        using var compStream = new MemoryStream(compressed);
        using var xzIn = new XzDecompressStream(compStream);

        byte[] result = new byte[original.Length + 100];
        int totalRead = 0;
        int readSize = 5;
        while (true)
        {
            int read = xzIn.Read(result.AsSpan(totalRead, readSize));
            if (read == 0) break;
            totalRead += read;
        }

        await Assert.That(totalRead).IsEqualTo(original.Length);
        await Assert.That(result[..totalRead].SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(4)]
    public async Task RoundTrip_MultiThreaded_LargeData(int threads)
    {
        byte[] original = new byte[4 * 1024 * 1024];
        var rng = new Random(99887);
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 256 < 200 ? i % 37 : rng.Next(256));

        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = 0, Threads = threads });
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_MultiThreaded_SmallData()
    {
        byte[] original = "Multithreaded small data test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Threads = 2 });
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_MultiThreaded_ViaStream()
    {
        byte[] original = new byte[2 * 1024 * 1024];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        using var output = new MemoryStream();
        using (var xz = new XzCompressStream(output, new XzCompressOptions { Preset = 1, Threads = 4 }, leaveOpen: true))
        {
            xz.Write(original);
        }
        output.Position = 0;

        using var xzIn = new XzDecompressStream(output);
        using var result = new MemoryStream();
        xzIn.CopyTo(result);
        await Assert.That(result.ToArray().SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(3)]
    [Arguments(6)]
    [Arguments(9)]
    public async Task RoundTrip_ExtremeFlag(int preset)
    {
        byte[] original = new byte[8192];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 7 + i / 13);

        var options = new XzCompressOptions { Preset = preset, Extreme = true };
        byte[] compressed = XzCompressor.Compress(original, options);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_CompressOptions_AllSettings()
    {
        byte[] original = new byte[100_000];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        var options = new XzCompressOptions
        {
            Preset = 3,
            Extreme = true,
            Threads = 2,
            CheckType = XzCheckType.Crc32,
            DictionarySize = 1 << 18,
            BlockSize = 1 << 16,
        };
        byte[] compressed = XzCompressor.Compress(original, options);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_CompressOptions_ViaStream()
    {
        byte[] original = "Options via stream constructor test"u8.ToArray();

        using var output = new MemoryStream();
        var options = new XzCompressOptions { Preset = 1, Extreme = true };
        using (var xz = new XzCompressStream(output, options, leaveOpen: true))
        {
            xz.Write(original);
        }
        output.Position = 0;

        using var xzIn = new XzDecompressStream(output);
        using var result = new MemoryStream();
        xzIn.CopyTo(result);
        await Assert.That(result.ToArray().SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task CompressOptions_Validate_ThrowsOnInvalid()
    {
        await Assert.That(() => new XzCompressOptions { Preset = 10 }.Validate()).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => new XzCompressOptions { Threads = -1 }.Validate()).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => new XzCompressOptions { DictionarySize = 100 }.Validate()).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => new XzCompressOptions { BlockSize = 1000 }.Validate()).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Extreme_ProducesSmallerOrEqualOutput()
    {
        byte[] original = new byte[32768];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 37);

        byte[] normalCompressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = 6 });
        byte[] extremeCompressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = 6, Extreme = true });

        await Assert.That(extremeCompressed.Length <= normalCompressed.Length).IsTrue();
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(4, false)]
    [Arguments(5, false)]
    [Arguments(6, false)]
    [Arguments(7, false)]
    [Arguments(8, false)]
    [Arguments(9, false)]
    [Arguments(0, true)]
    [Arguments(1, true)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    [Arguments(4, true)]
    [Arguments(5, true)]
    [Arguments(6, true)]
    [Arguments(7, true)]
    [Arguments(8, true)]
    [Arguments(9, true)]
    public async Task RoundTrip_AllPresetsWithExtreme(int preset, bool extreme)
    {
        byte[] original = new byte[4096];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 7 + i / 13);

        var options = new XzCompressOptions { Preset = preset, Extreme = extreme };
        byte[] compressed = XzCompressor.Compress(original, options);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(0, true)]
    [Arguments(6, false)]
    [Arguments(6, true)]
    [Arguments(9, false)]
    [Arguments(9, true)]
    public async Task RoundTrip_AllPresetsWithExtreme_LargeData(int preset, bool extreme)
    {
        byte[] original = new byte[65536];
        var rng = new Random(preset * 100 + (extreme ? 1 : 0));
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 256 < 200 ? i % 37 : rng.Next(256));

        var options = new XzCompressOptions { Preset = preset, Extreme = extreme };
        byte[] compressed = XzCompressor.Compress(original, options);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(3)]
    [Arguments(6)]
    [Arguments(9)]
    public async Task RoundTrip_AllPresetsViaStream(int preset)
    {
        byte[] original = new byte[2048];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        using var output = new MemoryStream();
        var options = new XzCompressOptions { Preset = preset };
        using (var xz = new XzCompressStream(output, options, leaveOpen: true))
        {
            xz.Write(original);
        }
        output.Position = 0;

        using var xzIn = new XzDecompressStream(output);
        using var result = new MemoryStream();
        xzIn.CopyTo(result);
        await Assert.That(result.ToArray().SequenceEqual(original)).IsTrue();
    }
}
