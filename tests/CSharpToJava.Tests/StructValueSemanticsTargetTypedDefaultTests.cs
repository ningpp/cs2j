using System.IO;
using CSharpToJava.Core.StructValueSemantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Repro for SDK json <c>JsonDocument.Parse.cs</c> / <c>JsonTypeInfoOfT.ReadHelper.cs</c>:
/// clone 包裹与临时提升会把表达式移出目标类型上下文，使条件/switch 臂内的裸
/// <c>default</c> 失去目标类型（CS8716），须先按原模型展开为 <c>default(T)</c>；
/// 另覆盖「命名实参后接位置实参」的形参映射（曾把 CancellationToken 实参错配到
/// 结构体形参上，产出 <c>cancellationToken.Clone()</c>）。
/// </summary>
public class StructValueSemanticsTargetTypedDefaultTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    [Fact]
    public void SwitchExpressionArgumentWithDefaultArm_ExpandsTargetTypedDefault()
    {
        var (diagnostics, result) = Rewrite("""
            namespace Lib;

            public struct Box
            {
                public int X;
                public void Bump() => X++;
            }

            public static class Caller
            {
                public static void Use(int key, Box source)
                {
                    Consume(key switch { 0 => default, _ => source });
                }

                private static void Consume(Box value) { value.Bump(); }
            }
            """);

        Assert.DoesNotContain(diagnostics,
            d => d.Severity == StructValueSemanticsSeverity.Error);
        Assert.Contains("default(global::Lib.Box)", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Clone()", result.OutputCode, StringComparison.Ordinal);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void ConditionalArgumentWithDefaultArm_ClonesBranchAndCompiles()
    {
        var (diagnostics, result) = Rewrite("""
            namespace Lib;

            public struct Box
            {
                public int X;
                public void Bump() => X++;
            }

            public static class Caller
            {
                public static void Use(bool flag, Box source)
                {
                    Consume(flag ? default : source);
                }

                private static void Consume(Box value) { value.Bump(); }
            }
            """);

        Assert.DoesNotContain(diagnostics,
            d => d.Severity == StructValueSemanticsSeverity.Error);
        Assert.Contains("source.Clone()", result.OutputCode, StringComparison.Ordinal);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void PositionalArgumentAfterNamedArgument_IsNotMistakenForStructParameter()
    {
        var (diagnostics, result) = Rewrite("""
            namespace Lib;

            public struct Box
            {
                public int X;
                public void Bump() => X++;
            }

            public static class Caller
            {
                public static void Use(int token)
                {
                    Consume(state: new Box(), token);
                }

                private static void Consume(Box state, int token) { state.Bump(); }
            }
            """);

        Assert.DoesNotContain(diagnostics,
            d => d.Severity == StructValueSemanticsSeverity.Error);
        Assert.DoesNotContain("token.Clone()", result.OutputCode, StringComparison.Ordinal);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void NullForgivingDefaultLocalInit_IsFreshValueAndNotCloned()
    {
        var (diagnostics, result) = Rewrite("""
            namespace Lib;

            public struct Box
            {
                public int X;
                public void Bump() => X++;
            }

            public static class Caller
            {
                public static void Use(Box source)
                {
                    Box box = default!;
                    Consume(source);
                    Consume(box);
                }

                private static void Consume(Box value) { value.Bump(); }
            }
            """);

        Assert.DoesNotContain(diagnostics,
            d => d.Severity == StructValueSemanticsSeverity.Error);
        Assert.DoesNotContain("default!.Clone()", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("default(global::Lib.Box)!", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("source.Clone()", result.OutputCode, StringComparison.Ordinal);
        AssertCompiles(result.OutputCode);
    }

    private static (List<StructValueSemanticsDiagnostic> Diagnostics,
        StructValueSemanticsFileResult Result) Rewrite(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "svs-target-typed-default-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new StructValueSemanticsPreprocessor(
            new StructValueSemanticsOptions());
        preprocessor.BindCompilation(compilation);

        var statistics = new StructValueSemanticsStatistics();
        var diagnostics = new List<StructValueSemanticsDiagnostic>();
        var result = preprocessor.RewriteFile(
            source, compilation.GetSemanticModel(tree), statistics, diagnostics);
        return (diagnostics, result);
    }

    private static void AssertCompiles(string outputCode)
    {
        var verify = CSharpCompilation.Create(
            "svs-target-typed-default-emit-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(outputCode, ParseOptions) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = verify.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + outputCode);
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
