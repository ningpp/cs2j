using System.IO;
using CSharpToJava.Core.EnumDeclaration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.EnumDeclaration;

/// <summary>
/// Repro for the whole-tree enum-declaration pass over a merged multi-project
/// corpus: <c>Common/tests/System/EnumTypes.cs</c> declares
/// <c>System.ByteEnum</c>/<c>System.SByteEnum</c> for one project while
/// <c>SerializationTypes.cs</c> declares same-named enums for another. A file
/// with both <c>using System;</c> and <c>using SerializationTypes;</c> then hits
/// CS0104 in the merged compilation even though each real project compiles
/// unambiguously. The generic type-argument exposure rule (and every use-site
/// rewrite) keys off a resolved symbol, so it sees nothing; the enum is lowered
/// to a class and a real project binds <c>SerializeAndDeserialize&lt;ByteEnum&gt;</c>
/// to that class, which XmlSerializer rejects (not TypeKind.Enum).
///
/// Any enum candidate of an ambiguously resolved identifier in a runtime-type-
/// identity-sensitive position (generic type argument / typeof / System.Enum
/// API) must conservatively stay a real enum (ED020).
/// </summary>
public class EnumDeclarationMergedCompilationAmbiguityTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string SystemEnumSource = """
        namespace System
        {
            public enum ByteEnum : byte
            {
                Zero = 0,
                One = 1
            }
        }
        """;

    private const string SerializationTypesSource = """
        namespace SerializationTypes
        {
            public enum ByteEnum : byte
            {
                Option1 = 1
            }
        }
        """;

    private const string AmbiguousConsumerSource = """
        using System;
        using SerializationTypes;

        public class Consumer
        {
            private static T Ser<T>(T value) => value;

            public void M()
            {
                var x = Ser<ByteEnum>(ByteEnum.Option1);
            }
        }
        """;

    [Fact]
    public void AmbiguousTypeArgument_KeepsEnumReal_ED020()
    {
        var files = new[]
        {
            ("SystemEnum.cs", SystemEnumSource),
            ("SerializationTypes.cs", SerializationTypesSource),
            ("Consumer.cs", AmbiguousConsumerSource),
        };
        var (outputs, diags) = RewriteAll(files);

        Assert.Contains("enum ByteEnum", outputs["SerializationTypes.cs"]);
        Assert.Contains("enum ByteEnum", outputs["SystemEnum.cs"]);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("ambiguous"));
    }

    private static (Dictionary<string, string> Outputs, List<EnumDeclarationDiagnostic> Diags)
        RewriteAll((string Path, string Source)[] files)
    {
        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(f.Source, ParseOptions, path: f.Path))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "ed-merged-ambiguity-repro",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new EnumDeclarationPreprocessor();
        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tree in trees)
        {
            var source = tree.ToString();
            var result = preprocessor.RewriteFile(
                source, compilation.GetSemanticModel(tree), stats, diags);
            outputs[tree.FilePath] = result.OutputCode;
        }
        return (outputs, diags);
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
