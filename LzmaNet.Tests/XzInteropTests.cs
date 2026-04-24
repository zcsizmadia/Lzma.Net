// SPDX-License-Identifier: 0BSD

using System.Diagnostics;

namespace LzmaNet.Tests;

/// <summary>
/// Cross-validates LzmaNet against the reference xz command-line tool.
/// Tests both directions: compress with LzmaNet / decompress with xz,
/// and compress with xz / decompress with LzmaNet.
/// </summary>
[RequiresXz]
public class XzInteropTests
{
    // ---------------------------------------------------------------
    //  Compress with LzmaNet -> Decompress with xz
    // ---------------------------------------------------------------

    [Test]
    public async Task LzmaNetCompress_XzDecompress_Empty()
    {
        byte[] original = [];
        byte[] compressed = await XzCompressor.CompressAsync(original);
        byte[] decompressed = await XzDecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task LzmaNetCompress_XzDecompress_SingleByte()
    {
        byte[] original = [0x42];
        byte[] compressed = await XzCompressor.CompressAsync(original);
        byte[] decompressed = await XzDecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task LzmaNetCompress_XzDecompress_SmallText()
    {
        byte[] original = "Hello, World!"u8.ToArray();
        byte[] compressed = await XzCompressor.CompressAsync(original);
        byte[] decompressed = await XzDecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task LzmaNetCompress_XzDecompress_AllZeros()
    {
        byte[] original = new byte[4096];
        byte[] compressed = await XzCompressor.CompressAsync(original);
        byte[] decompressed = await XzDecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task LzmaNetCompress_XzDecompress_RepeatingPattern()
    {
        byte[] original = new byte[10_000];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        byte[] compressed = await XzCompressor.CompressAsync(original);
        byte[] decompressed = await XzDecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task LzmaNetCompress_XzDecompress_RandomData()
    {
        byte[] original = new byte[65_536];
        new Random(98765).NextBytes(original);

        byte[] compressed = await XzCompressor.CompressAsync(original);
        byte[] decompressed = await XzDecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task LzmaNetCompress_XzDecompress_TextData()
    {
        string text = string.Join("\n", Enumerable.Range(0, 200)
            .Select(i => $"Line {i}: The quick brown fox jumps over the lazy dog."));
        byte[] original = System.Text.Encoding.UTF8.GetBytes(text);

        byte[] compressed = await XzCompressor.CompressAsync(original);
        byte[] decompressed = await XzDecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(6)]
    [Arguments(9)]
    public async Task LzmaNetCompress_XzDecompress_AllPresets(int preset)
    {
        byte[] original = new byte[4096];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 7 + i / 13);

        byte[] compressed = await XzCompressor.CompressAsync(original, new XzCompressOptions { Preset = preset });
        byte[] decompressed = await XzDecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments(XzCheckType.None)]
    [Arguments(XzCheckType.Crc32)]
    [Arguments(XzCheckType.Crc64)]
    public async Task LzmaNetCompress_XzDecompress_CheckTypes(XzCheckType checkType)
    {
        byte[] original = "Integrity check interop test"u8.ToArray();
        byte[] compressed = await XzCompressor.CompressAsync(original, new XzCompressOptions { CheckType = checkType });
        byte[] decompressed = await XzDecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    // ---------------------------------------------------------------
    //  Compress with xz -> Decompress with LzmaNet
    // ---------------------------------------------------------------

    [Test]
    public async Task XzCompress_LzmaNetDecompress_Empty()
    {
        byte[] original = [];
        byte[] compressed = await XzCompressAsync(original);
        byte[] decompressed = await XzCompressor.DecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task XzCompress_LzmaNetDecompress_SingleByte()
    {
        byte[] original = [0x42];
        byte[] compressed = await XzCompressAsync(original);
        byte[] decompressed = await XzCompressor.DecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task XzCompress_LzmaNetDecompress_SmallText()
    {
        byte[] original = "Hello, World!"u8.ToArray();
        byte[] compressed = await XzCompressAsync(original);
        byte[] decompressed = await XzCompressor.DecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task XzCompress_LzmaNetDecompress_AllZeros()
    {
        byte[] original = new byte[4096];
        byte[] compressed = await XzCompressAsync(original);
        byte[] decompressed = await XzCompressor.DecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task XzCompress_LzmaNetDecompress_RepeatingPattern()
    {
        byte[] original = new byte[10_000];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        byte[] compressed = await XzCompressAsync(original);
        byte[] decompressed = await XzCompressor.DecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task XzCompress_LzmaNetDecompress_RandomData()
    {
        byte[] original = new byte[65_536];
        new Random(12345).NextBytes(original);

        byte[] compressed = await XzCompressAsync(original);
        byte[] decompressed = await XzCompressor.DecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task XzCompress_LzmaNetDecompress_TextData()
    {
        string text = string.Join("\n", Enumerable.Range(0, 200)
            .Select(i => $"Line {i}: The quick brown fox jumps over the lazy dog."));
        byte[] original = System.Text.Encoding.UTF8.GetBytes(text);

        byte[] compressed = await XzCompressAsync(original);
        byte[] decompressed = await XzCompressor.DecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(6)]
    [Arguments(9)]
    public async Task XzCompress_LzmaNetDecompress_AllPresets(int preset)
    {
        byte[] original = new byte[4096];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 7 + i / 13);

        byte[] compressed = await XzCompressAsync(original, $"-{preset}");
        byte[] decompressed = await XzCompressor.DecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments("none")]
    [Arguments("crc32")]
    [Arguments("crc64")]
    public async Task XzCompress_LzmaNetDecompress_CheckTypes(string checkName)
    {
        byte[] original = "Integrity check interop test"u8.ToArray();
        byte[] compressed = await XzCompressAsync(original, $"--check={checkName}");
        byte[] decompressed = await XzCompressor.DecompressAsync(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    // ---------------------------------------------------------------
    //  Cross-verify: both directions with same data
    // ---------------------------------------------------------------

    [Test]
    public async Task CrossVerify_LargeCompressibleData()
    {
        byte[] pattern = "ABCDEFGHIJKLMNOP"u8.ToArray();
        byte[] original = new byte[100_000];
        for (int i = 0; i < original.Length; i++)
            original[i] = pattern[i % pattern.Length];

        byte[] ourCompressed = await XzCompressor.CompressAsync(original);
        byte[] xzDecompressed = await XzDecompressAsync(ourCompressed);
        await Assert.That(xzDecompressed.SequenceEqual(original)).IsTrue();

        byte[] xzCompressed = await XzCompressAsync(original);
        byte[] ourDecompressed = await XzCompressor.DecompressAsync(xzCompressed);
        await Assert.That(ourDecompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task CrossVerify_StreamApi()
    {
        byte[] original = new byte[8192];
        new Random(42).NextBytes(original);

        using var compressedStream = new MemoryStream();
        await using (var xzOut = new XzCompressStream(compressedStream, leaveOpen: true))
        {
            int offset = 0;
            while (offset < original.Length)
            {
                int chunkSize = Math.Min(137, original.Length - offset);
                await xzOut.WriteAsync(original.AsMemory(offset, chunkSize));
                offset += chunkSize;
            }
        }

        byte[] decompressed = await XzDecompressAsync(compressedStream.ToArray());
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task CrossVerify_StreamDecompress_XzCompressed()
    {
        byte[] original = new byte[8192];
        new Random(42).NextBytes(original);

        byte[] compressed = await XzCompressAsync(original);

        using var inputStream = new MemoryStream(compressed);
        await using var xzIn = new XzDecompressStream(inputStream);
        using var outputStream = new MemoryStream();

        await xzIn.CopyToAsync(outputStream);

        await Assert.That(outputStream.ToArray().SequenceEqual(original)).IsTrue();
    }

    // ---------------------------------------------------------------
    //  Helpers: shell out to the xz executable
    // ---------------------------------------------------------------

    private static async Task<byte[]> XzCompressAsync(byte[] data, string extraArgs = "")
    {
        return await RunXzAsync($"--compress --stdout --force {extraArgs}", data);
    }

    private static async Task<byte[]> XzDecompressAsync(byte[] data)
    {
        return await RunXzAsync("--decompress --stdout --force", data);
    }

    private static async Task<byte[]> RunXzAsync(string arguments, byte[] stdin)
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

        var writeTask = Task.Run(async () =>
        {
            await proc.StandardInput.BaseStream.WriteAsync(stdin);
            proc.StandardInput.Close();
        });

        using var outputStream = new MemoryStream();
        var stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(outputStream);
        var stderrTask = proc.StandardError.ReadToEndAsync();

        await Task.WhenAll(writeTask, stdoutTask);
        string stderr = await stderrTask;
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"xz exited with code {proc.ExitCode}: {stderr}");

        return outputStream.ToArray();
    }
}

/// <summary>
/// Skips all tests in the class when the xz command-line tool is not available.
/// </summary>
public class RequiresXzAttribute : SkipAttribute
{
    public RequiresXzAttribute() : base("xz command-line tool is not available") { }

    public override Task<bool> ShouldSkip(TestRegisteredContext context)
    {
        return Task.FromResult(!CheckXzAvailable());
    }

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
}
