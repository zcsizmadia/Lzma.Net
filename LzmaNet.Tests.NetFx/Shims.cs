// SPDX-License-Identifier: 0BSD

// Compiler and generator features that .NET Framework's reference assemblies do
// not declare. The C# compiler and TUnit's source generator look these up by
// name, so supplying them here is enough — nothing references them directly.

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// TUnit's source generator emits a module initializer to register its
    /// tests. net472 has no such attribute, so the test assembly declares one.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute
    {
    }

    /// <summary>
    /// Required by the compiler to emit <c>init</c> accessors and records.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}
