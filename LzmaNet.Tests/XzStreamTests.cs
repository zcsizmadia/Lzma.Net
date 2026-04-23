// SPDX-License-Identifier: 0BSD

using LzmaNet.Xz;

namespace LzmaNet.Tests;

/// <summary>
/// Tests for XZ stream header, footer, and format validation.
/// </summary>
public class XzStreamTests
{
    [Fact]
    public void StreamHeader_WriteAndRead()
    {
        Span<byte> header = stackalloc byte[12];
        XzHeader.WriteStreamHeader(header, XzConstants.CheckCrc64);

        int checkType = XzHeader.ReadStreamHeader(header);
        Assert.Equal(XzConstants.CheckCrc64, checkType);
    }

    [Theory]
    [InlineData(XzConstants.CheckNone)]
    [InlineData(XzConstants.CheckCrc32)]
    [InlineData(XzConstants.CheckCrc64)]
    [InlineData(XzConstants.CheckSha256)]
    public void StreamHeader_AllCheckTypes(int checkType)
    {
        Span<byte> header = stackalloc byte[12];
        XzHeader.WriteStreamHeader(header, checkType);

        int read = XzHeader.ReadStreamHeader(header);
        Assert.Equal(checkType, read);
    }

    [Fact]
    public void StreamHeader_InvalidMagic_Throws()
    {
        byte[] header = new byte[12];
        header[0] = 0xFF; // Wrong magic

        Assert.Throws<LzmaFormatException>(() => XzHeader.ReadStreamHeader(header));
    }

    [Fact]
    public void StreamHeader_CorruptCrc_Throws()
    {
        byte[] header = new byte[12];
        XzHeader.WriteStreamHeader(header, XzConstants.CheckCrc64);
        header[8] ^= 0xFF; // Corrupt CRC

        Assert.Throws<LzmaDataErrorException>(() => XzHeader.ReadStreamHeader(header));
    }

    [Fact]
    public void StreamHeader_TooShort_Throws()
    {
        byte[] header = new byte[6];
        Assert.Throws<LzmaFormatException>(() => XzHeader.ReadStreamHeader(header));
    }

    [Fact]
    public void StreamFooter_WriteAndRead()
    {
        Span<byte> footer = stackalloc byte[12];
        long indexSize = 16; // Must be multiple of 4
        XzHeader.WriteStreamFooter(footer, XzConstants.CheckCrc64, indexSize);

        long readIndexSize = XzHeader.ReadStreamFooter(footer, XzConstants.CheckCrc64);
        Assert.Equal(indexSize, readIndexSize);
    }

    [Fact]
    public void StreamFooter_CheckTypeMismatch_Throws()
    {
        byte[] footer = new byte[12];
        XzHeader.WriteStreamFooter(footer, XzConstants.CheckCrc64, 16);

        Assert.Throws<LzmaDataErrorException>(() =>
            XzHeader.ReadStreamFooter(footer, XzConstants.CheckCrc32));
    }

    [Fact]
    public void StreamFooter_InvalidMagic_Throws()
    {
        byte[] footer = new byte[12];
        footer[10] = 0x00; // Wrong magic

        Assert.Throws<LzmaFormatException>(() =>
            XzHeader.ReadStreamFooter(footer, XzConstants.CheckNone));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 4)]
    [InlineData(4, 8)]
    [InlineData(10, 32)]
    public void CheckSize_KnownValues(int checkType, int expectedSize)
    {
        Assert.Equal(expectedSize, XzConstants.GetCheckSize(checkType));
    }

    [Fact]
    public void CompressedData_StartsWithXzMagic()
    {
        byte[] data = "test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(data);

        Assert.True(compressed.Length >= 6);
        Assert.Equal(0xFD, compressed[0]);
        Assert.Equal(0x37, compressed[1]);
        Assert.Equal(0x7A, compressed[2]);
        Assert.Equal(0x58, compressed[3]);
        Assert.Equal(0x5A, compressed[4]);
        Assert.Equal(0x00, compressed[5]);
    }

    [Fact]
    public void CompressedData_EndsWithXzFooterMagic()
    {
        byte[] data = "test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(data);

        Assert.True(compressed.Length >= 2);
        Assert.Equal(0x59, compressed[^2]); // 'Y'
        Assert.Equal(0x5A, compressed[^1]); // 'Z'
    }

    [Fact]
    public void Decompress_InvalidFormat_Throws()
    {
        byte[] garbage = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];
        Assert.Throws<LzmaFormatException>(() => XzCompressor.Decompress(garbage));
    }

    [Fact]
    public void Decompress_TruncatedData_Throws()
    {
        byte[] data = "test"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(data);

        // Truncate
        byte[] truncated = compressed[..(compressed.Length / 2)];
        Assert.ThrowsAny<LzmaException>(() => XzCompressor.Decompress(truncated));
    }

    [Fact]
    public void Decompress_CorruptedData_Throws()
    {
        byte[] data = "test data for corruption check"u8.ToArray();
        byte[] compressed = XzCompressor.Compress(data);

        // Corrupt a byte in the middle (compressed data area)
        if (compressed.Length > 20)
        {
            compressed[15] ^= 0xFF;
            Assert.ThrowsAny<LzmaException>(() => XzCompressor.Decompress(compressed));
        }
    }

    [Fact]
    public void XzDecompressStream_Properties()
    {
        using var ms = new MemoryStream(XzCompressor.Compress("x"u8.ToArray()));
        using var xz = new XzDecompressStream(ms);

        Assert.True(xz.CanRead);
        Assert.False(xz.CanWrite);
        Assert.False(xz.CanSeek);
        Assert.Throws<NotSupportedException>(() => xz.Length);
        Assert.Throws<NotSupportedException>(() => xz.Position);
        Assert.Throws<NotSupportedException>(() => xz.Position = 0);
        Assert.Throws<NotSupportedException>(() => xz.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => xz.SetLength(0));
        Assert.Throws<NotSupportedException>(() => xz.Write(new byte[1], 0, 1));
    }

    [Fact]
    public void XzCompressStream_Properties()
    {
        using var ms = new MemoryStream();
        using var xz = new XzCompressStream(ms, leaveOpen: true);

        Assert.False(xz.CanRead);
        Assert.True(xz.CanWrite);
        Assert.False(xz.CanSeek);
        Assert.Throws<NotSupportedException>(() => xz.Length);
        Assert.Throws<NotSupportedException>(() => xz.Position);
        Assert.Throws<NotSupportedException>(() => xz.Position = 0);
        Assert.Throws<NotSupportedException>(() => xz.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => xz.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => xz.SetLength(0));
    }

    [Fact]
    public void XzCompressStream_InvalidPreset_Throws()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new XzCompressStream(ms, new XzCompressOptions { Preset = 10 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XzCompressStream(ms, new XzCompressOptions { Preset = -1 }));
    }

    [Fact]
    public void XzCompressStream_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new XzCompressStream(null!));
    }

    [Fact]
    public void XzDecompressStream_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new XzDecompressStream(null!));
    }

    [Fact]
    public void MaxCompressedSize_ReturnsReasonableValue()
    {
        long maxSize = XzCompressor.MaxCompressedSize(1000);
        Assert.True(maxSize > 1000);
        Assert.True(maxSize < 2000);
    }

    [Fact]
    public void XzDecompressStream_LeaveOpen_True()
    {
        using var ms = new MemoryStream(XzCompressor.Compress("test"u8.ToArray()));
        using (var xz = new XzDecompressStream(ms, leaveOpen: true))
        {
            using var result = new MemoryStream();
            xz.CopyTo(result);
        }
        // Stream should still be usable
        Assert.True(ms.CanRead);
    }
}
