// SPDX-License-Identifier: 0BSD

namespace LzmaNet.Xz;

/// <summary>
/// Constants for the XZ container format.
/// </summary>
internal static class XzConstants
{
    /// <summary>XZ stream header magic bytes: FD 37 7A 58 5A 00 (= 0xFD + "7zXZ" + 0x00).</summary>
    public static ReadOnlySpan<byte> HeaderMagic => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    /// <summary>XZ stream footer magic bytes: 59 5A ("YZ").</summary>
    public static ReadOnlySpan<byte> FooterMagic => [0x59, 0x5A];

    /// <summary>Stream header size: 6 magic + 2 stream flags + 4 CRC32 = 12 bytes.</summary>
    public const int StreamHeaderSize = 12;

    /// <summary>Stream footer size: 4 CRC32 + 4 backward size + 2 stream flags + 2 magic = 12 bytes.</summary>
    public const int StreamFooterSize = 12;

    /// <summary>XZ filter ID for LZMA2.</summary>
    public const ulong FilterIdLzma2 = 0x21;

    /// <summary>XZ filter ID for Delta.</summary>
    public const ulong FilterIdDelta = 0x03;

    /// <summary>XZ filter ID for x86 BCJ.</summary>
    public const ulong FilterIdX86 = 0x04;

    /// <summary>XZ filter ID for PowerPC (big endian) BCJ.</summary>
    public const ulong FilterIdPowerPc = 0x05;

    /// <summary>XZ filter ID for IA-64 (Itanium) BCJ.</summary>
    public const ulong FilterIdIa64 = 0x06;

    /// <summary>XZ filter ID for ARM (32-bit) BCJ.</summary>
    public const ulong FilterIdArm = 0x07;

    /// <summary>XZ filter ID for ARM-Thumb BCJ.</summary>
    public const ulong FilterIdArmThumb = 0x08;

    /// <summary>XZ filter ID for SPARC BCJ.</summary>
    public const ulong FilterIdSparc = 0x09;

    /// <summary>XZ filter ID for ARM64 (AArch64) BCJ.</summary>
    public const ulong FilterIdArm64 = 0x0A;

    /// <summary>XZ filter ID for RISC-V BCJ.</summary>
    public const ulong FilterIdRiscV = 0x0B;

    /// <summary>Check type: None (0 bytes).</summary>
    public const int CheckNone = 0x00;

    /// <summary>Check type: CRC32 (4 bytes).</summary>
    public const int CheckCrc32 = 0x01;

    /// <summary>Check type: CRC64 (8 bytes).</summary>
    public const int CheckCrc64 = 0x04;

    /// <summary>Check type: SHA-256 (32 bytes).</summary>
    public const int CheckSha256 = 0x0A;

    /// <summary>
    /// Gets the size in bytes for the given check type.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static int GetCheckSize(int checkType) => checkType switch
    {
        0 => 0,
        1 or 2 or 3 => 4,
        4 or 5 or 6 => 8,
        7 or 8 or 9 => 16,
        10 or 11 or 12 => 32,
        13 or 14 or 15 => 64,
        _ => throw new LzmaDataErrorException($"Invalid check type: {checkType}")
    };
}
