using System.IO;
using CSharpToJava.Core.EnumDeclaration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.EnumDeclaration;

/// <summary>
/// Repro for SDK json CustomConverterTests.Enum.cs: is/else-if enum pattern
/// rewrites must hoist their single-evaluation temps so the output compiles.
/// </summary>
public class EnumDeclarationIsPatternTempReproTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string Source = """
        namespace Repro
        {
            public static class CustomConverterTests
            {
                public enum MyBoolEnum
                {
                    True = 1,
                    False = 2,
                    Unknown = 3
                }

                private class MyBoolEnumConverter
                {
                    public void Write(MyBoolEnum value)
                    {
                        if (value is MyBoolEnum.True)
                        {
                            System.Console.WriteLine("TRUE");
                        }
                        else if (value is MyBoolEnum.False)
                        {
                            System.Console.WriteLine("FALSE");
                        }
                        else
                        {
                            System.Console.WriteLine("?");
                        }
                    }
                }

                public static void CustomEnumConverter()
                {
                    MyBoolEnum value = MyBoolEnum.True;
                    System.Console.WriteLine(value);
                }
            }
        }
        """;

    [Fact]
    public void IsPattern_InIfElseIf_OutputCompiles()
    {
        var tree = CSharpSyntaxTree.ParseText(Source, ParseOptions, path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            "ed-is-repro",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new EnumDeclarationPreprocessor();
        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var result = preprocessor.RewriteFile(
            Source, compilation.GetSemanticModel(tree), stats, diags);

        var outputTree = CSharpSyntaxTree.ParseText(
            result.OutputCode, ParseOptions, path: "Rewritten.cs");
        var errors = CSharpCompilation.Create(
                "ed-is-repro-verify",
                new[] { outputTree },
                GetReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true))
            .GetDiagnostics()
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
