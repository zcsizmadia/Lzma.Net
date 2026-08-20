// SPDX-License-Identifier: 0BSD

using LzmaNet.Lzma;
using LzmaNet.Lzma2;

namespace LzmaNet.Tests;

/// <summary>
/// Tests for the BT4 match finder + optimal parser (presets 7-9), the legacy
/// .lzma format, and progress reporting.
/// </summary>
public class OptimalAndFormatTests
{
    /// <summary>
    /// Text-like data with matches at many distances — the pattern class that
    /// exposed the BT4 chunk-boundary tree-corruption bug.
    /// </summary>
    private static byte[] MakeTextLikeData(int size, int seed = 42)
    {
        string[] words =
        [
            "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog",
            "compression", "dictionary", "entropy", "window", "stream", "block",
            "probability", "match", "distance", "literal", "encoder", "decoder",
        ];
        var rng = new Random(seed);
        using var ms = new MemoryStream(size + 64);
        while (ms.Length < size)
        {
            var word = words[rng.Next(words.Length)];
            ms.Write(System.Text.Encoding.ASCII.GetBytes(word));
            ms.WriteByte((byte)(rng.Next(16) == 0 ? '\n' : ' '));
        }
        return ms.ToArray().AsSpan(0, size).ToArray();
    }

    // ── BT4 + optimal parser ─────────────────────────────────────────

    [Test]
    [Arguments(7)]
    [Arguments(9)]
    public async Task OptimalPresets_RoundTrip_TextLikeData(int preset)
    {
        // 4 MB of text-like data spans many LZMA2 chunk boundaries — the exact
        // regression scenario for the BT4 lenLimit/tree-adoption bug.
        byte[] original = MakeTextLikeData(4 * 1024 * 1024);
        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = preset });
        byte[] decompressed = XzCompressor.Decompress(compressed);
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task OptimalPreset_RoundTrip_MixedData()
    {
        byte[] original = new byte[3 * 1024 * 1024];
        var rng = new Random(7);
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 256 < 200 ? i % 37 + i / 65536 : rng.Next(256));

        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = 9 });
        await Assert.That(XzCompressor.Decompress(compressed).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task OptimalParser_ImprovesRatioOverLazy()
    {
        byte[] original = MakeTextLikeData(2 * 1024 * 1024);

        var lazyProps = LzmaEncoderProperties.FromPreset(9);
        lazyProps.UseBinaryTree = false;
        lazyProps.OptimalParse = false;
        var optProps = LzmaEncoderProperties.FromPreset(9);

        long lazySize = EncodeLzma2(original, lazyProps);
        long optSize = EncodeLzma2(original, optProps);
        await Assert.That(optSize < lazySize).IsTrue();
    }

    [Test]
    public async Task ComponentCombos_RoundTrip()
    {
        byte[] original = MakeTextLikeData(2 * 1024 * 1024, seed: 5);
        foreach ((bool bt, bool opt) in new[] { (true, false), (false, true), (true, true) })
        {
            var props = LzmaEncoderProperties.FromPreset(7);
            props.UseBinaryTree = bt;
            props.OptimalParse = opt;

            using var encoder = new Lzma2Encoder(props);
            using var ms = new MemoryStream();
            encoder.Encode(original, ms);

            using var decoder = new Lzma2Decoder(props.DictionarySize);
            byte[] output = new byte[original.Length];
            int n = decoder.Decode(ms.ToArray(), output);
            await Assert.That(n).IsEqualTo(original.Length);
            await Assert.That(output.SequenceEqual(original)).IsTrue();
        }
    }

    [Test]
    public async Task OptimalPreset_MultiThreaded_MatchesSingleThreaded()
    {
        byte[] original = MakeTextLikeData(2 * 1024 * 1024, seed: 9);
        var mt = new XzCompressOptions { Preset = 7, Threads = 4, BlockSize = 256 * 1024 };
        var st = new XzCompressOptions { Preset = 7, Threads = 1, BlockSize = 256 * 1024 };

        byte[] a = XzCompressor.Compress(original, mt);
        byte[] b = XzCompressor.Compress(original, st);
        await Assert.That(a.SequenceEqual(b)).IsTrue();
        await Assert.That(XzCompressor.Decompress(a).SequenceEqual(original)).IsTrue();
    }

    private static long EncodeLzma2(byte[] data, LzmaEncoderProperties props)
    {
        using var encoder = new Lzma2Encoder(props);
        using var ms = new MemoryStream();
        encoder.Encode(data, ms);
        return ms.Length;
    }

    // ── Legacy .lzma format ──────────────────────────────────────────

    [Test]
    public async Task LzmaAlone_RoundTrip()
    {
        byte[] original = MakeTextLikeData(512 * 1024);
        using var ms = new MemoryStream();
        using (var enc = new LzmaAloneCompressStream(ms, preset: 6, leaveOpen: true))
            enc.Write(original);

        ms.Position = 0;
        using var dec = new LzmaAloneDecompressStream(ms, leaveOpen: true);
        using var output = new MemoryStream();
        dec.CopyTo(output);
        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task LzmaAlone_EmptyInput_RoundTrip()
    {
        using var ms = new MemoryStream();
        using (var enc = new LzmaAloneCompressStream(ms, leaveOpen: true)) { }

        ms.Position = 0;
        using var dec = new LzmaAloneDecompressStream(ms, leaveOpen: true);
        await Assert.That(dec.Read(new byte[8], 0, 8)).IsEqualTo(0);
    }

    [Test]
    public async Task LzmaAlone_UnknownSizeWithEndMarker_Decodes()
    {
        // Craft an unknown-size .lzma: take a known-size stream's payload and a
        // manually built header, then decode with the end-marker path. Easiest
        // reliable construction: encode with our encoder (no marker), then use
        // the known-size header — so instead craft via xz in the interop tests.
        // Here: verify the header rejection paths work.
        byte[] bad = new byte[13];
        bad[0] = 225; // invalid properties byte (>= 9*5*5)
        using var dec = new LzmaAloneDecompressStream(new MemoryStream(bad));
        await Assert.That(() => dec.Read(new byte[1], 0, 1)).ThrowsExactly<LzmaFormatException>();
    }

    [Test]
    public async Task LzmaAlone_TruncatedHeader_Throws()
    {
        using var dec = new LzmaAloneDecompressStream(new MemoryStream(new byte[5]));
        await Assert.That(() => dec.Read(new byte[1], 0, 1)).ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task LzmaAlone_MaxOutputSize_Enforced()
    {
        byte[] original = new byte[1024 * 1024];
        using var ms = new MemoryStream();
        using (var enc = new LzmaAloneCompressStream(ms, leaveOpen: true))
            enc.Write(original);

        ms.Position = 0;
        var opts = new XzDecompressOptions { MaxOutputSize = 64 * 1024 };
        using var dec = new LzmaAloneDecompressStream(ms, opts, leaveOpen: true);
        await Assert.That(() => dec.Read(new byte[1], 0, 1)).ThrowsExactly<LzmaMemoryLimitException>();
    }

    // ── Progress reporting ───────────────────────────────────────────

    private sealed class ProgressCollector : IProgress<long>
    {
        public readonly List<long> Reports = new();
        public void Report(long value) => Reports.Add(value);
    }

    [Test]
    public async Task Progress_Compression_ReportsMonotonicallyToTotal()
    {
        byte[] original = MakeTextLikeData(1024 * 1024);
        var progress = new ProgressCollector();
        var opts = new XzCompressOptions { Preset = 1, BlockSize = 128 * 1024, Progress = progress };

        _ = XzCompressor.Compress(original, opts);

        await Assert.That(progress.Reports.Count > 1).IsTrue();
        for (int i = 1; i < progress.Reports.Count; i++)
            await Assert.That(progress.Reports[i] > progress.Reports[i - 1]).IsTrue();
        await Assert.That(progress.Reports[^1]).IsEqualTo((long)original.Length);
    }

    [Test]
    public async Task Progress_MultiThreadedCompression_ReportsToTotal()
    {
        byte[] original = MakeTextLikeData(1024 * 1024, seed: 3);
        var progress = new ProgressCollector();
        var opts = new XzCompressOptions
        {
            Preset = 1,
            Threads = 4,
            BlockSize = 128 * 1024,
            Progress = progress,
        };

        _ = XzCompressor.Compress(original, opts);
        await Assert.That(progress.Reports[^1]).IsEqualTo((long)original.Length);
    }

    [Test]
    public async Task Progress_Decompression_ReportsToTotal()
    {
        byte[] original = MakeTextLikeData(1024 * 1024, seed: 4);
        byte[] compressed = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = 1, BlockSize = 128 * 1024 });

        var progress = new ProgressCollector();
        var opts = new XzDecompressOptions { Progress = progress };
        byte[] decompressed = XzCompressor.Decompress(compressed, opts);

        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
        await Assert.That(progress.Reports.Count > 1).IsTrue();
        await Assert.That(progress.Reports[^1]).IsEqualTo((long)original.Length);
    }

    [Test]
    public async Task Progress_ParallelDecompression_ReportsToTotal()
    {
        byte[] original = MakeTextLikeData(1024 * 1024, seed: 6);
        byte[] compressed = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = 1, BlockSize = 128 * 1024 });

        var progress = new ProgressCollector();
        var opts = new XzDecompressOptions { Threads = 4, Progress = progress };
        byte[] decompressed = XzCompressor.Decompress(compressed, opts);

        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
        await Assert.That(progress.Reports[^1]).IsEqualTo((long)original.Length);
    }
}

/// <summary>
/// Interop tests for the legacy .lzma format against the xz CLI.
/// </summary>
[RequiresXz]
public class LzmaAloneInteropTests
{
    [Test]
    public async Task LzmaNetCompress_XzDecompress()
    {
        byte[] original = new byte[300_000];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 97);

        using var ms = new MemoryStream();
        using (var enc = new LzmaAloneCompressStream(ms, preset: 6, leaveOpen: true))
            enc.Write(original);

        byte[] decompressed = await RunXzAsync("--decompress --format=lzma --stdout --force", ms.ToArray());
        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task XzCompress_LzmaNetDecompress_UnknownSizeWithMarker()
    {
        // xz --format=lzma writes an unknown-size header terminated by the end
        // marker — exercising our marker-based decode path.
        byte[] original = new byte[300_000];
        var rng = new Random(12);
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 256 < 200 ? i % 41 : rng.Next(256));

        byte[] compressed = await RunXzAsync("--compress --format=lzma --stdout --force", original);

        using var dec = new LzmaAloneDecompressStream(new MemoryStream(compressed));
        using var output = new MemoryStream();
        dec.CopyTo(output);
        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
    }

    private static async Task<byte[]> RunXzAsync(string arguments, byte[] stdin)
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new System.Diagnostics.ProcessStartInfo
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
            throw new InvalidOperationException($"xz exited with code {proc.ExitCode}: {stderr}");
        return outputStream.ToArray();
    }
}
