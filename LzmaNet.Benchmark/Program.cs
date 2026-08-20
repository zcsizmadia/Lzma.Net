// SPDX-License-Identifier: 0BSD
//
// Benchmark methodology (see BENCHMARK.md):
//  - Scenarios: Silesia corpus (real-world mix, ~212 MB), synthetic 16 MB
//    (small-file case), incompressible 32 MB (random).
//  - Median of N timed runs (3 for large inputs, 5 for small), after warmup.
//  - Compression: every implementation runs its own defaults at 1 thread and
//    at ProcessorCount threads; xz additionally runs -T N --block-size=1MiB on
//    the 16 MB scenario, where its 24 MiB default block leaves nothing to
//    parallelize.
//  - Decompression: cross-decode — all decoders decode the SAME reference
//    files (one single-block, one multi-block, both produced by the xz CLI),
//    so the decode column measures decoder speed, not encoder output shape.
//  - Decoded output is verified against the original once per configuration.
//  - Not measured: peak memory (block-parallel modes hold up to N blocks in
//    flight). The xz CLI numbers include process spawn and pipe overhead.

using System.Diagnostics;
using System.IO.Compression;
using LzmaNet;

const int Preset = 6;
int mt = Environment.ProcessorCount;

// "crc" argument: micro-benchmark the checksum implementations and exit.
if (args.Length > 0 && args[0] == "crc")
{
    RunCrcMicroBenchmark();
    return;
}

// ── Test data ───────────────────────────────────────────────────────

string cacheDir = Path.Combine(Path.GetTempPath(), "lzmanet-bench");
Directory.CreateDirectory(cacheDir);

byte[]? silesia = LoadSilesia(cacheDir);

byte[] mixed16 = new byte[16 * 1024 * 1024];
var rng = new Random(42);
for (int i = 0; i < mixed16.Length; i++)
    mixed16[i] = (byte)(i % 256 < 200 ? i % 37 : rng.Next(256));

byte[] random32 = new byte[32 * 1024 * 1024];
new Random(1234).NextBytes(random32);

// ── xz CLI detection ────────────────────────────────────────────────

string? xzPath = DetectXz();
Console.WriteLine($"xz CLI: {xzPath ?? "NOT FOUND (native comparison skipped)"}");
Console.WriteLine($"Threads (MT runs): {mt}");
Console.WriteLine();

// Warmup (JIT) for the in-process implementation
_ = XzCompressor.Decompress(XzCompressor.Compress(mixed16.AsSpan(0, 65536)));

// ── Scenarios ───────────────────────────────────────────────────────

var scenarios = new List<(string Name, byte[]? Data, int Runs, bool XzBlockMatchedRow)>
{
    ("Silesia corpus (211.9 MB, real-world mix)", silesia, 3, false),
    ("Synthetic 16 MB (patterns + random)", mixed16, 5, true),
    ("Incompressible 32 MB (random)", random32, 3, false),
};

foreach (var (name, data, runs, xzBlockMatchedRow) in scenarios)
{
    if (data == null)
    {
        Console.WriteLine($"### {name}: SKIPPED (data unavailable)");
        continue;
    }
    RunScenario(name, data, runs, xzBlockMatchedRow);
}

// ── High-preset comparison (BT4 + optimal parser) ───────────────────
if (silesia != null)
{
    Console.WriteLine("═══════════════════════════════════════════════════════════════════");
    Console.WriteLine("  Silesia corpus — preset 9 (BT4 match finder + optimal parser)");
    Console.WriteLine("═══════════════════════════════════════════════════════════════════");
    Console.WriteLine();
    Console.WriteLine("  | Implementation | Config | MB/s | Ratio | Size | Alloc |");
    Console.WriteLine("  |---|---|---:|---:|---:|---:|");

    double mb9 = silesia.Length / (1024.0 * 1024.0);
    {
        var opts9 = new XzCompressOptions { Preset = 9 };
        long size = 0;
        var (s, alloc) = MedianSecondsWithAlloc(1, () => { size = XzCompressor.Compress(silesia, opts9).Length; });
        PrintComp("LzmaNet", "preset 9, 1T", mb9 / s, size, silesia.Length, alloc);

        var optsMt = new XzCompressOptions { Preset = 9, Threads = mt };
        var (sMt, allocMt) = MedianSecondsWithAlloc(1, () => { size = XzCompressor.Compress(silesia, optsMt).Length; });
        PrintComp("LzmaNet", $"preset 9, {mt}T", mb9 / sMt, size, silesia.Length, allocMt);
    }

    if (xzPath != null)
    {
        string inputFile = Path.Combine(cacheDir, "bench-input9.bin");
        File.WriteAllBytes(inputFile, silesia);
        string outFile = inputFile + ".xz";
        foreach (int threads in new[] { 1, mt })
        {
            long size = 0;
            double s = MedianSeconds(1, () =>
            {
                File.Delete(outFile);
                RunProcess(xzPath, $"-9 -T {threads} -k -q \"{inputFile}\"");
                size = new FileInfo(outFile).Length;
            });
            PrintComp("xz CLI", $"-9, {threads}T", mb9 / s, size, silesia.Length);
        }
        File.Delete(outFile);
        File.Delete(inputFile);
    }
    Console.WriteLine();
}

using (var self = System.Diagnostics.Process.GetCurrentProcess())
{
    Console.WriteLine($"Peak working set (whole benchmark process): {self.PeakWorkingSet64 / (1024.0 * 1024.0):F0} MB");
}

return;

// ── Scenario driver ─────────────────────────────────────────────────

void RunScenario(string name, byte[] data, int runs, bool xzBlockMatchedRow)
{
    double mb = data.Length / (1024.0 * 1024.0);
    Console.WriteLine("═══════════════════════════════════════════════════════════════════");
    Console.WriteLine($"  {name} — median of {runs} runs");
    Console.WriteLine("═══════════════════════════════════════════════════════════════════");

    // ---- Compression (each implementation with its own defaults) ----
    Console.WriteLine();
    Console.WriteLine("  COMPRESSION");
    Console.WriteLine("  | Implementation | Config | MB/s | Ratio | Size | Alloc |");
    Console.WriteLine("  |---|---|---:|---:|---:|---:|");

    foreach (int threads in new[] { 1, mt })
    {
        var opts = new XzCompressOptions { Preset = Preset, Threads = threads };
        long size = 0;
        var (s, alloc) = MedianSecondsWithAlloc(runs, () => { size = XzCompressor.Compress(data, opts).Length; });
        PrintComp("LzmaNet", $"{threads}T defaults", mb / s, size, data.Length, alloc);
    }

    if (xzBlockMatchedRow)
    {
        // Small inputs fit in one default-size block for BOTH implementations,
        // so show the matched small-block MT row for each of them.
        var opts = new XzCompressOptions { Preset = Preset, Threads = mt, BlockSize = 1 << 20 };
        long size = 0;
        var (s, alloc) = MedianSecondsWithAlloc(runs, () => { size = XzCompressor.Compress(data, opts).Length; });
        PrintComp("LzmaNet", $"{mt}T BlockSize=1MiB", mb / s, size, data.Length, alloc);
    }

    string? inputFile = null;
    if (xzPath != null)
    {
        inputFile = Path.Combine(cacheDir, "bench-input.bin");
        File.WriteAllBytes(inputFile, data);

        foreach (var (args, label) in XzCompressConfigs(xzBlockMatchedRow))
        {
            string outFile = inputFile + ".xz";
            long size = 0;
            double s = MedianSeconds(runs, () =>
            {
                File.Delete(outFile);
                RunProcess(xzPath, $"{args} -k -q \"{inputFile}\"");
                size = new FileInfo(outFile).Length;
            });
            PrintComp("xz CLI", label, mb / s, size, data.Length);
            File.Delete(outFile);
        }
    }

    // ---- Decompression (cross-decode shared reference files) ----
    // inputFile is non-null exactly when xzPath is; checking both satisfies
    // nullable flow analysis.
    if (xzPath == null || inputFile == null)
    {
        Console.WriteLine("  (decompression cross-decode skipped: reference files need the xz CLI)");
        Console.WriteLine();
        return;
    }

    // Reference files produced by the NATIVE encoder so no decoder gets
    // home-field advantage.
    string refSingle = inputFile + ".single.xz";
    string refMulti = inputFile + ".multi.xz";
    File.Delete(inputFile + ".xz");
    RunProcess(xzPath, $"-{Preset} -q -k \"{inputFile}\"");
    File.Move(inputFile + ".xz", refSingle, overwrite: true);
    RunProcess(xzPath, $"-{Preset} -q -T {mt} --block-size=1MiB -k \"{inputFile}\"");
    File.Move(inputFile + ".xz", refMulti, overwrite: true);

    byte[] refSingleBytes = File.ReadAllBytes(refSingle);
    byte[] refMultiBytes = File.ReadAllBytes(refMulti);

    Console.WriteLine();
    Console.WriteLine($"  DECOMPRESSION — shared references: single-block ({refSingleBytes.Length:N0} B), " +
                      $"multi-block 1MiB ({refMultiBytes.Length:N0} B), both produced by xz");
    Console.WriteLine("  | Implementation | Config | Single-block MB/s | Multi-block MB/s |");
    Console.WriteLine("  |---|---|---:|---:|");

    foreach (int threads in new[] { 1, mt })
    {
        Verify(XzCompressor.Decompress(refSingleBytes, threads), data, $"LzmaNet {threads}T single");
        Verify(XzCompressor.Decompress(refMultiBytes, threads), data, $"LzmaNet {threads}T multi");
        double sSingle = MedianSeconds(runs, () => DrainLzmaNet(refSingleBytes, threads));
        double sMulti = MedianSeconds(runs, () => DrainLzmaNet(refMultiBytes, threads));
        Console.WriteLine($"  | LzmaNet | {threads}T | {mb / sSingle,8:F1} | {mb / sMulti,8:F1} |");
    }

    foreach (int threads in new[] { 1, mt })
    {
        Verify(DecodeXzCli(xzPath, refSingle, threads), data, $"xz {threads}T single");
        Verify(DecodeXzCli(xzPath, refMulti, threads), data, $"xz {threads}T multi");
        double sSingle = MedianSeconds(runs, () => DrainXzCli(xzPath, refSingle, threads));
        double sMulti = MedianSeconds(runs, () => DrainXzCli(xzPath, refMulti, threads));
        Console.WriteLine($"  | xz CLI | {threads}T | {mb / sSingle,8:F1} | {mb / sMulti,8:F1} |");
    }

    File.Delete(refSingle);
    File.Delete(refMulti);
    File.Delete(inputFile);
    Console.WriteLine();
}

IEnumerable<(string Args, string Label)> XzCompressConfigs(bool blockMatchedRow)
{
    yield return ($"-{Preset} -T 1", "1T defaults");
    yield return ($"-{Preset} -T {mt}", $"{mt}T defaults");
    if (blockMatchedRow)
        yield return ($"-{Preset} -T {mt} --block-size=1MiB", $"{mt}T --block-size=1MiB");
}

static void RunCrcMicroBenchmark()
{
    const int Size = 256 * 1024 * 1024;
    byte[] data = new byte[Size];
    new Random(42).NextBytes(data);
    double gb = Size / (1024.0 * 1024.0 * 1024.0);

    // Warmup
    _ = LzmaNet.Check.Crc32.Compute(data.AsSpan(0, 1 << 20));
    _ = LzmaNet.Check.Crc32.ComputeScalar(data.AsSpan(0, 1 << 20));
    _ = LzmaNet.Check.Crc64.Compute(data.AsSpan(0, 1 << 20));
    _ = LzmaNet.Check.Crc64.ComputeScalar(data.AsSpan(0, 1 << 20));

    Console.WriteLine($"CRC micro-benchmark over {Size / (1024 * 1024)} MB (median of 5):");
    Report("CRC32 table (slicing-by-8)", gb, MedianSeconds(5, () => _ = LzmaNet.Check.Crc32.ComputeScalar(data)));
    Report("CRC32 vector (clmul fold)  ", gb, MedianSeconds(5, () => _ = LzmaNet.Check.Crc32.Compute(data)));
    Report("CRC64 table (slicing-by-8)", gb, MedianSeconds(5, () => _ = LzmaNet.Check.Crc64.ComputeScalar(data)));
    Report("CRC64 vector (clmul fold)  ", gb, MedianSeconds(5, () => _ = LzmaNet.Check.Crc64.Compute(data)));

    // Consistency spot-check
    if (LzmaNet.Check.Crc64.Compute(data) != LzmaNet.Check.Crc64.ComputeScalar(data)
        || LzmaNet.Check.Crc32.Compute(data) != LzmaNet.Check.Crc32.ComputeScalar(data))
        throw new Exception("CRC mismatch between vector and scalar paths!");
    Console.WriteLine("Vector/scalar results verified identical.");

    static void Report(string name, double gb, double seconds)
        => Console.WriteLine($"  {name}: {gb / seconds,7:F2} GB/s");
}

// ── Helpers ─────────────────────────────────────────────────────────

static void PrintComp(string impl, string config, double mbps, long size, long originalLength,
    long allocBytes = -1)
{
    string alloc = allocBytes >= 0 ? $"{allocBytes / (1024.0 * 1024.0),7:F0} MB" : "n/a";
    Console.WriteLine($"  | {impl} | {config} | {mbps,8:F1} | {(double)size / originalLength * 100,5:F1}% | {size:N0} | {alloc} |");
}

static double MedianSeconds(int runs, Action action)
{
    var times = new double[runs];
    for (int i = 0; i < runs; i++)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        times[i] = sw.Elapsed.TotalSeconds;
    }
    Array.Sort(times);
    return times[runs / 2];
}

static (double Seconds, long AllocBytes) MedianSecondsWithAlloc(int runs, Action action)
{
    var times = new double[runs];
    var allocs = new long[runs];
    for (int i = 0; i < runs; i++)
    {
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        times[i] = sw.Elapsed.TotalSeconds;
        allocs[i] = GC.GetTotalAllocatedBytes(precise: true) - before;
    }
    Array.Sort(times, allocs);
    return (times[runs / 2], allocs[runs / 2]);
}

static void Verify(byte[] decoded, byte[] original, string what)
{
    if (!decoded.AsSpan().SequenceEqual(original))
        throw new Exception($"VERIFICATION FAILED: {what}");
}

static void DrainLzmaNet(byte[] compressed, int threads)
{
    using var input = new MemoryStream(compressed, 0, compressed.Length, false, publiclyVisible: true);
    using var xz = new XzDecompressStream(input, threads, leaveOpen: true);
    xz.CopyTo(Stream.Null);
}

static byte[] DecodeXzCli(string xzPath, string file, int threads)
{
    var p = Process.Start(new ProcessStartInfo(xzPath, $"-d -T {threads} -q -k -c \"{file}\"")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    })!;
    using var output = new MemoryStream();
    p.StandardOutput.BaseStream.CopyTo(output);
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception($"xz decode failed (exit {p.ExitCode})");
    return output.ToArray();
}

static void DrainXzCli(string xzPath, string file, int threads)
{
    var p = Process.Start(new ProcessStartInfo(xzPath, $"-d -T {threads} -q -k -c \"{file}\"")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    })!;
    p.StandardOutput.BaseStream.CopyTo(Stream.Null);
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception($"xz decode failed (exit {p.ExitCode})");
}

static void RunProcess(string path, string args)
{
    var p = Process.Start(new ProcessStartInfo(path, args)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
    })!;
    p.WaitForExit();
    if (p.ExitCode != 0)
        throw new Exception($"{path} {args} failed (exit {p.ExitCode}): {p.StandardError.ReadToEnd()}");
}

static string? DetectXz()
{
    try
    {
        var p = Process.Start(new ProcessStartInfo("xz", "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        p?.WaitForExit();
        if (p?.ExitCode == 0) return "xz";
    }
    catch { }

    foreach (var path in new[] { "/usr/bin/xz", "/usr/local/bin/xz" })
        if (File.Exists(path)) return path;
    return null;
}

static byte[]? LoadSilesia(string cacheDir)
{
    string binPath = Path.Combine(cacheDir, "silesia.bin");
    if (File.Exists(binPath))
        return File.ReadAllBytes(binPath);

    string zipPath = Path.Combine(cacheDir, "silesia.zip");
    try
    {
        if (!File.Exists(zipPath))
        {
            Console.WriteLine("Downloading Silesia corpus (~68 MB)...");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            byte[] zip = http.GetByteArrayAsync("https://sun.aei.polsl.pl/~sdeor/corpus/silesia.zip")
                .GetAwaiter().GetResult();
            File.WriteAllBytes(zipPath, zip);
        }

        // Concatenate the corpus files in deterministic (sorted) order.
        using var archive = ZipFile.OpenRead(zipPath);
        using var output = new MemoryStream();
        foreach (var entry in archive.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            if (entry.Length == 0) continue;
            using var s = entry.Open();
            s.CopyTo(output);
        }
        byte[] data = output.ToArray();
        File.WriteAllBytes(binPath, data);
        return data;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Silesia corpus unavailable ({ex.Message}) — scenario will be skipped.");
        return null;
    }
}
