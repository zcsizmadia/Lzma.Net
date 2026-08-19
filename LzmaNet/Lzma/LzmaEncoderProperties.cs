// SPDX-License-Identifier: 0BSD

namespace LzmaNet.Lzma;

/// <summary>
/// Compression properties for the LZMA encoder.
/// </summary>
internal sealed class LzmaEncoderProperties
{
    /// <summary>Number of literal context bits (0-8). Default: 3.</summary>
    public int Lc { get; set; } = 3;

    /// <summary>Number of literal position bits (0-4). Default: 0.</summary>
    public int Lp { get; set; } = 0;

    /// <summary>Number of position bits (0-4). Default: 2.</summary>
    public int Pb { get; set; } = 2;

    /// <summary>Dictionary size in bytes. Default: 8 MB.</summary>
    public int DictionarySize { get; set; } = 1 << 23; // 8 MB

    /// <summary>Maximum match length. Default: 273 (LZMA maximum).</summary>
    public int MatchMaxLen { get; set; } = LzmaConstants.kMatchMaxLen;

    /// <summary>Hash chain cut value (search depth). Default: 32.</summary>
    public int CutValue { get; set; } = 32;

    /// <summary>
    /// Gets the properties byte encoding lc, lp, pb.
    /// </summary>
    public byte PropertiesByte => LzmaConstants.EncodeProperties(Lc, Lp, Pb);

    /// <summary>
    /// Creates properties matching a preset level (0-9), optionally with extreme mode.
    /// </summary>
    public static LzmaEncoderProperties FromPreset(int level, bool extreme = false)
    {
        var props = new LzmaEncoderProperties();

        props.DictionarySize = level switch
        {
            0 => 1 << 16,   // 64 KB
            1 => 1 << 20,   // 1 MB
            2 => 1 << 21,   // 2 MB
            3 => 1 << 22,   // 4 MB
            4 => 1 << 22,   // 4 MB
            5 => 1 << 23,   // 8 MB
            6 => 1 << 23,   // 8 MB
            7 => 1 << 24,   // 16 MB
            8 => 1 << 25,   // 32 MB
            9 => 1 << 26,   // 64 MB
            _ => throw new ArgumentOutOfRangeException(nameof(level), "Preset level must be 0-9.")
        };

        props.CutValue = level switch
        {
            0 or 1 => 8,
            2 or 3 => 16,
            4 or 5 => 32,
            6 or 7 => 64,
            8 or 9 => 128,
            _ => 32
        };

        props.MatchMaxLen = level <= 1 ? 128 : LzmaConstants.kMatchMaxLen;

        // Extreme mode: significantly increase search depth for better compression
        // at the cost of more CPU time, matching xz --extreme behavior.
        if (extreme)
        {
            props.CutValue = level switch
            {
                0 or 1 => 32,
                2 or 3 => 64,
                4 or 5 => 128,
                6 or 7 => 256,
                8 or 9 => 512,
                _ => 128
            };
            // Also use maximum match length at all levels
            props.MatchMaxLen = LzmaConstants.kMatchMaxLen;
        }

        // Standard lc/lp/pb
        props.Lc = 3;
        props.Lp = 0;
        props.Pb = 2;

        return props;
    }

    /// <summary>
    /// Validates the properties.
    /// </summary>
    public void Validate()
    {
        if (Lc < 0 || Lc > LzmaConstants.kNumLitContextBitsMax)
            throw new ArgumentOutOfRangeException(nameof(Lc));
        if (Lp < 0 || Lp > LzmaConstants.kNumLitPosStatesBitsMax)
            throw new ArgumentOutOfRangeException(nameof(Lp));
        if (Pb < 0 || Pb > LzmaConstants.kNumPosStatesBitsMax)
            throw new ArgumentOutOfRangeException(nameof(Pb));
        if (DictionarySize < 1)
            throw new ArgumentOutOfRangeException(nameof(DictionarySize));
    }
}
