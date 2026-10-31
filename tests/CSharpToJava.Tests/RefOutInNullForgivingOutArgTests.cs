using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Repro for SDK json <c>JsonReaderHelper.Unescaping.cs</c>:
/// <c>TryDecodeBase64InPlace(utf8Unescaped, out bytes!)</c> passes a
/// null-forgiving argument. The statement lifter only peeled parentheses,
/// so the postfix <c>!</c> fell through to the unsupported-shape branch and
/// failed the whole ref-out-in pass (CS2J3303).
/// </summary>
public class RefOutInNullForgivingOutArgTests
{
    private const string Source = """
        class C
        {
            static bool TryDecode(byte[] data, out byte[]? bytes)
            {
                bytes = data;
                return true;
            }

            static void M(byte[] data)
            {
                byte[]? bytes = null;
                if (TryDecode(data, out bytes!))
                {
                    System.Console.WriteLine(bytes!.Length);
                }
            }
        }
        """;

    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public sealed class ByRef<T>
            {
                public T Value;
                public static ByRef<T> Create() => new ByRef<T>();
                public static ByRef<T> Create(T value) => new ByRef<T> { Value = value };
                public static int ReadbackInt(ByRef<byte> slot) => slot.Value;
            }
        }
        """;

    [Fact]
    public void OutArgument_WithNullForgivingOperator_IsLiftedAndCompiles()
        => RewriteAndAssertCompiles(Source);

    [Fact]
    public void OutParameter_ForwardedWithNullForgivingOperator_IsLiftedAndCompiles()
        => RewriteAndAssertCompiles(ParameterSource);

    [Fact]
    public void RefArgument_ByRefReturningInvocation_IsLiftedAndCompiles()
        => RewriteAndAssertCompiles(ByRefReturnInvocationSource);

    private const string ParameterSource = """
        class C
        {
            static bool TryDecode(byte[] data, out byte[]? bytes)
            {
                bytes = data;
                return true;
            }

            static bool M(byte[] data, out byte[]? bytes)
            {
                return TryDecode(data, out bytes!);
            }
        }
        """;

    private const string ByRefReturnInvocationSource = """
        using System;
        using System.Runtime.InteropServices;

        class C
        {
            static int IndexOfOrLessThan(ref byte searchSpace, byte value, int length)
                => searchSpace + value + length;

            static int M(ReadOnlySpan<byte> span)
                => IndexOfOrLessThan(ref MemoryMarshal.GetReference(span), 1, span.Length);
        }
        """;

    private static void RewriteAndAssertCompiles(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-null-forgiving-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = new RefOutInRewriter().Rewrite(
            source, compilation.GetSemanticModel(tree));

        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);

        var outputTree = CSharpSyntaxTree.ParseText(result.OutputCode, parseOptions);
        var verify = CSharpCompilation.Create(
            "roi-null-forgiving-emit-" + Guid.NewGuid().ToString("N"),
            new[] { outputTree, CSharpSyntaxTree.ParseText(ByRefSupportStub) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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
