using System.Text.Json;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// R12: preprocess-manifest schema/serialization contract — field order,
/// camelCase, UTF-8/LF, no timestamps or absolute paths, optional artifacts
/// omitted when null.
/// </summary>
public sealed class PreprocessManifestTests
{
    [Fact]
    public void Serialize_uses_the_appendix_f_field_order_and_camel_case()
    {
        var json = PreprocessManifestJson.Serialize(BuildManifest());

        var keys = new[]
        {
            "schemaVersion",
            "subsetVersion",
            "profile",
            "solutionVariant",
            "passPlan",
            "contractHash",
            "inputHash",
            "capabilities",
            "artifacts",
        };

        var lastIndex = -1;
        foreach (var key in keys)
        {
            var index = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
            Assert.True(index > lastIndex, $"expected '{key}' after the previous field");
            lastIndex = index;
        }

        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.DoesNotContain("timestamp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("generatedAt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_omits_null_artifact_entries()
    {
        using var document = JsonDocument.Parse(PreprocessManifestJson.Serialize(BuildManifest()));
        var artifacts = document.RootElement.GetProperty("artifacts");

        Assert.False(artifacts.TryGetProperty("apiManifest", out _));
        Assert.Equal(
            "preprocess-residual-report.json",
            artifacts.GetProperty("residualReport").GetString());
        Assert.Equal(
            "preprocess-report.json",
            artifacts.GetProperty("complexityLedger").GetString());
    }

    [Fact]
    public void Serialize_is_deterministic_and_round_trips_the_core_fields()
    {
        var manifest = BuildManifest();
        var first = PreprocessManifestJson.Serialize(manifest);
        var second = PreprocessManifestJson.Serialize(BuildManifest());
        Assert.Equal(first, second);

        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;

        Assert.Equal(PreprocessManifest.ManifestSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(PreprocessManifest.ManifestSubsetVersion, root.GetProperty("subsetVersion").GetString());
        Assert.Equal("java-ready", root.GetProperty("profile").GetString());
        Assert.Equal(
            "default",
            root.GetProperty("solutionVariant").GetProperty("configuration").GetString());

        var project = root.GetProperty("solutionVariant").GetProperty("projects")[0];
        Assert.Equal("src/App/App.csproj", project.GetProperty("path").GetString());
        Assert.Equal("net10.0", project.GetProperty("tfm").GetString());

        var plan = root.GetProperty("passPlan")[0];
        Assert.Equal("expression-desugar", plan.GetProperty("id").GetString());
        Assert.Equal("sha256:abc", plan.GetProperty("contractHash").GetString());

        var capability = root.GetProperty("capabilities")[0];
        Assert.Equal("JCS.VALUE", capability.GetProperty("id").GetString());
        Assert.Equal("partial", capability.GetProperty("status").GetString());
        Assert.Equal("A.cs", capability.GetProperty("residuals")[0].GetProperty("file").GetString());
    }

    private static PreprocessManifest BuildManifest()
        => new(
            SchemaVersion: PreprocessManifest.ManifestSchemaVersion,
            SubsetVersion: PreprocessManifest.ManifestSubsetVersion,
            Profile: "java-ready",
            SolutionVariant: new ManifestSolutionVariant(
                "default",
                new[] { new ManifestProjectEntry("src/App/App.csproj", "net10.0") }),
            PassPlan: new[] { new ManifestPassPlanEntry("expression-desugar", "sha256:abc") },
            ContractHash: "sha256:contract",
            InputHash: "sha256:input",
            Capabilities: new[]
            {
                JcsCapability.Partial(
                    JcsCapabilityId.Value,
                    new[]
                    {
                        new JcsCapabilityResidual(
                            "A.cs",
                            "struct-value-semantics",
                            "final-tree construct scan deferred to R13"),
                    },
                    constructs: new[] { "NORM-VALUE-COPY" },
                    finalizers: Array.Empty<string>()),
            },
            Artifacts: new ManifestArtifacts(
                ApiManifest: null,
                ResidualReport: "preprocess-residual-report.json",
                ComplexityLedger: "preprocess-report.json"));
}
