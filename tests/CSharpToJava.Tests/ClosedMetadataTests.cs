using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.ClosedMetadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

public class ClosedMetadataTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
    };

    private static (ClosedMetadataAnalysis Analysis, ClosedMetadataPreprocessor Preprocessor) RunDetailed(
        string source,
        ClosedMetadataOptions? options = null,
        string fileName = "Test.cs")
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: fileName);
        var compilation = CSharpCompilation.Create("cm-test",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new ClosedMetadataPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(options ?? new ClosedMetadataOptions());
        return (preprocessor.Analysis, preprocessor);
    }

    private static void AssertCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("cm-verify",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    private static string GeneratedText(ClosedMetadataAnalysis analysis, ClosedMetadataPreprocessor preprocessor, string projectName = "P")
    {
        var files = preprocessor.GenerateRegistrationFiles(
            new Dictionary<string, IReadOnlyList<ClosedTypeRecord>> { [projectName] = analysis.ClosedTypes });
        return files.Single().Content;
    }

    // ---- CM-ENUM-1: declared closures ----

    [Fact]
    public void DeclaredClosures_FieldPropertyAndDictionary()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            public class Dto {
                public List<List<short>> Matrix;
                public Dictionary<string, List<int>> Index { get; set; }
            }
            """);

        Assert.Equal(4, analysis.ClosedTypes.Count);
        Assert.Contains(analysis.ClosedTypes, r =>
            r.TypeText == "global::System.Collections.Generic.List<global::System.Collections.Generic.List<short>>");
        Assert.Contains(analysis.ClosedTypes, r =>
            r.TypeText == "global::System.Collections.Generic.List<short>");
        Assert.Contains(analysis.ClosedTypes, r =>
            r.TypeText == "global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.List<int>>");
        Assert.Contains(analysis.ClosedTypes, r =>
            r.TypeText == "global::System.Collections.Generic.List<int>");
        Assert.Equal(4, analysis.ClosedTypes.Count);
        Assert.All(analysis.ClosedTypes, r => Assert.Equal(ClosedMetadataCollector.DeclaredSitesRule, r.SourceRule));
        Assert.Equal(4, analysis.Statistics.ClosedTypesEnumerated);
        Assert.Equal(0, analysis.Statistics.Deduplicated);

        // CM-REG: records are ordered by the normalized key, never by enumeration order.
        var sortTexts = analysis.ClosedTypes.Select(r => r.Key.SortText).ToList();
        Assert.Equal(sortTexts.OrderBy(t => t, StringComparer.Ordinal).ToList(), sortTexts);
    }

    [Fact]
    public void ThreeLevelNesting_RegistersEveryLayer()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            public class Dto { public List<List<List<short>>> Deep; }
            """);

        Assert.Equal(3, analysis.ClosedTypes.Count);
        Assert.Contains(analysis.ClosedTypes, r => r.TypeText.Contains("List<global::System.Collections.Generic.List<global::System.Collections.Generic.List<short>>>"));
        Assert.Contains(analysis.ClosedTypes, r => r.TypeText == "global::System.Collections.Generic.List<global::System.Collections.Generic.List<short>>");
        Assert.Contains(analysis.ClosedTypes, r => r.TypeText == "global::System.Collections.Generic.List<short>");
    }

    [Fact]
    public void SharedClosure_AcrossMembers_IsDeduplicated()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            public class A { public List<List<short>> Values; }
            public class B { public List<List<short>> Values; }
            """);

        Assert.Equal(2, analysis.ClosedTypes.Count);
        Assert.Equal(4, analysis.Statistics.ClosedTypesEnumerated);
        Assert.Equal(2, analysis.Statistics.Deduplicated);
    }

    [Fact]
    public void ArrayAndNullableShapes_RegisterWithShapeMarkers()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            public class Dto {
                public List<short>[] Rows;
                public List<short?> Maybe;
                public Dictionary<string, List<int>>[] Indexed;
            }
            """);

        // List<short>[] contributes the array closure + its element closure;
        // List<short?> and Dictionary<string, List<int>>[] contribute theirs. The
        // Nullable<short> argument keeps its Annotated marker ("?") in the key.
        Assert.Contains(analysis.ClosedTypes, r => r.Key.SortText.Contains("List<T>(short)[0]"));
        Assert.Contains(analysis.ClosedTypes, r => r.Key.SortText.Contains("Nullable<T>(short)?"));
        Assert.Equal(2, analysis.ClosedTypes.Count(r => r.Key.SortText.EndsWith("[0]", StringComparison.Ordinal)));
        Assert.True(analysis.ClosedTypes.Count >= 5, $"expected >=5 records, got {analysis.ClosedTypes.Count}: {string.Join("|", analysis.ClosedTypes.Select(r => r.Key.SortText))}");
    }

    // ---- CM-ENUM-2: explicit typeof sites (M3 scope) ----

    [Fact]
    public void ExplicitTypeOfSite_IsEnumerated()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            public class C {
                public object Make() { return typeof(List<List<short>>); }
            }
            """);

        Assert.Equal(2, analysis.ClosedTypes.Count);
        Assert.All(analysis.ClosedTypes, r => Assert.Equal(ClosedMetadataCollector.ExplicitTypeOfRule, r.SourceRule));
    }

    // ---- CM-DYN: dynamic sites ----

    [Fact]
    public void GenericContextTypeArgument_ReportsCS2J1210_AndSkips()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            public class Bag<T> { public List<T> Items; }
            """);

        Assert.Empty(analysis.ClosedTypes);
        Assert.Equal(1, analysis.Statistics.DynamicSitesSkipped);
        Assert.Contains(analysis.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.DynamicClosedTypeSite
            && d.Severity == ClosedMetadataSeverity.Info);
    }

    [Fact]
    public void DynamicSite_StrictUpgradesToWarning()
    {
        var (analysis, _) = RunDetailed(
            """
            using System.Collections.Generic;
            public class Bag<T> { public List<T> Items; }
            """,
            new ClosedMetadataOptions { Strict = true });

        Assert.Contains(analysis.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.DynamicClosedTypeSite
            && d.Severity == ClosedMetadataSeverity.Warning);
    }

    [Fact]
    public void PointerShape_ReportsCS2J1203()
    {
        var (analysis, _) = RunDetailed("""
            public unsafe class C { public int* P; }
            """);

        Assert.Contains(analysis.Diagnostics, d => d.Code == ClosedMetadataCodes.JavaTargetUndecidable);
        Assert.Empty(analysis.ClosedTypes);
    }

    // ---- generation form ----

    [Fact]
    public void GeneratedFile_HasBootstrapRegistryAndDeterministicOrder()
    {
        var (analysis, preprocessor) = RunDetailed("""
            using System.Collections.Generic;
            public class Dto {
                public List<List<short>> Matrix;
                public Dictionary<string, List<int>> Index;
            }
            """);

        var text = GeneratedText(analysis, preprocessor);

        Assert.Contains("// <auto-generated> cs2j closed-metadata (C30) closed-type registration for P. Do not edit.", text);
        Assert.Contains("[global::System.Runtime.CompilerServices.CompilerGenerated]", text);
        Assert.Contains("internal static class __Cs2jClosedTypes_P", text);
        Assert.Contains("public static void __cs2j_register_closed_types()", text);
        Assert.Contains("typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<short>>)", text);
        // Idempotent registry: putIfAbsent semantics, lock-guarded.
        Assert.Contains("lock (__cs2j_gate)", text);
        Assert.Contains("if (!__cs2j_registered.ContainsKey(__cs2j_key))", text);

        // Determinism: the same record set always produces the same text, in key order.
        Assert.Equal(text, ClosedMetadataRegistrationGenerator.Generate("P", analysis.ClosedTypes));
        var keyOrder = analysis.ClosedTypes.Select(r => r.Key.SortText).ToList();
        var emittedKeys = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            const string marker = "__cs2j_register_closed(\"";
            var start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0)
            {
                var rest = line[(start + marker.Length)..];
                emittedKeys.Add(rest[..rest.IndexOf('"', StringComparison.Ordinal)]);
            }
        }

        Assert.Equal(keyOrder, emittedKeys);

        // The generated file plus its consumer source must compile standalone.
        AssertCompiles("""
            using System.Collections.Generic;
            public class Dto {
                public List<List<short>> Matrix;
                public Dictionary<string, List<int>> Index;
            }
            """ + text);
    }

    // ---- residual gate (M3 minimal) ----

    [Fact]
    public void ResidualScan_DetectsDuplicateRegistrationsWithinAFile()
    {
        var (_, preprocessor) = RunDetailed("class C { }");
        var files = new[]
        {
            new ClosedMetadataGeneratedFile("P", "F.cs", """
                internal static class X {
                    void M() {
                        __cs2j_register_closed("K", typeof(A));
                        __cs2j_register_closed("K", typeof(A));
                    }
                }
                """),
        };

        var residual = preprocessor.ResidualScan(files);

        Assert.Equal(1, residual.Counts["duplicate-registration"]);
        Assert.Contains(residual.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.ResidualFound
            && d.Severity == ClosedMetadataSeverity.Warning);
    }

    [Fact]
    public void ResidualScan_StrictUpgradesResidualToError()
    {
        var (_, preprocessor) = RunDetailed("class C { }", new ClosedMetadataOptions { Strict = true });
        var files = new[]
        {
            new ClosedMetadataGeneratedFile("P", "F.cs", """
                internal static class X {
                    void M() {
                        __cs2j_register_closed("K", typeof(A));
                    }
                }
                """),
            new ClosedMetadataGeneratedFile("Q", "G.cs", """
                internal static class Y {
                    void M() {
                        __cs2j_register_closed("K", typeof(A));
                    }
                }
                """),
        };

        var residual = preprocessor.ResidualScan(files);

        Assert.Equal(1, residual.Counts["conflicting-registration-key"]);
        Assert.Contains(residual.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.ResidualFound
            && d.Severity == ClosedMetadataSeverity.Error);
    }

    // ---- clean copy contract ----

    [Fact]
    public void RewriteFile_KeepsFilesClean()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { }", path: "Test.cs");
        var compilation = CSharpCompilation.Create("cm-test",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new ClosedMetadataPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ClosedMetadataOptions());

        Assert.Null(preprocessor.RewriteFile(compilation.GetSemanticModel(tree)));
    }

    // ---- csproj wiring (shared GeneratedFileWiring, idempotent) ----

    [Fact]
    public void CsprojWiring_IsIdempotentAcrossRuns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2j-cm-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var projectFile = Path.Combine(dir, "P.csproj");
            File.WriteAllText(projectFile, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                  </ItemGroup>
                </Project>
                """);
            const string fileName = "_Cs2jClosedTypes.P.cs";
            var rootPath = Path.Combine(dir, fileName);

            GeneratedFileWiring.EnsureCompileItem(projectFile, fileName, dir, rootPath);
            var onceCount = CountCompileIncludes(File.ReadAllText(projectFile), fileName);
            GeneratedFileWiring.EnsureCompileItem(projectFile, fileName, dir, rootPath);
            var twiceCount = CountCompileIncludes(File.ReadAllText(projectFile), fileName);

            Assert.Equal(1, onceCount);
            Assert.Equal(onceCount, twiceCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int CountCompileIncludes(string xml, string fileName)
    {
        var document = System.Xml.Linq.XDocument.Parse(xml);
        return document.Descendants()
            .Count(e => e.Name.LocalName == "Compile"
                && ((string?)e.Attribute("Include"))?.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) == true);
    }
}
