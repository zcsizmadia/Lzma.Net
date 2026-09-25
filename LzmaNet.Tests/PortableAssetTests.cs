// SPDX-License-Identifier: 0BSD

#if PORTABLE_ASSET_TESTS

using System.Reflection;
using System.Runtime.Versioning;

using LzmaNet.Check;

namespace LzmaNet.Tests;

/// <summary>
/// Guards the wiring of the portable test project. These sources are compiled
/// twice: once against the net8.0+ build of the library and once against the
/// netstandard2.1 one. Only the second defines PORTABLE_ASSET_TESTS, so if the
/// SetTargetFramework on its project reference is ever dropped, the whole suite
/// would quietly start testing the modern asset twice — and this test is what
/// notices.
/// </summary>
public class PortableAssetTests
{
    [Test]
    public async Task TestsRunAgainstTheNetStandardAsset()
    {
        string? framework = typeof(XzCompressor).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        await Assert.That(framework).StartsWith(".NETStandard");
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
