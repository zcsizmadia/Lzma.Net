// SPDX-License-Identifier: 0BSD

#if NETSTANDARD2_0

using System.Buffers;

namespace LzmaNet.Compatibility;

/// <summary>
/// Span and memory overloads of <see cref="Stream"/> for netstandard2.0, which
/// only has the <c>byte[]</c> forms. The library calls these on streams the
/// caller supplies, so they have to work for any <see cref="Stream"/>, not just
/// this library's own.
/// </summary>
/// <remarks>
/// Each one rents a pooled array, because the underlying stream can only be
/// handed a <c>byte[]</c>. That copy is the cost of the old target and is one
/// reason the netstandard2.0 build is slower; on .NET 8+ these do not exist and
/// the framework's own span overloads are called directly.
///
/// Extension methods lose to instance methods in overload resolution, so even
/// if this file were compiled for a newer target the framework's methods would
/// still win — the <c>#if</c> makes that certain rather than merely true.
/// </remarks>
internal static class StreamCompat
{
    /// <summary>Reads into <paramref name="buffer"/>, returning the byte count.</summary>
    public static int Read(this Stream stream, Span<byte> buffer)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read = stream.Read(rented, 0, buffer.Length);
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Writes <paramref name="buffer"/> to the stream.</summary>
    public static void Write(this Stream stream, ReadOnlySpan<byte> buffer)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(rented);
            stream.Write(rented, 0, buffer.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Asynchronously reads into <paramref name="buffer"/>.</summary>
    public static async ValueTask<int> ReadAsync(this Stream stream, Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read = await stream.ReadAsync(rented, 0, buffer.Length, cancellationToken)
                .ConfigureAwait(false);
            rented.AsSpan(0, read).CopyTo(buffer.Span);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Copies this stream to <paramref name="destination"/>. netstandard2.0 has
    /// no overload taking just a token, so the framework default buffer size is
    /// passed explicitly.
    /// </summary>
    public static Task CopyToAsync(this Stream stream, Stream destination,
        CancellationToken cancellationToken)
        => stream.CopyToAsync(destination, 81920, cancellationToken);

    /// <summary>
    /// Disposes the stream. netstandard2.0's <see cref="Stream"/> has no
    /// asynchronous dispose, so this is the synchronous one wrapped in a
    /// completed task.
    /// </summary>
    public static ValueTask DisposeAsync(this Stream stream)
    {
        stream.Dispose();
        return default;
    }

    /// <summary>Asynchronously writes <paramref name="buffer"/> to the stream.</summary>
    public static async ValueTask WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.Span.CopyTo(rented);
            await stream.WriteAsync(rented, 0, buffer.Length, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

#endif
