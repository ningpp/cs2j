using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
    /// Tests that C# MethodInfo.DeclaringType maps to the compat wrapper's
    /// getDeclaringType() method. System.Reflection.MethodInfo is now mapped to
    /// io.github.ningpp.compat.MethodInfo, which exposes getDeclaringType().
    /// </summary>
    public class MethodInfoDeclaringTypeTests
    {
        [Fact]
        public void MethodInfo_DeclaringType_MapsToGetDeclaringType()
        {
            var result = Convert(@"
using System;
using System.Reflection;

class Test {
    Type GetDeclaringType(MethodInfo methodInfo) {
        return methodInfo.DeclaringType;
    }
}
");

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Contains("methodInfo.getDeclaringType()", result.GeneratedCode, StringComparison.Ordinal);
            Assert.DoesNotContain(".getDeclaringClass()", result.GeneratedCode, StringComparison.Ordinal);
        }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
