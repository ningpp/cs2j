using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that TryGetValue lowering casts the map get() result when the out
/// variable is a Java primitive. Reproduces Regex.java:[275,96] "不兼容的类型:
/// Object 无法转换为 int" — C# 'caps.TryGetValue(i, out i)' (Hashtable extension,
/// int out reused) lowered to 'i = caps.get(i)', but get() returns Object and
/// cannot be assigned to an int without an unboxing cast.
/// </summary>
public class TryGetValuePrimitiveOutCastTests
{
}
