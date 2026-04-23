// SPDX-License-Identifier: 0BSD

using LzmaNet.Xz;

namespace LzmaNet.Filters;

/// <summary>
/// Creates BCJ/Delta filter instances from XZ filter IDs and properties.
/// </summary>
internal static class FilterFactory
{
    /// <summary>
    /// Creates a filter from its XZ filter ID and properties.
    /// Returns null if the filter ID is LZMA2 (handled separately).
    /// </summary>
    public static IBcjFilter Create(ulong filterId, ReadOnlySpan<byte> properties)
    {
        return filterId switch
        {
            XzConstants.FilterIdDelta => CreateDelta(properties),
            XzConstants.FilterIdX86 => CreateWithOffset<X86Filter>(properties),
            XzConstants.FilterIdPowerPc => CreateWithOffset<PowerPcFilter>(properties),
            XzConstants.FilterIdIa64 => CreateWithOffset<Ia64Filter>(properties),
            XzConstants.FilterIdArm => CreateWithOffset<ArmFilter>(properties),
            XzConstants.FilterIdArmThumb => CreateWithOffset<ArmThumbFilter>(properties),
            XzConstants.FilterIdSparc => CreateWithOffset<SparcFilter>(properties),
            XzConstants.FilterIdArm64 => CreateWithOffset<Arm64Filter>(properties),
            XzConstants.FilterIdRiscV => CreateWithOffset<RiscVFilter>(properties),
            _ => throw new LzmaException($"Unsupported XZ filter: 0x{filterId:X}.")
        };
    }

    /// <summary>
    /// Checks if a filter ID is a supported BCJ or Delta filter.
    /// </summary>
    public static bool IsSupported(ulong filterId)
    {
        return filterId == XzConstants.FilterIdDelta
            || filterId == XzConstants.FilterIdX86
            || filterId == XzConstants.FilterIdPowerPc
            || filterId == XzConstants.FilterIdIa64
            || filterId == XzConstants.FilterIdArm
            || filterId == XzConstants.FilterIdArmThumb
            || filterId == XzConstants.FilterIdSparc
            || filterId == XzConstants.FilterIdArm64
            || filterId == XzConstants.FilterIdRiscV
            || filterId == XzConstants.FilterIdLzma2;
    }

    private static DeltaFilter CreateDelta(ReadOnlySpan<byte> properties)
    {
        if (properties.Length != 1)
            throw new LzmaDataErrorException("Invalid Delta filter properties size.");
        return new DeltaFilter(properties[0] + 1);
    }

    private static T CreateWithOffset<T>(ReadOnlySpan<byte> properties) where T : IBcjFilter, new()
    {
        // BCJ filters have 0 or 4 bytes of properties (start offset)
        // The start offset is rarely used — we accept it but ignore it since
        // the offset is applied via the startPos parameter during code()
        if (properties.Length != 0 && properties.Length != 4)
            throw new LzmaDataErrorException($"Invalid BCJ filter properties size: {properties.Length}.");
        return new T();
    }
}
