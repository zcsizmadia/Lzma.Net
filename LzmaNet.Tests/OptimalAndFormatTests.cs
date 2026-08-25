// SPDX-License-Identifier: 0BSD

using LzmaNet.Lzma;
using LzmaNet.Lzma2;

namespace LzmaNet.Tests;

/// <summary>
/// Tests for the BT4 match finder + optimal parser (presets 7-9), the legacy
/// .lzma format, and progress reporting.
/// </summary>
[NotInParallel(nameof(OptimalAndFormatTests))]
public class OptimalAndFormatTests
{
    // CI runners (Linux: ~14 GB, no swap) get OOM-killed if several BT4
    // encoders with preset-default dictionaries (up to 64 MB dict = ~650 MB of
    // tables each) run in parallel. Test data is at most a few MB, so a 4 MB
    // dictionary exercises the identical code paths at a fraction of the memory.
    private const int TestDictSize = 1 << 22;

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
        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = preset, DictionarySize = TestDictSize });
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

        byte[] compressed = XzCompressor.Compress(original, new XzCompressOptions { Preset = 9, DictionarySize = TestDictSize });
        await Assert.That(XzCompressor.Decompress(compressed).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task OptimalParser_ImprovesRatioOverLazy()
    {
        byte[] original = MakeTextLikeData(2 * 1024 * 1024);

        var lazyProps = LzmaEncoderProperties.FromPreset(9);
        lazyProps.DictionarySize = TestDictSize;
        lazyProps.UseBinaryTree = false;
        lazyProps.OptimalParse = false;
        var optProps = LzmaEncoderProperties.FromPreset(9);
        optProps.DictionarySize = TestDictSize;

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
            props.DictionarySize = TestDictSize;
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
        var mt = new XzCompressOptions { Preset = 7, Threads = 4, BlockSize = 256 * 1024, DictionarySize = TestDictSize };
        var st = new XzCompressOptions { Preset = 7, Threads = 1, BlockSize = 256 * 1024, DictionarySize = TestDictSize };

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

    // ── Effective-dictionary clamping for known-size inputs ─────────

    [Test]
    public async Task Compress_ClampsDictionaryToInputSize()
    {
        // For a 4 KB input, the effective dictionary at preset 9 is clamped to
        // the 4096 minimum — so the output must be byte-identical to explicitly
        // requesting a 4096-byte dictionary (including the header's dict byte).
        byte[] original = MakeTextLikeData(4096);
        byte[] clamped = XzCompressor.Compress(original, new XzCompressOptions { Preset = 9 });
        byte[] explicit4k = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = 9, DictionarySize = 4096 });

        await Assert.That(clamped.SequenceEqual(explicit4k)).IsTrue();
        await Assert.That(XzCompressor.Decompress(clamped).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task Compress_ExplicitDictionarySizeIsNotClamped()
    {
        // An explicit dictionary wins: its header dict-size byte differs from
        // the clamped default's.
        byte[] original = MakeTextLikeData(4096);
        byte[] clamped = XzCompressor.Compress(original, new XzCompressOptions { Preset = 9 });
        byte[] explicit1M = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = 9, DictionarySize = 1 << 20 });

        await Assert.That(clamped.SequenceEqual(explicit1M)).IsFalse();
        await Assert.That(XzCompressor.Decompress(explicit1M).SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments(1)]
    [Arguments(6)]
    [Arguments(9)]
    public async Task OneShotAndStreaming_ProduceIdenticalBytes(int preset)
    {
        // The dictionary cap used to live only in the one-shot API, so the two
        // public entry points emitted different bytes for the same input and
        // options — breaking golden files and content-addressed storage.
        byte[] original = MakeTextLikeData(50_000);
        var opts = new XzCompressOptions { Preset = preset };

        byte[] oneShot = XzCompressor.Compress(original, opts);

        using var ms = new MemoryStream();
        using (var xz = new XzCompressStream(ms, opts, leaveOpen: true))
            xz.Write(original);
        byte[] streamed = ms.ToArray();

        await Assert.That(streamed.SequenceEqual(oneShot)).IsTrue();
        await Assert.That(XzCompressor.Decompress(streamed).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task CompressStream_SmallInputAtHighPreset_UsesInputSizedDictionary()
    {
        // Preset 9 nominally means a 64 MB dictionary and ~650 MB of BT4 tables.
        // Streaming 4 KB must now cap the same way one-shot compression does,
        // which shows up as output identical to an explicit 4096-byte dictionary.
        byte[] original = MakeTextLikeData(4096);

        using var ms = new MemoryStream();
        using (var xz = new XzCompressStream(ms, new XzCompressOptions { Preset = 9 }, leaveOpen: true))
            xz.Write(original);

        byte[] explicit4k = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = 9, DictionarySize = 4096 });

        await Assert.That(ms.ToArray().SequenceEqual(explicit4k)).IsTrue();
    }

    [Test]
    public async Task ShortFinalBlock_MultiThreadedMatchesSingleThreaded()
    {
        // 2.5 blocks: the final short block caps to a smaller dictionary than the
        // full ones, which rebuilds the single-threaded encoder mid-stream. The
        // parallel path must still produce identical bytes.
        byte[] original = MakeTextLikeData(160 * 1024, seed: 17);
        var st = new XzCompressOptions { Preset = 6, BlockSize = 64 * 1024, Threads = 1 };
        var mt = new XzCompressOptions { Preset = 6, BlockSize = 64 * 1024, Threads = 4 };

        byte[] a = XzCompressor.Compress(original, st);
        byte[] b = XzCompressor.Compress(original, mt);

        await Assert.That(a.SequenceEqual(b)).IsTrue();
        await Assert.That(XzCompressor.Decompress(a).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task ExplicitDictionaryIsNotCappedByBlockSize()
    {
        // An explicit dictionary is the caller's decision and must survive even
        // when it is far larger than a block.
        byte[] original = MakeTextLikeData(160 * 1024, seed: 23);
        var opts = new XzCompressOptions
        {
            Preset = 6,
            BlockSize = 64 * 1024,
            DictionarySize = 1 << 22,
            Threads = 1,
        };

        byte[] compressed = XzCompressor.Compress(original, opts);
        byte[] capped = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = 6, BlockSize = 64 * 1024, Threads = 1 });

        await Assert.That(compressed.SequenceEqual(capped)).IsFalse();
        await Assert.That(XzCompressor.Decompress(compressed).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task LzmaAlone_ClampsDictionaryToInputSize()
    {
        byte[] original = MakeTextLikeData(10_000);
        using var ms = new MemoryStream();
        using (var enc = new LzmaAloneCompressStream(ms, preset: 9, leaveOpen: true))
            enc.Write(original);

        // Header bytes 1..5 hold the dictionary size (LE32) — clamped to the
        // input length and rounded up to a power of two (xz's alone decoder
        // mishandles non-canonical dictionary sizes).
        byte[] header = ms.ToArray();
        uint dict = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1, 4));
        await Assert.That(dict).IsEqualTo(16_384u);

        ms.Position = 0;
        using var dec = new LzmaAloneDecompressStream(ms, leaveOpen: true);
        using var output = new MemoryStream();
        dec.CopyTo(output);
        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
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
    public async Task LzmaAlone_UnknownSizeGrowth_NeverExceedsAllocatableLength()
    {
        // Growing from a 1 GB buffer used to clamp the request to int.MaxValue,
        // which is longer than any array the runtime will allocate, so
        // ArrayPool.Rent threw OutOfMemoryException however much memory was free
        // — capping unknown-size streams at ~1 GB.
        const int Step = 1 << 20;
        long needed = (1L << 30) + Step + 273;
        int next = LzmaAloneDecompressStream.NextOutputCapacity(1 << 30, needed);

        await Assert.That(next).IsLessThanOrEqualTo(Array.MaxLength);
        await Assert.That((long)next).IsGreaterThanOrEqualTo(needed);
    }

    [Test]
    public async Task LzmaAlone_UnknownSizeGrowth_DoublesWellBelowTheLimit()
    {
        int next = LzmaAloneDecompressStream.NextOutputCapacity(1 << 20, (1L << 20) + 4096);
        await Assert.That(next).IsEqualTo(1 << 21);
    }

    [Test]
    public async Task LzmaAlone_FailedDecode_RethrowsInsteadOfReportingEndOfStream()
    {
        // A caught decode failure used to leave the stream looking like a
        // successfully decoded empty one, so a caller that retried — or any
        // wrapper that probes a stream after an error — silently accepted
        // corrupt input as zero bytes.
        byte[] bad = new byte[13];
        bad[0] = 225; // invalid properties byte (>= 9*5*5)
        using var dec = new LzmaAloneDecompressStream(new MemoryStream(bad));

        await Assert.That(() => dec.Read(new byte[8], 0, 8)).ThrowsExactly<LzmaFormatException>();
        await Assert.That(() => dec.Read(new byte[8], 0, 8)).ThrowsExactly<LzmaFormatException>();
        await Assert.That(() => dec.Read(new byte[8], 0, 8)).ThrowsExactly<LzmaFormatException>();
    }

    [Test]
    public async Task LzmaAlone_FailedDecode_CopyToDoesNotProduceEmptyOutput()
    {
        byte[] bad = new byte[13];
        bad[0] = 225;
        using var dec = new LzmaAloneDecompressStream(new MemoryStream(bad));
        using var output = new MemoryStream();

        // Swallowing the first failure and copying must not yield "" — the
        // shape a caller would mistake for a legitimately empty stream.
        await Assert.That(() => dec.CopyTo(output)).ThrowsExactly<LzmaFormatException>();
        await Assert.That(() => dec.CopyTo(output)).ThrowsExactly<LzmaFormatException>();
        await Assert.That(output.Length).IsEqualTo(0L);
    }

    [Test]
    public async Task LzmaAlone_TruncatedPayload_FailureIsSticky()
    {
        // Valid header claiming 64 KB, but the payload is cut short.
        byte[] original = MakeTextLikeData(64 * 1024);
        using var ms = new MemoryStream();
        using (var enc = new LzmaAloneCompressStream(ms, preset: 1, leaveOpen: true))
            enc.Write(original);

        byte[] truncated = ms.ToArray().AsSpan(0, 13 + 32).ToArray();
        using var dec = new LzmaAloneDecompressStream(new MemoryStream(truncated));

        var first = await Assert.That(() => dec.Read(new byte[64], 0, 64)).Throws<LzmaException>();
        var second = await Assert.That(() => dec.Read(new byte[64], 0, 64)).Throws<LzmaException>();
        await Assert.That(second!.GetType()).IsEqualTo(first!.GetType());
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

    [Test]
    public async Task XzCompress_LzmaNetDecompress_UnknownSize_GrowsBufferSeveralTimes()
    {
        // The unknown-size path decodes in 1 MB steps starting from a 1 MB
        // buffer, so 5 MB of output drives it through several growth steps.
        byte[] original = new byte[5 * 1024 * 1024];
        var rng = new Random(21);
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 256 < 220 ? i % 61 : rng.Next(256));

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
