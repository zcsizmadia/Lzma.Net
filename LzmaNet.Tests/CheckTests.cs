// SPDX-License-Identifier: 0BSD

using LzmaNet.Check;

namespace LzmaNet.Tests;

/// <summary>
/// Tests for CRC32 and CRC64 implementations using known test vectors.
/// </summary>
public class CheckTests
{
    [Fact]
    public void Crc32_EmptyInput_ReturnsZero()
    {
        uint result = Crc32.Compute(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0u, result);
    }

    [Fact]
    public void Crc32_KnownVector_123456789()
    {
        // "123456789" -> CRC32 = 0xCBF43926 (IEEE 802.3)
        byte[] data = "123456789"u8.ToArray();
        uint result = Crc32.Compute(data);
        Assert.Equal(0xCBF43926u, result);
    }

    [Fact]
    public void Crc32_SingleByte()
    {
        byte[] data = [0x00];
        uint result = Crc32.Compute(data);
        Assert.Equal(0xD202EF8Du, result);
    }

    [Fact]
    public void Crc32_Incremental()
    {
        byte[] data = "Hello, World!"u8.ToArray();
        uint full = Crc32.Compute(data);

        // Compute incrementally
        uint partial = Crc32.Compute(data.AsSpan(0, 5));
        uint complete = Crc32.Compute(data.AsSpan(5), partial);

        Assert.Equal(full, complete);
    }

    [Fact]
    public void Crc32_WriteAndVerify()
    {
        byte[] data = "Test data for CRC verification"u8.ToArray();
        byte[] crcBytes = new byte[4];
        Crc32.WriteLE(data, crcBytes);

        Assert.True(Crc32.Verify(data, crcBytes));
    }

    [Fact]
    public void Crc32_VerifyFailsOnCorruptData()
    {
        byte[] data = "Test data"u8.ToArray();
        byte[] crcBytes = new byte[4];
        Crc32.WriteLE(data, crcBytes);

        data[0] ^= 0xFF; // Corrupt one byte
        Assert.False(Crc32.Verify(data, crcBytes));
    }

    [Fact]
    public void Crc64_EmptyInput_ReturnsZero()
    {
        ulong result = Crc64.Compute(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0UL, result);
    }

    [Fact]
    public void Crc64_KnownVector_123456789()
    {
        // "123456789" -> CRC64-ECMA = 0x995DC9BBDF1939FA
        byte[] data = "123456789"u8.ToArray();
        ulong result = Crc64.Compute(data);
        Assert.Equal(0x995DC9BBDF1939FAUL, result);
    }

    [Fact]
    public void Crc64_Incremental()
    {
        byte[] data = "Hello, World!"u8.ToArray();
        ulong full = Crc64.Compute(data);

        ulong partial = Crc64.Compute(data.AsSpan(0, 7));
        ulong complete = Crc64.Compute(data.AsSpan(7), partial);

        Assert.Equal(full, complete);
    }

    [Fact]
    public void Crc64_WriteAndVerify()
    {
        byte[] data = "Test data for CRC64 verification"u8.ToArray();
        byte[] crcBytes = new byte[8];
        Crc64.WriteLE(data, crcBytes);

        Assert.True(Crc64.Verify(data, crcBytes));
    }

    [Fact]
    public void Crc64_VerifyFailsOnCorruptData()
    {
        byte[] data = "Test data"u8.ToArray();
        byte[] crcBytes = new byte[8];
        Crc64.WriteLE(data, crcBytes);

        data[0] ^= 0xFF;
        Assert.False(Crc64.Verify(data, crcBytes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(1024)]
    [InlineData(65536)]
    public void Crc32_VariousLengths(int length)
    {
        byte[] data = new byte[length];
        new Random(42).NextBytes(data);

        uint crc1 = Crc32.Compute(data);
        uint crc2 = Crc32.Compute(data);
        Assert.Equal(crc1, crc2);

        // Incremental should match
        uint incremental = 0u;
        int chunkSize = Math.Max(1, length / 4);
        for (int i = 0; i < length; i += chunkSize)
        {
            int len = Math.Min(chunkSize, length - i);
            incremental = Crc32.Compute(data.AsSpan(i, len), incremental);
        }
        Assert.Equal(crc1, incremental);
    }
}
