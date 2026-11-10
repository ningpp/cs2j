using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// M3 acceptance gate for R3 (§6.4): library names may only live in
/// <c>config/ClosedTypeConsumerSurfaces.json</c>. Core must contain neither the
/// YAML library name nor the STJ serializer type name. The closed-metadata
/// subtree is enforced with zero exemptions; a small whitelist documents the
/// pre-existing, unrelated STJ runtime/report code that this task does not own
/// (see <see cref="AllowedSerializerNameFiles"/>).
/// </summary>
public sealed class ClosedMetadataConsumerSurfaceGuardTests
{
    private const string ForbiddenYamlLibrary = "YamlDotNet";
    private const string ForbiddenSerializerType = "JsonSerializer";

    /// <summary>
    /// Files outside the closed-metadata subsystem that legitimately use the STJ
    /// serializer type name for their own JSON runtime/report serialization, or
    /// quote it as the user-facing API name. This is a documented exemption, not
    /// a blanket directory exclusion: any file inside
    /// <c>src/CSharpToJava.Core/ClosedMetadata/</c> is still required to be clean.
    /// </summary>
    private static readonly HashSet<string> AllowedSerializerNameFiles = new(StringComparer.Ordinal)
    {
        "src/CSharpToJava.Core/AttributeNormalize/KnownAttributeCatalog.cs",
        "src/CSharpToJava.Core/Context/JavaNaming.cs",
        "src/CSharpToJava.Core/DynamicDesugar/DynamicAuditReport.cs",
        "src/CSharpToJava.Core/DynamicDesugar/DynamicSiteInventory.cs",
        "src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs",
        "src/CSharpToJava.Core/LinqChannel/LinqChannelReport.cs",
        "src/CSharpToJava.Core/MemberDesugar/Finalizers/FinalizerSidecarReport.cs",
        "src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs",
        "src/CSharpToJava.Core/Normalization/ResidualReport.cs",
        "src/CSharpToJava.Core/PartialMerge/PartialMergeAnalyzer.cs",
        "src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs",
        "src/CSharpToJava.Core/Pipeline/Planning/CanarySummarySnapshot.cs",
        "src/CSharpToJava.Core/Pipeline/Planning/InputFingerprintSnapshot.cs",
        "src/CSharpToJava.Core/Pipeline/Planning/JavaWorkspacePlanJsonSerializer.cs",
        "src/CSharpToJava.Core/Pipeline/Planning/OutputIncrementalManifest.cs",
        "src/CSharpToJava.Core/Pipeline/Planning/PassProfileSnapshot.cs",
        "src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs",
        "src/CSharpToJava.Core/PlatformIsolation/PlatformIsolationReport.cs",
        "src/CSharpToJava.Core/ReadOnlyStructMaker/BoxingReachabilityAnalyzer.cs",
        "src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs",
        "src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs",
        "src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs",
        "src/CSharpToJava.Core/Transformers/Type/EnumTransformer.cs",
        "src/CSharpToJava.Core/TypeMapping/TypeMappingRegistry.cs",
        "src/CSharpToJava.Core/VarianceAudit/VarianceAuditPreprocessor.cs",
        "src/CSharpToJava.Core/VarianceUseSite/VarianceUseSiteNormalizePreprocessor.cs",
    };

    [Fact]
    public void Core_ClosedMetadata_And_YamlNames_NeverAppear()
    {
        var repoRoot = FindRepoRoot();
        var coreRoot = Path.Combine(repoRoot, "src", "CSharpToJava.Core");
        var violations = new List<string>();

        foreach (var file in EnumerateCoreSources(coreRoot))
        {
            var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var text = File.ReadAllText(file);

            if (text.Contains(ForbiddenYamlLibrary, StringComparison.Ordinal))
            {
                violations.Add($"{relative}: contains '{ForbiddenYamlLibrary}'");
                continue;
            }

            if (!text.Contains(ForbiddenSerializerType, StringComparison.Ordinal))
            {
                continue;
            }

            if (relative.Contains("/ClosedMetadata/", StringComparison.Ordinal))
            {
                violations.Add($"{relative}: closed-metadata code must not contain '{ForbiddenSerializerType}'");
                continue;
            }

            if (!AllowedSerializerNameFiles.Contains(relative))
            {
                violations.Add(
                    $"{relative}: contains '{ForbiddenSerializerType}' but is not a documented " +
                    "unrelated-runtime exemption; move the name to config/ClosedTypeConsumerSurfaces.json " +
                    "or add an explicit, justified whitelist entry");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void SerializerNameWhitelist_HasNoStaleEntries()
    {
        var repoRoot = FindRepoRoot();

        foreach (var relative in AllowedSerializerNameFiles)
        {
            var path = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"whitelisted file no longer exists: {relative}");
            Assert.Contains(ForbiddenSerializerType, File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> EnumerateCoreSources(string coreRoot)
        => Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(coreRoot, file));

    private static bool IsBuildOutput(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CSharpToJavaConverter.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
