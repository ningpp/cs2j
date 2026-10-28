using CSharpToJava.Core.ClosedMetadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Regression for the D6 ownership rule on cross-project generic arguments
/// (GraphLayout): the closed construction <c>Set&lt;Edge&gt;</c> is declared in
/// the MSAGL project but its type argument <c>Edge</c> lives in Drawing, which
/// MSAGL does not reference. Owning the registration by the declaration project
/// emits an unnameable <c>typeof(...)</c> there (CS0234/CS0400). Ownership must
/// fall back to the discovery site (construction site) project, which by
/// definition can name both the declaration and the argument types.
/// </summary>
public class ClosedMetadataOwnershipFallbackTests
{
    private static readonly MetadataReference[] References = GetReferences().ToArray();

    private const string OuterSource = """
        namespace ProjectA
        {
            public class Outer<T> { public T Value = default!; }
        }
        """;

    private const string SiteSource = """
        namespace ProjectB
        {
            public class BArg { }

            public class Site
            {
                public ProjectA.Outer<BArg> Simple = new();

                // Nested generic: the framework Tuple's own declaration is
                // metadata, but its argument is declared in ProjectB.
                public ProjectA.Outer<System.Tuple<BArg, BArg>> Nested = new();
            }
        }
        """;

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static IEnumerable<SyntaxTree> Trees() => new[]
    {
        CSharpSyntaxTree.ParseText(OuterSource, path: Path.Combine("ProjectA", "Outer.cs")),
        CSharpSyntaxTree.ParseText(SiteSource, path: Path.Combine("ProjectB", "Site.cs")),
    };

    private static CSharpCompilation CreateAnalysisCompilation() =>
        CSharpCompilation.Create(
            "cm-ownership",
            Trees(),
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static bool IsProjectB(string path) => path.Contains("ProjectB", StringComparison.Ordinal);
    private static bool IsProjectA(string path) => path.Contains("ProjectA", StringComparison.Ordinal);

    private static ClosedMetadataOptions CreateOptions() => new()
    {
        ProjectIdentityResolver = path => IsProjectB(path) ? "ProjectB.csproj"
            : IsProjectA(path) ? "ProjectA.csproj"
            : null,
        // ProjectA does not reference ProjectB (the reverse of the GraphLayout
        // Drawing/MSAGL direction does not matter: only the declaration project's
        // own reference closure decides nameability).
        IsProjectVisible = (from, to) => string.Equals(from, to, StringComparison.OrdinalIgnoreCase),
    };

    [Fact]
    public void ClosedType_WithArgumentProjectInvisibleToDeclaringProject_FallsBackToDiscoverySite()
    {
        var collector = new ClosedMetadataCollector(CreateAnalysisCompilation(), CreateOptions());

        var analysis = collector.Analyze();

        var simple = Assert.Single(analysis.ClosedTypes.Where(r =>
            r.TypeText == "global::ProjectA.Outer<global::ProjectB.BArg>"));
        Assert.EndsWith("Site.cs", simple.DeclaringFilePath);

        var nested = Assert.Single(analysis.ClosedTypes.Where(r =>
            r.TypeText == "global::ProjectA.Outer<global::System.Tuple<global::ProjectB.BArg, global::ProjectB.BArg>>"));
        Assert.EndsWith("Site.cs", nested.DeclaringFilePath);
    }

    [Fact]
    public void FallbackRegistrationFile_CompilesInDiscoveryProject()
    {
        // ProjectA is a separate assembly: its emitted metadata is the only way the
        // discovery project can name Outer<T>, exactly like the MSAGL/Drawing split.
        var outerTree = CSharpSyntaxTree.ParseText(OuterSource, path: Path.Combine("ProjectA", "Outer.cs"));
        var outerCompilation = CSharpCompilation.Create(
            "ProjectA",
            new[] { outerTree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var outerStream = new MemoryStream();
        Assert.True(outerCompilation.Emit(outerStream).Success);

        var collector = new ClosedMetadataCollector(CreateAnalysisCompilation(), CreateOptions());
        var analysis = collector.Analyze();

        var simple = Assert.Single(analysis.ClosedTypes.Where(r =>
            r.TypeText == "global::ProjectA.Outer<global::ProjectB.BArg>"));
        Assert.EndsWith("Site.cs", simple.DeclaringFilePath);

        // Emit into the project the ownership rule chose: the discovery site
        // (ProjectB). Pre-fix this is ProjectA, where BArg is not nameable.
        var ownerProject = simple.DeclaringFilePath.Contains("ProjectB", StringComparison.Ordinal)
            ? "ProjectB"
            : "ProjectA";
        var registration = ClosedMetadataRegistrationGenerator.Generate(ownerProject, new[] { simple });

        var siteTree = CSharpSyntaxTree.ParseText(SiteSource, path: Path.Combine("ProjectB", "Site.cs"));
        var verification = CSharpCompilation.Create(
            "cm-ownership-verify",
            new[] { siteTree, CSharpSyntaxTree.ParseText(registration, path: "_Cs2jClosedTypes.ProjectB.cs") },
            References.Append(MetadataReference.CreateFromImage(outerStream.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = verification.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);
    }
}
