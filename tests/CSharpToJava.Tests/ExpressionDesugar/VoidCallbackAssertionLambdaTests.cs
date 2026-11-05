using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Initializers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.ExpressionDesugar;

/// <summary>
/// MSAGL test-module regression: an object initializer inside an assertion
/// callback lambda is lowered to a <c>__ed_N</c> temp + member assignments.
/// The callback's value is always discarded by the assertion API, so the
/// lowered lambda body must stay void even when the test-framework metadata is
/// absent (delegate type unresolved and the body type would otherwise look
/// value-producing). A <c>Func</c>-style context (LINQ <c>Select</c>) must
/// still keep its <c>return</c>.
/// </summary>
public sealed class VoidCallbackAssertionLambdaTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Array).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    /// <summary>
    /// <c>Assert</c> is deliberately undefined (the preprocessing compilation
    /// has no MSTest reference), so only the syntactic method name can drive
    /// the void decision. The message argument verifies that the lambda does
    /// not have to be the first parameter.
    /// </summary>
    private const string AssertCallbackSource = """
        #pragma warning disable
        using System;

        public sealed class Foo
        {
            public int X { get; set; }
        }

        public static class Sample
        {
            public static void M()
            {
                Assert.ThrowsException<Exception>(() => new Foo { X = 1 }, "message");
            }
        }
        """;

    /// <summary>Assert callback (void) and LINQ Select (value) in one file.</summary>
    private const string AssertAndSelectSource = """
        #pragma warning disable
        using System;
        using System.Collections.Generic;
        using System.Linq;

        public sealed class Foo
        {
            public int X { get; set; }
        }

        public static class Sample
        {
            public static void M(List<int> list)
            {
                Assert.ThrowsException<Exception>(() => new Foo { X = 1 });
                var picked = list.Select(x => new Foo { X = 1 }).ToList();
            }
        }
        """;

    /// <summary>Ordinary Action delegate: still void after initializer lifting.</summary>
    private const string ActionDelegateSource = """
        #pragma warning disable
        using System;

        public sealed class Foo
        {
            public int X { get; set; }
        }

        public static class Sample
        {
            static void Run(Action a) { a(); }

            public static void M()
            {
                Run(() => new Foo { X = 1 });
            }
        }
        """;

    /// <summary>
    /// Discard-assignment tail (<c>() =&gt; _ = obj.Prop</c>) inside an assertion
    /// callback. The initializer makes the desugar pass rewrite this lambda (the
    /// tail then assigns the lifted <c>__ed_N</c> temp to the discard), which is
    /// the void-tail path that used to render <c>_ = …;</c> — Java reserved
    /// <c>_</c>, so it became an undeclared <c>_Value</c>.
    /// </summary>
    private const string DiscardAssignmentSource = """
        #pragma warning disable
        using System;

        public sealed class Foo
        {
            public int X { get; set; }
        }

        public static class Sample
        {
            public static void M()
            {
                Assert.ThrowsException<Exception>(() => _ = new Foo { X = 1 }.X);
            }
        }
        """;

    [Fact]
    public void AssertThrowsExceptionCallback_DoesNotReturnLiftedTemp()
    {
        var result = Run(AssertCallbackSource);

        Assert.True(result.Changed,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("ThrowsException", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("__ed_", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return __ed", result.OutputCode, StringComparison.Ordinal);
        AssertNoBareLiftedTempStatement(result.OutputCode);
    }

    [Fact]
    public void AssertThrowsExceptionCallback_VoidSelectLambda_KeepsReturn()
    {
        var result = Run(AssertAndSelectSource);
        var code = result.OutputCode;

        Assert.True(result.Changed,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        // The assertion callback precedes Select in the source; its lowered
        // block must not return the temp.
        var selectIndex = code.IndexOf("Select", StringComparison.Ordinal);
        Assert.True(selectIndex >= 0, "Select lambda missing:\n" + code);
        var assertPart = code.Substring(0, selectIndex);
        Assert.Contains("ThrowsException", assertPart, StringComparison.Ordinal);
        Assert.DoesNotContain("return __ed", assertPart, StringComparison.Ordinal);

        // Value context keeps the return tail.
        Assert.Contains("return __ed", code, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryActionDelegate_IsVoid()
    {
        var result = Run(ActionDelegateSource);

        Assert.True(result.Changed,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("__ed_", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return __ed", result.OutputCode, StringComparison.Ordinal);
        AssertNoBareLiftedTempStatement(result.OutputCode);
    }

    [Fact]
    public void DiscardAssignmentTail_IsMaterializedAsDiscardTemp()
    {
        var result = Run(DiscardAssignmentSource);
        var code = result.OutputCode;

        Assert.True(result.Changed,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("var __cs2jDiscard", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_ =", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return _", code, StringComparison.Ordinal);
    }

    /// <summary>`__ed_N;` is not a legal C# statement; the tail must be omitted.</summary>
    private static void AssertNoBareLiftedTempStatement(string code)
    {
        foreach (var line in code.Split('\n'))
        {
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(line.Trim(), @"^__ed_\d+;"),
                "Illegal bare lifted temp statement emitted:\n" + code);
        }
    }

    private static ExpressionDesugarResult Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "void-callback-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Disable));
        var model = compilation.GetSemanticModel(tree);
        return new ExpressionDesugarPreprocessor().Preprocess(
            source, model,
            new ExpressionDesugarOptions
            {
                Initializers = new InitializerOptions { Strict = true },
            });
    }
}
