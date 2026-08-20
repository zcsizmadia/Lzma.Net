// SPDX-License-Identifier: 0BSD

namespace LzmaNet.LZ;

/// <summary>
/// Match finder abstraction: hash-chain (fast) or binary-tree (better matches).
/// Positions advance via <see cref="MovePos"/>/<see cref="Skip"/>; every position
/// must be inserted into the finder's data structures exactly once, which
/// <see cref="FindMatches"/> also does for the current position.
/// </summary>
internal interface IMatchFinder : IDisposable
{
    /// <summary>Appends data to the search window without consuming it.</summary>
    void SetInput(ReadOnlySpan<byte> data);

    /// <summary>Resets positions and clears all tables (new independent unit).</summary>
    void Reset();

    /// <summary>Bytes appended but not yet consumed.</summary>
    int Available { get; }

    /// <summary>
    /// Finds matches at the current position (also inserts it). Returns matches
    /// with strictly increasing lengths; the distance for each reported length is
    /// the nearest one found. Does not advance the position.
    /// </summary>
    int FindMatches(Span<int> distances, Span<int> lengths, int maxMatches);

    /// <summary>Advances one position, inserting it if <see cref="FindMatches"/> has not already.</summary>
    void MovePos();

    /// <summary>Advances <paramref name="count"/> positions.</summary>
    void Skip(int count);
}
