using System.IO;
using CSharpToJava.Core.IteratorLowering;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.IteratorLowering;

/// <summary>
/// Repro for SDK json <c>CollectionTests.Dictionary.cs</c>: an iterator
/// (<c>IEnumerator&lt;T&gt; GetEnumerator()</c> with <c>yield return</c>)
/// declared in a NESTED generic class. The rewriter used to replace the outer
/// type first, then lost the stale nested-type node instances and silently
/// left the yield behind (1 residual yield, failing the whole pass).
/// </summary>
public class NestedTypeIteratorLoweringTests
{
    private const string Source = """
        using System.Collections;
        using System.Collections.Generic;

        namespace Lib
        {
            public class Outer
            {
                private class Inner<T> : IEnumerable<T>
                {
                    private readonly List<T> _inner = new List<T>();

                    public IEnumerator<T> GetEnumerator()
                    {
                        foreach (T item in _inner)
                        {
                            yield return item;
                        }
                    }

                    IEnumerator IEnumerable.GetEnumerator()
                    {
                        throw new System.NotImplementedException();
                    }
                }
            }
        }
        """;

    [Fact]
    public void Iterator_InNestedType_IsLowered_WithoutResidualYield()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(Source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "il-nested-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var inputErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(inputErrors.Count == 0,
            "test input failed to compile:\n" + string.Join("\n", inputErrors));

        var preprocessor = new IteratorLoweringPreprocessor();
        var result = preprocessor.Preprocess(
            Source, compilation.GetSemanticModel(tree), new IteratorLoweringOptions());

        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.DoesNotContain("yield return", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == IteratorLoweringSeverity.Error);

        var verify = CSharpCompilation.Create(
            "il-nested-emit-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(result.OutputCode, parseOptions) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = verify.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + result.OutputCode);
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
