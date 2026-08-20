// SPDX-License-Identifier: 0BSD

namespace LzmaNet;

/// <summary>
/// Options for XZ decompression, controlling threading and resource limits.
/// </summary>
public sealed class XzDecompressOptions
{
    /// <summary>
    /// Number of threads for parallel block decoding.
    /// <list type="bullet">
    /// <item><description><c>0</c> = use all available CPUs (<see cref="Environment.ProcessorCount"/>).</description></item>
    /// <item><description><c>1</c> = single-threaded (default).</description></item>
    /// <item><description><c>N</c> = use up to N threads.</description></item>
    /// </list>
    /// Parallelism applies per XZ block, so it only helps for multi-block streams.
    /// </summary>
    public int Threads { get; set; } = 1;

    /// <summary>
    /// Maximum total number of decompressed bytes to produce before throwing
    /// <see cref="LzmaMemoryLimitException"/>. Protects against decompression
    /// bombs: a malicious file can claim enormous output sizes that would
    /// otherwise be allocated before any data is validated.
    /// Default is <see cref="long.MaxValue"/> (unlimited).
    /// For <see cref="XzSeekableStream"/> the limit applies per block.
    /// </summary>
    public long MaxOutputSize { get; set; } = long.MaxValue;

    /// <summary>
    /// Returns a default options instance (single-threaded, no output limit).
    /// </summary>
    public static XzDecompressOptions Default => new();

    /// <summary>
    /// Validates all option values.
    /// </summary>
    public void Validate()
    {
        if (Threads < 0)
            throw new ArgumentOutOfRangeException(nameof(Threads), "Threads must be >= 0.");
        if (MaxOutputSize < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxOutputSize), "MaxOutputSize must be positive.");
    }

    internal int ResolvedThreads => Threads == 0 ? Environment.ProcessorCount : Threads;
}
