// SPDX-License-Identifier: 0BSD

namespace LzmaNet.Tests;

/// <summary>
/// Round-trip compression/decompression tests ensuring data integrity
/// through the full XZ pipeline.
/// </summary>
public class RoundTripTests
{
    [Fact]
    public void RoundTrip_EmptyData()
    {
        byte[] original = [];
        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void RoundTrip_SingleByte()
    {
        byte[] original = [42];
        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void RoundTrip_SmallData()
    {
        byte[] original = "Hello, World!"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void RoundTrip_AllZeros()
    {
        byte[] original = new byte[4096];
        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);

        // Highly compressible data should compress well
        Assert.True(compressed.Length < original.Length);
    }

    [Fact]
    public void RoundTrip_RepeatingPattern()
    {
        byte[] original = new byte[10000];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void RoundTrip_RandomData_1KB()
    {
        byte[] original = new byte[1024];
        new Random(12345).NextBytes(original);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void RoundTrip_RandomData_64KB()
    {
        byte[] original = new byte[65536];
        new Random(54321).NextBytes(original);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void RoundTrip_AllPresetLevels(int preset)
    {
        byte[] original = new byte[2048];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 7 + i / 13);

        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = preset });
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Theory]
    [InlineData(XzCheckType.None)]
    [InlineData(XzCheckType.Crc32)]
    [InlineData(XzCheckType.Crc64)]
    public void RoundTrip_CheckTypes(XzCheckType checkType)
    {
        byte[] original = "Test data with different check types"u8.ToArray();

        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { CheckType = checkType });
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void RoundTrip_TextData()
    {
        string text = string.Join("\n", Enumerable.Range(0, 100)
            .Select(i => $"Line {i}: The quick brown fox jumps over the lazy dog."));
        byte[] original = System.Text.Encoding.UTF8.GetBytes(text);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);

        // Text should compress well
        Assert.True(compressed.Length < original.Length / 2);
    }

    [Fact]
    public void RoundTrip_LargeCompressibleData()
    {
        // Create data with lots of repeated patterns
        byte[] pattern = "ABCDEFGHIJKLMNOP"u8.ToArray();
        byte[] original = new byte[100_000];
        for (int i = 0; i < original.Length; i++)
            original[i] = pattern[i % pattern.Length];

        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = 1 });
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void RoundTrip_DecompressIntoSpan()
    {
        byte[] original = "Span-based decompression test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original);

        byte[] buffer = new byte[1024];
        int written = XzCompressor.Decompress(compressed, buffer.AsSpan());

        Assert.Equal(original.Length, written);
        Assert.Equal(original, buffer[..written]);
    }

    [Fact]
    public void RoundTrip_ViaStreams()
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

        Assert.Equal(original, resultStream.ToArray());
    }

    [Fact]
    public void RoundTrip_StreamWriteInChunks()
    {
        byte[] original = new byte[10000];
        new Random(777).NextBytes(original);

        using var compressedStream = new MemoryStream();
        using (var xzOut = new XzCompressStream(compressedStream, leaveOpen: true))
        {
            // Write in small chunks
            int pos = 0;
            int chunkSize = 137; // Odd size to test buffer handling
            while (pos < original.Length)
            {
                int len = Math.Min(chunkSize, original.Length - pos);
                xzOut.Write(original.AsSpan(pos, len));
                pos += len;
            }
        }

        compressedStream.Position = 0;
        byte[] decompressed = XzCompressor.Decompress(compressedStream.ToArray());
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void RoundTrip_StreamReadInChunks()
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

        Assert.Equal(original.Length, totalRead);
        Assert.Equal(original, result[..totalRead]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void RoundTrip_MultiThreaded_LargeData(int threads)
    {
        // Data must be large enough to span multiple blocks at preset 0 (block size ~1MB)
        byte[] original = new byte[4 * 1024 * 1024];
        var rng = new Random(99887);
        // Mix compressible and random sections
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 256 < 200 ? i % 37 : rng.Next(256));

        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = 0, Threads = threads });
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void RoundTrip_MultiThreaded_SmallData()
    {
        // Small data that fits in a single block — should still work with threads > 1
        byte[] original = "Multithreaded small data test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Threads = 2 });
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void RoundTrip_MultiThreaded_ViaStream()
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
        Assert.Equal(original, result.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void RoundTrip_ExtremeFlag(int preset)
    {
        byte[] original = new byte[8192];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 7 + i / 13);

        var options = new XzCompressOptions { Preset = preset, Extreme = true };
        byte[] compressed = XzCompressor.Compress(original, options);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void RoundTrip_CompressOptions_AllSettings()
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
            DictionarySize = 1 << 18, // 256 KB
            BlockSize = 1 << 16,      // 64 KB blocks
        };
        byte[] compressed = XzCompressor.Compress(original, options);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void RoundTrip_CompressOptions_ViaStream()
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
        Assert.Equal(original, result.ToArray());
    }

    [Fact]
    public void CompressOptions_Validate_ThrowsOnInvalid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new XzCompressOptions { Preset = 10 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new XzCompressOptions { Threads = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new XzCompressOptions { DictionarySize = 100 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new XzCompressOptions { BlockSize = 1000 }.Validate());
    }

    [Fact]
    public void Extreme_ProducesSmallerOrEqualOutput()
    {
        byte[] original = new byte[32768];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 37);

        byte[] normalCompressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = 6 });
        byte[] extremeCompressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = 6, Extreme = true });

        // Extreme should produce same or smaller output
        Assert.True(extremeCompressed.Length <= normalCompressed.Length,
            $"Extreme ({extremeCompressed.Length}) should be <= normal ({normalCompressed.Length})");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    public void RoundTrip_AllPresetsWithExtreme(int preset, bool extreme)
    {
        byte[] original = new byte[4096];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 7 + i / 13);

        var options = new XzCompressOptions { Preset = preset, Extreme = extreme };
        byte[] compressed = XzCompressor.Compress(original, options);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    public void RoundTrip_AllPresetsWithExtreme_LargeData(int preset, bool extreme)
    {
        // 64 KB of mixed data to exercise multi-chunk encoding at low presets
        byte[] original = new byte[65536];
        var rng = new Random(preset * 100 + (extreme ? 1 : 0));
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 256 < 200 ? i % 37 : rng.Next(256));

        var options = new XzCompressOptions { Preset = preset, Extreme = extreme };
        byte[] compressed = XzCompressor.Compress(original, options);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void RoundTrip_AllPresetsViaStream(int preset)
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
        Assert.Equal(original, result.ToArray());
    }
}
