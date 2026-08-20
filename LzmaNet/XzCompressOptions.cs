// SPDX-License-Identifier: 0BSD

using LzmaNet.Xz;

namespace LzmaNet;

/// <summary>
/// Options for XZ compression, controlling compression level, threading,
/// buffer sizes, and other parameters.
/// </summary>
/// <remarks>
/// <para>
/// The simplest usage is to specify only a <see cref="Preset"/> level (0-9):
/// </para>
/// <code>
/// var options = new XzCompressOptions { Preset = 6 };
/// </code>
/// <para>
/// For maximum compression at the expense of CPU time, enable <see cref="Extreme"/>:
/// </para>
/// <code>
/// var options = new XzCompressOptions { Preset = 9, Extreme = true };
/// </code>
/// <para>
/// For parallel compression, set <see cref="Threads"/> to 0 (all CPUs) or a specific count:
/// </para>
/// <code>
/// var options = new XzCompressOptions { Preset = 6, Threads = 0 };
/// </code>
/// </remarks>
public sealed class XzCompressOptions
{
    /// <summary>
    /// Compression preset level (0-9). Higher values use more memory and CPU
    /// but produce smaller output. Default is 6.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    /// <listheader><term>Preset</term><description>Dictionary Size</description></listheader>
    /// <item><term>0</term><description>64 KB</description></item>
    /// <item><term>1</term><description>1 MB</description></item>
    /// <item><term>2</term><description>2 MB</description></item>
    /// <item><term>3-4</term><description>4 MB</description></item>
    /// <item><term>5-6</term><description>8 MB</description></item>
    /// <item><term>7</term><description>16 MB</description></item>
    /// <item><term>8</term><description>32 MB</description></item>
    /// <item><term>9</term><description>64 MB</description></item>
    /// </list>
    /// </remarks>
    public int Preset { get; set; } = 6;

    /// <summary>
    /// When <c>true</c>, uses significantly more CPU time to find better matches,
    /// improving compression ratio without increasing memory usage.
    /// Equivalent to <c>xz -e</c> / <c>xz --extreme</c>. Default is <c>false</c>.
    /// </summary>
    public bool Extreme { get; set; }

    /// <summary>
    /// Number of threads for parallel block compression.
    /// <list type="bullet">
    /// <item><description><c>0</c> = use all available CPUs (<see cref="Environment.ProcessorCount"/>).</description></item>
    /// <item><description><c>1</c> = single-threaded (default).</description></item>
    /// <item><description><c>N</c> = use N threads.</description></item>
    /// </list>
    /// </summary>
    public int Threads { get; set; } = 1;

    /// <summary>
    /// Integrity check type written into the XZ stream. Default is CRC64.
    /// </summary>
    /// <seealso cref="XzCheckType"/>
    public XzCheckType CheckType { get; set; } = XzCheckType.Crc64;

    /// <summary>
    /// Dictionary size in bytes. When <c>null</c> (default), determined by <see cref="Preset"/>.
    /// Must be at least 4 KB. Larger dictionaries improve compression of repetitive data
    /// at the cost of higher memory usage during both compression and decompression.
    /// </summary>
    public int? DictionarySize { get; set; }

    /// <summary>
    /// XZ block size in bytes. When <c>null</c> (default), set to
    /// <c>max(dictionarySize × 2, 1 MB)</c>.
    /// Larger blocks improve compression ratio; smaller blocks reduce memory usage
    /// and allow parallel decompression of the output.
    /// Must be at least 4 KB.
    /// </summary>
    public int? BlockSize { get; set; }

    /// <summary>
    /// Optional pre-compression filter (BCJ or Delta) applied before LZMA2.
    /// BCJ filters substantially improve the ratio on machine code (executables,
    /// shared libraries); use the variant matching the target architecture.
    /// Default is <see cref="XzFilterType.None"/>.
    /// </summary>
    public XzFilterType Filter { get; set; } = XzFilterType.None;

    /// <summary>
    /// Byte distance for the <see cref="XzFilterType.Delta"/> filter (1–256).
    /// Ignored for other filters. Default is 1.
    /// </summary>
    public int DeltaDistance { get; set; } = 1;

    /// <summary>
    /// Optional progress sink. Reports the cumulative number of uncompressed
    /// bytes compressed so far, once per completed XZ block. Reports are made
    /// from the thread performing the write, never from worker threads.
    /// </summary>
    public IProgress<long>? Progress { get; set; }

    /// <summary>
    /// Returns a default options instance equivalent to <c>xz -6</c>.
    /// </summary>
    public static XzCompressOptions Default => new();

    /// <summary>
    /// Validates all option values and throws <see cref="ArgumentOutOfRangeException"/>
    /// if any are invalid.
    /// </summary>
    public void Validate()
    {
        if (Preset < 0 || Preset > 9)
            throw new ArgumentOutOfRangeException(nameof(Preset), "Preset must be 0-9.");
        if (Threads < 0)
            throw new ArgumentOutOfRangeException(nameof(Threads), "Threads must be >= 0.");
        if (DictionarySize.HasValue && DictionarySize.Value < 4096)
            throw new ArgumentOutOfRangeException(nameof(DictionarySize), "Dictionary size must be at least 4 KB.");
        if (BlockSize.HasValue && BlockSize.Value < 4096)
            throw new ArgumentOutOfRangeException(nameof(BlockSize), "Block size must be at least 4 KB.");
        if (!Enum.IsDefined(Filter))
            throw new ArgumentOutOfRangeException(nameof(Filter));
        if (Filter == XzFilterType.Delta && (DeltaDistance < 1 || DeltaDistance > 256))
            throw new ArgumentOutOfRangeException(nameof(DeltaDistance), "Delta distance must be 1-256.");
    }

    /// <summary>
    /// Resolves the configured filter to its XZ filter ID and encoded properties,
    /// or (0, null) when no filter is configured.
    /// </summary>
    internal (ulong Id, byte[]? Props) ResolvedFilter => Filter switch
    {
        XzFilterType.None => (0UL, null),
        XzFilterType.Delta => (XzConstants.FilterIdDelta, new[] { (byte)(DeltaDistance - 1) }),
        _ => (Filter.ToFilterId(), Array.Empty<byte>()),
    };

    /// <summary>
    /// Gets the resolved thread count (replacing 0 with <see cref="Environment.ProcessorCount"/>).
    /// </summary>
    internal int ResolvedThreads => Threads == 0 ? Environment.ProcessorCount : Threads;

    /// <summary>
    /// Returns options whose effective dictionary is capped at the input size,
    /// for APIs that know the full input up front. A dictionary larger than the
    /// input cannot improve compression but costs proportional match-finder
    /// memory (~650 MB at preset 9). An explicit <see cref="DictionarySize"/>
    /// always wins; the cap never raises the preset's dictionary.
    /// </summary>
    internal XzCompressOptions EffectiveForInputSize(long inputLength)
    {
        if (DictionarySize.HasValue)
            return this;

        int presetDict = Lzma.LzmaEncoderProperties.FromPreset(Preset, Extreme).DictionarySize;
        int capped = (int)Math.Clamp(inputLength, 4096, presetDict);
        if (capped >= presetDict)
            return this;

        return new XzCompressOptions
        {
            Preset = Preset,
            Extreme = Extreme,
            Threads = Threads,
            CheckType = CheckType,
            DictionarySize = capped,
            BlockSize = BlockSize,
            Filter = Filter,
            DeltaDistance = DeltaDistance,
            Progress = Progress,
        };
    }

    /// <summary>
    /// Gets the XZ check type constant used internally.
    /// </summary>
    internal int CheckTypeValue => CheckType switch
    {
        XzCheckType.None => XzConstants.CheckNone,
        XzCheckType.Crc32 => XzConstants.CheckCrc32,
        XzCheckType.Crc64 => XzConstants.CheckCrc64,
        XzCheckType.Sha256 => XzConstants.CheckSha256,
        _ => XzConstants.CheckCrc64
    };
}

/// <summary>
/// Integrity check type for XZ streams.
/// </summary>
public enum XzCheckType
{
    /// <summary>No integrity check.</summary>
    None = 0,

    /// <summary>CRC32 (4 bytes). Fast but less robust.</summary>
    Crc32 = 1,

    /// <summary>CRC64 (8 bytes). Good balance of speed and integrity (default).</summary>
    Crc64 = 4,

    /// <summary>SHA-256 (32 bytes). Strongest integrity check.</summary>
    Sha256 = 10,
}
