// Compiler polyfills needed when targeting .NET Framework 4.8 with a modern
// C# language version (LangVersion=latest). These types exist natively on
// .NET 5+ but must be declared locally for older frameworks.
//
// IsExternalInit lets the compiler emit `init` accessors (used by records
// and { get; init; } properties).

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
