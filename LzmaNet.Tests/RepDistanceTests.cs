// SPDX-License-Identifier: 0BSD

using LzmaNet.Lzma;

namespace LzmaNet.Tests;

/// <summary>
/// The encoder applies the LZMA rep-distance transitions in two places —
/// emission, and the optimal parser's dynamic program predicting them for a
/// candidate path — and the decoder applies them again when reading the symbol
/// back. These pin the shared definition against the decoder's behavior, which
/// is the authority: if the encoder's rotation ever drifted from it, the parser
/// would price one distance while the encoder emitted another and the output
/// would decode to different bytes.
/// </summary>
public class RepDistanceTests
{
    /// <summary>
    /// The rep shuffle exactly as LzmaDecoder performs it while decoding a rep
    /// match (LzmaDecoder.cs, the isRepG0/G1/G2 branch), written out
    /// independently so the assertions below are a real cross-check rather than
    /// a restatement of the encoder's own code.
    /// </summary>
    private static (int, int, int, int) DecoderRepMatch(
        int rep0, int rep1, int rep2, int rep3, int index)
    {
        if (index == 0)
            return (rep0, rep1, rep2, rep3);

        int dist;
        if (index == 1)
        {
            dist = rep1;
        }
        else
        {
            if (index == 2)
            {
                dist = rep2;
            }
            else
            {
                dist = rep3;
                rep3 = rep2;
            }
            rep2 = rep1;
        }
        rep1 = rep0;
        rep0 = dist;
        return (rep0, rep1, rep2, rep3);
    }

    /// <summary>The decoder's normal-match shuffle.</summary>
    private static (int, int, int, int) DecoderMatch(
        int rep0, int rep1, int rep2, int _, int dist)
        => (dist, rep0, rep1, rep2);

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AfterRepMatch_MatchesTheDecoder(int index)
    {
        var reps = new RepDistances(11, 22, 33, 44);
        var actual = reps.AfterRepMatch(index);
        var (e0, e1, e2, e3) = DecoderRepMatch(11, 22, 33, 44, index);

        await Assert.That((actual.Rep0, actual.Rep1, actual.Rep2, actual.Rep3))
            .IsEqualTo((e0, e1, e2, e3));
    }

    [Test]
    public async Task AfterMatch_MatchesTheDecoder()
    {
        var reps = new RepDistances(11, 22, 33, 44);
        var actual = reps.AfterMatch(99);
        var (e0, e1, e2, e3) = DecoderMatch(11, 22, 33, 44, 99);

        await Assert.That((actual.Rep0, actual.Rep1, actual.Rep2, actual.Rep3))
            .IsEqualTo((e0, e1, e2, e3));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AfterRepMatch_PromotesTheChosenDistance(int index)
    {
        var reps = new RepDistances(11, 22, 33, 44);
        await Assert.That(reps.AfterRepMatch(index).Rep0).IsEqualTo(reps[index]);
    }

    [Test]
    public async Task AfterRepMatch_IsAPermutation()
    {
        // Nothing may be lost or duplicated: a rep match reorders the window,
        // unlike a normal match, which pushes a new distance in and drops the
        // oldest.
        var reps = new RepDistances(11, 22, 33, 44);
        for (int index = 0; index < 4; index++)
        {
            var r = reps.AfterRepMatch(index);
            int[] after = [r.Rep0, r.Rep1, r.Rep2, r.Rep3];
            Array.Sort(after);
            await Assert.That(after).IsEquivalentTo(new[] { 11, 22, 33, 44 });
        }
    }

    [Test]
    public async Task Indexer_ReadsInOrder()
    {
        var reps = new RepDistances(11, 22, 33, 44);
        await Assert.That((reps[0], reps[1], reps[2], reps[3])).IsEqualTo((11, 22, 33, 44));
    }
}
