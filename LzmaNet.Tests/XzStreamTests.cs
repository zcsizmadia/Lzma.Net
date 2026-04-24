// SPDX-License-Identifier: 0BSD

using LzmaNet.Xz;

namespace LzmaNet.Tests;

/// <summary>
/// Tests for XZ stream header, footer, and format validation.
/// </summary>
public class XzStreamTests
{
    [Test]
    public async Task StreamHeader_WriteAndRead()
    {
        Span<byte> header = stackalloc byte[12];
        XzHeader.WriteStreamHeader(header, XzConstants.CheckCrc64);

        int checkType = XzHeader.ReadStreamHeader(header);
        await Assert.That(checkType).IsEqualTo(XzConstants.CheckCrc64);
    }

    [Test]
    [Arguments(XzConstants.CheckNone)]
    [Arguments(XzConstants.CheckCrc32)]
    [Arguments(XzConstants.CheckCrc64)]
    [Arguments(XzConstants.CheckSha256)]
    public async Task StreamHeader_AllCheckTypes(int checkType)
    {
        Span<byte> header = stackalloc byte[12];
        XzHeader.WriteStreamHeader(header, checkType);

        int read = XzHeader.ReadStreamHeader(header);
        await Assert.That(read).IsEqualTo(checkType);
    }

    [Test]
    public async Task StreamHeader_InvalidMagic_Throws()
    {
        byte[] header = new byte[12];
        header[0] = 0xFF;

        await Assert.That(() =>
        {
            XzHeader.ReadStreamHeader(header);
        }).ThrowsExactly<LzmaFormatException>();
    }

    [Test]
    public async Task StreamHeader_CorruptCrc_Throws()
    {
        byte[] header = new byte[12];
        XzHeader.WriteStreamHeader(header, XzConstants.CheckCrc64);
        header[8] ^= 0xFF;

        await Assert.That(() =>
        {
            XzHeader.ReadStreamHeader(header);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task StreamHeader_TooShort_Throws()
    {
        byte[] header = new byte[6];
        await Assert.That(() =>
        {
            XzHeader.ReadStreamHeader(header);
        }).ThrowsExactly<LzmaFormatException>();
    }

    [Test]
    public async Task StreamFooter_WriteAndRead()
    {
        Span<byte> footer = stackalloc byte[12];
        long indexSize = 16;
        XzHeader.WriteStreamFooter(footer, XzConstants.CheckCrc64, indexSize);

        long readIndexSize = XzHeader.ReadStreamFooter(footer, XzConstants.CheckCrc64);
        await Assert.That(readIndexSize).IsEqualTo(indexSize);
    }

    [Test]
    public async Task StreamFooter_CheckTypeMismatch_Throws()
    {
        byte[] footer = new byte[12];
        XzHeader.WriteStreamFooter(footer, XzConstants.CheckCrc64, 16);

        await Assert.That(() =>
        {
            XzHeader.ReadStreamFooter(footer, XzConstants.CheckCrc32);
        }).ThrowsExactly<LzmaDataErrorException>();
    }

    [Test]
    public async Task StreamFooter_InvalidMagic_Throws()
    {
        byte[] footer = new byte[12];
        footer[10] = 0x00;

        await Assert.That(() =>
        {
            XzHeader.ReadStreamFooter(footer, XzConstants.CheckNone);
        }).ThrowsExactly<LzmaFormatException>();
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(1, 4)]
    [Arguments(4, 8)]
    [Arguments(10, 32)]
    public async Task CheckSize_KnownValues(int checkType, int expectedSize)
    {
        await Assert.That(XzConstants.GetCheckSize(checkType)).IsEqualTo(expectedSize);
    }

    [Test]
    public async Task CompressedData_StartsWithXzMagic()
    {
        byte[] data = "test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(data);

        await Assert.That(compressed.Length >= 6).IsTrue();
        await Assert.That(compressed[0]).IsEqualTo((byte)0xFD);
        await Assert.That(compressed[1]).IsEqualTo((byte)0x37);
        await Assert.That(compressed[2]).IsEqualTo((byte)0x7A);
        await Assert.That(compressed[3]).IsEqualTo((byte)0x58);
        await Assert.That(compressed[4]).IsEqualTo((byte)0x5A);
        await Assert.That(compressed[5]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task CompressedData_EndsWithXzFooterMagic()
    {
        byte[] data = "test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(data);

        await Assert.That(compressed.Length >= 2).IsTrue();
        await Assert.That(compressed[^2]).IsEqualTo((byte)0x59);
        await Assert.That(compressed[^1]).IsEqualTo((byte)0x5A);
    }

    [Test]
    public async Task Decompress_InvalidFormat_Throws()
    {
        byte[] garbage = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];
        await Assert.That(() =>
        {
            XzCompressor.Decompress(garbage);
        }).ThrowsExactly<LzmaFormatException>();
    }

    [Test]
    public async Task Decompress_TruncatedData_Throws()
    {
        byte[] data = "test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(data);

        byte[] truncated = compressed[..(compressed.Length / 2)];
        await Assert.That(() =>
        {
            XzCompressor.Decompress(truncated);
        }).Throws<LzmaException>();
    }

    [Test]
    public async Task Decompress_CorruptedData_Throws()
    {
        byte[] data = "test data for corruption check"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(data);

        if (compressed.Length > 20)
        {
            compressed[15] ^= 0xFF;
            await Assert.That(() =>
            {
                XzCompressor.Decompress(compressed);
            }).Throws<LzmaException>();
        }
    }

    [Test]
    public async Task XzDecompressStream_Properties()
    {
        using var ms = new MemoryStream(XzCompressor.Compress("x"u8.ToArray()));
        using var xz = new XzDecompressStream(ms);

        await Assert.That(xz.CanRead).IsTrue();
        await Assert.That(xz.CanWrite).IsFalse();
        await Assert.That(xz.CanSeek).IsFalse();
        await Assert.That(() => xz.Length).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => xz.Position).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => xz.Position = 0).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => xz.Seek(0, SeekOrigin.Begin)).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => xz.SetLength(0)).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => xz.Write(new byte[1], 0, 1)).ThrowsExactly<NotSupportedException>();
    }

    [Test]
    public async Task XzCompressStream_Properties()
    {
        using var ms = new MemoryStream();
        using var xz = new XzCompressStream(ms, leaveOpen: true);

        await Assert.That(xz.CanRead).IsFalse();
        await Assert.That(xz.CanWrite).IsTrue();
        await Assert.That(xz.CanSeek).IsFalse();
        await Assert.That(() => xz.Length).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => xz.Position).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => xz.Position = 0).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => xz.Read(new byte[1], 0, 1)).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => xz.Seek(0, SeekOrigin.Begin)).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => xz.SetLength(0)).ThrowsExactly<NotSupportedException>();
    }

    [Test]
    public async Task XzCompressStream_InvalidPreset_Throws()
    {
        using var ms = new MemoryStream();
        await Assert.That(() => new XzCompressStream(ms, new XzCompressOptions { Preset = 10 })).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => new XzCompressStream(ms, new XzCompressOptions { Preset = -1 })).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task XzCompressStream_NullStream_Throws()
    {
        await Assert.That(() => new XzCompressStream(null!)).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    public async Task XzDecompressStream_NullStream_Throws()
    {
        await Assert.That(() => new XzDecompressStream(null!)).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    public async Task MaxCompressedSize_ReturnsReasonableValue()
    {
        long maxSize = XzCompressor.MaxCompressedSize(1000);
        await Assert.That(maxSize).IsGreaterThan(1000L);
        await Assert.That(maxSize).IsLessThan(2000L);
    }

    [Test]
    public async Task XzDecompressStream_LeaveOpen_True()
    {
        using var ms = new MemoryStream(XzCompressor.Compress("test"u8.ToArray()));
        using (var xz = new XzDecompressStream(ms, leaveOpen: true))
        {
            using var result = new MemoryStream();
            xz.CopyTo(result);
        }
        await Assert.That(ms.CanRead).IsTrue();
    }
}
