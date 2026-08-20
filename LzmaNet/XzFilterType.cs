// SPDX-License-Identifier: 0BSD

using LzmaNet.Xz;

namespace LzmaNet;

/// <summary>
/// Optional pre-compression filter applied before LZMA2 in the XZ filter chain.
/// BCJ (Branch/Call/Jump) filters convert relative branch addresses in machine
/// code to absolute ones, making executables significantly more compressible.
/// The Delta filter helps data with a fixed-stride structure (e.g., raw audio
/// or bitmap channels).
/// </summary>
public enum XzFilterType
{
    /// <summary>No filter — LZMA2 only (default).</summary>
    None = 0,

    /// <summary>Delta filter with a configurable byte distance (see <see cref="XzCompressOptions.DeltaDistance"/>).</summary>
    Delta = 1,

    /// <summary>BCJ filter for x86/x64 machine code.</summary>
    X86 = 2,

    /// <summary>BCJ filter for PowerPC (big endian) machine code.</summary>
    PowerPc = 3,

    /// <summary>BCJ filter for IA-64 (Itanium) machine code.</summary>
    Ia64 = 4,

    /// <summary>BCJ filter for ARM (32-bit little endian) machine code.</summary>
    Arm = 5,

    /// <summary>BCJ filter for ARM-Thumb machine code.</summary>
    ArmThumb = 6,

    /// <summary>BCJ filter for SPARC machine code.</summary>
    Sparc = 7,

    /// <summary>BCJ filter for ARM64 (AArch64) machine code.</summary>
    Arm64 = 8,

    /// <summary>BCJ filter for RISC-V machine code.</summary>
    RiscV = 9,
}

internal static class XzFilterTypeExtensions
{
    /// <summary>Maps the public filter type to its XZ filter ID.</summary>
    public static ulong ToFilterId(this XzFilterType type) => type switch
    {
        XzFilterType.Delta => XzConstants.FilterIdDelta,
        XzFilterType.X86 => XzConstants.FilterIdX86,
        XzFilterType.PowerPc => XzConstants.FilterIdPowerPc,
        XzFilterType.Ia64 => XzConstants.FilterIdIa64,
        XzFilterType.Arm => XzConstants.FilterIdArm,
        XzFilterType.ArmThumb => XzConstants.FilterIdArmThumb,
        XzFilterType.Sparc => XzConstants.FilterIdSparc,
        XzFilterType.Arm64 => XzConstants.FilterIdArm64,
        XzFilterType.RiscV => XzConstants.FilterIdRiscV,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
