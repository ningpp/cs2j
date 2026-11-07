using CSharpToJava.Core.GenericConstraint;
using CSharpToJava.Core.GenericPreprocess;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// R2/CN: unit tests for the <c>generic-constraint</c> pass. Every rule is
/// exercised on the shape that survives the upstream <c>generic</c> pass;
/// CN-01/03/07 only carry derived assertions (the pass must be a no-op on
/// their rewritten output).
/// </summary>
public class GenericConstraintPassTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
    };

    private static GenericConstraintPreprocessResult Run(
        string source,
        GenericConstraintOptions? options = null)
        => new GenericConstraintPreprocessor().Preprocess(source, options);

    private static void AssertCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "gc-verify",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    // ── CN-04: T? with a struct constraint ──────────────────────────────

    [Fact]
    public void CN04_NullableValueTypeParameter_ExplicitizedToNullableT()
    {
        var result = Run("""
            class C
            {
                public T? Echo<T>(T? value) where T : struct => value;
            }
            """, new GenericConstraintOptions());

        Assert.True(result.Changed);
        Assert.Contains("global::System.Nullable<T>", result.OutputCode);
        Assert.DoesNotContain("T?", result.OutputCode);
        Assert.Equal(2, result.Statistics.Rewritten[GenericConstraintRule.CN04]);
        Assert.Empty(result.Diagnostics);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void CN04_NullComparison_CascadesToHasValue()
    {
        var result = Run("""
            class C
            {
                public bool Has<T>(T? value) where T : struct => value != null;
                public bool Missing<T>(T? value) where T : struct => value == null;
            }
            """);

        Assert.True(result.Changed);
        Assert.Contains("value.HasValue", result.OutputCode);
        Assert.Contains("!value.HasValue", result.OutputCode);
        Assert.DoesNotContain("== null", result.OutputCode);
        Assert.DoesNotContain("!= null", result.OutputCode);
        Assert.Equal(4, result.Statistics.Rewritten[GenericConstraintRule.CN04]);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void CN04_NullPattern_CascadesToHasValue()
    {
        var result = Run("""
            class C
            {
                public bool Has<T>(T? value) where T : struct => value is not null;
                public bool Missing<T>(T? value) where T : struct => value is null;
            }
            """);

        Assert.True(result.Changed);
        Assert.Contains("value.HasValue", result.OutputCode);
        Assert.Contains("!value.HasValue", result.OutputCode);
        Assert.DoesNotContain("is not null", result.OutputCode);
        Assert.DoesNotContain("is null", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void CN04_ReferenceConstrainedNullableParameter_IsPreservedWithoutDiagnostics()
    {
        var result = Run("""
            class C
            {
                public T? Echo<T>(T? value) where T : class => value;
            }
            """);

        Assert.False(result.Changed);
        Assert.Empty(result.Diagnostics);
        Assert.Contains("T?", result.OutputCode);
        Assert.Equal(2, result.Statistics.Preserved[GenericConstraintRule.CN05]);
    }

    // ── CN-02 / CN-06: preserved without diagnostics ───────────────────

    [Fact]
    public void CN02_DefaultOfReferenceParameter_IsPreserved()
    {
        var result = Run("""
            class C
            {
                public T Zero<T>() => default(T);
            }
            """);

        Assert.False(result.Changed);
        Assert.Empty(result.Diagnostics);
        Assert.Contains("default(T)", result.OutputCode);
        Assert.Equal(1, result.Statistics.Preserved[GenericConstraintRule.CN02]);
    }

    [Fact]
    public void CN06_ValueTypeBoxing_KeepsTheExplicitCast()
    {
        var result = Run("""
            class C
            {
                public object Box<T>(T value) where T : struct => (object)value;
            }
            """);

        Assert.False(result.Changed);
        Assert.Empty(result.Diagnostics);
        Assert.Contains("(object)value", result.OutputCode);
        Assert.Equal(1, result.Statistics.Preserved[GenericConstraintRule.CN06]);
    }

    // ── CN-08: multi-dimensional / jagged type-parameter arrays ────────

    [Theory]
    [InlineData("new T[n, n]")]
    [InlineData("new T[n][]")]
    public void CN08_MultiDimensionalOrJaggedArray_IsDeclaredUnsupported(string creation)
    {
        var result = Run($$"""
            class C
            {
                public int Length<T>(int n) where T : struct => {{creation}}.Length;
            }
            """);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(
            result.Diagnostics, d => d.Rule == GenericConstraintRule.CN08);
        Assert.Equal(GenericConstraintCodes.DeclaredUnsupported, diagnostic.Code);
        Assert.Equal(1, result.Statistics.Unsupported[GenericConstraintRule.CN08]);
    }

    // ── CN-09: Enum.Parse/GetValues/GetNames<T> ─────────────────────────

    [Fact]
    public void CN09_EnumParse_RewritesToNonGenericOverloadWithTypeof()
    {
        var result = Run("""
            using System;
            class C
            {
                public T Parse<T>(string value) where T : struct, Enum
                    => Enum.Parse<T>(value);
            }
            """);

        Assert.True(result.Changed);
        Assert.Contains("(T)global::System.Enum.Parse(typeof(T), value)", result.OutputCode);
        Assert.DoesNotContain("Enum.Parse<T>", result.OutputCode);
        Assert.Equal(1, result.Statistics.Rewritten[GenericConstraintRule.CN09]);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void CN09_EnumGetValuesAndGetNames_UseNonGenericOverloads()
    {
        var result = Run("""
            using System;
            class C
            {
                public T[] Values<T>() where T : struct, Enum => Enum.GetValues<T>();
                public string[] Names<T>() where T : struct, Enum => Enum.GetNames<T>();
            }
            """);

        Assert.True(result.Changed);
        Assert.Contains("(T[])global::System.Enum.GetValues(typeof(T))", result.OutputCode);
        Assert.Contains("global::System.Enum.GetNames(typeof(T))", result.OutputCode);
        Assert.Equal(2, result.Statistics.Rewritten[GenericConstraintRule.CN09]);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void CN09_EnumTryParse_IsPreservedWithWarning()
    {
        var result = Run("""
            using System;
            class C
            {
                public bool Try<T>(string value, out T parsed) where T : struct, Enum
                    => Enum.TryParse<T>(value, out parsed);
            }
            """);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(
            result.Diagnostics, d => d.Rule == GenericConstraintRule.CN09);
        Assert.Equal(GenericConstraintCodes.ConstraintNotExpressible, diagnostic.Code);
        Assert.Equal(GenericConstraintSeverity.Warning, diagnostic.Severity);
        Assert.Equal(1, result.Statistics.Preserved[GenericConstraintRule.CN09]);
    }

    // ── CN-10: Delegate.Combine/Remove<T> ───────────────────────────────

    [Fact]
    public void CN10_DelegateCombine_RewritesToNonGenericCallWithCast()
    {
        var result = Run("""
            using System;
            class C
            {
                public T Join<T>(T left, T right) where T : Delegate
                    => Delegate.Combine<T>(left, right);
            }
            """);

        Assert.True(result.Changed);
        Assert.Contains("(T)global::System.Delegate.Combine(left, right)", result.OutputCode);
        Assert.DoesNotContain("Combine<T>", result.OutputCode);
        Assert.Equal(1, result.Statistics.Rewritten[GenericConstraintRule.CN10]);
        AssertCompiles(result.OutputCode);
    }

    // ── CN-11 / CN-12: unsupported / unexpressible constraints ──────────

    [Fact]
    public void CN11_EnumConstraint_IsDeclaredUnsupportedAndPreserved()
    {
        var result = Run("""
            using System;
            class C
            {
                public string Describe<T>(T value) where T : Enum => value.ToString();
            }
            """);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(
            result.Diagnostics, d => d.Rule == GenericConstraintRule.CN11);
        Assert.Equal(GenericConstraintCodes.DeclaredUnsupported, diagnostic.Code);
        Assert.Equal(1, result.Statistics.Unsupported[GenericConstraintRule.CN11]);
        Assert.Contains("where T : Enum", result.OutputCode);
    }

    [Fact]
    public void CN12_UnexpressibleConstraintWithoutDependentSite_IsPreservedWithWarning()
    {
        var result = Run("""
            class C
            {
                public int Count<T>() where T : new() => 0;
            }
            """);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(
            result.Diagnostics, d => d.Rule == GenericConstraintRule.CN12);
        Assert.Equal(GenericConstraintCodes.ConstraintNotExpressible, diagnostic.Code);
        Assert.Equal(GenericConstraintSeverity.Warning, diagnostic.Severity);
        Assert.Contains("where T : new()", result.OutputCode);
    }

    [Fact]
    public void CN12_DependentSite_SuppressesTheConstraintWarning()
    {
        var result = Run("""
            class C
            {
                public T Create<T>() where T : new() => new T();
            }
            """);

        Assert.False(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Rule == GenericConstraintRule.CN12);
    }

    // ── CN-01/03/07 derived: the pass is a no-op on generic's output ────

    [Fact]
    public void GenericPassOutput_IsNotRewrittenAgain()
    {
        var genericSource = """
            class C
            {
                public T Create<T>() where T : new() => new T();
                public T Zero<T>() where T : struct => default(T);
                public T[] Allocate<T>(int n) where T : struct => new T[n];
            }
            """;
        var generic = new GenericPreprocessor().Preprocess(genericSource, semanticModel: null);
        Assert.True(generic.Changed);

        var result = new GenericConstraintPreprocessor().Preprocess(generic.OutputCode);

        Assert.False(result.Changed);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new T[n]", result.OutputCode, StringComparison.Ordinal);
    }

    // ── Idempotence / summary ───────────────────────────────────────────

    [Fact]
    public void SecondRun_IsAFixedPoint()
    {
        var first = Run("""
            using System;
            class C
            {
                public T? Echo<T>(T? value) where T : struct => value;
                public bool Has<T>(T? value) where T : struct => value != null;
                public T[] Values<T>() where T : struct, Enum => Enum.GetValues<T>();
            }
            """);
        Assert.True(first.Changed);

        var second = new GenericConstraintPreprocessor().Preprocess(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Equal(0, second.Statistics.Rewritten.Values.Sum());
    }

    [Fact]
    public void SummaryLine_CarriesTheGenericConstraintPrefixAndPerRuleCounters()
    {
        var result = Run("""
            class C
            {
                public int Count<T>() where T : new() => 0;
            }
            """);

        var summary = result.Statistics.SummaryLine();

        Assert.StartsWith("generic-constraint:", summary, StringComparison.Ordinal);
        Assert.Contains("CN12 1/0/1/0", summary);
    }
}
