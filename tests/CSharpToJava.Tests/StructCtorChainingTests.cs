using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression: a readonly struct whose parameterized constructor chains via
/// ': this(...)' and whose target constructor chains via ': this()' must not
/// lose the chained argument assignments (e.g. BorderInfo(double innerMargin)).
/// </summary>
public class _PlaceholderStructCtorChainingTests
{
}
