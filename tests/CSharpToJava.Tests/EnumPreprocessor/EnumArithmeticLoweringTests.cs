using CSharpToJava.Core.EnumPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

// Namespace deliberately differs from the folder name: a
// CSharpToJava.Tests.EnumPreprocessor namespace would shadow the
// EnumPreprocessor type for every existing test in CSharpToJava.Tests.
namespace CSharpToJava.Tests.EnumPreprocessing;

using EnumPreprocessorImpl = CSharpToJava.Core.EnumPreprocessor.EnumPreprocessor;

/// <summary>
/// M2: every source-declared enum's Java-incompatible binary shapes are
/// lowered to explicit underlying-integer operations. Covers byte/explicit
/// value enums (previously skipped by the plain-real-enum gate), enum member
/// initializers, and `is`/`is not` member tests. Output must stay compilable
/// C# and the lowering must be idempotent.
/// </summary>
public sealed class EnumArithmeticLoweringTests
{
    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create("enum-lowering-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private static EnumPreprocessorResult Preprocess(string source)
    {
        var compilation = CreateCompilation(source);
        return new EnumPreprocessorImpl().Preprocess(
            source, compilation.GetSemanticModel(compilation.SyntaxTrees.Single()));
    }

    private static EnumPreprocessorResult PreprocessOutput(string outputCode)
        => Preprocess(outputCode);

    private static string Squish(string code)
        => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " ");

    private static string CompileErrors(string code)
        => string.Join("\n", CreateCompilation(code).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

    // ── 1) byte enum E - E ──

    [Fact]
    public void Byte_Enum_Subtraction_Is_Lowered_To_Byte_Casts()
    {
        var src = """
        enum E : byte { A = 1, B = 2 }
        class C
        {
            bool M(E a, E b) => (a - b) > 0;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        var code = Squish(result.OutputCode);
        Assert.Contains("(byte)a - (byte)b", code);
        Assert.DoesNotContain("a - b", code);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    // ── 2) explicit-value enum member initializer ──

    [Fact]
    public void Explicit_Value_Enum_Member_Initializer_Is_Lowered()
    {
        var src = """
        enum E { A = 1, B = 2, Any = A | B }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        var code = Squish(result.OutputCode);
        Assert.Contains("Any = (int)A | (int)B", code);
        Assert.DoesNotContain("(E)", code);
        Assert.DoesNotContain("unchecked", code);
        Assert.Empty(CompileErrors(result.OutputCode));

        // Any == 3: the lowered initializer is a compile-time constant, so the
        // enum itself compiles and the member value is still 3.
        var compilation = CreateCompilation(result.OutputCode);
        Assert.Empty(compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
        var model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());
        var enumSymbol = compilation.GetTypeByMetadataName("E");
        Assert.NotNull(enumSymbol);
        var any = enumSymbol!.GetMembers("Any").OfType<IFieldSymbol>().Single();
        Assert.Equal(3, any.ConstantValue);
    }

    // ── 3) is / is not member tests ──

    [Fact]
    public void Is_Enum_Member_Is_Lowered_To_Equality()
    {
        var src = """
        enum MyBoolEnum { False, True }
        class C
        {
            bool M(MyBoolEnum value) => value is MyBoolEnum.True;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("value == MyBoolEnum.True", result.OutputCode);
        Assert.DoesNotContain(" is ", result.OutputCode);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Is_Not_Enum_Member_Is_Lowered_To_Not_Equality()
    {
        var src = """
        enum MyBoolEnum { False, True }
        class C
        {
            bool M(MyBoolEnum value) => value is not MyBoolEnum.True;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("value != MyBoolEnum.True", result.OutputCode);
        Assert.DoesNotContain(" is ", result.OutputCode);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Is_Metadata_Enum_Member_Is_Lowered_To_Equality()
    {
        // BCL (metadata) enums are mapped to the compat constant surface, so
        // `is <member>` constant patterns must lower exactly like source enums:
        // the Java gate only accepts same-enum ==/!=.
        var src = """
        using System;
        class C
        {
            bool M(DayOfWeek value) => value is DayOfWeek.Monday;
            bool N(DayOfWeek value) => value is not DayOfWeek.Friday;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("value == DayOfWeek.Monday", result.OutputCode);
        Assert.Contains("value != DayOfWeek.Friday", result.OutputCode);
        Assert.DoesNotContain(" is ", result.OutputCode);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Is_Constant_Pattern_On_NonEnum_Left_Is_Untouched()
    {
        var src = """
        using System;
        class C
        {
            bool TypeTest(object value) => value is DayOfWeek;
            bool ConstantOnObject(object value) => value is DayOfWeek.Monday;
        }
        """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("value is DayOfWeek", result.OutputCode);
        Assert.Contains("value is DayOfWeek.Monday", result.OutputCode);
    }

    [Fact]
    public void Nullable_Enum_Is_Member_Is_Lowered_To_Object_Equals()
    {
        // `E? x; x is E.A` is a constant pattern on a nullable operand,
        // rewritten to object.Equals(x, E.A): single evaluation of x,
        // null-safe (null → false) and Java-rendered as Objects.equals.
        // Lifted `==` would make numeric-boundary demand a hoistable temporary
        // (CS2J5355) in initializer/short-circuit contexts.
        var src = """
        enum E { A, B }
        class C
        {
            bool IsA(E? x) => x is E.A;
            bool IsNotA(E? x) => x is not E.A;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("object.Equals(x, E.A)", result.OutputCode);
        Assert.Contains("!object.Equals(x, E.A)", result.OutputCode);
        Assert.DoesNotContain(" is E.A", result.OutputCode);
        Assert.DoesNotContain(" is not E.A", result.OutputCode);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Nullable_Enum_Is_Member_On_Metadata_Enum_Is_Lowered_Too()
    {
        var src = """
        using System;
        class C
        {
            bool M(DayOfWeek? value) => value is DayOfWeek.Monday;
            bool N(DayOfWeek? value) => value is not DayOfWeek.Friday;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("object.Equals(value, DayOfWeek.Monday)", result.OutputCode);
        Assert.Contains("!object.Equals(value, DayOfWeek.Friday)", result.OutputCode);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Nullable_Enum_Is_Other_Enum_Member_Is_Untouched()
    {
        // Cross-enum constant: the member does not belong to the left operand's
        // enum type, so the site is not a same-enum test and stays untouched.
        var src = """
        enum E { A }
        enum F { A }
        class C
        {
            bool M(object value) => value is F.A;
            bool N(E value) => value is E.A;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("value is F.A", result.OutputCode);
        Assert.Contains("value == E.A", result.OutputCode);
    }

    [Fact]
    public void Is_Enum_Member_Shadowed_By_SameNamed_Namespace_Is_Lowered()
    {
        // The parser keeps the RHS of `x is E.M` in TYPE position. When a
        // same-named namespace is in scope — the WindowsDesktop payload adds a
        // top-level `Accessibility` namespace that shadows
        // Microsoft.CodeAnalysis.Accessibility used by source-generator
        // projects — type binding fails (CS0234) and GetSymbolInfo is empty.
        // The tokens are re-bound speculatively as an expression, which is the
        // only legal reading of a constant pattern.
        var src = """
        using Microsoft.CodeAnalysis;
        namespace Accessibility { }
        class C
        {
            bool M(IMethodSymbol m) => m.DeclaredAccessibility is Accessibility.Public;
        }
        """;
        var tree = CSharpSyntaxTree.ParseText(src, new CSharpParseOptions(LanguageVersion.Latest));
        // Full framework reference set: with a bare object/Console set the
        // metadata enum surfaces as an error type and the left operand cannot
        // be classified at all (CS0012 on Enum/IEquatable).
        var compilation = CSharpCompilation.Create(
            "enum-ambiguous-constant-test",
            new[] { tree },
            CSharpToJava.CLI.ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var result = new EnumPreprocessorImpl().Preprocess(
            src, compilation.GetSemanticModel(tree));

        Assert.True(result.Changed);
        // The shadowed qualifier cannot bind in expression position either, so
        // the emitted member reference is fully qualified.
        Assert.Contains(
            "m.DeclaredAccessibility == global::Microsoft.CodeAnalysis.Accessibility.Public",
            result.OutputCode);
        Assert.DoesNotContain(" is Accessibility.Public", result.OutputCode);
    }

    // ── 4) narrow underlying bitwise / arithmetic ──

    [Fact]
    public void Byte_Enum_Bitwise_Or_Is_Wrapped_Unchecked()
    {
        var src = """
        [System.Flags]
        enum FB : byte { A = 1, B = 2 }
        class C
        {
            FB M() => FB.A | FB.B;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        var code = Squish(result.OutputCode);
        Assert.Contains("unchecked((FB)((byte)FB.A | (byte)FB.B))", code);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Byte_Enum_Subtraction_Wraps_At_Byte_Width()
    {
        // C# E-E for a byte enum yields a byte that wraps at byte width; the
        // lowered form must keep that (not become a plain int subtraction).
        var src = """
        enum FB : byte { A = 1, B = 2 }
        class C
        {
            bool M(FB a, FB b) => (a - b) > 0;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        var code = Squish(result.OutputCode);
        Assert.Contains("unchecked((byte)((byte)a - (byte)b))", code);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    // ── 复合赋值 / E +/- n / n - E ──

    [Fact]
    public void Enum_Compound_Add_And_Subtract_Are_Lowered()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C
        {
            void M(E x)
            {
                x += 2;
                x -= 1;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        var code = Squish(result.OutputCode);
        Assert.Contains("x = ((E)((int)x + 2))", code);
        Assert.Contains("x = ((E)((int)x - 1))", code);
        Assert.DoesNotContain("+=", code);
        Assert.DoesNotContain("-=", code);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Enum_Plus_Int_And_Int_Minus_Enum_Are_Lowered()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C
        {
            E Add(E e) => e + 2;
            E Sub(E e) => 10 - e;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        var code = Squish(result.OutputCode);
        Assert.Contains("(E)((int)e + 2)", code);
        Assert.Contains("(E)(10 - (int)e)", code);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    // ── 5) negative shapes ──

    [Fact]
    public void Same_Enum_Equality_And_Plain_Int_Arithmetic_Are_Untouched()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C
        {
            bool Eq(E a, E b) => a == b;
            int Diff(int a, int b) => a - b;
        }
        """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("a == b", result.OutputCode);
        Assert.Contains("a - b", result.OutputCode);
    }

    // ── 6) idempotence ──

    [Fact]
    public void Second_Pass_Does_Not_Change_Lowered_Output()
    {
        var src = """
        [System.Flags]
        enum FB : byte { A = 1, B = 2, AB = A | B }
        enum E { X = 1, Y = 2, Any = X | Y }
        class C
        {
            bool M(FB a, FB b, E e, MyBoolEnum v)
                => (a - b) > 0 && (e is E.Any) && (v is MyBoolEnum.True);
            FB Or(FB a, FB b) => a | b;
            void Inc(E x) { x += 2; }
        }
        enum MyBoolEnum { False, True }
        """;

        var first = Preprocess(src);
        Assert.True(first.Changed);

        var second = PreprocessOutput(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Empty(CompileErrors(second.OutputCode));
    }
}
