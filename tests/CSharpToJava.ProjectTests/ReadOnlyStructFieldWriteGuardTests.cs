using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Guards for structs whose instance fields are written through a copy of the
/// struct type (local variable, parameter, element, ...) instead of through
/// <c>this</c>. Marking the fields readonly would turn those writes into
/// CS0191 (GraphLayout's desugared <c>OverlappedEdge.Create</c> factory).
/// </summary>
public class ReadOnlyStructFieldWriteGuardTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

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

    private static (ReadOnlyStructMakerResult Result, string Output) RunMaker(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "readonly-struct-field-write-guard",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var result = new ReadOnlyStructMaker()
            .MakeReadOnly(tree, compilation.GetSemanticModel(tree));
        return (result, result.OutputCode ?? source);
    }

    private static void AssertOutputCompiles(string output)
    {
        var errors = CSharpCompilation.Create(
                "readonly-struct-field-write-guard-output",
                new[] { CSharpSyntaxTree.ParseText(output, ParseOptions) },
                GetReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten output failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    [Fact]
    public void ReadOnlyStruct_FieldWrittenThroughLocalCopy_IsNotConverted()
    {
        // Expression-desugar expands `new OverlappedEdge { source = ... }` into a
        // local + field writes. Making the struct readonly here is CS0191.
        var src = """
        namespace App;
        public struct OverlappedEdge
        {
            public int source;
            public int target;
            public double weight;

            public static OverlappedEdge Create(int source, int target, double weight)
            {
                var tmp = new OverlappedEdge();
                tmp.source = source;
                tmp.target = target;
                tmp.weight = weight;
                return tmp;
            }
        }
        """;
        var (result, output) = RunMaker(src);

        Assert.Contains(result.Diagnostics, d =>
            d.Level is ConversionLevel.Skip or ConversionLevel.NotConvertible);
        Assert.DoesNotContain("readonly struct", output);
        Assert.DoesNotContain("readonly int", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ReadOnlyStruct_FieldsOnlyAssignedInConstructor_StillConverts()
    {
        var src = """
        namespace App;
        public struct Pair
        {
            public int X;
            public int Y;

            public Pair(int x, int y)
            {
                this.X = x;
                this.Y = y;
            }

            public int Sum => X + Y;
        }
        """;
        var (result, output) = RunMaker(src);

        Assert.Contains("readonly struct Pair", output);
        Assert.Contains("public readonly int X;", output);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
        AssertOutputCompiles(output);
    }
}
