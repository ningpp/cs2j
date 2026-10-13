using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Initializers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for C24 field/event-field/property initializer lowering
/// (CS2J0538 root fix): anchor-free initializer sites are lowered into a
/// synthesized <c>private static __cs2j_init_&lt;name&gt;()</c> builder method on
/// the enclosing type and the initializer RHS becomes the builder call.
/// The emitted C# must compile cleanly and must carry no residual
/// object/collection initializer syntax (CS2J0545 gate stays closed).
/// </summary>
public class FieldInitializerBuilderDesugarTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.IEnumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private static ExpressionDesugarResult RunC24(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c24-field-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        var model = compilation.GetSemanticModel(tree);
        Assert.Empty(compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
        return new ExpressionDesugarPreprocessor().Preprocess(
            source, model,
            new ExpressionDesugarOptions { Initializers = new InitializerOptions() });
    }

    private static void AssertStrictSuccess(ExpressionDesugarResult result)
    {
        var errors = string.Join("\n",
            result.Diagnostics.Where(d => d.Severity == ExpressionDesugarSeverity.Error));
        Assert.True(result.Changed, "file should be rewritten. Diagnostics:\n" + errors);
        Assert.Equal("", errors);
        Assert.DoesNotContain("CS2J0545", result.OutputCode, StringComparison.Ordinal);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    private static string Squish(string code)
        => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " ");

    private static string CompileErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c24-field-out-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        return string.Join("\n", compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void StaticCollectionField_LowersToPrivateStaticBuilder()
    {
        const string source = """
            using System.Collections.Generic;
            public class SR
            {
                private static readonly Dictionary<string, string> Map = new Dictionary<string, string>
                {
                    ["a"] = "b",
                    ["c"] = "d",
                };
            }
            """;

        var result = RunC24(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        Assert.Contains(
            "private static readonly Dictionary<string, string> Map = __cs2j_init_Map();",
            code);
        Assert.Contains(
            "private static Dictionary<string, string> __cs2j_init_Map()",
            code);
        Assert.Contains("__ed_0[\"a\"] = \"b\";", code);
        Assert.Contains("return __ed_0;", code);
        // No initializer syntax survives on the field site.
        Assert.DoesNotContain("new Dictionary<string, string> {", code);
    }

    [Fact]
    public void BuilderMethod_PreservesConstructorArguments()
    {
        const string source = """
            using System.Collections.Generic;
            public class C
            {
                private const int Capacity = 38;
                private static readonly Dictionary<string, string> Categories =
                    new Dictionary<string, string>(Capacity) { ["x"] = "y" };
            }
            """;

        var result = RunC24(source);
        AssertStrictSuccess(result);

        Assert.Contains(
            "new Dictionary<string, string>(Capacity);",
            result.OutputCode);
    }

    [Fact]
    public void PropertyInitializer_LowersToBuilder()
    {
        const string source = """
            using System.Collections.Generic;
            public class Token { public int NodeType { get; set; } }
            public class C
            {
                public Token First { get; set; } = new Token { NodeType = 7 };
            }
            """;

        var result = RunC24(source);
        AssertStrictSuccess(result);

        Assert.Contains(
            "public Token First { get; set; } = __cs2j_init_First();",
            result.OutputCode);
        Assert.Contains(
            "private static Token __cs2j_init_First()",
            result.OutputCode);
    }

    [Fact]
    public void InstanceField_UsesStaticBuilder()
    {
        // Field initializers cannot reference instance members (CS0236), so a
        // static builder method is always semantically sufficient.
        const string source = """
            using System.Collections.Generic;
            public class Token { public int A { get; set; } }
            public class C
            {
                private readonly List<Token> _tokens = new List<Token>
                {
                    new Token { A = 1 },
                };
            }
            """;

        var result = RunC24(source);
        AssertStrictSuccess(result);

        Assert.Contains(
            "private readonly List<Token> _tokens = __cs2j_init__tokens();",
            result.OutputCode);
        Assert.Contains(
            "private static List<Token> __cs2j_init__tokens()",
            result.OutputCode);
    }

    [Fact]
    public void NestedObjectCreationInFieldMember_LowersInOneBuilder()
    {
        const string source = """
            public class Inner { public int V { get; set; } }
            public class Outer { public Inner Inner { get; set; } }
            public class C
            {
                private static readonly Outer O = new Outer
                {
                    Inner = new Inner { V = 42 },
                };
            }
            """;

        var result = RunC24(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        // Inner receiver is built before the outer member assignment, all in
        // the same builder method.
        var idxInner = code.IndexOf(
            "var __ed_1 = new Inner();", StringComparison.Ordinal);
        var idxOuter = code.IndexOf(
            "var __ed_0 = new Outer", StringComparison.Ordinal);
        var idxAssign = code.IndexOf(
            "__ed_0.Inner = __ed_1;", StringComparison.Ordinal);
        Assert.True(idxInner >= 0 && idxOuter >= 0 && idxAssign >= 0);
        Assert.True(idxOuter < idxInner);
        Assert.True(idxInner < idxAssign);
    }

    [Fact]
    public void ArgumentPositionSiteInsideFieldInitializer_LowersToSameBuilder()
    {
        const string source = """
            using System.Collections.Generic;
            public class C
            {
                private static int Count<T>(IEnumerable<T> items) => 0;
                private static readonly int N = Count(new List<int> { 1, 2, 3 });
            }
            """;

        var result = RunC24(source);
        AssertStrictSuccess(result);

        Assert.Contains(
            "private static readonly int N = __cs2j_init_N();",
            result.OutputCode);
        Assert.Contains("return Count(__ed_0);", result.OutputCode);
    }

    [Fact]
    public void TernaryBranchSitesInsideFieldInitializer_ShareBuilder()
    {
        const string source = """
            public class Token { public int A { get; set; } }
            public class C
            {
                private static readonly bool Flag = true;
                private static readonly Token T =
                    Flag ? new Token { A = 1 } : new Token { A = 2 };
            }
            """;

        var result = RunC24(source);
        AssertStrictSuccess(result);

        Assert.Contains("__cs2j_init_T()", result.OutputCode);
        Assert.Contains("if (Flag)", result.OutputCode);
    }

    [Fact]
    public void BuilderMethodName_DeduplicatesAgainstExistingMembers()
    {
        const string source = """
            public class Token { public int A { get; set; } }
            public class C
            {
                private static readonly Token Data = new Token { A = 1 };

                private static Token __cs2j_init_Data() => null;
            }
            """;

        var result = RunC24(source);
        AssertStrictSuccess(result);

        Assert.Contains("__cs2j_init_Data_2()", result.OutputCode);
    }

    [Fact]
    public void SiteInsideLambdaBodyInFieldInitializer_UsesStatementLiftingNotBuilder()
    {
        const string source = """
            using System;
            public class Token { public int A { get; set; } }
            public class C
            {
                private static readonly Action Callback = () =>
                {
                    var t = new Token { A = 1 };
                    Console.WriteLine(t);
                };
            }
            """;

        var result = RunC24(source);
        AssertStrictSuccess(result);

        // No builder method is synthesized for the statement-anchored site.
        Assert.DoesNotContain("__cs2j_init_", result.OutputCode);
    }
}
