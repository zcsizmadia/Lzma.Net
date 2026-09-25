// SPDX-License-Identifier: 0BSD

#if PORTABLE_ASSET_TESTS

using System.Reflection;
using System.Runtime.Versioning;

using LzmaNet.Check;

namespace LzmaNet.Tests;

/// <summary>
/// Guards the wiring of the portable test projects. These sources are compiled
/// three times: against the net8.0+ build of the library, against the
/// netstandard2.1 one, and against the netstandard2.0 one. Only the portable
/// projects define PORTABLE_ASSET_TESTS, so if the SetTargetFramework on a
/// project reference is ever dropped — or the two portable projects are pointed
/// at the same asset — the suite would quietly stop covering a target, and
/// these tests are what notice.
/// </summary>
public class PortableAssetTests
{
#if PORTABLE_ASSET_20
    private const string ExpectedFramework = ".NETStandard,Version=v2.0";
#else
    private const string ExpectedFramework = ".NETStandard,Version=v2.1";
#endif

    [Test]
    public async Task TestsRunAgainstTheExpectedNetStandardAsset()
    {
        string? framework = typeof(XzCompressor).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        await Assert.That(framework).IsEqualTo(ExpectedFramework);
    }

    [Test]
    public async Task PortableAssetHasNoCarrylessMultiplyPath()
    {
        // netstandard2.1 cannot reference System.Runtime.Intrinsics, so the
        // folding path is not compiled into this asset at all and every CRC in
        // the run above went through slicing-by-8.
        await Assert.That(CrcFolding.IsSupported).IsFalse();
    }
}

#endif
