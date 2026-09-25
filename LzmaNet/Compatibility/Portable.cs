// SPDX-License-Identifier: 0BSD

using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace LzmaNet.Compatibility;

/// <summary>
/// Framework APIs the library uses that arrived after netstandard2.1, routed
/// through one place so the call sites read the same on every target.
/// </summary>
/// <remarks>
/// On .NET 8 and later every member here is a direct forward to the framework
/// method — the same overload the code called before this type existed, chosen
/// per target rather than lowered to a portable one. All are marked
/// <see cref="MethodImplOptions.AggressiveInlining"/> and are internal to this
/// assembly, so the JIT inlines them away and the modern targets keep the exact
/// code they had.
/// </remarks>
internal static class Portable
{
    /// <summary>
    /// Largest number of elements an array can hold. Matches the runtime's own
    /// limit, which is a little under <see cref="int.MaxValue"/>.
    /// </summary>
    public static int ArrayMaxLength
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get =>
#if NET8_0_OR_GREATER
            Array.MaxLength;
#else
            0x7FFFFFC7;
#endif
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> naming
    /// <paramref name="instance"/> when <paramref name="disposed"/> is true.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowIfDisposed(bool disposed, object instance)
    {
#if NET8_0_OR_GREATER
        ObjectDisposedException.ThrowIf(disposed, instance);
#else
        if (disposed)
            ThrowDisposed(instance);
#endif
    }

#if !NET8_0_OR_GREATER
    // Kept out of line so the check above stays small enough to inline.
    private static void ThrowDisposed(object instance)
        => throw new ObjectDisposedException(instance.GetType().FullName);
#endif

    /// <summary>
    /// Clears the whole array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clear<T>(T[] array)
    {
#if NET8_0_OR_GREATER
        Array.Clear(array);
#else
        Array.Clear(array, 0, array.Length);
#endif
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a declared member of its enum type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDefined<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
#if NET8_0_OR_GREATER
        return Enum.IsDefined(value);
#else
        return Enum.IsDefined(typeof(TEnum), value);
#endif
    }

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="count"/> elements of
    /// <paramref name="array"/> starting at <paramref name="startIndex"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fill<T>(T[] array, T value, int startIndex, int count)
    {
#if NETSTANDARD2_0
        // Span.Fill is vectorized by System.Memory, so this stays close to the
        // framework method it stands in for.
        array.AsSpan(startIndex, count).Fill(value);
#else
        Array.Fill(array, value, startIndex, count);
#endif
    }

    /// <summary>
    /// Assigns <paramref name="value"/> to every element of <paramref name="array"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fill<T>(T[] array, T value)
    {
#if NETSTANDARD2_0
        array.AsSpan().Fill(value);
#else
        Array.Fill(array, value);
#endif
    }

    /// <summary>
    /// Clamps <paramref name="value"/> to the inclusive range
    /// <paramref name="min"/>..<paramref name="max"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clamp(int value, int min, int max)
    {
#if NETSTANDARD2_0
        return value < min ? min : value > max ? max : value;
#else
        return Math.Clamp(value, min, max);
#endif
    }

    /// <inheritdoc cref="Clamp(int, int, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Clamp(long value, long min, long max)
    {
#if NETSTANDARD2_0
        return value < min ? min : value > max ? max : value;
#else
        return Math.Clamp(value, min, max);
#endif
    }

    /// <summary>
    /// Writes the SHA-256 digest of <paramref name="source"/> into
    /// <paramref name="destination"/>, which must be 32 bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Sha256(ReadOnlySpan<byte> source, Span<byte> destination)
    {
#if NET8_0_OR_GREATER
        SHA256.HashData(source, destination);
#elif NETSTANDARD2_1_OR_GREATER
        using var sha = SHA256.Create();
        if (!sha.TryComputeHash(source, destination, out int written) || written != 32)
            throw new InvalidOperationException("SHA-256 digest could not be computed.");
#else
        // netstandard2.0's HashAlgorithm is byte[]-only.
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(source.ToArray());
        hash.AsSpan().CopyTo(destination);
#endif
    }
}
