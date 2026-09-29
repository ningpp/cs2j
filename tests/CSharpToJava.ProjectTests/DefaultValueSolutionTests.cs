using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class DefaultValueSolutionTests
{
    private static string SlnPath =>
        Path.Combine(AppContext.BaseDirectory, "SampleDefaultSolution", "SampleDefaultSolution.sln");
}
