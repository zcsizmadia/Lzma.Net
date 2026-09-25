// SPDX-License-Identifier: 0BSD

#if !NET8_0_OR_GREATER

using System.Runtime.CompilerServices;

namespace LzmaNet.Compatibility;

/// <summary>
/// Stand-in for <c>System.Runtime.InteropServices.MemoryMarshal</c> on targets
/// that lack <c>GetArrayDataReference</c>. Files that need it alias this type
/// for the portable build only, so the code in their hot paths stays identical
/// across every target.
/// </summary>
/// <remarks>
/// <c>GetArrayDataReference</c> here goes through the array's span, which costs
/// a null check and a length read that the framework method skips. The callers
/// hold the reference across a whole decode loop rather than re-taking it per
/// symbol, so this is paid once.
/// </remarks>
internal static class MemoryMarshal
{
    /// <inheritdoc cref="System.Runtime.InteropServices.MemoryMarshal.GetReference{T}(ReadOnlySpan{T})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T GetReference<T>(ReadOnlySpan<T> span)
        => ref System.Runtime.InteropServices.MemoryMarshal.GetReference(span);

    /// <inheritdoc cref="System.Runtime.InteropServices.MemoryMarshal.GetReference{T}(Span{T})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T GetReference<T>(Span<T> span)
        => ref System.Runtime.InteropServices.MemoryMarshal.GetReference(span);

    /// <summary>
    /// Reference to the first element of <paramref name="array"/>, without the
    /// per-access bounds check of the indexer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T GetArrayDataReference<T>(T[] array)
        => ref System.Runtime.InteropServices.MemoryMarshal.GetReference(array.AsSpan());
}

#endif
