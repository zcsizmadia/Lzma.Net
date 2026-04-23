// SPDX-License-Identifier: 0BSD

using System.Diagnostics;

namespace LzmaNet.Tests;

/// <summary>
/// Cross-validates LzmaNet against the reference xz command-line tool.
/// Tests both directions: compress with LzmaNet / decompress with xz,
/// and compress with xz / decompress with LzmaNet.
/// </summary>
public class XzInteropTests
{
    private static readonly bool XzAvailable = CheckXzAvailable();

    private static bool CheckXzAvailable()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "xz",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            proc?.WaitForExit(5000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string SkipReason => "xz command-line tool is not available";

    private static void SkipIfNoXz()
    {
        Assert.SkipWhen(!XzAvailable, SkipReason);
    }

    // ---------------------------------------------------------------
    //  Compress with LzmaNet → Decompress with xz
    // ---------------------------------------------------------------

    [Fact]
    public void LzmaNetCompress_XzDecompress_Empty()
    {
        SkipIfNoXz();

        byte[] original = [];
        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzDecompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void LzmaNetCompress_XzDecompress_SingleByte()
    {
        SkipIfNoXz();

        byte[] original = [0x42];
        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzDecompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void LzmaNetCompress_XzDecompress_SmallText()
    {
        SkipIfNoXz();

        byte[] original = "Hello, World!"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzDecompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void LzmaNetCompress_XzDecompress_AllZeros()
    {
        SkipIfNoXz();

        byte[] original = new byte[4096];
        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzDecompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void LzmaNetCompress_XzDecompress_RepeatingPattern()
    {
        SkipIfNoXz();

        byte[] original = new byte[10_000];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzDecompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void LzmaNetCompress_XzDecompress_RandomData()
    {
        SkipIfNoXz();

        byte[] original = new byte[65_536];
        new Random(98765).NextBytes(original);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzDecompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void LzmaNetCompress_XzDecompress_TextData()
    {
        SkipIfNoXz();

        string text = string.Join("\n", Enumerable.Range(0, 200)
            .Select(i => $"Line {i}: The quick brown fox jumps over the lazy dog."));
        byte[] original = System.Text.Encoding.UTF8.GetBytes(text);

        byte[] compressed = XzCompressor.Compress(original);
        byte[] decompressed = XzDecompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void LzmaNetCompress_XzDecompress_AllPresets(int preset)
    {
        SkipIfNoXz();

        byte[] original = new byte[4096];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 7 + i / 13);

        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = preset });
        byte[] decompressed = XzDecompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Theory]
    [InlineData(XzCheckType.None)]
    [InlineData(XzCheckType.Crc32)]
    [InlineData(XzCheckType.Crc64)]
    public void LzmaNetCompress_XzDecompress_CheckTypes(XzCheckType checkType)
    {
        SkipIfNoXz();

        byte[] original = "Integrity check interop test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { CheckType = checkType });
        byte[] decompressed = XzDecompress(compressed);
        Assert.Equal(original, decompressed);
    }

    // ---------------------------------------------------------------
    //  Compress with xz → Decompress with LzmaNet
    // ---------------------------------------------------------------

    [Fact]
    public void XzCompress_LzmaNetDecompress_Empty()
    {
        SkipIfNoXz();

        byte[] original = [];
        byte[] compressed = XzCompress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void XzCompress_LzmaNetDecompress_SingleByte()
    {
        SkipIfNoXz();

        byte[] original = [0x42];
        byte[] compressed = XzCompress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void XzCompress_LzmaNetDecompress_SmallText()
    {
        SkipIfNoXz();

        byte[] original = "Hello, World!"u8.ToArray();
        byte[] compressed = XzCompress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void XzCompress_LzmaNetDecompress_AllZeros()
    {
        SkipIfNoXz();

        byte[] original = new byte[4096];
        byte[] compressed = XzCompress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void XzCompress_LzmaNetDecompress_RepeatingPattern()
    {
        SkipIfNoXz();

        byte[] original = new byte[10_000];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        byte[] compressed = XzCompress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void XzCompress_LzmaNetDecompress_RandomData()
    {
        SkipIfNoXz();

        byte[] original = new byte[65_536];
        new Random(12345).NextBytes(original);

        byte[] compressed = XzCompress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void XzCompress_LzmaNetDecompress_TextData()
    {
        SkipIfNoXz();

        string text = string.Join("\n", Enumerable.Range(0, 200)
            .Select(i => $"Line {i}: The quick brown fox jumps over the lazy dog."));
        byte[] original = System.Text.Encoding.UTF8.GetBytes(text);

        byte[] compressed = XzCompress(original);
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void XzCompress_LzmaNetDecompress_AllPresets(int preset)
    {
        SkipIfNoXz();

        byte[] original = new byte[4096];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 7 + i / 13);

        byte[] compressed = XzCompress(original, $"-{preset}");
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("crc32")]
    [InlineData("crc64")]
    public void XzCompress_LzmaNetDecompress_CheckTypes(string checkName)
    {
        SkipIfNoXz();

        byte[] original = "Integrity check interop test"u8.ToArray();
        byte[] compressed = XzCompress(original, $"--check={checkName}");
        byte[] decompressed = XzCompressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    // ---------------------------------------------------------------
    //  Cross-verify: both directions with same data
    // ---------------------------------------------------------------

    [Fact]
    public void CrossVerify_LargeCompressibleData()
    {
        SkipIfNoXz();

        byte[] pattern = "ABCDEFGHIJKLMNOP"u8.ToArray();
        byte[] original = new byte[100_000];
        for (int i = 0; i < original.Length; i++)
            original[i] = pattern[i % pattern.Length];

        // Our compress → xz decompress
        byte[] ourCompressed = XzCompressor.Compress(original);
        byte[] xzDecompressed = XzDecompress(ourCompressed);
        Assert.Equal(original, xzDecompressed);

        // xz compress → our decompress
        byte[] xzCompressed = XzCompress(original);
        byte[] ourDecompressed = XzCompressor.Decompress(xzCompressed);
        Assert.Equal(original, ourDecompressed);
    }

    [Fact]
    public void CrossVerify_StreamApi()
    {
        SkipIfNoXz();

        byte[] original = new byte[8192];
        new Random(42).NextBytes(original);

        // Compress via our stream API
        using var compressedStream = new MemoryStream();
        using (var xzOut = new XzCompressStream(compressedStream, leaveOpen: true))
        {
            // Write in small chunks to exercise the stream
            int offset = 0;
            while (offset < original.Length)
            {
                int chunkSize = Math.Min(137, original.Length - offset); // odd size
                xzOut.Write(original, offset, chunkSize);
                offset += chunkSize;
            }
        }

        // Decompress with xz tool
        byte[] decompressed = XzDecompress(compressedStream.ToArray());
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void CrossVerify_StreamDecompress_XzCompressed()
    {
        SkipIfNoXz();

        byte[] original = new byte[8192];
        new Random(42).NextBytes(original);

        // Compress with xz tool
        byte[] compressed = XzCompress(original);

        // Decompress via our stream API, reading in small chunks
        using var inputStream = new MemoryStream(compressed);
        using var xzIn = new XzDecompressStream(inputStream);
        using var outputStream = new MemoryStream();

        byte[] readBuf = new byte[100]; // small reads
        int read;
        while ((read = xzIn.Read(readBuf, 0, readBuf.Length)) > 0)
            outputStream.Write(readBuf, 0, read);

        Assert.Equal(original, outputStream.ToArray());
    }

    // ---------------------------------------------------------------
    //  Helpers: shell out to the xz executable
    // ---------------------------------------------------------------

    /// <summary>
    /// Compresses data using the xz command-line tool.
    /// </summary>
    private static byte[] XzCompress(byte[] data, string extraArgs = "")
    {
        return RunXz($"--compress --stdout --force {extraArgs}", data);
    }

    /// <summary>
    /// Decompresses data using the xz command-line tool.
    /// </summary>
    private static byte[] XzDecompress(byte[] data)
    {
        return RunXz("--decompress --stdout --force", data);
    }

    private static byte[] RunXz(string arguments, byte[] stdin)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "xz",
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        proc.Start();

        // Write stdin on a background thread to avoid deadlock
        var writeTask = Task.Run(() =>
        {
            proc.StandardInput.BaseStream.Write(stdin);
            proc.StandardInput.Close();
        });

        using var outputStream = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(outputStream);

        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        writeTask.Wait(5_000);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"xz exited with code {proc.ExitCode}: {stderr}");

        return outputStream.ToArray();
    }
}
