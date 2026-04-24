// SPDX-License-Identifier: 0BSD

using LzmaNet.Check;

namespace LzmaNet.Tests;

/// <summary>
/// Tests for CRC32 and CRC64 implementations using known test vectors.
/// </summary>
public class CheckTests
{
    [Test]
    public async Task Crc32_EmptyInput_ReturnsZero()
    {
        uint result = Crc32.Compute(ReadOnlySpan<byte>.Empty);
        await Assert.That(result).IsEqualTo(0u);
    }

    [Test]
    public async Task Crc32_KnownVector_123456789()
    {
        // "123456789" -> CRC32 = 0xCBF43926 (IEEE 802.3)
        byte[] data = "123456789"u8.ToArray();
        uint result = Crc32.Compute(data);
        await Assert.That(result).IsEqualTo(0xCBF43926u);
    }

    [Test]
    public async Task Crc32_SingleByte()
    {
        byte[] data = [0x00];
        uint result = Crc32.Compute(data);
        await Assert.That(result).IsEqualTo(0xD202EF8Du);
    }

    [Test]
    public async Task Crc32_Incremental()
    {
        byte[] data = "Hello, World!"u8.ToArray();
        uint full = Crc32.Compute(data);

        // Compute incrementally
        uint partial = Crc32.Compute(data.AsSpan(0, 5));
        uint complete = Crc32.Compute(data.AsSpan(5), partial);

        await Assert.That(complete).IsEqualTo(full);
    }

    [Test]
    public async Task Crc32_WriteAndVerify()
    {
        byte[] data = "Test data for CRC verification"u8.ToArray();
        byte[] crcBytes = new byte[4];
        Crc32.WriteLE(data, crcBytes);

        await Assert.That(Crc32.Verify(data, crcBytes)).IsTrue();
    }

    [Test]
    public async Task Crc32_VerifyFailsOnCorruptData()
    {
        byte[] data = "Test data"u8.ToArray();
        byte[] crcBytes = new byte[4];
        Crc32.WriteLE(data, crcBytes);

        data[0] ^= 0xFF; // Corrupt one byte
        await Assert.That(Crc32.Verify(data, crcBytes)).IsFalse();
    }

    [Test]
    public async Task Crc64_EmptyInput_ReturnsZero()
    {
        ulong result = Crc64.Compute(ReadOnlySpan<byte>.Empty);
        await Assert.That(result).IsEqualTo(0UL);
    }

    [Test]
    public async Task Crc64_KnownVector_123456789()
    {
        // "123456789" -> CRC64-ECMA = 0x995DC9BBDF1939FA
        byte[] data = "123456789"u8.ToArray();
        ulong result = Crc64.Compute(data);
        await Assert.That(result).IsEqualTo(0x995DC9BBDF1939FAUL);
    }

    [Test]
    public async Task Crc64_Incremental()
    {
        byte[] data = "Hello, World!"u8.ToArray();
        ulong full = Crc64.Compute(data);

        ulong partial = Crc64.Compute(data.AsSpan(0, 7));
        ulong complete = Crc64.Compute(data.AsSpan(7), partial);

        await Assert.That(complete).IsEqualTo(full);
    }

    [Test]
    public async Task Crc64_WriteAndVerify()
    {
        byte[] data = "Test data for CRC64 verification"u8.ToArray();
        byte[] crcBytes = new byte[8];
        Crc64.WriteLE(data, crcBytes);

        await Assert.That(Crc64.Verify(data, crcBytes)).IsTrue();
    }

    [Test]
    public async Task Crc64_VerifyFailsOnCorruptData()
    {
        byte[] data = "Test data"u8.ToArray();
        byte[] crcBytes = new byte[8];
        Crc64.WriteLE(data, crcBytes);

        data[0] ^= 0xFF;
        await Assert.That(Crc64.Verify(data, crcBytes)).IsFalse();
    }

    [Test]
    [Arguments(1)]
    [Arguments(255)]
    [Arguments(1024)]
    [Arguments(65536)]
    public async Task Crc32_VariousLengths(int length)
    {
        byte[] data = new byte[length];
        new Random(42).NextBytes(data);

        uint crc1 = Crc32.Compute(data);
        uint crc2 = Crc32.Compute(data);
        await Assert.That(crc2).IsEqualTo(crc1);

        // Incremental should match
        uint incremental = 0u;
        int chunkSize = Math.Max(1, length / 4);
        for (int i = 0; i < length; i += chunkSize)
        {
            int len = Math.Min(chunkSize, length - i);
            incremental = Crc32.Compute(data.AsSpan(i, len), incremental);
        }
        await Assert.That(incremental).IsEqualTo(crc1);
    }
}
