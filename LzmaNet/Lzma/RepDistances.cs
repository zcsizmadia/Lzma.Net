// SPDX-License-Identifier: 0BSD

namespace LzmaNet.Lzma;

/// <summary>
/// The LZMA rep-distance window (rep0..rep3) and the two transitions the format
/// defines for it.
/// </summary>
/// <remarks>
/// <para>
/// The encoder applies these transitions in two independent places: emission
/// updates its own reps as symbols are written, while the optimal parser's
/// dynamic program predicts them for each candidate path so it can price the
/// next step. Both must agree with what the decoder does when it reads the
/// symbol back — if they ever diverged, the parser would price one distance and
/// the encoder emit another, producing output that decodes to different bytes.
/// Keeping the transitions here means there is one definition to get right
/// rather than four to keep in step.
/// </para>
/// <para>
/// The mirror image lives in <see cref="LzmaDecoder"/>, which performs the same
/// shuffles while decoding.
/// </para>
/// </remarks>
internal readonly record struct RepDistances(int Rep0, int Rep1, int Rep2, int Rep3)
{
    /// <summary>Distance at <paramref name="index"/> (0-3).</summary>
    public int this[int index] => index switch
    {
        0 => Rep0,
        1 => Rep1,
        2 => Rep2,
        _ => Rep3,
    };

    /// <summary>
    /// The window after a rep match reusing distance <paramref name="index"/>:
    /// the chosen distance moves to the front and the ones it passed shift back.
    /// Index 0 is already at the front, so it is unchanged.
    /// </summary>
    public RepDistances AfterRepMatch(int index) => index switch
    {
        0 => this,
        1 => new RepDistances(Rep1, Rep0, Rep2, Rep3),
        2 => new RepDistances(Rep2, Rep0, Rep1, Rep3),
        _ => new RepDistances(Rep3, Rep0, Rep1, Rep2),
    };

    /// <summary>
    /// The window after a normal match at <paramref name="distance"/>: it becomes
    /// rep0 and the rest shift back, dropping the oldest.
    /// </summary>
    public RepDistances AfterMatch(int distance) =>
        new RepDistances(distance, Rep0, Rep1, Rep2);
}
