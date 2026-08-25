// SPDX-License-Identifier: 0BSD

using LzmaNet.LZ;

namespace LzmaNet.Tests;

/// <summary>
/// Invariant tests that drive the match finders directly: every match a finder
/// reports must be genuine (the bytes really do match for the reported length)
/// and must use a decoder-legal distance. A corrupted search structure shows up
/// here deterministically, whereas a round trip only fails once a bad match
/// happens to be selected by the parser.
/// </summary>
public class MatchFinderInvariantTests
{
    /// <summary>
    /// Data whose period is exactly the dictionary size, so nearly every position
    /// has a candidate at distance == dictSize — the window-floor boundary where a
    /// tree node's cyclic slot can collide with the inserting position's own slot.
    /// Noise bytes break up the runs so match lengths vary and the tree grows deep
    /// instead of every position terminating on a full-length match.
    /// </summary>
    private static byte[] MakePeriodicWithNoise(int period, int length, int seed)
    {
        var rng = new Random(seed);
        byte[] pattern = new byte[period];
        rng.NextBytes(pattern);

        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = pattern[i % period];
        for (int i = 0; i < length; i += 48 + rng.Next(32))
            data[i] = (byte)rng.Next(256);
        return data;
    }

    /// <summary>
    /// Random data over a tiny alphabet: matches exist at a great many distances
    /// and lengths, which produces deep binary trees with heavy re-linking.
    /// </summary>
    private static byte[] MakeSmallAlphabet(int length, int seed)
    {
        var rng = new Random(seed);
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)rng.Next(4);
        return data;
    }

    private static string? FindBogusMatch(IMatchFinder finder, byte[] data, int dictSize)
    {
        const int maxMatches = 64;
        int[] distances = new int[maxMatches];
        int[] lengths = new int[maxMatches];

        for (int pos = 0; pos < data.Length; pos++)
        {
            int n = finder.FindMatches(distances, lengths, maxMatches);
            for (int i = 0; i < n; i++)
            {
                int distance = distances[i] + 1;
                int len = lengths[i];

                if (distance < 1 || distance > dictSize)
                    return $"pos {pos}: distance {distance} outside 1..{dictSize}";
                if (distance > pos)
                    return $"pos {pos}: distance {distance} reaches before the start of the data";
                if (len < 2 || pos + len > data.Length)
                    return $"pos {pos}: length {len} out of range";

                int src = pos - distance;
                if (!data.AsSpan(src, len).SequenceEqual(data.AsSpan(pos, len)))
                    return $"pos {pos}: reported match (distance {distance}, length {len}) does not match the data";
            }
            finder.MovePos();
        }
        return null;
    }

    [Test]
    [Arguments(4096)]
    [Arguments(8192)]
    public async Task BinaryTree_PeriodicAtDictionaryDistance_ReportsOnlyGenuineMatches(int dictSize)
    {
        // A power-of-two dictionary makes the cyclic buffer exactly the window
        // size, which is the configuration where a candidate at distance ==
        // dictSize indexes the same son[] slot pair as the position being
        // inserted. Every preset dictionary is a power of two.
        byte[] data = MakePeriodicWithNoise(dictSize, dictSize * 24, seed: 1234);

        using var finder = new BinaryTreeMatchFinder(dictSize, 273, cutValue: 32);
        finder.SetInput(data);

        await Assert.That(FindBogusMatch(finder, data, dictSize)).IsNull();
    }

    [Test]
    public async Task BinaryTree_SmallAlphabet_ReportsOnlyGenuineMatches()
    {
        const int dictSize = 4096;
        byte[] data = MakeSmallAlphabet(dictSize * 24, seed: 99);

        using var finder = new BinaryTreeMatchFinder(dictSize, 273, cutValue: 64);
        finder.SetInput(data);

        await Assert.That(FindBogusMatch(finder, data, dictSize)).IsNull();
    }

    [Test]
    public async Task HashChain_PeriodicAtDictionaryDistance_ReportsOnlyGenuineMatches()
    {
        const int dictSize = 4096;
        byte[] data = MakePeriodicWithNoise(dictSize, dictSize * 16, seed: 77);

        using var finder = new HashChainMatchFinder(dictSize, 273, cutValue: 32);
        finder.SetInput(data);

        await Assert.That(FindBogusMatch(finder, data, dictSize)).IsNull();
    }

    /// <summary>
    /// The hash chain's window slide only runs from SetInput, so feeding it
    /// incrementally must keep the buffer near window + cyclic. Feeding a whole
    /// block up front instead leaves the buffer grown to the block size, which
    /// costs a second copy of every in-flight block.
    /// </summary>
    [Test]
    public async Task HashChain_FedIncrementally_KeepsBufferNearTheWindow()
    {
        const int dictSize = 1 << 16;   // 64 KB window
        const int chunk = 1 << 16;      // the LZMA2 encoder's chunk size
        byte[] data = MakeSmallAlphabet(4 << 20, seed: 8);  // 4 MB "block"

        using var finder = new HashChainMatchFinder(dictSize, 273, cutValue: 32);
        for (int pos = 0; pos < data.Length; pos += chunk)
        {
            int n = Math.Min(chunk, data.Length - pos);
            finder.SetInput(data.AsSpan(pos, n));
            finder.Skip(n);
        }

        // Steady state is window + cyclic + 64 KB + matchMaxLen + 4096; anything
        // near the 4 MB block size means the slide never ran.
        await Assert.That(finder.BufferLength).IsLessThan(1 << 20);
    }

    [Test]
    public async Task HashChainPresets_BlockMuchLargerThanDictionary_RoundTrip()
    {
        // Blocks far larger than the dictionary are where incremental feeding
        // matters; verify the encoder still produces decodable output there.
        byte[] original = MakeSmallAlphabet(4 << 20, seed: 12);
        var opts = new XzCompressOptions
        {
            Preset = 1,                  // hash chain
            DictionarySize = 1 << 16,    // 64 KB dictionary
            BlockSize = 4 << 20,         // 64x the dictionary
            Threads = 1,
        };

        byte[] compressed = XzCompressor.Compress(original, opts);
        await Assert.That(XzCompressor.Decompress(compressed).SequenceEqual(original)).IsTrue();
    }

    /// <summary>
    /// End-to-end counterpart: input several times larger than the dictionary at a
    /// BT4 preset, which is the regime every other round-trip test avoids by
    /// capping the dictionary at or above the data length.
    /// </summary>
    [Test]
    [Arguments(7)]
    [Arguments(9)]
    public async Task OptimalPresets_InputLargerThanDictionary_RoundTrips(int preset)
    {
        const int dictSize = 64 * 1024;
        byte[] original = MakePeriodicWithNoise(dictSize, dictSize * 24, seed: 2024);

        byte[] compressed = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = preset, DictionarySize = dictSize });
        byte[] decompressed = XzCompressor.Decompress(compressed);

        await Assert.That(decompressed.SequenceEqual(original)).IsTrue();
    }

    /// <summary>
    /// Reads the LZMA2 dictionary-size byte out of the first block header, which
    /// is how the effective dictionary shows up in the output.
    /// </summary>
    private static int EffectiveDictionarySize(byte[] xz)
    {
        // Stream header is 12 bytes; the block header follows. Its first byte is
        // the header size in 4-byte units, then flags, then the filter chain:
        // for a lone LZMA2 filter that is filter id 0x21, property size 1, and
        // the dictionary byte.
        int p = 12;
        int headerSize = (xz[p] + 1) * 4;
        var header = xz.AsSpan(p, headerSize);
        int i = 2;                       // skip header-size byte and flags
        while (header[i] != 0x21) i++;   // LZMA2 filter id
        i += 2;                          // filter id, property-size byte
        return LzmaNet.Lzma2.Lzma2Encoder.DecodeDictSize(header[i]);
    }

    [Test]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(9)]
    public async Task DefaultPresets_EffectiveDictionaryIsTheBlockLength(int preset)
    {
        // Presets 7-9 nominally mean 16/32/64 MB dictionaries. Encoding less than
        // that must not allocate for the nominal size — the effective dictionary
        // is the block length, which is why these presets can be tested at all
        // without the multi-hundred-MB table allocations they used to imply.
        const int blockSize = 256 * 1024;
        byte[] original = MakeSmallAlphabet(blockSize * 3, seed: 44);

        byte[] compressed = XzCompressor.Compress(original,
            new XzCompressOptions { Preset = preset, BlockSize = blockSize });

        await Assert.That(EffectiveDictionarySize(compressed)).IsLessThanOrEqualTo(blockSize);
        await Assert.That(XzCompressor.Decompress(compressed).SequenceEqual(original)).IsTrue();
    }

    /// <summary>
    /// Exercises the BT4 buffer slide and table rebase repeatedly: the input is
    /// many times the dictionary, so the finder must slide and rebase its hash
    /// and son tables dozens of times within a single block. A full 64 MB-
    /// dictionary run at this ratio would need several GB and does not belong in
    /// CI, but the slide arithmetic it would exercise is the same.
    /// </summary>
    [Test]
    public async Task OptimalPreset_ManyWindowSlides_RoundTrips()
    {
        const int dictSize = 1 << 18;   // 256 KB window
        byte[] original = MakeSmallAlphabet(dictSize * 40, seed: 61);  // 10 MB, 40x the window

        var opts = new XzCompressOptions
        {
            Preset = 7,
            DictionarySize = dictSize,
            BlockSize = dictSize * 40,   // one block, so the slide runs within it
            Threads = 1,
        };

        byte[] compressed = XzCompressor.Compress(original, opts);
        await Assert.That(XzCompressor.Decompress(compressed).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task OptimalPreset_StreamedInputLargerThanDictionary_RoundTrips()
    {
        // The streaming path applies no input-size dictionary cap, so this is the
        // configuration where blocks genuinely exceed the search window.
        const int dictSize = 64 * 1024;
        byte[] original = MakeSmallAlphabet(dictSize * 20, seed: 31);

        using var compressed = new MemoryStream();
        var opts = new XzCompressOptions
        {
            Preset = 9,
            DictionarySize = dictSize,
            BlockSize = dictSize * 2,
        };
        using (var enc = new XzCompressStream(compressed, opts, leaveOpen: true))
            enc.Write(original);

        compressed.Position = 0;
        using var output = new MemoryStream();
        using (var dec = new XzDecompressStream(compressed, leaveOpen: true))
            dec.CopyTo(output);

        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
    }
}
