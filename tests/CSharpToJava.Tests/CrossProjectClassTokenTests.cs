using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that Class&lt;T&gt; tokens are injected at call sites when the generic
/// method is declared in a different project (referenced assembly). The
/// RuntimeClassParameterRegistry (on ConversionOptions, shared across projects)
/// carries the method's Class-token requirements from the declaring project's
/// ConversionContext to the calling project's ConversionContext.
/// </summary>
public class CrossProjectClassTokenTests
{
    private readonly ITestOutputHelper _out;

    public CrossProjectClassTokenTests(ITestOutputHelper output) => _out = output;
}
