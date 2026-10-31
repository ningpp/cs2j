using System.IO;
using CSharpToJava.Core.ArrayMethodRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Repro for SDK json <c>Utf8JsonReaderTests.ValueTextEquals.cs</c>:
/// <c>jsonChars.CopyTo(lookupChars)</c> where <c>jsonChars</c> is
/// <c>char[]</c> but the call binds to an extension method (receiver implicitly
/// converted to a span), not to <c>Array.CopyTo(Array, int)</code>. The rewriter
/// matched on the receiver's static array type + method name only and indexed
/// <c>visitedArgs[1]</c> for the 1-arg call, throwing IndexOutOfRangeException
/// and aborting the whole array-method pass.
/// </summary>
public class ArrayMethodInstanceCopyToReproTests
{
    private static readonly string Source = """
        using System;

        class C
        {
            static void M()
            {
                var jsonChars = new char[320];
                Span<char> lookupChars = new char[jsonChars.Length];
                jsonChars.CopyTo(lookupChars);
            }
        }
        """;

    [Fact]
    public void ArrayReceiver_CopyToExtensionCall_IsNotTreatedAsArrayInstanceMethod()
    {
        var tree = (SyntaxTree)CSharpSyntaxTree.ParseText(Source);
        var compilation = CSharpCompilation.Create(
            "array-copyto-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var inputErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(inputErrors.Count == 0,
            "test input failed to compile:\n" + string.Join("\n", inputErrors));

        var result = new ArrayMethodRewriter().Rewrite(
            Source, compilation.GetSemanticModel(tree));

        Assert.False(result.Changed);
        Assert.Contains("jsonChars.CopyTo(lookupChars)", result.OutputCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ArrayHelper.CopyTo", result.OutputCode,
            StringComparison.Ordinal);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }
}
