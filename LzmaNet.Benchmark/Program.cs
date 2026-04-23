using System.Diagnostics;
using LzmaNet;
using ZCS.XZ;

const int DataSize = 16 * 1024 * 1024; // 16 MB
const int Preset = 6;

// Generate test data: mix of compressible patterns and random bytes
Console.WriteLine($"Generating {DataSize / (1024 * 1024)} MB test data...");
var rng = new Random(42);
byte[] original = new byte[DataSize];
for (int i = 0; i < original.Length; i++)
    original[i] = (byte)(i % 256 < 200 ? i % 37 : rng.Next(256));

// Sanity check
Console.WriteLine("Sanity checking large round-trip...");
{
    byte[] c = XzCompressor.Compress(original);
    byte[] d = XzCompressor.Decompress(c);
    if (!original.AsSpan().SequenceEqual(d))
    {
        Console.WriteLine("  FAILED: round-trip mismatch!");
        return;
    }
    Console.WriteLine($"  OK ({c.Length:N0} bytes compressed, {(double)c.Length / original.Length * 100:F1}%)");
}
Console.WriteLine();

// Detect xz
string? xzPath = null;
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
    if (p?.ExitCode == 0)
        xzPath = "xz";
}
catch { }

if (xzPath == null)
{
    // Try common paths
    foreach (var path in new[] { "/usr/bin/xz", "/usr/local/bin/xz" })
    {
        if (File.Exists(path)) { xzPath = path; break; }
    }
}

Console.WriteLine($"xz CLI: {xzPath ?? "NOT FOUND"}");
Console.WriteLine();

int[] threadCounts = [1, Environment.ProcessorCount];
if (threadCounts[1] == 1)
    threadCounts = [1]; // avoid duplicate

// ── LzmaNet benchmarks ──────────────────────────────────────────────
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  LzmaNet (pure C#)");
Console.WriteLine("═══════════════════════════════════════════════════════");

var lzmaNetResults = new List<(int threads, long compressMs, long decompressMs, int compressedSize)>();

foreach (int threads in threadCounts)
{
    var opts = new XzCompressOptions { Preset = Preset, Threads = threads };
    // Use smaller block size for MT to create enough blocks for parallelism
    if (threads > 1)
        opts.BlockSize = 1 << 20; // 1 MB blocks → 16 blocks for 16 MB data

    // Warmup
    _ = XzCompressor.Compress(original.AsSpan(0, 4096), opts);

    // Compress
    var sw = Stopwatch.StartNew();
    byte[] compressed = XzCompressor.Compress(original, opts);
    sw.Stop();
    long compressMs = sw.ElapsedMilliseconds;

    // Decompress
    sw.Restart();
    byte[] decompressed = XzCompressor.Decompress(compressed);
    sw.Stop();
    long decompressMs = sw.ElapsedMilliseconds;

    if (!original.AsSpan().SequenceEqual(decompressed))
        throw new Exception("Round-trip verification failed!");

    double ratio = (double)compressed.Length / original.Length * 100;
    double compMBps = (double)original.Length / (1024 * 1024) / (compressMs / 1000.0);
    double decMBps = (double)original.Length / (1024 * 1024) / (decompressMs / 1000.0);

    Console.WriteLine($"  Threads: {threads,-4}  Compress: {compressMs,6} ms ({compMBps,6:F1} MB/s)  " +
                      $"Decompress: {decompressMs,5} ms ({decMBps,6:F1} MB/s)  " +
                      $"Ratio: {ratio:F1}%  Size: {compressed.Length:N0}");

    lzmaNetResults.Add((threads, compressMs, decompressMs, compressed.Length));
}

// ── ZCS.XZ (liblzma P/Invoke) benchmarks ─────────────────────────────
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  ZCS.XZ (liblzma via P/Invoke)");
Console.WriteLine("═══════════════════════════════════════════════════════");

foreach (int threads in threadCounts)
{
    var zcsOpts = new XZCompressOptions
    {
        Level = (XZCompressionLevel)Preset,
        Threads = threads,
    };

    // Warmup
    {
        using var warmMs = new MemoryStream();
        using (var warmXz = new XZCompressStream(warmMs, zcsOpts, leaveOpen: true))
            warmXz.Write(original.AsSpan(0, 4096));
    }

    // Compress
    byte[] zcsCompressed;
    var sw2 = Stopwatch.StartNew();
    {
        using var outMs = new MemoryStream();
        using (var xzStream = new XZCompressStream(outMs, zcsOpts, leaveOpen: true))
            xzStream.Write(original);
        zcsCompressed = outMs.ToArray();
    }
    sw2.Stop();
    long zcsCompressMs = sw2.ElapsedMilliseconds;

    // Decompress
    sw2.Restart();
    byte[] zcsDecompressed;
    {
        using var inMs = new MemoryStream(zcsCompressed);
        using var xzStream = new XZDecompressStream(inMs, leaveOpen: true);
        using var outMs = new MemoryStream();
        xzStream.CopyTo(outMs);
        zcsDecompressed = outMs.ToArray();
    }
    sw2.Stop();
    long zcsDecompressMs = sw2.ElapsedMilliseconds;

    if (!original.AsSpan().SequenceEqual(zcsDecompressed))
        throw new Exception("ZCS.XZ round-trip verification failed!");

    double zcsRatio = (double)zcsCompressed.Length / original.Length * 100;
    double zcsCompMBps = (double)original.Length / (1024 * 1024) / (zcsCompressMs / 1000.0);
    double zcsDecMBps = (double)original.Length / (1024 * 1024) / (zcsDecompressMs / 1000.0);

    Console.WriteLine($"  Threads: {threads,-4}  Compress: {zcsCompressMs,6} ms ({zcsCompMBps,6:F1} MB/s)  " +
                      $"Decompress: {zcsDecompressMs,5} ms ({zcsDecMBps,6:F1} MB/s)  " +
                      $"Ratio: {zcsRatio:F1}%  Size: {zcsCompressed.Length:N0}");
}

// ── xz CLI benchmarks ────────────────────────────────────────────────
if (xzPath != null)
{
    Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════════════════════");
    Console.WriteLine($"  xz CLI ({xzPath})");
    Console.WriteLine("═══════════════════════════════════════════════════════");

    string tmpInput = Path.GetTempFileName();
    File.WriteAllBytes(tmpInput, original);

    foreach (int threads in threadCounts)
    {
        string tmpCompressed = tmpInput + ".xz";

        // Compress
        if (File.Exists(tmpCompressed)) File.Delete(tmpCompressed);
        var sw = Stopwatch.StartNew();
        var pCompress = Process.Start(new ProcessStartInfo(xzPath,
            $"-{Preset} -T {threads} -k \"{tmpInput}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        });
        pCompress!.WaitForExit();
        sw.Stop();
        long compressMs = sw.ElapsedMilliseconds;

        if (pCompress.ExitCode != 0)
        {
            Console.WriteLine($"  xz compress failed (exit {pCompress.ExitCode}): {pCompress.StandardError.ReadToEnd()}");
            continue;
        }

        long compressedSize = new FileInfo(tmpCompressed).Length;

        // Decompress
        string tmpDecompressed = Path.GetTempFileName();
        sw.Restart();
        var pDecompress = Process.Start(new ProcessStartInfo(xzPath,
            $"-d -T {threads} -k -c \"{tmpCompressed}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        using (var outFile = File.Create(tmpDecompressed))
        {
            pDecompress!.StandardOutput.BaseStream.CopyTo(outFile);
        }
        pDecompress!.WaitForExit();
        sw.Stop();
        long decompressMs = sw.ElapsedMilliseconds;

        double ratio = (double)compressedSize / original.Length * 100;
        double compMBps = (double)original.Length / (1024 * 1024) / (compressMs / 1000.0);
        double decMBps = (double)original.Length / (1024 * 1024) / (decompressMs / 1000.0);

        Console.WriteLine($"  Threads: {threads,-4}  Compress: {compressMs,6} ms ({compMBps,6:F1} MB/s)  " +
                          $"Decompress: {decompressMs,5} ms ({decMBps,6:F1} MB/s)  " +
                          $"Ratio: {ratio:F1}%  Size: {compressedSize:N0}");

        // Cleanup
        File.Delete(tmpCompressed);
        File.Delete(tmpDecompressed);
    }

    File.Delete(tmpInput);

    // ── Comparison ────────────────────────────────────────────────────
    Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════════════════════");
    Console.WriteLine("  Note: xz CLI and ZCS.XZ use the native C liblzma.");
    Console.WriteLine("  LzmaNet is pure managed C# — no native dependencies.");
    Console.WriteLine("═══════════════════════════════════════════════════════");
}
else
{
    Console.WriteLine();
    Console.WriteLine("  (xz CLI not found — skipping comparison)");
}
