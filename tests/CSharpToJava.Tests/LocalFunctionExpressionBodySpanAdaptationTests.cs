using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Expression-bodied local functions must apply the same target-type adaptation as
/// regular expression-bodied methods. A local function returning ReadOnlySpan&lt;byte&gt;
/// from a byte[] initializer was emitted as a raw byte[] return, which javac rejects
/// ("byte[] cannot be converted to ReadOnlySpan&lt;Integer&gt;").
/// Regression: RegexCharClass.AsciiLookup() => new byte[] {...}.
/// </summary>
public class LocalFunctionExpressionBodySpanAdaptationTests
{
}
