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
    /// Writes the SHA-256 digest of <paramref name="source"/> into
    /// <paramref name="destination"/>, which must be 32 bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Sha256(ReadOnlySpan<byte> source, Span<byte> destination)
    {
#if NET8_0_OR_GREATER
        SHA256.HashData(source, destination);
#else
        using var sha = SHA256.Create();
        if (!sha.TryComputeHash(source, destination, out int written) || written != 32)
            throw new InvalidOperationException("SHA-256 digest could not be computed.");
#endif
    }
}
