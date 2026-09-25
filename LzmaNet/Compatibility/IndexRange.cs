// SPDX-License-Identifier: 0BSD

#if NETSTANDARD2_0

using System.Runtime.CompilerServices;

namespace System;

/// <summary>
/// Supports the <c>^n</c> index-from-end syntax. netstandard2.0 predates it, so
/// the library declares it for that target only; every other target uses the
/// framework's own type. Declared here rather than rewriting the range
/// expressions so the slicing code reads the same everywhere.
/// </summary>
internal readonly struct Index : IEquatable<Index>
{
    private readonly int _value;

    /// <summary>
    /// Creates an index. A <paramref name="fromEnd"/> index of <c>n</c> refers to
    /// the element <c>n</c> places before the end.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Index(int value, bool fromEnd = false)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Index must not be negative.");

        // Stored with the sign bit marking from-the-end, matching the framework.
        _value = fromEnd ? ~value : value;
    }

    /// <summary>An index pointing at the first element.</summary>
    public static Index Start => new(0);

    /// <summary>An index pointing one past the last element.</summary>
    public static Index End => new(0, fromEnd: true);

    /// <summary>The index value, without the from-the-end flag.</summary>
    public int Value => _value < 0 ? ~_value : _value;

    /// <summary>Whether the index counts back from the end.</summary>
    public bool IsFromEnd => _value < 0;

    /// <summary>
    /// Resolves this index against a collection of <paramref name="length"/>
    /// elements. Not range-checked — the caller's <c>Slice</c> does that.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetOffset(int length) => _value < 0 ? length + _value + 1 : _value;

    /// <summary>Creates an index counting forward from the start.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Index FromStart(int value) => new(value);

    /// <summary>Creates an index counting back from the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Index FromEnd(int value) => new(value, fromEnd: true);

    /// <summary>Converts an integer to a from-the-start index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Index(int value) => new(value);

    /// <inheritdoc/>
    public bool Equals(Index other) => _value == other._value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Index other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value;

    /// <inheritdoc/>
    public override string ToString() => IsFromEnd ? "^" + Value : Value.ToString();
}

/// <summary>
/// Supports the <c>a..b</c> range syntax, for the same reason as
/// <see cref="Index"/>.
/// </summary>
internal readonly struct Range : IEquatable<Range>
{
    /// <summary>Inclusive start of the range.</summary>
    public Index Start { get; }

    /// <summary>Exclusive end of the range.</summary>
    public Index End { get; }

    /// <summary>Creates a range between two indices.</summary>
    public Range(Index start, Index end)
    {
        Start = start;
        End = end;
    }

    /// <summary>A range covering everything.</summary>
    public static Range All => new(Index.Start, Index.End);

    /// <summary>A range from <paramref name="start"/> to the end.</summary>
    public static Range StartAt(Index start) => new(start, Index.End);

    /// <summary>A range from the beginning up to <paramref name="end"/>.</summary>
    public static Range EndAt(Index end) => new(Index.Start, end);

    /// <summary>
    /// Resolves this range against a collection of <paramref name="length"/>
    /// elements, throwing when it does not fit.
    /// </summary>
    public (int Offset, int Length) GetOffsetAndLength(int length)
    {
        int start = Start.GetOffset(length);
        int end = End.GetOffset(length);

        if ((uint)end > (uint)length || (uint)start > (uint)end)
            throw new ArgumentOutOfRangeException(nameof(length));

        return (start, end - start);
    }

    /// <inheritdoc/>
    public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Range other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (Start.GetHashCode() * 31) + End.GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => Start + ".." + End;
}

#endif
