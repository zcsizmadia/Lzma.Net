// SPDX-License-Identifier: 0BSD

using System.Buffers.Binary;
using System.Diagnostics;

using LzmaNet.Check;

using LzmaNet.Lzma;
using LzmaNet.Lzma2;
using LzmaNet.Xz;

namespace LzmaNet.Tests;

/// <summary>
/// Decoder strictness rules taken from XZ Utils 5.8.4: an LZMA2 chunk must be
/// consumed exactly, and a Number of Records value that cannot fit in the Index
/// is rejected before any Record is read.
/// </summary>
public class DecoderStrictnessTests
{
    // ── LZMA2: the chunk header's compressed size must match the coded data ──

    /// <summary>
    /// Encodes <paramref name="data"/> as a single-chunk LZMA2 stream and returns it
    /// together with the offset of the first chunk's 16-bit compressed-size field.
    /// </summary>
    private static (byte[] Stream, int CompSizeOffset) EncodeSingleChunk(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var encoder = new Lzma2Encoder(LzmaEncoderProperties.FromPreset(0)))
            encoder.Encode(data.AsMemory(), buffer);

        byte[] lzma2 = buffer.ToArray();

        // Control 0xE0..0xFF = LZMA chunk with dictionary reset and new properties:
        // control(1) + uncompressed size(2) + compressed size(2) + properties(1).
        if (lzma2[0] < 0xE0)
            throw new InvalidOperationException("Expected a first chunk with new properties.");

        return (lzma2, 3);
    }

    [Test]
    public async Task Lzma2Decoder_CompressedSizeLargerThanCodedData_Throws()
    {
        byte[] data = new byte[4096];
        new Random(1234).NextBytes(data);
        for (int i = 1024; i < data.Length; i++)
            data[i] = data[i - 1024]; // make it compressible enough to stay in one chunk

        var (lzma2, compSizeOffset) = EncodeSingleChunk(data);

        // Sanity: the untouched stream decodes.
        using (var decoder = new Lzma2Decoder(1 << 20))
        {
            byte[] output = new byte[data.Length];
            int written = decoder.Decode(lzma2.AsMemory(), output);
            await Assert.That(written).IsEqualTo(data.Length);
            await Assert.That(output.SequenceEqual(data)).IsTrue();
        }

        // Claim one more compressed byte than the range coder actually needs and
        // insert a filler byte so the chunk really is that long. liblzma rejects
        // this because the chunk's compressed size must reach zero exactly.
        int storedSize = BinaryPrimitives.ReadUInt16BigEndian(lzma2.AsSpan(compSizeOffset, 2));
        int chunkEnd = compSizeOffset + 3 + storedSize + 1; // +3: size field + props byte

        byte[] corrupt = new byte[lzma2.Length + 1];
        lzma2.AsSpan(0, chunkEnd).CopyTo(corrupt);
        corrupt[chunkEnd] = 0xFF; // the surplus byte inside the chunk
        lzma2.AsSpan(chunkEnd).CopyTo(corrupt.AsSpan(chunkEnd + 1));
        BinaryPrimitives.WriteUInt16BigEndian(corrupt.AsSpan(compSizeOffset, 2), (ushort)(storedSize + 1));

        using (var decoder = new Lzma2Decoder(1 << 20))
        {
            byte[] output = new byte[data.Length];
            await Assert.That(() => decoder.Decode(corrupt.AsMemory(), output))
                .ThrowsExactly<LzmaDataErrorException>();
        }
    }

    [Test]
    public async Task Lzma2Decoder_CompressedSizeSmallerThanCodedData_Throws()
    {
        byte[] data = new byte[4096];
        new Random(4321).NextBytes(data);
        for (int i = 512; i < data.Length; i++)
            data[i] = data[i - 512];

        var (lzma2, compSizeOffset) = EncodeSingleChunk(data);

        // Drop the last coded byte from the chunk: the range coder then runs out
        // of input before the chunk's uncompressed size is reached.
        int storedSize = BinaryPrimitives.ReadUInt16BigEndian(lzma2.AsSpan(compSizeOffset, 2));
        BinaryPrimitives.WriteUInt16BigEndian(lzma2.AsSpan(compSizeOffset, 2), (ushort)(storedSize - 1));

        using var decoder = new Lzma2Decoder(1 << 20);
        byte[] output = new byte[data.Length];
        await Assert.That(() => decoder.Decode(lzma2.AsMemory(), output))
            .ThrowsExactly<LzmaDataErrorException>();
    }

    // ── XZ Index: Number of Records is bounded by the Backward Size ──

    [Test]
    public async Task SeekableStream_BogusNumberOfRecords_RejectedWithoutReadingRecords()
    {
        byte[] compressed = XzCompressor.Compress("index record count guard"u8);

        // Footer: 4 CRC32 + 4 Backward Size + 2 flags + 2 magic.
        int footerPos = compressed.Length - XzConstants.StreamFooterSize;
        long indexSize = ((long)BinaryPrimitives.ReadUInt32LittleEndian(
            compressed.AsSpan(footerPos + 4, 4)) + 1) * 4;
        long indexPos = footerPos - indexSize;

        // One-record Index: indicator(1) + count(1) + record + padding + CRC32.
        await Assert.That(compressed[indexPos]).IsEqualTo((byte)0x00);
        await Assert.That(compressed[indexPos + 1]).IsEqualTo((byte)0x01);

        // 127 Records cannot fit in an Index this small (two bytes per Record
        // minimum), so the count must be rejected before any Record is read.
        byte[] corrupt = (byte[])compressed.Clone();
        corrupt[indexPos + 1] = 0x7F;

        // Watch the Record area: everything after the indicator and count bytes,
        // up to the footer.
        var tracker = new WindowedReadTrackingStream(
            new MemoryStream(corrupt), indexPos + 2, footerPos);
        await Assert.That(() => new XzSeekableStream(tracker))
            .ThrowsExactly<LzmaDataErrorException>();

        // The count is rejected up front, so not a single Record byte is read.
        await Assert.That(tracker.BytesReadInWindow).IsEqualTo(0L);
    }

    /// <summary>
    /// Seekable read-only wrapper that counts the bytes read from a given offset range.
    /// </summary>
    private sealed class WindowedReadTrackingStream(Stream inner, long windowStart, long windowEnd)
        : Stream
    {
        public long BytesReadInWindow { get; private set; }

        private void Track(long start, int count)
        {
            long overlap = Math.Min(start + count, windowEnd) - Math.Max(start, windowStart);
            if (overlap > 0)
                BytesReadInWindow += overlap;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long start = inner.Position;
            int read = inner.Read(buffer);
            Track(start, read);
            return read;
        }

        public override int ReadByte()
        {
            long start = inner.Position;
            int value = inner.ReadByte();
            if (value >= 0)
                Track(start, 1);
            return value;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Checks that LzmaNet and the reference xz tool agree on rejecting a .xz file
/// whose LZMA2 chunk header overstates the compressed size.
/// </summary>
[RequiresXz]
public class LzmaChunkSizeInteropTests
{
    [Test]
    public async Task OverstatedChunkCompressedSize_RejectedByBothXzAndLzmaNet()
    {
        byte[] data = new byte[2000];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 7);

        var (good, goodExit) = await RunXzAsync("--compress --stdout --force -0", data);
        await Assert.That(goodExit).IsEqualTo(0);
        await Assert.That(XzCompressor.Decompress(good).SequenceEqual(data)).IsTrue();

        byte[] corrupt = OverstateFirstChunkCompressedSize(good);

        // xz produced the original file, so it is the authority on the corrupt one.
        var (_, corruptExit) = await RunXzAsync("--test --stdout", corrupt);
        await Assert.That(corruptExit).IsNotEqualTo(0);

        await Assert.That(() => XzCompressor.Decompress(corrupt))
            .ThrowsExactly<LzmaDataErrorException>();
    }

    /// <summary>
    /// Adds four bytes to the first LZMA2 chunk and to its declared compressed size.
    /// Every other size in the file is corrected to match, so the only thing wrong with
    /// the result is that the chunk header claims more compressed bytes than the range
    /// coder consumes. Four keeps all 4-byte alignment intact, so the Block padding,
    /// the Index size and the Stream Footer stay as they are.
    /// </summary>
    private static byte[] OverstateFirstChunkCompressedSize(byte[] xzFile)
    {
        const int Surplus = 4;

        int blockHeaderSize = (xzFile[XzConstants.StreamHeaderSize] + 1) * 4;
        int payloadStart = XzConstants.StreamHeaderSize + blockHeaderSize;
        if (xzFile[payloadStart] < 0xE0)
            throw new InvalidOperationException("Expected an LZMA2 chunk with a dictionary reset.");

        int compSizeOffset = payloadStart + 3;
        int storedCompSize = BinaryPrimitives.ReadUInt16BigEndian(xzFile.AsSpan(compSizeOffset, 2));
        int chunkEnd = payloadStart + 6 + storedCompSize + 1; // control + sizes + props + data

        byte[] corrupt = new byte[xzFile.Length + Surplus];
        xzFile.AsSpan(0, chunkEnd).CopyTo(corrupt);
        corrupt.AsSpan(chunkEnd, Surplus).Fill(0xFF);
        xzFile.AsSpan(chunkEnd).CopyTo(corrupt.AsSpan(chunkEnd + Surplus));
        BinaryPrimitives.WriteUInt16BigEndian(
            corrupt.AsSpan(compSizeOffset, 2), (ushort)(storedCompSize + Surplus));

        // The Block grew by four bytes, so the Block header's Compressed Size says so
        // too. Its CRC32 covers everything in the header before the CRC itself.
        int blockHeaderPos = XzConstants.StreamHeaderSize;
        if ((corrupt[blockHeaderPos + 1] & 0x40) == 0)
            throw new InvalidOperationException("Expected a Compressed Size in the Block header.");

        int blockCompPos = blockHeaderPos + 2;
        int cursor = blockCompPos;
        ulong blockCompSize = ReadVli(corrupt, ref cursor);
        if (VliLength(blockCompSize + Surplus) != cursor - blockCompPos)
            throw new InvalidOperationException("Compressed Size no longer fits its original encoding.");
        WriteVli(corrupt.AsSpan(blockCompPos), blockCompSize + Surplus);
        Crc32.WriteLE(corrupt.AsSpan(blockHeaderPos, blockHeaderSize - 4),
                      corrupt.AsSpan(blockHeaderPos + blockHeaderSize - 4, 4));

        // The Index record's Unpadded Size covers the same four bytes.
        int footerPos = corrupt.Length - XzConstants.StreamFooterSize;
        int indexSize = (int)(((long)BinaryPrimitives.ReadUInt32LittleEndian(
            corrupt.AsSpan(footerPos + 4, 4)) + 1) * 4);
        int indexPos = footerPos - indexSize;

        int pos = indexPos + 1; // skip the Index Indicator
        if (ReadVli(corrupt, ref pos) != 1)
            throw new InvalidOperationException("Expected a single-Block Index.");

        int unpaddedPos = pos;
        ulong unpadded = ReadVli(corrupt, ref pos);
        int unpaddedLength = pos - unpaddedPos;
        if (VliLength(unpadded + Surplus) != unpaddedLength)
            throw new InvalidOperationException("Unpadded Size no longer fits its original encoding.");
        WriteVli(corrupt.AsSpan(unpaddedPos), unpadded + Surplus);

        Crc32.WriteLE(corrupt.AsSpan(indexPos, indexSize - 4), corrupt.AsSpan(indexSize + indexPos - 4, 4));
        return corrupt;
    }

    private static ulong ReadVli(byte[] buffer, ref int pos)
    {
        ulong value = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = buffer[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
        }
    }

    private static void WriteVli(Span<byte> buffer, ulong value)
    {
        int i = 0;
        while (value >= 0x80)
        {
            buffer[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        buffer[i] = (byte)value;
    }

    private static int VliLength(ulong value)
    {
        int length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }
        return length;
    }

    private static async Task<(byte[] Output, int ExitCode)> RunXzAsync(string arguments, byte[] stdin)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "xz",
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        proc.Start();

        var writeTask = Task.Run(async () =>
        {
            try
            {
                await proc.StandardInput.BaseStream.WriteAsync(stdin);
            }
            catch (IOException)
            {
                // xz may reject the input and exit before reading all of it.
            }
            proc.StandardInput.Close();
        });

        using var output = new MemoryStream();
        var stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(output);
        var stderrTask = proc.StandardError.ReadToEndAsync();

        await Task.WhenAll(writeTask, stdoutTask, stderrTask);
        await proc.WaitForExitAsync();

        return (output.ToArray(), proc.ExitCode);
    }
}
