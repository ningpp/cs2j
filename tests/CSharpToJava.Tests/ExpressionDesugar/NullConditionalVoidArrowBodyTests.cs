using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.NullConditional;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests.ExpressionDesugar;

/// <summary>
/// rt10 System.Text.Json regression (C35 void chain in an expression-bodied
/// member):
/// <code>
/// internal static void ClearMemberAccessorCaches() => s_memberAccessor?.Clear();
/// private void VerifyMutable() => DeclaringTypeInfo?.VerifyMutable();
/// protected override void OnCollectionModifying() => _parent.DeclaringTypeInfo?.VerifyMutable();
/// </code>
/// A void <c>?.</c> site previously needed an
/// <see cref="ExpressionStatementSyntax"/> anchor, so every expression-bodied
/// void member was preserved with CS2J0558 and the strict pass aborted. The
/// anchor is now the <see cref="ArrowExpressionClauseSyntax"/> itself: the
/// member is converted to a block body and the lowered guard chain becomes its
/// statements (the receiver is still evaluated at most once through the
/// existing segment-temp mechanism).
/// </summary>
public sealed class NullConditionalVoidArrowBodyTests
{
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview);

    private static readonly IReadOnlyList<MetadataReference> References =
        LoadReferences();

    private static IReadOnlyList<MetadataReference> LoadReferences()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
                  ?? string.Empty;
        return tpa.Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
    }

    private const string MethodSource = """
        namespace C35VoidArrow;

        public sealed class Cache
        {
            public void Clear() { }
        }

        public static class Holder
        {
            private static Cache? s_cache;

            internal static void ClearMemberAccessorCaches() => s_cache?.Clear();
        }
        """;

    private const string SetAccessorSource = """
        namespace C35VoidArrow;

        public sealed class Target
        {
            public void Verify() { }
        }

        public sealed class Widget
        {
            private Target? _target;

            public Target? Value
            {
                get => _target;
                set => _target?.Verify();
            }
        }
        """;

    private const string LocalFunctionSource = """
        namespace C35VoidArrow;

        public sealed class Target
        {
            public void Verify() { }
        }

        public static class Runner
        {
            public static void Run(Target? target)
            {
                void VerifyLocal() => target?.Verify();
                VerifyLocal();
            }
        }
        """;

    [Fact]
    public void ArrowBodiedVoidMethod_IsConvertedToBlock_AndCompiles()
    {
        var result = Preprocess(MethodSource);

        AssertNoVoidAnchorDiagnostics(result);
        Assert.True(result.Changed, Join(result));
        Assert.True(
            result.OutputCode.Contains("if (__ed_s0 != null)", StringComparison.Ordinal),
            result.OutputCode);
        Assert.DoesNotContain("?.", result.OutputCode, StringComparison.Ordinal);
        Assert.Equal(1, result.Statistics.NullConditional.VoidStatementSites);
        AssertBlockBodiedMethod(result.OutputCode, "ClearMemberAccessorCaches");
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void ArrowBodiedSetAccessor_IsConvertedToBlock_AndCompiles()
    {
        var result = Preprocess(SetAccessorSource);

        AssertNoVoidAnchorDiagnostics(result);
        Assert.True(result.Changed, Join(result));
        Assert.DoesNotContain("?.", result.OutputCode, StringComparison.Ordinal);
        Assert.Equal(1, result.Statistics.NullConditional.VoidStatementSites);

        var property = CSharpSyntaxTree.ParseText(result.OutputCode, ParseOptions)
            .GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.Text == "Value");
        var setter = property.AccessorList!.Accessors
            .Single(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.Null(setter.ExpressionBody);
        Assert.NotNull(setter.Body);
        Assert.Contains(
            setter.Body!.Statements.OfType<IfStatementSyntax>(),
            statement => statement.Condition.ToString().Contains(
                "!= null", StringComparison.Ordinal));

        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void ArrowBodiedLocalFunction_IsConvertedToBlock_AndCompiles()
    {
        var result = Preprocess(LocalFunctionSource);

        AssertNoVoidAnchorDiagnostics(result);
        Assert.True(result.Changed, Join(result));
        Assert.DoesNotContain("?.", result.OutputCode, StringComparison.Ordinal);

        var local = CSharpSyntaxTree.ParseText(result.OutputCode, ParseOptions)
            .GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<LocalFunctionStatementSyntax>()
            .Single(l => l.Identifier.Text == "VerifyLocal");
        Assert.Null(local.ExpressionBody);
        Assert.NotNull(local.Body);

        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void ArrowBodiedVoidMethod_PropertyReceiver_IsEvaluatedOnce()
    {
        const string source = """
            namespace C35VoidArrow;

            public sealed class Target
            {
                public void Verify() { }
            }

            public sealed class Holder
            {
                public Target? Current { get; set; }

                public void VerifyMutable() => Current?.Verify();
            }
            """;

        var result = Preprocess(source);

        AssertNoVoidAnchorDiagnostics(result);
        Assert.True(result.Changed, Join(result));

        // The getter is a non-whitelist receiver: exactly one temp read.
        var normalized = string.Join(
            " ", result.OutputCode.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("var __ed_s0 = Current;", normalized, StringComparison.Ordinal);
        Assert.Contains("if (__ed_s0 != null)", normalized, StringComparison.Ordinal);
        Assert.Contains("__ed_s0.Verify()", normalized, StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(result.OutputCode, "Current",
                "public void VerifyMutable"));

        AssertOutputCompiles(result.OutputCode);
    }

    private static void AssertNoVoidAnchorDiagnostics(ExpressionDesugarResult result)
    {
        Assert.DoesNotContain(
            NullConditionalDiagnosticCodes.CS2J0558,
            result.Diagnostics.Select(d => d.Code));
        Assert.DoesNotContain(
            NullConditionalDiagnosticCodes.CS2J0557,
            result.Diagnostics.Select(d => d.Code));
    }

    private static void AssertBlockBodiedMethod(string output, string name)
    {
        var method = CSharpSyntaxTree.ParseText(output, ParseOptions)
            .GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == name);
        Assert.Null(method.ExpressionBody);
        Assert.NotNull(method.Body);
    }

    /// <summary>Counts <paramref name="needle"/> after the marker (the member body only).</summary>
    private static int CountOccurrences(string output, string needle, string marker)
    {
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return 0;

        var count = 0;
        for (var index = start;
             (index = output.IndexOf(needle, index, StringComparison.Ordinal)) >= 0;
             index += needle.Length)
        {
            count++;
        }

        return count;
    }

    private static string Join(ExpressionDesugarResult result)
        => string.Join("; ", result.Diagnostics.Select(d => d.ToMsBuildString()));

    private static ExpressionDesugarResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "c35-void-arrow-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var model = compilation.GetSemanticModel(tree);
        return new ExpressionDesugarPreprocessor().Preprocess(
            source,
            model,
            new ExpressionDesugarOptions
            {
                NullConditional = new NullConditionalOptions { Strict = true },
            });
    }

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "c35-void-arrow-out-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            "preprocessed output does not compile: " +
            string.Join("; ", errors.Select(e => e.ToString())));
    }
}
