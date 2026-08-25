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
