// SPDX-License-Identifier: 0BSD

#if !NET8_0_OR_GREATER

namespace System.Runtime.CompilerServices;

/// <summary>
/// Marker the compiler requires to emit <c>init</c> accessors and positional
/// records. .NET 5 and later declare it; older targets do not, so the library
/// supplies its own. Never referenced from source — the compiler looks it up
/// by name.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}

#endif
