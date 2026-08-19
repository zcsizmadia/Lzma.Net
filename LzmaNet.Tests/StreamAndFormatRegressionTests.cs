// SPDX-License-Identifier: 0BSD

using System.Buffers.Binary;

using LzmaNet.Check;
using LzmaNet.Filters;
using LzmaNet.Lzma;
using LzmaNet.Lzma2;
using LzmaNet.Xz;

namespace LzmaNet.Tests;

public class StreamAndFormatRegressionTests
{
    [Test]
    public async Task CompressStream_SupportsNonSeekableOutput()
    {
        byte[] original = new byte[128 * 1024];
        new Random(42).NextBytes(original);
        using var storage = new MemoryStream();
        using (var destination = new NonSeekableStream(storage, canRead: false, canWrite: true))
        using (var xz = new XzCompressStream(destination, leaveOpen: true))
        {
            xz.Write(original);
        }

        byte[] result = XzCompressor.Decompress(storage.ToArray());
        await Assert.That(result.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task CompressStreamAsync_SupportsNonSeekableOutput()
    {
        byte[] original = "async non-seekable XZ output"u8.ToArray();
        using var storage = new MemoryStream();
        await using (var destination = new NonSeekableStream(
            storage, canRead: false, canWrite: true))
        await using (var xz = new XzCompressStream(destination, leaveOpen: true))
        {
            await xz.WriteAsync(original);
        }

        byte[] result = XzCompressor.Decompress(storage.ToArray());
        await Assert.That(result.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task DecompressStream_SupportsNonSeekableInput()
    {
        byte[] original = "non-seekable XZ input"u8.ToArray();
        using var storage = new MemoryStream(XzCompressor.Compress(original));
        using var source = new NonSeekableStream(storage, canRead: true, canWrite: false);
        using var xz = new XzDecompressStream(source);
        using var output = new MemoryStream();
        xz.CopyTo(output);

        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task UnsizedBlock_OnNonSeekableInput_StopsAtBlockBoundary()
    {
        byte[] original = "unsized block followed by more container data"u8.ToArray();
        byte[] block = BuildUnsizedBlock(original);
        using var storage = new MemoryStream();
        storage.Write(block);
        storage.WriteByte(0x7B);
        storage.Position = 0;

        using var source = new NonSeekableStream(storage, canRead: true, canWrite: false);
        using var output = new MemoryStream();
        bool result = XzBlock.ReadBlock(
            source, XzConstants.CheckNone, output, out _, out _);

        await Assert.That(result).IsTrue();
        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
        await Assert.That(source.ReadByte()).IsEqualTo(0x7B);
    }

    [Test]
    public async Task DecompressStream_ReadAsync_UsesUnderlyingAsyncIo()
    {
        byte[] original = new byte[32 * 1024];
        new Random(7).NextBytes(original);
        using var storage = new MemoryStream(XzCompressor.Compress(original));
        await using var source = new AsyncOnlyReadStream(storage);
        await using var xz = new XzDecompressStream(source);
        using var output = new MemoryStream();

        await xz.CopyToAsync(output);

        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task XzBlock_AppliesBcjStartOffsetFromProperties()
    {
        byte[] original = new byte[96];
        for (int i = 0; i + 5 <= original.Length; i += 8)
            original[i] = 0xE8;

        const uint startPosition = 0x1234;
        byte[] encoded = original.ToArray();
        var filter = new X86Filter();
        filter.Encode(encoded, startPosition);
        await Assert.That(encoded.SequenceEqual(original)).IsFalse();

        byte[] block = BuildBcjBlock(encoded, original.Length, startPosition);
        using var input = new MemoryStream(block);
        using var output = new MemoryStream();
        bool result = XzBlock.ReadBlock(
            input, XzConstants.CheckNone, output, out _, out _);

        await Assert.That(result).IsTrue();
        await Assert.That(output.ToArray().SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task DeltaFilter_RoundTripsRepresentativeData()
    {
        byte[] original = new byte[1024];
        new Random(11).NextBytes(original);
        byte[] filtered = original.ToArray();
        new DeltaFilter(17).Encode(filtered, 0);
        await Assert.That(filtered.SequenceEqual(original)).IsFalse();

        new DeltaFilter(17).Decode(filtered, 0);
        await Assert.That(filtered.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task XzBlock_OversizedCompressedSize_ThrowsDataErrorBeforeAllocation()
    {
        byte[] header = BuildSingleFilterHeader((ulong)int.MaxValue + 1);
        using var input = new MemoryStream(header);

        await Assert.That(() => XzBlock.ReadBlock(
            input, XzConstants.CheckNone, Stream.Null, out _, out _))
            .ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task XzIndex_NonCanonicalVli_ThrowsDataError()
    {
        using var input = new MemoryStream([0x80, 0x00]);
        await Assert.That(() => XzIndex.ReadIndex(input, out _))
            .ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task Lzma2Decoder_MissingEndMarker_ThrowsDataError()
    {
        using var decoder = new Lzma2Decoder(4096);
        byte[] truncated = [0x01, 0x00, 0x00, 0x41];
        await Assert.That(() => decoder.Decode(truncated, new byte[1]))
            .ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task XzHeader_ReservedFlagBits_ThrowFormatError()
    {
        byte[] header = new byte[XzConstants.StreamHeaderSize];
        XzHeader.WriteStreamHeader(header, XzConstants.CheckCrc64);
        header[7] |= 0x10;
        Crc32.WriteLE(header.AsSpan(6, 2), header.AsSpan(8, 4));

        await Assert.That(() => XzHeader.ReadStreamHeader(header))
            .ThrowsExactly<LzmaFormatException>();
    }

    [Test]
    public async Task MaxCompressedSize_RejectsInvalidOrOverflowingInput()
    {
        await Assert.That(() => XzCompressor.MaxCompressedSize(-1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => XzCompressor.MaxCompressedSize(long.MaxValue))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    private static byte[] BuildUnsizedBlock(ReadOnlyMemory<byte> data)
    {
        var properties = LzmaEncoderProperties.FromPreset(0);
        using var encoder = new Lzma2Encoder(properties);
        using var compressed = new MemoryStream();
        encoder.Encode(data, compressed);

        using var block = new MemoryStream();
        block.Write(BuildBlockHeader(0, writer =>
        {
            WriteVli(writer, XzConstants.FilterIdLzma2);
            WriteVli(writer, 1);
            writer.WriteByte(encoder.DictionarySizeByte);
        }));
        block.Write(compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        int padding = (4 - ((int)compressed.Length & 3)) & 3;
        for (int i = 0; i < padding; i++)
            block.WriteByte(0);
        return block.ToArray();
    }

    private static byte[] BuildBcjBlock(
        ReadOnlyMemory<byte> filteredData, int uncompressedSize, uint startPosition)
    {
        var properties = LzmaEncoderProperties.FromPreset(0);
        using var encoder = new Lzma2Encoder(properties);
        using var compressed = new MemoryStream();
        encoder.Encode(filteredData, compressed);

        using var block = new MemoryStream();
        block.Write(BuildBlockHeader(0xC1, writer =>
        {
            WriteVli(writer, (ulong)compressed.Length);
            WriteVli(writer, (ulong)uncompressedSize);
            WriteVli(writer, XzConstants.FilterIdX86);
            WriteVli(writer, 4);
            Span<byte> start = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(start, startPosition);
            writer.Write(start);
            WriteVli(writer, XzConstants.FilterIdLzma2);
            WriteVli(writer, 1);
            writer.WriteByte(encoder.DictionarySizeByte);
        }));
        block.Write(compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        int padding = (4 - ((int)compressed.Length & 3)) & 3;
        for (int i = 0; i < padding; i++)
            block.WriteByte(0);
        return block.ToArray();
    }

    private static byte[] BuildSingleFilterHeader(ulong compressedSize)
    {
        return BuildBlockHeader(0x40, writer =>
        {
            WriteVli(writer, compressedSize);
            WriteVli(writer, XzConstants.FilterIdLzma2);
            WriteVli(writer, 1);
            writer.WriteByte(0);
        });
    }

    private static byte[] BuildBlockHeader(byte flags, Action<MemoryStream> writeFields)
    {
        using var fields = new MemoryStream();
        fields.WriteByte(0);
        fields.WriteByte(flags);
        writeFields(fields);

        int contentLength = (int)fields.Length;
        int totalSize = (contentLength + 7) & ~3;
        while (fields.Length < totalSize - 4)
            fields.WriteByte(0);

        byte[] header = new byte[totalSize];
        fields.GetBuffer().AsSpan(0, totalSize - 4).CopyTo(header);
        header[0] = (byte)(totalSize / 4 - 1);
        Crc32.WriteLE(header.AsSpan(0, totalSize - 4), header.AsSpan(totalSize - 4));
        return header;
    }

    private static void WriteVli(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    private sealed class NonSeekableStream(
        Stream inner, bool canRead, bool canWrite) : Stream
    {
        public override bool CanRead => canRead;
        public override bool CanSeek => false;
        public override bool CanWrite => canWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) =>
            canRead ? inner.Read(buffer, offset, count) : throw new NotSupportedException();
        public override int Read(Span<byte> buffer) =>
            canRead ? inner.Read(buffer) : throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            canRead ? inner.ReadAsync(buffer, cancellationToken) :
                ValueTask.FromException<int>(new NotSupportedException());
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!canWrite) throw new NotSupportedException();
            inner.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!canWrite) throw new NotSupportedException();
            inner.Write(buffer);
        }
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            canWrite ? inner.WriteAsync(buffer, cancellationToken) :
                ValueTask.FromException(new NotSupportedException());
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class AsyncOnlyReadStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous reads are not allowed.");
        public override int Read(Span<byte> buffer) =>
            throw new InvalidOperationException("Synchronous reads are not allowed.");
        public override int ReadByte() =>
            throw new InvalidOperationException("Synchronous reads are not allowed.");
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}