using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Tests;

/// <summary>
/// C# overloads that differ only in the signedness of the same-width primitive
/// (long/ulong, int/uint) collide after the unsigned→signed Java type mapping.
/// The later-declared overload must survive under the erasure suffix and call
/// sites must resolve by static argument type — otherwise every ulong payload
/// (e.g. the EnumConverter's UInt64 write path) silently formats as SIGNED,
/// producing "MyUInt64Enum":-1 where C# writes 18446744073709551615.
/// </summary>
public class UnsignedOverloadCollisionTests
{
}
