// SPDX-License-Identifier: 0BSD

namespace LzmaNet.Tests;

/// <summary>
/// What <see cref="XzCompressStream"/> leaves behind when a write fails or is
/// cancelled: never a valid .xz that silently lacks data the caller handed over.
/// </summary>
public class CompressStreamFailureTests
{
    private static byte[] Compressible(int length, int seed)
    {
        var rng = new Random(seed);
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)('a' + rng.Next(8));
        return data;
    }

    private static byte[] DecodeAll(byte[] xz)
    {
        using var dec = new XzDecompressStream(new MemoryStream(xz));
        using var output = new MemoryStream();
        dec.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// The output must either fail to decode or decode to an exact prefix of the
    /// input — never to data with a gap.
    /// </summary>
    private static async Task AssertTruncatedOrExactPrefix(byte[] produced, byte[] input)
    {
        byte[] decoded;
        try
        {
            decoded = DecodeAll(produced);
        }
        catch (LzmaException)
        {
            return;
        }
        await Assert.That(input.AsSpan().StartsWith(decoded)).IsTrue();
    }

    /// <summary>A sink whose Nth write call throws once (a transient I/O error).</summary>
    private sealed class FailOnceStream(int failingWrite) : MemoryStream
    {
        private int _writes;

        private void Count()
        {
            if (++_writes == failingWrite)
                throw new IOException("transient write failure");
        }

        public override void Write(byte[] buffer, int offset, int count) { Count(); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Count(); base.Write(buffer.ToArray(), 0, buffer.Length); }
        public override void WriteByte(byte value) { Count(); base.WriteByte(value); }
    }

    /// <summary>A sink that fails every write after the first <paramref name="okWrites"/>.</summary>
    private sealed class BrokenAfterStream(int okWrites) : MemoryStream
    {
        private int _writes;
        public bool Disposed { get; private set; }

        private void Count()
        {
            if (++_writes > okWrites)
                throw new IOException("device gone");
        }

        public override void Write(byte[] buffer, int offset, int count) { Count(); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Count(); base.Write(buffer.ToArray(), 0, buffer.Length); }
        public override void WriteByte(byte value) { Count(); base.WriteByte(value); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    /// <summary>Cancels <paramref name="cts"/> during its Nth asynchronous write, then honours it.</summary>
    private sealed class CancelOnWriteStream(CancellationTokenSource cts, int cancellingWrite) : MemoryStream
    {
        private int _writes;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_writes == cancellingWrite)
                cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return default;
        }

        // The netstandard2.0 asset writes through this overload.
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Test]
    public async Task MultiThreaded_TransientWriteFailure_DisposeDoesNotSealAStreamWithAGap()
    {
        const int block = 64 * 1024;
        byte[] data = Compressible(4 * block, 1);
        var opts = new XzCompressOptions { Preset = 0, Threads = 2, BlockSize = block };

        // Write #1 is the stream header; write #2 is the first encoded block.
        var sink = new FailOnceStream(failingWrite: 2);
        var xz = new XzCompressStream(sink, opts, leaveOpen: true);
        await Assert.That(() => xz.Write(data)).Throws<IOException>();
        xz.Dispose(); // what a `using` block does after the exception

        // Used to decode cleanly to blocks 2-4: the first 64 KiB silently missing.
        await AssertTruncatedOrExactPrefix(sink.ToArray(), data);
        await Assert.That(() => xz.Write(data)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task MultiThreaded_CancelledWhileWritingABlock_DisposeDoesNotSealAStreamWithAGap()
    {
        const int block = 64 * 1024;
        byte[] data = Compressible(4 * block, 2);
        var opts = new XzCompressOptions { Preset = 0, Threads = 2, BlockSize = block };
        using var cts = new CancellationTokenSource();
        // The user cancels while the first block is being written out.
        var sink = new CancelOnWriteStream(cts, cancellingWrite: 2);

        var xz = new XzCompressStream(sink, opts, leaveOpen: true);
        await xz.WriteAsync(data.AsMemory(0, 2 * block), cts.Token);  // blocks 1 and 2 in flight
        await Assert.That(async () => await xz.WriteAsync(data.AsMemory(2 * block, block), cts.Token))
            .Throws<OperationCanceledException>();
        await xz.DisposeAsync(); // `await using` after cancellation

        // Used to decode cleanly to blocks 2 and 3: the first 64 KiB silently missing.
        await AssertTruncatedOrExactPrefix(sink.ToArray(), data);
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    public async Task WriteAfterAFailedWrite_RethrowsTheFailure(int threads)
    {
        var sink = new FailOnceStream(failingWrite: 1); // the stream header
        var xz = new XzCompressStream(sink, new XzCompressOptions { Preset = 0, Threads = threads }, leaveOpen: true);
        await Assert.That(() => xz.Write(new byte[10])).Throws<IOException>();
        // The original error again, so a wrapper that writes on Dispose (StreamWriter) surfaces it.
        await Assert.That(() => xz.Write(new byte[10])).Throws<IOException>().WithMessage("transient write failure");
        xz.Dispose();
        await Assert.That(sink.Length).IsEqualTo(0); // nothing sealed
    }

    [Test]
    public async Task Dispose_WhenFinalizationFails_StillDisposesTheBaseStream()
    {
        var sink = new BrokenAfterStream(okWrites: 1); // header written, then the device is gone
        var xz = new XzCompressStream(sink, new XzCompressOptions { Preset = 0 }, leaveOpen: false);
        xz.Write(new byte[1000]); // buffered: below one block

        await Assert.That(() => xz.Dispose()).Throws<IOException>();
        // Otherwise a FileStream's handle stays open and the caller cannot delete the partial file.
        await Assert.That(sink.Disposed).IsTrue();
        xz.Dispose(); // a second Dispose is a no-op
    }

    [Test]
    public async Task DisposeAsync_WhenFinalizationFails_StillDisposesTheBaseStream()
    {
        var sink = new BrokenAfterStream(okWrites: 1);
        var xz = new XzCompressStream(sink, new XzCompressOptions { Preset = 0 }, leaveOpen: false);
        await xz.WriteAsync(new byte[1000]);

        await Assert.That(async () => await xz.DisposeAsync()).Throws<IOException>();
        await Assert.That(sink.Disposed).IsTrue();
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    public async Task CancellationRequestedBeforeAnyWork_DoesNotSpoilTheOutput(int threads)
    {
        // A token already cancelled when the call starts loses nothing, so the
        // stream must still finish into a valid .xz.
        byte[] data = Compressible(100_000, 9);
        var sink = new MemoryStream();
        var xz = new XzCompressStream(sink, new XzCompressOptions { Preset = 1, Threads = threads }, leaveOpen: true);
        await xz.WriteAsync(data);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(async () => await xz.WriteAsync(data, cancelled.Token)).Throws<OperationCanceledException>();
        await xz.DisposeAsync();
        await Assert.That(DecodeAll(sink.ToArray()).AsSpan().SequenceEqual(data)).IsTrue();
    }
}
