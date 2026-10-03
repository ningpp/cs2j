using CSharpToJava.Core.ExtensionMethodResolve;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// M3 Core tests (design §4.2/§5.1/§6): external-metadata host mapping
/// grading (CS2J1203), InCompilationSupport vs InCompilationUser split,
/// single-file CS2J1290 conservative path, and the verbose EM001 detail line.
/// </summary>
public sealed class ExtensionMethodResolveM3Tests
{
    private static readonly MetadataReference[] BaseReferences =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.IEnumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private const string ExternalHostSource = """
        namespace ExtHosts
        {
            public static class StringMetadataExtensions
            {
                public static string ShoutMetadata(this string s) => s.ToUpperInvariant();
            }
        }
        """;

    private const string CallerSource = """
        using ExtHosts;

        namespace App
        {
            public class Program
            {
                public string Run() => "hi".ShoutMetadata();
            }
        }
        """;

    private static string Compact(string code)
        => string.Concat(code.Where(c => !char.IsWhiteSpace(c)));

    private static MetadataReference EmitExternalHostAssembly()
    {
        var hostTree = CSharpSyntaxTree.ParseText(
            ExternalHostSource, new CSharpParseOptions(LanguageVersion.Latest));
        var hostCompilation = CSharpCompilation.Create(
            "em-m3-host-" + Guid.NewGuid().ToString("N"),
            new[] { hostTree },
            BaseReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var dllPath = Path.Combine(Path.GetTempPath(), "em-m3-host-" + Guid.NewGuid().ToString("N") + ".dll");
        var emit = hostCompilation.Emit(dllPath);
        Assert.True(emit.Success,
            "host emit failed: " + string.Join("; ", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())));
        return MetadataReference.CreateFromFile(dllPath);
    }

    private static (CSharpCompilation Compilation, SemanticModel Model, ExtensionFileResult Result)
        RunExternalHost(MetadataReference hostReference, ExtensionMethodResolveOptions options)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            CallerSource, new CSharpParseOptions(LanguageVersion.Latest), path: "Program.cs");
        var compilation = CSharpCompilation.Create(
            "em-m3-app-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            BaseReferences.Append(hostReference),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(options);
        var model = compilation.GetSemanticModel(tree);
        return (compilation, model, preprocessor.RewriteFile(model));
    }

    // ── EM-EXTERNAL / CS2J1203 ────────────────────────────────────────────────

    [Fact]
    public void ExternalMetadataHost_UnknownMapper_Reports1203Warning_ButStillRewrites()
    {
        var hostReference = EmitExternalHostAssembly();
        var options = new ExtensionMethodResolveOptions
        {
            ExternalHostMapper = static _ => ExternalHostMappingKind.Unknown,
        };

        var (_, _, result) = RunExternalHost(hostReference, options);

        Assert.True(result.Changed);
        Assert.Contains(
            "global::ExtHosts.StringMetadataExtensions.ShoutMetadata(\"hi\")",
            Compact(result.OutputCode));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ExtensionSeverity.Warning, diagnostic.Severity);
        Assert.Equal(ExtensionRule.External, diagnostic.Rule);
        Assert.Equal(ExtensionMethodCodes.ExternalHostNoMapping, diagnostic.Code);
        Assert.Contains("ExtHosts.StringMetadataExtensions", diagnostic.Message);
        Assert.Contains("ShoutMetadata", diagnostic.Message);

        Assert.Equal(1, result.Statistics.ExternalHostSites);
        Assert.Equal(1, result.Statistics.ExternalHostUnmappedSites);
        Assert.Equal(0, result.Statistics.ExternalHostMappedSites);
        Assert.Equal(0, result.Statistics.InCompilationHostSites);
    }

    [Fact]
    public void ExternalMetadataHost_UnknownMapper_Strict_Reports1203Error()
    {
        var hostReference = EmitExternalHostAssembly();
        var options = new ExtensionMethodResolveOptions
        {
            Strict = true,
            ExternalHostMapper = static _ => ExternalHostMappingKind.Unknown,
        };

        var (_, _, result) = RunExternalHost(hostReference, options);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ExtensionSeverity.Error, diagnostic.Severity);
        Assert.Equal(ExtensionMethodCodes.ExternalHostNoMapping, diagnostic.Code);
    }

    [Fact]
    public void ExternalMetadataHost_MappedMapper_SilentRewriteAndMappedCounter()
    {
        var hostReference = EmitExternalHostAssembly();
        var options = new ExtensionMethodResolveOptions
        {
            ExternalHostMapper = static _ => ExternalHostMappingKind.Mapped,
        };

        var (_, _, result) = RunExternalHost(hostReference, options);

        Assert.True(result.Changed);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Statistics.ExternalHostSites);
        Assert.Equal(1, result.Statistics.ExternalHostMappedSites);
        Assert.Equal(0, result.Statistics.ExternalHostUnmappedSites);
    }

    [Fact]
    public void ExternalMetadataHost_NoMapper_StaysSilentBackCompat()
    {
        var hostReference = EmitExternalHostAssembly();

        var (_, _, result) = RunExternalHost(hostReference, new ExtensionMethodResolveOptions());

        Assert.True(result.Changed);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Statistics.ExternalHostSites);
        Assert.Equal(0, result.Statistics.ExternalHostUnmappedSites);
        Assert.Equal(0, result.Statistics.ExternalHostMappedSites);
    }

    [Fact]
    public void ExternalMetadataHost_Unknown_RewriteDisabled_SiteUnchangedWith1203()
    {
        var hostReference = EmitExternalHostAssembly();
        var options = new ExtensionMethodResolveOptions
        {
            RewriteExternalMetadataHosts = false,
            ReportInfo = false,
            ExternalHostMapper = static _ => ExternalHostMappingKind.Unknown,
        };

        var (_, _, result) = RunExternalHost(hostReference, options);

        Assert.False(result.Changed);
        Assert.Contains(ExtensionMethodCodes.ExternalHostNoMapping, result.Diagnostics.Select(d => d.Code));
        Assert.Contains("\"hi\".ShoutMetadata()", Compact(result.OutputCode));
        Assert.Equal(1, result.Statistics.SitesSkippedByReason[
            ExtensionCallBinder.ExternalRewriteDisabledReason]);
    }

    // ── InCompilationSupport / InCompilationUser split ────────────────────────

    [Fact]
    public void SourceHost_ClassifiedSupport_WhenSupportClassifierMatches()
    {
        const string source = """
            using Cs2jArray;

            namespace Cs2jArray
            {
                public static class ArrayLikeExtensions
                {
                    public static int DoubleIt(this int x) => x * 2;
                }
            }

            namespace App
            {
                public class Program
                {
                    public int Run() => (21).DoubleIt();
                }
            }
            """;
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest), path: "Cs2jArray/ArrayLike.cs");
        var compilation = CSharpCompilation.Create(
            "em-m3-support-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            BaseReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ExtensionMethodResolveOptions
        {
            IsSupportLibraryHost = host => host.DeclaringSyntaxReferences.Any(r =>
                r.SyntaxTree.FilePath.Replace('\\', '/').Contains("Cs2jArray/")),
        });

        var result = preprocessor.RewriteFile(compilation.GetSemanticModel(tree));

        Assert.True(result.Changed);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Statistics.InCompilationHostSites);
        Assert.Equal(1, result.Statistics.InCompilationSupportHostSites);
        Assert.Equal(0, result.Statistics.InCompilationUserHostSites);
        Assert.Contains(
            "global::Cs2jArray.ArrayLikeExtensions.DoubleIt(",
            Compact(result.OutputCode));
    }

    [Fact]
    public void SourceHost_DefaultClassifiedAsUser()
    {
        const string source = """
            namespace N
            {
                public static class X
                {
                    public static int DoubleIt(this int x) => x * 2;
                }

                public class Program
                {
                    public int Run() => (1).DoubleIt();
                }
            }
            """;
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "em-m3-user-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            BaseReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ExtensionMethodResolveOptions());

        var result = preprocessor.RewriteFile(compilation.GetSemanticModel(tree));

        Assert.Equal(1, result.Statistics.InCompilationHostSites);
        Assert.Equal(0, result.Statistics.InCompilationSupportHostSites);
        Assert.Equal(1, result.Statistics.InCompilationUserHostSites);
    }

    // ── CS2J1290 single-file conservative path ────────────────────────────────

    private const string UnboundCandidateSource = """
        namespace N
        {
            public static class ConstraintExtensions
            {
                public static string NeedsClass<T>(this T value) where T : class => "";
            }

            public class Program
            {
                public string Run()
                {
                    int x = 1;
                    return x.NeedsClass();
                }
            }
        }
        """;

    private static ExtensionFileResult RunUnboundCandidate(ExtensionMethodResolveOptions options)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            UnboundCandidateSource, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "em-m3-unbound-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            BaseReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(options);
        return preprocessor.RewriteFile(compilation.GetSemanticModel(tree));
    }

    [Fact]
    public void SingleFileMode_FailedExtensionCandidate_Reports1290Warning_EvenUnderStrict()
    {
        var result = RunUnboundCandidate(new ExtensionMethodResolveOptions
        {
            SingleFileIncompleteReferences = true,
            Strict = true,
            ReportInfo = false,
        });

        var diagnostic = Assert.Single(result.Diagnostics.Where(
            d => d.Code == ExtensionMethodCodes.SingleFileIncompleteBinding));
        Assert.Equal(ExtensionSeverity.Warning, diagnostic.Severity);
        Assert.Equal(1, result.Statistics.SingleFileDeferredSites);
        Assert.Equal(1, result.Statistics.SitesSkippedByReason[ExtensionMethodCodes.SingleFileIncompleteBinding]);
        Assert.Equal(0, result.Statistics.SitesSkippedByReason.GetValueOrDefault(ExtensionMethodCodes.Unbound));
        Assert.False(result.Changed);
    }

    [Fact]
    public void ProjectMode_FailedExtensionCandidate_Reports1202()
    {
        var result = RunUnboundCandidate(new ExtensionMethodResolveOptions
        {
            ReportInfo = false,
        });

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ExtensionMethodCodes.Unbound, diagnostic.Code);
        Assert.Equal(ExtensionSeverity.Warning, diagnostic.Severity);
        Assert.Equal(0, result.Statistics.SingleFileDeferredSites);
    }

    [Fact]
    public void ProjectMode_FailedExtensionCandidate_Strict_Reports1202Error()
    {
        var result = RunUnboundCandidate(new ExtensionMethodResolveOptions
        {
            Strict = true,
            ReportInfo = false,
        });

        Assert.Equal(ExtensionSeverity.Error, Assert.Single(result.Diagnostics).Severity);
    }

    // ── EM001 verbose detail + statistics ─────────────────────────────────────

    [Fact]
    public void SiteDetail_EmitsEM001Info_WithHostClassificationAndFlags()
    {
        const string source = """
            namespace N
            {
                public static class StringExtensions
                {
                    public static string Reverse(this string s) => s;
                }

                public class Program
                {
                    public string Run() => "abc".Reverse();
                }
            }
            """;
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "em-m3-detail-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            BaseReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ExtensionMethodResolveOptions
        {
            ReportInfo = true,
            ReportSiteDetail = true,
        });

        var result = preprocessor.RewriteFile(compilation.GetSemanticModel(tree));

        var detail = Assert.Single(result.Diagnostics);
        Assert.Equal(ExtensionMethodCodes.Info, detail.Code);
        Assert.Equal(ExtensionSeverity.Info, detail.Severity);
        Assert.Contains("N.StringExtensions", detail.Message);
        Assert.Contains(nameof(ReceiverClassification.InCompilationUser), detail.Message);
        Assert.Contains("Reverse", detail.Message);
    }

    [Fact]
    public void Statistics_SummaryLine_CarriesM3Counters()
    {
        var stats = new ExtensionMethodResolveStatistics();
        stats.InCompilationUserHostSites = 2;
        stats.InCompilationSupportHostSites = 1;
        stats.ExternalHostSites = 3;
        stats.ExternalHostMappedSites = 2;
        stats.ExternalHostUnmappedSites = 1;
        stats.SingleFileDeferredSites = 4;

        var line = stats.SummaryLine();
        Assert.Contains("2 user + 1 support in-compilation", line);
        Assert.Contains("3 external hosts [2 mapped/1 unmapped]", line);
        Assert.Contains("4 single-file deferred", line);
    }
}
